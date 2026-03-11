"""Tests for profile rename endpoint."""

import json

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestProfileRename:
    def test_rename_success(self, client, tmp_config):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "children"},
        )
        assert resp.status_code == 200
        assert resp.json()["ok"] is True
        config = read_config(tmp_config)
        assert "children" in config["profiles"]
        assert "kids" not in config["profiles"]

    def test_rename_updates_client_assignments(self, client, tmp_config):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "children"},
        )
        assert resp.json()["ok"] is True
        config = read_config(tmp_config)
        ipad = next(c for c in config["clients"] if c["name"] == "iPad")
        assert ipad["profile"] == "children"

    def test_rename_updates_default_profile(self, client, tmp_config):
        config = read_config(tmp_config)
        config["defaultProfile"] = "kids"
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "children"},
        )
        assert resp.json()["ok"] is True
        config = read_config(tmp_config)
        assert config["defaultProfile"] == "children"

    def test_rename_updates_base_profile(self, client, tmp_config):
        config = read_config(tmp_config)
        config["baseProfile"] = "kids"
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "children"},
        )
        assert resp.json()["ok"] is True
        config = read_config(tmp_config)
        assert config["baseProfile"] == "children"

    def test_rename_missing_names(self, client):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "", "new_name": ""},
        )
        assert resp.status_code == 400
        assert "required" in resp.json()["error"]

    def test_rename_nonexistent_profile(self, client):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "nonexistent", "new_name": "new"},
        )
        assert resp.status_code == 404
        assert "not found" in resp.json()["error"]

    def test_rename_to_existing_name(self, client):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "adults"},
        )
        assert resp.status_code == 409
        assert "already exists" in resp.json()["error"]

    def test_rename_same_name_noop(self, client):
        resp = client.post(
            "/api/profiles/rename",
            json={"old_name": "kids", "new_name": "kids"},
        )
        assert resp.status_code == 200
        assert resp.json()["ok"] is True

    def test_rename_save_error(self, client):
        from unittest.mock import patch

        with patch("config.save_config", side_effect=OSError("disk full")):
            resp = client.post(
                "/api/profiles/rename",
                json={"old_name": "kids", "new_name": "children"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]
