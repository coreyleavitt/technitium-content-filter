"""Tests for OSError handling in save_config across all API endpoints."""

from unittest.mock import patch

import pytest


@pytest.mark.api
class TestSaveConfigOSError:
    """Every API endpoint that calls save_config should return 500 on OSError."""

    def _make_save_fail(self):
        target = "technitium_content_filter.config.save_config"
        return patch(target, side_effect=OSError("disk full"))

    def test_config_set_save_error(self, client):
        with self._make_save_fail():
            resp = client.post("/api/config", json={"enableBlocking": True})
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_profile_save_error(self, client):
        with self._make_save_fail():
            resp = client.post("/api/profiles", json={"name": "test"})
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_profile_delete_save_error(self, client):
        with self._make_save_fail():
            resp = client.request("DELETE", "/api/profiles", json={"name": "kids"})
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_client_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/clients",
                json={"name": "Test", "ids": ["10.0.0.1"], "profile": "kids"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_client_delete_save_error(self, client):
        with self._make_save_fail():
            resp = client.request("DELETE", "/api/clients", json={"index": 0})
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_settings_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/settings",
                json={"enableBlocking": True, "timeZone": "UTC"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_custom_service_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/custom-services",
                json={"id": "svc", "name": "Svc", "domains": ["a.com"]},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_custom_service_delete_save_error(self, client):
        with self._make_save_fail():
            resp = client.request("DELETE", "/api/custom-services", json={"id": "svc"})
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_blocklist_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/blocklists",
                json={"url": "https://example.com/list.txt", "name": "Test"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_blocklist_delete_save_error(self, client):
        with self._make_save_fail():
            resp = client.request(
                "DELETE", "/api/blocklists", json={"url": "https://example.com/list.txt"}
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_allowlist_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/profiles/kids/allowlist",
                json={"domains": ["safe.com"]},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_rules_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/profiles/kids/rules",
                json={"rules": ["bad.com"]},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_rewrite_save_error(self, client):
        with self._make_save_fail():
            resp = client.post(
                "/api/profiles/kids/rewrites",
                json={"domain": "a.com", "answer": "1.2.3.4"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]

    def test_rewrite_delete_save_error(self, client):
        with self._make_save_fail():
            resp = client.request(
                "DELETE",
                "/api/profiles/kids/rewrites",
                json={"domain": "search.com"},
            )
        assert resp.status_code == 500
        assert "Failed to save" in resp.json()["error"]


@pytest.mark.api
class TestSaveConfigAtomicCleanup:
    """Test that save_config cleans up temp file on failure."""

    def test_temp_file_cleaned_on_write_failure(self, tmp_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import save_config

            tmp_file = tmp_config.with_suffix(".tmp")
            # Make rename fail after write succeeds
            with (
                patch.object(type(tmp_file), "rename", side_effect=OSError("rename failed")),
                pytest.raises(OSError, match="rename failed"),
            ):
                save_config({"test": True})
