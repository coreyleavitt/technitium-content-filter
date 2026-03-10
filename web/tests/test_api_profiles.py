"""API tests for profile CRUD and cascade behaviors."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestProfileSave:
    def test_create_profile(self, client, tmp_config):
        resp = client.post(
            "/api/profiles",
            json={
                "name": "toddler",
                "description": "Young children",
                "blockedServices": ["youtube"],
                "blockLists": [],
                "allowList": ["pbskids.org"],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        assert resp.json()["ok"] is True
        config = read_config(tmp_config)
        assert "toddler" in config["profiles"]
        assert config["profiles"]["toddler"]["description"] == "Young children"

    def test_update_existing_profile(self, client, tmp_config):
        resp = client.post(
            "/api/profiles",
            json={
                "name": "kids",
                "description": "Updated",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["description"] == "Updated"
        assert config["profiles"]["kids"]["blockedServices"] == []

    def test_profile_with_all_fields(self, client, tmp_config):
        resp = client.post(
            "/api/profiles",
            json={
                "name": "full",
                "description": "Everything",
                "blockedServices": ["youtube", "tiktok"],
                "blockLists": ["https://example.com/list.txt"],
                "allowList": ["safe.com"],
                "customRules": ["evil.com", "@@exception.com"],
                "dnsRewrites": [{"domain": "search.com", "answer": "1.2.3.4"}],
                "schedule": {"mon": {"allDay": True}},
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        profile = config["profiles"]["full"]
        assert len(profile["blockedServices"]) == 2
        assert len(profile["dnsRewrites"]) == 1
        assert "mon" in profile["schedule"]


@pytest.mark.api
class TestProfileDelete:
    def test_delete_profile(self, client, tmp_config):
        resp = client.request("DELETE", "/api/profiles", json={"name": "kids"})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "kids" not in config["profiles"]

    def test_delete_unassigns_clients(self, client, tmp_config):
        """Clients assigned to deleted profile get profile cleared."""
        client.request("DELETE", "/api/profiles", json={"name": "kids"})
        config = read_config(tmp_config)
        ipad = next(c for c in config["clients"] if c["name"] == "iPad")
        assert ipad["profile"] == ""

    def test_delete_preserves_other_clients(self, client, tmp_config):
        """Clients on other profiles are unaffected."""
        client.request("DELETE", "/api/profiles", json={"name": "kids"})
        config = read_config(tmp_config)
        laptop = next(c for c in config["clients"] if c["name"] == "Laptop")
        assert laptop["profile"] == "adults"

    def test_delete_nonexistent_no_error(self, client):
        resp = client.request("DELETE", "/api/profiles", json={"name": "nonexistent"})
        assert resp.status_code == 200
