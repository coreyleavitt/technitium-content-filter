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
        config["clients"] = [{"name": "iPad", "ids": ["10.0.0.5"], "profile": "kids"}]
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
        config["clients"] = [{"name": "LAN", "ids": ["192.168.1.0/24"], "profile": "kids"}]
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
            tmp_config,
            config,
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
            tmp_config,
            config,
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
            tmp_config,
            config,
            {"kids": _empty_profile(dnsRewrites=rw)},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "ads.example.com"})
        data = resp.json()
        assert data["verdict"] == "REWRITE"
        assert data["rewriteAnswer"] == "0.0.0.0"  # noqa: S104

    def test_rewrite_subdomain_match(self, client, tmp_config):
        config = read_config(tmp_config)
        rw = [{"domain": "example.com", "answer": "1.2.3.4"}]
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile(dnsRewrites=rw)},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "sub.example.com"})
        data = resp.json()
        assert data["verdict"] == "REWRITE"


@pytest.mark.api
class TestTestDomainAllowlist:
    def test_allowlisted_domain(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "kids": _empty_profile(
                    allowList=["safe.example.com"],
                    customRules=["example.com"],
                )
            },
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "safe.example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any(s["step"] == "Allowlist" and s["result"] == "ALLOW" for s in data["steps"])

    def test_exception_rule_allows(self, client, tmp_config):
        """@@-prefixed custom rules act as allowlist entries."""
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "kids": _empty_profile(
                    customRules=["example.com", "@@safe.example.com"],
                )
            },
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "safe.example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainBlocking:
    def test_blocked_service(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile(blockedServices=["test-svc"])},
            defaultProfile="kids",
        )
        services_path = tmp_config.parent / "blocked-services.json"
        services_path.write_text(
            json.dumps(
                {
                    "test-svc": {
                        "name": "Test Service",
                        "domains": ["blocked.example.com", "blocked2.test"],
                    }
                }
            )
        )

        resp = client.post("/api/test-domain", json={"domain": "blocked.example.com"})
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_custom_rule_blocks(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "kids": _empty_profile(
                    customRules=["malware.example.com"],
                )
            },
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "malware.example.com"})
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_subdomain_blocked(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile(customRules=["example.com"])},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "sub.deep.example.com"})
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_allowed_domain_passes(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "safe.example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainBaseProfile:
    def test_base_profile_blocks_merged(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "base": _empty_profile(customRules=["ads.example.com"]),
                "kids": _empty_profile(),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post("/api/test-domain", json={"domain": "ads.example.com"})
        data = resp.json()
        assert data["verdict"] == "BLOCK"

    def test_profile_allowlist_overrides_base_block(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "base": _empty_profile(customRules=["example.com"]),
                "kids": _empty_profile(allowList=["safe.example.com"]),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post("/api/test-domain", json={"domain": "safe.example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"


@pytest.mark.api
class TestTestDomainStepsStructure:
    def test_steps_have_required_fields(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
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
            tmp_config,
            config,
            {"kids": _empty_profile(blockLists=[bl_url])},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "something.test"})
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
            tmp_config,
            config,
            {"kids": _empty_profile(blockedServices=["my-svc"])},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "custom.example.com"})
        data = resp.json()
        assert data["verdict"] == "BLOCK"


@pytest.mark.api
class TestTestDomainBlockingDisabledStep:
    """Verify the blocking-disabled path returns the correct step structure."""

    def test_blocking_disabled_step_detail(self, client, tmp_config):
        config = read_config(tmp_config)
        config["enableBlocking"] = False
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        step = data["steps"][0]
        assert step["step"] == "Blocking enabled"
        assert step["result"] == "ALLOW"
        assert "disabled" in step["detail"].lower()


@pytest.mark.api
class TestTestDomainNoClientIp:
    """Test domain with no client IP and no default profile."""

    def test_no_ip_no_default_no_base(self, client, tmp_config):
        config = read_config(tmp_config)
        config["defaultProfile"] = None
        config["baseProfile"] = None
        config["clients"] = []
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any("No client IP provided" in s["detail"] for s in data["steps"])

    def test_no_ip_with_default_profile(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {"kids": _empty_profile()},
            defaultProfile="kids",
        )

        resp = client.post("/api/test-domain", json={"domain": "example.com"})
        data = resp.json()
        assert data["profile"] == "kids"
        assert any("default profile" in s["detail"] for s in data["steps"])


@pytest.mark.api
class TestTestDomainProfileNotFound:
    """Profile name resolved but doesn't exist in config."""

    def test_profile_not_in_config(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = [{"name": "PC", "ids": ["10.0.0.1"], "profile": "nonexistent"}]
        config["profiles"] = {"kids": _empty_profile()}
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.1"},
        )
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any("not found" in s["detail"] for s in data["steps"])


@pytest.mark.api
class TestTestDomainBaseProfileFallback:
    """Test falling back to base profile when no profile resolved."""

    def test_base_profile_used_when_no_match(self, client, tmp_config):
        config = read_config(tmp_config)
        config["clients"] = []
        config["defaultProfile"] = None
        _set_profile(
            tmp_config,
            config,
            {"base": _empty_profile(customRules=["bad.com"])},
            baseProfile="base",
            defaultProfile=None,
        )

        resp = client.post(
            "/api/test-domain",
            json={"domain": "bad.com", "clientIp": "10.0.0.99"},
        )
        data = resp.json()
        assert data["verdict"] == "BLOCK"
        assert any("base profile" in s["detail"].lower() for s in data["steps"])


@pytest.mark.api
class TestTestDomainBaseProfileRewrites:
    """Test that base profile rewrites are merged into the active profile."""

    def test_base_rewrite_merged(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "base": _empty_profile(
                    dnsRewrites=[{"domain": "ads.com", "answer": "0.0.0.0"}]  # noqa: S104
                ),
                "kids": _empty_profile(),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post("/api/test-domain", json={"domain": "ads.com"})
        data = resp.json()
        assert data["verdict"] == "REWRITE"
        assert data["rewriteAnswer"] == "0.0.0.0"  # noqa: S104


@pytest.mark.api
class TestTestDomainBaseProfileAllowlist:
    """Test that base profile allowlist entries are merged."""

    def test_base_allowlist_merged(self, client, tmp_config):
        config = read_config(tmp_config)
        _set_profile(
            tmp_config,
            config,
            {
                "base": _empty_profile(
                    allowList=["safe.com"],
                    customRules=["safe.com"],
                ),
                "kids": _empty_profile(customRules=["safe.com"]),
            },
            defaultProfile="kids",
            baseProfile="base",
        )

        resp = client.post("/api/test-domain", json={"domain": "safe.com"})
        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any(s["step"] == "Allowlist" and s["result"] == "ALLOW" for s in data["steps"])


@pytest.mark.api
class TestTestDomainScheduleInactive:
    """Test schedule-inactive path."""

    def test_schedule_outside_window(self, client, tmp_config):
        from datetime import datetime
        from unittest.mock import patch
        from zoneinfo import ZoneInfo

        config = read_config(tmp_config)
        # Create a profile with a narrow window
        profile = _empty_profile(
            customRules=["blocked.com"],
            schedule={"mon": {"allDay": False, "start": "08:00", "end": "09:00"}},
        )
        _set_profile(
            tmp_config,
            config,
            {"kids": profile},
            defaultProfile="kids",
            scheduleAllDay=False,
        )

        # Mock time to Monday 22:00 UTC (outside 08:00-09:00)
        fake_now = datetime(2026, 3, 9, 22, 0, tzinfo=ZoneInfo("UTC"))  # Monday
        with patch("filtering.datetime") as mock_dt:
            mock_dt.now.return_value = fake_now
            mock_dt.side_effect = lambda *a, **kw: datetime(*a, **kw)
            resp = client.post("/api/test-domain", json={"domain": "blocked.com"})

        data = resp.json()
        assert data["verdict"] == "ALLOW"
        assert any(s["step"] == "Schedule" and s["result"] == "ALLOW" for s in data["steps"])


@pytest.mark.api
class TestTestDomainClientResolutionEdgeCases:
    """Edge cases in client resolution for the test domain endpoint."""

    def test_invalid_client_id_in_config_skipped(self, client, tmp_config):
        """Non-IP client IDs (like DNS names) should be skipped without error."""
        config = read_config(tmp_config)
        config["clients"] = [
            {"name": "PC", "ids": ["not-an-ip", "10.0.0.5"], "profile": "kids"},
        ]
        _set_profile(tmp_config, config, {"kids": _empty_profile()})

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.5"},
        )
        data = resp.json()
        assert data["profile"] == "kids"

    def test_invalid_cidr_in_config_skipped(self, client, tmp_config):
        """Invalid CIDR entries should be skipped without error."""
        config = read_config(tmp_config)
        config["clients"] = [
            {"name": "PC", "ids": ["bad/cidr", "10.0.0.0/24"], "profile": "kids"},
        ]
        _set_profile(tmp_config, config, {"kids": _empty_profile()})

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.5"},
        )
        data = resp.json()
        assert data["profile"] == "kids"

    def test_non_dict_client_entry_skipped(self, client, tmp_config):
        """Non-dict entries in clients list should be skipped."""
        config = read_config(tmp_config)
        config["clients"] = [
            "not-a-dict",
            {"name": "PC", "ids": ["10.0.0.5"], "profile": "kids"},
        ]
        _set_profile(tmp_config, config, {"kids": _empty_profile()})

        resp = client.post(
            "/api/test-domain",
            json={"domain": "example.com", "clientIp": "10.0.0.5"},
        )
        data = resp.json()
        assert data["profile"] == "kids"
