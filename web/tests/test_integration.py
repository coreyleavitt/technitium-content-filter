"""Integration tests: multi-step workflows and cascade behaviors."""

import json
from unittest.mock import patch

import pytest
import respx
from httpx import Response

from tests.conftest import read_config


@pytest.mark.api
class TestProfileClientCascade:
    def test_create_profile_assign_client_delete_profile(self, client, tmp_config):
        """Full lifecycle: profile -> client -> delete profile -> client unassigned."""
        # Create profile
        client.post(
            "/api/profiles",
            json={
                "name": "temp",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        # Assign client
        client.post(
            "/api/clients",
            json={
                "name": "Test Device",
                "ids": ["192.168.1.100"],
                "profile": "temp",
            },
        )
        config = read_config(tmp_config)
        assert config["clients"][-1]["profile"] == "temp"

        # Delete profile
        client.request("DELETE", "/api/profiles", json={"name": "temp"})

        config = read_config(tmp_config)
        device = next(c for c in config["clients"] if c["name"] == "Test Device")
        assert device["profile"] == ""


@pytest.mark.api
class TestBlocklistCascade:
    def test_delete_blocklist_removes_from_all_profiles(self, client, tmp_config):
        """Add blocklist to multiple profiles, delete it, verify removed everywhere."""
        # Add second profile using same blocklist
        client.post(
            "/api/profiles",
            json={
                "name": "teens",
                "blockedServices": [],
                "blockLists": ["https://example.com/list.txt"],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )

        # Delete the blocklist
        client.request("DELETE", "/api/blocklists", json={"url": "https://example.com/list.txt"})

        config = read_config(tmp_config)
        assert "https://example.com/list.txt" not in config["profiles"]["kids"]["blockLists"]
        assert "https://example.com/list.txt" not in config["profiles"]["teens"]["blockLists"]
        assert not any(bl["url"] == "https://example.com/list.txt" for bl in config["blockLists"])


@pytest.mark.api
class TestRewriteRoundTrip:
    def test_create_update_delete_rewrite(self, client, tmp_config):
        """Full rewrite lifecycle."""
        # Create
        client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "test.local",
                "answer": "1.1.1.1",
            },
        )
        config = read_config(tmp_config)
        assert any(r["domain"] == "test.local" for r in config["profiles"]["kids"]["dnsRewrites"])

        # Update
        client.post(
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "test.local",
                "answer": "2.2.2.2",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        found = [r for r in rewrites if r["domain"] == "test.local"]
        assert len(found) == 1
        assert found[0]["answer"] == "2.2.2.2"

        # Delete
        client.request(
            "DELETE",
            "/api/rewrites",
            json={
                "profile": "kids",
                "domain": "test.local",
            },
        )
        config = read_config(tmp_config)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        assert not any(r["domain"] == "test.local" for r in rewrites)


@pytest.mark.api
class TestTechnitiumReload:
    def test_reload_failure_still_saves(self, tmp_config, sample_config):
        """Config saved even when Technitium reload fails."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            # Technitium returns 500
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(500, text="Internal error")
            )
            from starlette.testclient import TestClient

            from app import app

            c = TestClient(app, raise_server_exceptions=True)

            resp = c.post(
                "/api/settings",
                json={
                    "enableBlocking": False,
                    "timeZone": "UTC",
                    "defaultProfile": "",
                    "baseProfile": "",
                    "scheduleAllDay": True,
                },
            )

        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["enableBlocking"] is False

    def test_no_token_skips_reload(self, tmp_config, sample_config):
        """No API token means reload is skipped (returns False)."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", ""),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False),
        ):
            from starlette.testclient import TestClient

            from app import app

            c = TestClient(app, raise_server_exceptions=True)

            resp = c.post("/api/config", json={"enableBlocking": True})

        data = resp.json()
        assert data["ok"] is True
        assert data["reloaded"] is False
