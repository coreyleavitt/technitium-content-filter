"""Tests for error handling: missing fields, malformed input, unhandled exceptions."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestMissingRequiredFields:
    def test_profile_save_missing_name(self, client, tmp_config):
        """POST /api/profiles without 'name' saves with empty-string key."""
        resp = client.post(
            "/api/profiles",
            json={
                "blockedServices": [],
                "blockLists": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "" in config["profiles"]

    def test_profile_delete_missing_name(self, client):
        """DELETE /api/profiles without 'name' is a no-op (empty string not found)."""
        resp = client.request("DELETE", "/api/profiles", json={})
        assert resp.status_code == 200

    def test_custom_service_save_missing_id(self, client, tmp_config):
        """POST /api/custom-services without 'id' saves with empty-string key."""
        resp = client.post(
            "/api/custom-services",
            json={
                "name": "Missing ID",
                "domains": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "" in config["customServices"]

    def test_custom_service_delete_missing_id(self, client):
        """DELETE /api/custom-services without 'id' is a no-op."""
        resp = client.request("DELETE", "/api/custom-services", json={})
        assert resp.status_code == 200


@pytest.mark.api
class TestMalformedRequestBody:
    def test_profile_save_invalid_json(self, client_permissive):
        """Sending non-JSON body to API endpoint."""
        resp = client_permissive.post(
            "/api/profiles",
            content=b"not json{{{",
            headers={"content-type": "application/json"},
        )
        assert resp.status_code in (400, 500)

    def test_config_set_invalid_json(self, client_permissive):
        resp = client_permissive.post(
            "/api/config",
            content=b"<xml>bad</xml>",
            headers={"content-type": "application/json"},
        )
        assert resp.status_code in (400, 500)

    def test_settings_save_empty_body(self, client, tmp_config):
        """Empty JSON object should use defaults, not crash."""
        resp = client.post("/api/settings", json={})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "enableBlocking" in config


@pytest.mark.api
class TestClientProfileValidation:
    def test_client_with_nonexistent_profile_accepted(self, client, tmp_config):
        """Client can reference a profile that doesn't exist (unvalidated)."""
        resp = client.post(
            "/api/clients",
            json={
                "name": "Ghost Profile",
                "ids": ["10.0.0.99"],
                "profile": "does-not-exist",
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        added = config["clients"][-1]
        assert added["profile"] == "does-not-exist"

    def test_client_save_minimal_fields(self, client, tmp_config):
        """Client with no optional fields uses defaults."""
        resp = client.post("/api/clients", json={})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        added = config["clients"][-1]
        assert added["name"] == ""
        assert added["ids"] == []
        assert added["profile"] == ""

    def test_client_delete_missing_index(self, client, tmp_config):
        """DELETE /api/clients without index field is a no-op save."""
        initial = read_config(tmp_config)["clients"].copy()
        resp = client.request("DELETE", "/api/clients", json={})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert len(config["clients"]) == len(initial)

    def test_config_set_empty_object(self, client, tmp_config):
        """POST /api/config with {} should save and not crash."""
        resp = client.post("/api/config", json={})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert isinstance(config, dict)


@pytest.mark.api
class TestConfigFullOverwrite:
    def test_config_set_replaces_entire_config(self, client, tmp_config):
        """POST /api/config overwrites everything -- profiles, clients, all gone."""
        resp = client.post("/api/config", json={"foo": "bar"})
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config == {"foo": "bar"}
        assert "profiles" not in config
        assert "clients" not in config

    def test_config_set_then_get_reflects_overwrite(self, client, tmp_config):
        """GET /api/config after full overwrite returns the new config (with defaults merged)."""
        client.post("/api/config", json={"enableBlocking": False, "_blockListsSeeded": True})
        resp = client.get("/api/config")
        data = resp.json()
        assert data["enableBlocking"] is False


@pytest.mark.api
class TestProfileSaveSchema:
    def test_arbitrary_keys_persisted(self, client, tmp_config):
        """Profile save uses data.pop('name') and saves the rest -- arbitrary keys are stored."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "test-schema",
                "blockedServices": [],
                "unexpected_field": "injected",
                "another": 42,
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        profile = config["profiles"]["test-schema"]
        assert profile["unexpected_field"] == "injected"
        assert profile["another"] == 42


@pytest.mark.api
class TestDanglingProfileReferences:
    def test_default_profile_dangles_after_delete(self, client, tmp_config):
        """Deleting a profile that is defaultProfile leaves a dangling reference."""
        # Set defaultProfile to kids
        client.post(
            "/api/settings",
            json={
                "enableBlocking": True,
                "timeZone": "UTC",
                "defaultProfile": "kids",
                "baseProfile": "",
                "scheduleAllDay": True,
            },
        )
        config = read_config(tmp_config)
        assert config["defaultProfile"] == "kids"

        # Delete the kids profile
        client.request("DELETE", "/api/profiles", json={"name": "kids"})
        config = read_config(tmp_config)
        assert "kids" not in config["profiles"]
        # defaultProfile still references deleted profile
        assert config["defaultProfile"] == "kids"

    def test_base_profile_dangles_after_delete(self, client, tmp_config):
        """Deleting a profile that is baseProfile leaves a dangling reference."""
        client.post(
            "/api/settings",
            json={
                "enableBlocking": True,
                "timeZone": "UTC",
                "defaultProfile": "",
                "baseProfile": "adults",
                "scheduleAllDay": True,
            },
        )
        client.request("DELETE", "/api/profiles", json={"name": "adults"})
        config = read_config(tmp_config)
        assert "adults" not in config["profiles"]
        assert config["baseProfile"] == "adults"
