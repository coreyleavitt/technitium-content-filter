"""API tests for allowlists, custom rules, and DNS rewrites."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestAllowlistSave:
    def test_update_allowlist(self, client, tmp_config):
        resp = client.post(
            "/api/allowlists",
            json={
                "profile": "kids",
                "domains": ["youtube.com", "youtubekids.com"],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["allowList"] == ["youtube.com", "youtubekids.com"]

    def test_clear_allowlist(self, client, tmp_config):
        client.post("/api/allowlists", json={"profile": "kids", "domains": []})
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["allowList"] == []

    def test_nonexistent_profile_returns_400(self, client):
        resp = client.post(
            "/api/allowlists",
            json={
                "profile": "nonexistent",
                "domains": ["example.com"],
            },
        )
        assert resp.status_code == 400
        assert "Profile not found" in resp.json()["error"]


@pytest.mark.api
class TestRulesSave:
    def test_update_rules(self, client, tmp_config):
        resp = client.post(
            "/api/rules",
            json={
                "profile": "kids",
                "rules": ["ads.com", "@@safe.ads.com", "# comment"],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        expected = ["ads.com", "@@safe.ads.com", "# comment"]
        assert config["profiles"]["kids"]["customRules"] == expected

    def test_clear_rules(self, client, tmp_config):
        client.post("/api/rules", json={"profile": "kids", "rules": []})
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["customRules"] == []

    def test_nonexistent_profile_returns_400(self, client):
        resp = client.post(
            "/api/rules",
            json={
                "profile": "nonexistent",
                "rules": ["blocked.com"],
            },
        )
        assert resp.status_code == 400


@pytest.mark.api
class TestRewriteSave:
    def test_create_rewrite(self, client, tmp_config):
        resp = client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "internal.local",
                "answer": "192.168.1.1",
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        found = [r for r in rewrites if r["domain"] == "internal.local"]
        assert len(found) == 1
        assert found[0]["answer"] == "192.168.1.1"

    def test_update_existing_rewrite(self, client, tmp_config):
        client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "search.com",
                "answer": "10.0.0.1",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        found = [r for r in rewrites if r["domain"] == "search.com"]
        assert len(found) == 1
        assert found[0]["answer"] == "10.0.0.1"

    def test_domain_normalized_lowercase(self, client, tmp_config):
        client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "UPPER.COM",
                "answer": "1.2.3.4",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        found = [r for r in rewrites if r["domain"] == "upper.com"]
        assert len(found) == 1

    def test_domain_trailing_dot_stripped(self, client, tmp_config):
        client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "dotted.com.",
                "answer": "1.2.3.4",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        found = [r for r in rewrites if r["domain"] == "dotted.com"]
        assert len(found) == 1

    def test_empty_domain_returns_400(self, client):
        resp = client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "",
                "answer": "1.2.3.4",
            },
        )
        assert resp.status_code == 400
        assert "Domain and answer required" in resp.json()["error"]

    def test_empty_answer_returns_400(self, client):
        resp = client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "example.com",
                "answer": "",
            },
        )
        assert resp.status_code == 400

    def test_whitespace_only_domain_returns_400(self, client):
        resp = client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "   ",
                "answer": "1.2.3.4",
            },
        )
        assert resp.status_code == 400

    def test_nonexistent_profile_returns_400(self, client):
        resp = client.post(
            "/api/rewrites",
            json={
                "profile": "nonexistent",
                "domain": "example.com",
                "answer": "1.2.3.4",
            },
        )
        assert resp.status_code == 400


@pytest.mark.api
class TestRewriteDelete:
    def test_delete_rewrite(self, client, tmp_config):
        resp = client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "search.com",
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        assert not any(r["domain"] == "search.com" for r in rewrites)

    def test_delete_case_insensitive(self, client, tmp_config):
        client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "SEARCH.COM",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        assert not any("search" in r["domain"].lower() for r in rewrites)

    def test_delete_with_trailing_dot(self, client, tmp_config):
        client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "search.com.",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        assert not any(r["domain"] == "search.com" for r in rewrites)

    def test_delete_nonexistent_profile_returns_400(self, client):
        resp = client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "nonexistent",
                "domain": "example.com",
            },
        )
        assert resp.status_code == 400

    def test_delete_nonexistent_domain_no_error(self, client):
        resp = client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "doesnt-exist.com",
            },
        )
        assert resp.status_code == 200
