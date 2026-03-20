"""API tests for blocklist CRUD and cascade behaviors."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestBlocklistSave:
    def test_create_new_blocklist(self, client, tmp_config):
        resp = client.post(
            "/api/blocklists",
            json={
                "url": "https://new-list.txt",
                "name": "New Blocklist",
                "enabled": True,
                "refreshHours": 12,
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://new-list.txt"]
        assert len(found) == 1
        assert found[0]["name"] == "New Blocklist"
        assert found[0]["refreshHours"] == 12

    def test_update_existing_blocklist(self, client, tmp_config):
        resp = client.post(
            "/api/blocklists",
            json={
                "url": "https://example.com/list.txt",
                "name": "Updated Name",
                "enabled": False,
                "refreshHours": 48,
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://example.com/list.txt"]
        assert found[0]["name"] == "Updated Name"
        assert found[0]["enabled"] is False
        assert found[0]["refreshHours"] == 48

    def test_empty_url_returns_400(self, client):
        resp = client.post(
            "/api/blocklists",
            json={
                "url": "",
                "name": "Bad",
            },
        )
        assert resp.status_code == 400
        assert "URL required" in resp.json()["error"]

    def test_whitespace_url_returns_400(self, client):
        resp = client.post(
            "/api/blocklists",
            json={
                "url": "   ",
                "name": "Bad",
            },
        )
        assert resp.status_code == 400

    def test_defaults_for_missing_fields(self, client, tmp_config):
        client.post("/api/blocklists", json={"url": "https://minimal.txt"})
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://minimal.txt"]
        assert found[0]["name"] == ""
        assert found[0]["enabled"] is True
        assert found[0]["refreshHours"] == 24

    def test_no_duplicate_entries(self, client, tmp_config):
        """Saving same URL twice should update, not duplicate."""
        client.post("/api/blocklists", json={"url": "https://example.com/list.txt", "name": "V1"})
        client.post("/api/blocklists", json={"url": "https://example.com/list.txt", "name": "V2"})
        config = read_config(tmp_config)
        matches = [bl for bl in config["blockLists"] if bl["url"] == "https://example.com/list.txt"]
        assert len(matches) == 1
        assert matches[0]["name"] == "V2"

    def test_save_with_regex_type(self, client, tmp_config):
        resp = client.post(
            "/api/blocklists",
            json={
                "url": "https://regex-list.txt",
                "name": "Regex List",
                "type": "regex",
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://regex-list.txt"]
        assert len(found) == 1
        assert found[0]["type"] == "regex"

    def test_save_with_invalid_type_returns_400(self, client):
        resp = client.post(
            "/api/blocklists",
            json={"url": "https://bad-type.txt", "type": "invalid"},
        )
        assert resp.status_code == 400
        assert "Invalid type" in resp.json()["error"]

    def test_save_without_type_defaults_to_domain(self, client, tmp_config):
        client.post("/api/blocklists", json={"url": "https://no-type.txt"})
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://no-type.txt"]
        assert found[0]["type"] == "domain"

    def test_toggle_preserves_type(self, client, tmp_config):
        """Toggle enabled/disabled preserves the type field."""
        client.post(
            "/api/blocklists",
            json={"url": "https://toggle-test.txt", "type": "regex", "enabled": True},
        )
        # Toggle off
        client.post(
            "/api/blocklists",
            json={"url": "https://toggle-test.txt", "type": "regex", "enabled": False},
        )
        config = read_config(tmp_config)
        found = [bl for bl in config["blockLists"] if bl["url"] == "https://toggle-test.txt"]
        assert found[0]["type"] == "regex"
        assert found[0]["enabled"] is False


@pytest.mark.api
class TestBlocklistDelete:
    def test_delete_blocklist(self, client, tmp_config):
        resp = client.request(
            "DELETE", "/api/blocklists", json={"url": "https://example.com/list.txt"}
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        urls = [bl["url"] for bl in config["blockLists"]]
        assert "https://example.com/list.txt" not in urls

    def test_delete_cascades_to_profiles(self, client, tmp_config):
        """URL reference removed from all profiles."""
        client.request("DELETE", "/api/blocklists", json={"url": "https://example.com/list.txt"})
        config = read_config(tmp_config)
        assert "https://example.com/list.txt" not in config["profiles"]["kids"]["blockLists"]

    def test_delete_nonexistent_no_error(self, client):
        resp = client.request("DELETE", "/api/blocklists", json={"url": "https://missing.txt"})
        assert resp.status_code == 200


@pytest.mark.api
class TestBlocklistRefresh:
    def test_refresh_returns_reloaded(self, client):
        resp = client.post("/api/blocklists/refresh")
        assert resp.status_code == 200
        data = resp.json()
        assert data["ok"] is True
        assert "reloaded" in data
