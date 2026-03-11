"""Tests for the domain test endpoint (#118)."""

import json

import pytest

from tests.conftest import read_config


def _empty_profile(**overrides):
    """Build a minimal profile dict with optional overrides."""
    base = {
        "blockedServices": [],
        "allowList": [],
        "customRules": [],
        "dnsRewrites": [],
    }
    base.update(overrides)
    return base


def _set_profile(tmp_config, config, profiles, **config_overrides):
    """Write config with given profiles and overrides."""
    config["profiles"] = profiles
    config.update(config_overrides)
    tmp_config.write_text(json.dumps(config, indent=2))


@pytest.mark.api
class TestTestDomainValidation:
    def test_missing_domain(self, client):
        resp = client.post("/api/test-domain", json={"domain": ""})
        assert resp.status_code == 400
        assert "required" in resp.json()["error"].lower()

    def test_invalid_ip(self, client):
        resp = client.post(
            "/api/test-domain", json={"domain": "example.com", "clientIp": "not-an-ip"}
        )
        assert resp.status_code == 400
        assert "Invalid IP" in resp.json()["error"]


@pytest.mark.api
class TestTestDomainBlockingDisabled:
    def test_blocking_disabled_returns_allow(self, client, tmp_config):
        config = read_config(tmp_config)
        config["enableBlocking"] = False
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
        data = resp.json()
        assert data["ok"] is True
        assert data["verdict"] == "ALLOW"
        assert any(s["result"] == "ALLOW" for s in data["steps"])


@pytest.mark.api
class TestTestDomainClientResolution:
    def test_exact_ip_match(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = [
            {"name": "iPad", "ids": ["10.0.0.5"], "profile": "kids"}
        ]
        _set_profile(tmp_config, config, {"kids": _empty_profile()})

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.5"},
        )
        data = resp.json()
        assert data["profile"] == "kids"
        assert any("exact IP" in s["detail"] for s in data["steps"])

    def test_cidr_match(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = [
            {"name": "LAN", "ids": ["192.168.1.0/24"], "profile": "kids"}
        ]
        _set_profile(tmp_config, config, {"kids": _empty_profile()})

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "192.168.1.50"},
        )
        data = resp.json()
        assert data["profile"] == "kids"
        assert any("CIDR" in s["detail"] for s in data["steps"])

    def test_default_profile(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = []
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.99"},
        )
        data = resp.json()
        assert data["profile"] == "kids"
        assert any("default profile" in s["detail"] for s in data["steps"])

    def test_no_profile_returns_allow(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = []
        config["defaultProfile"] = None
        config["baseProfile"] = None
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.99"},
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"

    def test_no_client_ip_uses_default(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
        data = resp.json()
        assert data["profile"] == "kids"


@pytest.mark.api
class TestTestDomainRewrite:
    def test_rewrite_match(self, client, tmp_config):
        config = read_config(tmp_config)
        rw = [{"domain": "ads.example.com", "answer": "0.0.0.0"}]  # noqa: S104
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(dnsRewrites=rw)},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "ads.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "REWRITE"
        assert data["rewriteAnswer"] == "0.0.0.0"  # noqa: S104

    def test_rewrite_subdomain_match(self, client, tmp_config):
        config = read_config(tmp_config)
        rw = [{"domain": "example.com", "answer": "1.2.3.4"}]
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(dnsRewrites=rw)},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "sub.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "REWRITE"


@pytest.mark.api
class TestTestDomainAllowlist:
    def test_allowlisted_domain(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(
                allowList=["safe.example.com"],
                customRules=["example.com"],
            )},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "safe.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any(
            s["step"] == "Allowlist" and s["result"] == "ALLOW"
            for s in data["steps"]
        )

    def test_exception_rule_allows(self, client, tmp_config):
        """@@-prefixed custom rules act as allowlist entries."""
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(
                customRules=["example.com", "@@safe.example.com"],
            )},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "safe.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainBlocking:
    def test_blocked_service(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(blockedServices=["test-svc"])},
            defaultProfile="kids",
        )
        services_path = tmp_config.parent / "blocked-services.json"
        services_path.write_text(json.dumps({
            "test-svc": {
                "name": "Test Service",
                "domains": ["blocked.example.com", "blocked2.test"],
            }
        }))

        resp = client.post(
            "/api/test-domain", json={"domain": "blocked.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_custom_rule_blocks(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(
                customRules=["malware.example.com"],
            )},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "malware.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_subdomain_blocked(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(customRules=["example.com"])},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "sub.deep.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_allowed_domain_passes(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "safe.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainBaseProfile:
    def test_base_profile_blocks_merged(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {
                "base": _empty_profile(customRules=["ads.example.com"]),
                "kids": _empty_profile(),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "ads.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_profile_allowlist_overrides_base_block(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {
                "base": _empty_profile(customRules=["example.com"]),
                "kids": _empty_profile(allowList=["safe.example.com"]),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "safe.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainStepsStructure:
    def test_steps_have_required_fields(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "example.com"}
        )
        data = resp.json()
        assert data["ok"] is True
        assert "steps" in data
        assert "verdict" in data
        for step in data["steps"]:
            assert "step" in step
            assert "result" in step
            assert "detail" in step

    def test_blocklist_urls_reported(self, client, tmp_config):
        config = read_config(tmp_config)
        bl_url = "https://example.com/hosts.txt"
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(blockLists=[bl_url])},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "something.test"}
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert data.get("blocklistUrls") == [bl_url]


@pytest.mark.api
class TestTestDomainCustomServices:
    def test_custom_service_blocks(self, client, tmp_config):
        config = read_config(tmp_config)
        config["customServices"] = {
            "my-svc": {
                "name": "My Service",
                "domains": ["custom.example.com"],
            }
        }
        _set_profile(
            tmp_config, config,
            {"kids": _empty_profile(blockedServices=["my-svc"])},
            defaultProfile="kids",
        )

        resp = client.post(
            "/api/test-domain", json={"domain": "custom.example.com"}
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"
