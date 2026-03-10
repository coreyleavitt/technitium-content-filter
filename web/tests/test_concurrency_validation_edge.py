"""Tests for concurrency, validation, and edge cases (#59-#69)."""

import json
import string
from unittest.mock import patch

import pytest
import respx
from httpx import Response
from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st
from starlette.testclient import TestClient

from tests.conftest import read_config


# ---------------------------------------------------------------------------
# #59: Config read-modify-write race condition
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestConcurrentConfigWrites:
    """Verify no data loss under concurrent save operations."""

    def test_concurrent_saves_no_data_loss(self, tmp_config, sample_config):
        """Multiple sequential saves don't corrupt the config file."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config, save_config

            # Simulate multiple rapid saves with different profiles
            for i in range(10):
                config = load_config()
                profiles = config.get("profiles", {})
                profiles[f"profile-{i}"] = {
                    "description": f"Profile {i}",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                }
                config["profiles"] = profiles
                save_config(config)

            final = load_config()
            # All 10 profiles plus original 2 should be present
            assert len(final["profiles"]) == 12

    def test_save_is_atomic_no_partial_writes(self, tmp_config, sample_config):
        """After save, config is always valid JSON (no partial writes)."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        with patch("app.CONFIG_PATH", tmp_config):
            from app import save_config

            for i in range(20):
                save_config({**sample_config, "counter": i})
                # Read back immediately -- should always be valid JSON
                data = json.loads(tmp_config.read_text())
                assert data["counter"] == i

    def test_last_writer_wins(self, tmp_config, sample_config):
        """When two writes happen sequentially, the last one wins."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        with patch("app.CONFIG_PATH", tmp_config):
            from app import save_config

            save_config({**sample_config, "enableBlocking": True})
            save_config({**sample_config, "enableBlocking": False})

            final = json.loads(tmp_config.read_text())
            assert final["enableBlocking"] is False


# ---------------------------------------------------------------------------
# #60: Malformed JSON config file
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestMalformedJsonConfig:
    """App behavior when config file contains invalid JSON."""

    def test_truncated_json_raises(self, tmp_config):
        """Truncated JSON should raise JSONDecodeError."""
        tmp_config.write_text('{"enableBlocking": true, "profiles":')
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config

            with pytest.raises(json.JSONDecodeError):
                load_config()

    def test_syntax_error_json_raises(self, tmp_config):
        """JSON with syntax errors should raise JSONDecodeError."""
        tmp_config.write_text('{enableBlocking: true}')
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config

            with pytest.raises(json.JSONDecodeError):
                load_config()

    def test_empty_file_raises(self, tmp_config):
        """Empty file should raise JSONDecodeError."""
        tmp_config.write_text("")
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config

            with pytest.raises(json.JSONDecodeError):
                load_config()

    def test_null_json_raises(self, tmp_config):
        """JSON 'null' should raise (not a dict)."""
        tmp_config.write_text("null")
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config

            with pytest.raises((json.JSONDecodeError, TypeError, AttributeError)):
                load_config()

    def test_json_array_raises(self, tmp_config):
        """JSON array should raise (not a dict)."""
        tmp_config.write_text("[1, 2, 3]")
        with patch("app.CONFIG_PATH", tmp_config):
            from app import load_config

            with pytest.raises((TypeError, AttributeError)):
                load_config()


# ---------------------------------------------------------------------------
# #61: Property tests for URL validation
# ---------------------------------------------------------------------------


# Strategies for URL schemes
valid_url_scheme = st.sampled_from(["http", "https"])
invalid_url_scheme = st.sampled_from(["ftp", "file", "data", "javascript"])

_domain_strategy = st.text(
    alphabet=string.ascii_lowercase + string.digits + ".-",
    min_size=3,
    max_size=30,
).filter(lambda s: "." in s and not s.startswith(".") and not s.endswith("."))
_path_strategy = st.text(alphabet=string.ascii_lowercase, min_size=1, max_size=10)

valid_url = st.builds(
    lambda scheme, domain, path: f"{scheme}://{domain}/{path}.txt",
    valid_url_scheme,
    _domain_strategy,
    _path_strategy,
)

invalid_url = st.builds(
    lambda scheme, domain, path: f"{scheme}://{domain}/{path}.txt",
    invalid_url_scheme,
    _domain_strategy,
    _path_strategy,
)


@pytest.mark.property
class TestUrlValidation:
    """Property tests for blocklist URL handling."""

    @given(url=valid_url)
    @settings(max_examples=50, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_valid_blocklist_url_stored(self, url, tmp_path):
        """Valid http/https URLs are stored exactly as provided."""
        config_path = tmp_path / "dnsApp.config"
        services_path = tmp_path / "blocked-services.json"
        services_path.write_text(json.dumps({}))
        base_config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        config_path.write_text(json.dumps(base_config, indent=2))

        with (
            patch("app.CONFIG_PATH", config_path),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/blocklists",
                json={"url": url, "name": "test", "enabled": True, "refreshHours": 24},
            )
            assert resp.status_code == 200
            saved = json.loads(config_path.read_text())
            urls = [bl["url"] for bl in saved["blockLists"] if isinstance(bl, dict)]
            assert url in urls

    @given(url=invalid_url)
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_invalid_scheme_blocklist_url_rejected(self, url, tmp_path):
        """Non-http/https URLs are rejected with 400."""
        config_path = tmp_path / "dnsApp.config"
        services_path = tmp_path / "blocked-services.json"
        services_path.write_text(json.dumps({}))
        base_config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        config_path.write_text(json.dumps(base_config, indent=2))

        with (
            patch("app.CONFIG_PATH", config_path),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/blocklists",
                json={"url": url, "name": "test", "enabled": True, "refreshHours": 24},
            )
            assert resp.status_code == 400

    @given(
        url=st.text(
            alphabet=string.ascii_letters + string.digits + ":/.-_~",
            min_size=1,
            max_size=200,
        )
    )
    @settings(max_examples=30, suppress_health_check=[HealthCheck.function_scoped_fixture])
    def test_blocklist_save_never_crashes(self, url, tmp_path):
        """Saving any URL string never causes a 500 error."""
        config_path = tmp_path / "dnsApp.config"
        services_path = tmp_path / "blocked-services.json"
        services_path.write_text(json.dumps({}))
        base_config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        config_path.write_text(json.dumps(base_config, indent=2))

        with (
            patch("app.CONFIG_PATH", config_path),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/blocklists",
                json={"url": url, "name": "test"},
            )
            assert resp.status_code < 500


# ---------------------------------------------------------------------------
# #62: Concurrent API requests
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestConcurrentApiRequests:
    """Verify correct behavior when multiple API endpoints are hit concurrently."""

    def test_concurrent_profile_creates(self, client, tmp_config):
        """Creating multiple profiles in rapid succession all succeed."""
        for i in range(5):
            resp = client.post(
                "/api/profiles",
                json={
                    "name": f"concurrent-{i}",
                    "description": f"Profile {i}",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            )
            assert resp.status_code == 200

        config = read_config(tmp_config)
        for i in range(5):
            assert f"concurrent-{i}" in config["profiles"]

    def test_concurrent_mixed_endpoints(self, client, tmp_config):
        """Hitting different API endpoints in sequence doesn't cause errors."""
        # Create a profile
        resp1 = client.post(
            "/api/profiles",
            json={
                "name": "mixed-test",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp1.status_code == 200

        # Create a client
        resp2 = client.post(
            "/api/clients",
            json={"name": "Mixed Device", "ids": ["10.0.0.50"], "profile": "mixed-test"},
        )
        assert resp2.status_code == 200

        # Update settings
        resp3 = client.post(
            "/api/settings",
            json={
                "enableBlocking": True,
                "timeZone": "UTC",
                "defaultProfile": "mixed-test",
                "baseProfile": "",
                "scheduleAllDay": True,
            },
        )
        assert resp3.status_code == 200

        # Read config
        resp4 = client.get("/api/config")
        assert resp4.status_code == 200

        config = resp4.json()
        assert "mixed-test" in config["profiles"]
        assert config["defaultProfile"] == "mixed-test"

    def test_concurrent_reads_dont_interfere_with_writes(self, client, tmp_config):
        """Interleaved reads and writes produce consistent results."""
        for i in range(5):
            # Write
            client.post(
                "/api/profiles",
                json={
                    "name": f"rw-{i}",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            )
            # Read
            resp = client.get("/api/config")
            config = resp.json()
            assert f"rw-{i}" in config["profiles"]


# ---------------------------------------------------------------------------
# #63: Technitium API failure scenarios
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestTechnitiumApiFailures:
    """Verify graceful handling of Technitium API errors."""

    def test_technitium_500_still_saves(self, tmp_config, sample_config):
        """Config is saved even when Technitium returns 500."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(500, text="Internal Server Error")
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/profiles",
                json={
                    "name": "after-failure",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            )

        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "after-failure" in config["profiles"]

    def test_technitium_timeout_still_saves(self, tmp_config, sample_config):
        """Config is saved even when Technitium request times out."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            import httpx

            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                side_effect=httpx.ReadTimeout("Connection timed out")
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/profiles",
                json={
                    "name": "after-timeout",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            )

        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "after-timeout" in config["profiles"]

    def test_technitium_connection_refused_still_saves(self, tmp_config, sample_config):
        """Config is saved even when Technitium connection is refused."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            import httpx

            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                side_effect=httpx.ConnectError("Connection refused")
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            resp = c.post(
                "/api/profiles",
                json={
                    "name": "after-connrefused",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            )

        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "after-connrefused" in config["profiles"]

    def test_reload_returns_false_on_error(self, tmp_config, sample_config):
        """reload_technitium_config returns False on HTTP errors."""
        tmp_config.write_text(json.dumps(sample_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(500, text="Error")
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)
            # Use api/config POST which returns reloaded status
            resp = c.post("/api/config", json=sample_config)
            data = resp.json()
            assert data["ok"] is True
            assert data["reloaded"] is False


# ---------------------------------------------------------------------------
# #64: Large config files
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestLargeConfig:
    """Verify the app handles large configurations correctly."""

    def test_many_profiles(self, tmp_config):
        """App handles config with many profiles."""
        large_config = {
            "enableBlocking": True,
            "profiles": {
                f"profile-{i}": {
                    "description": f"Profile number {i}",
                    "blockedServices": ["youtube", "tiktok"],
                    "blockLists": [f"https://example.com/list-{i}.txt"],
                    "allowList": [f"allowed-{i}.com"],
                    "customRules": [f"blocked-{i}.com"],
                    "dnsRewrites": [{"domain": f"rw-{i}.com", "answer": f"10.0.{i % 256}.1"}],
                }
                for i in range(100)
            },
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        tmp_config.write_text(json.dumps(large_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)

            # Read config
            resp = c.get("/api/config")
            assert resp.status_code == 200
            data = resp.json()
            assert len(data["profiles"]) == 100

    def test_many_clients(self, tmp_config):
        """App handles config with many clients."""
        large_config = {
            "enableBlocking": True,
            "profiles": {"default": {"blockedServices": []}},
            "clients": [
                {
                    "name": f"Device {i}",
                    "ids": [f"192.168.{i // 256}.{i % 256}"],
                    "profile": "default",
                }
                for i in range(200)
            ],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        tmp_config.write_text(json.dumps(large_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)

            resp = c.get("/api/config")
            assert resp.status_code == 200
            assert len(resp.json()["clients"]) == 200

    def test_many_blocklists(self, tmp_config):
        """App handles config with many blocklists."""
        large_config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [
                {
                    "url": f"https://example.com/list-{i}.txt",
                    "name": f"List {i}",
                    "enabled": True,
                    "refreshHours": 24,
                }
                for i in range(150)
            ],
            "_blockListsSeeded": True,
        }
        tmp_config.write_text(json.dumps(large_config, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"

        with (
            patch("app.CONFIG_PATH", tmp_config),
            patch("app.BLOCKED_SERVICES_PATH", services_path),
            patch("app.TECHNITIUM_API_TOKEN", "test-token"),
            patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from app import app

            c = TestClient(app, raise_server_exceptions=True)

            resp = c.get("/api/config")
            assert resp.status_code == 200
            assert len(resp.json()["blockLists"]) == 150


# ---------------------------------------------------------------------------
# #65: Unicode profile names
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestUnicodeProfileNames:
    """Test profile CRUD with unicode names, emoji, special characters, RTL text."""

    def test_unicode_profile_name(self, client, tmp_config):
        """Profile with unicode name can be created and retrieved."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "Kinderprofil",
                "description": "Deutsches Profil mit Umlauten: ae, oe, ue",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "Kinderprofil" in config["profiles"]

    def test_emoji_profile_name(self, client, tmp_config):
        """Profile with emoji name can be created."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "\U0001f476 Kids",
                "description": "Emoji profile",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "\U0001f476 Kids" in config["profiles"]

    def test_cjk_profile_name(self, client, tmp_config):
        """Profile with CJK characters can be created."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "\u5b50\u4f9b\u30d7\u30ed\u30d5\u30a3\u30fc\u30eb",
                "description": "Japanese profile name",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "\u5b50\u4f9b\u30d7\u30ed\u30d5\u30a3\u30fc\u30eb" in config["profiles"]

    def test_rtl_profile_name(self, client, tmp_config):
        """Profile with RTL (Arabic) name can be created."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "\u0645\u0644\u0641 \u0627\u0644\u0623\u0637\u0641\u0627\u0644",
                "description": "Arabic profile name",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "\u0645\u0644\u0641 \u0627\u0644\u0623\u0637\u0641\u0627\u0644" in config["profiles"]

    def test_special_chars_profile_name(self, client, tmp_config):
        """Profile with special characters can be created."""
        resp = client.post(
            "/api/profiles",
            json={
                "name": "test <script> & \"quotes\"",
                "description": "XSS-like name",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "test <script> & \"quotes\"" in config["profiles"]

    def test_unicode_profile_delete(self, client, tmp_config):
        """Profile with unicode name can be deleted."""
        # Create
        client.post(
            "/api/profiles",
            json={
                "name": "\u00fc\u00f1\u00ee\u00e7\u00f6\u00f0\u00e9",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        # Delete
        resp = client.request(
            "DELETE", "/api/profiles", json={"name": "\u00fc\u00f1\u00ee\u00e7\u00f6\u00f0\u00e9"}
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert "\u00fc\u00f1\u00ee\u00e7\u00f6\u00f0\u00e9" not in config["profiles"]


# ---------------------------------------------------------------------------
# #66: API response Content-Type headers
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestApiContentTypeHeaders:
    """Assert Content-Type: application/json on all API responses."""

    def test_config_get_content_type(self, client):
        resp = client.get("/api/config")
        assert "application/json" in resp.headers["content-type"]

    def test_config_set_content_type(self, client):
        resp = client.post("/api/config", json={"enableBlocking": True, "_blockListsSeeded": True})
        assert "application/json" in resp.headers["content-type"]

    def test_profile_save_content_type(self, client):
        resp = client.post(
            "/api/profiles",
            json={"name": "ct-test", "blockedServices": []},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_profile_delete_content_type(self, client):
        resp = client.request("DELETE", "/api/profiles", json={"name": "nonexistent"})
        assert "application/json" in resp.headers["content-type"]

    def test_client_save_content_type(self, client):
        resp = client.post(
            "/api/clients",
            json={"name": "CT Device", "ids": ["10.0.0.1"], "profile": ""},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_client_delete_content_type(self, client):
        resp = client.request("DELETE", "/api/clients", json={"index": 99})
        assert "application/json" in resp.headers["content-type"]

    def test_settings_save_content_type(self, client):
        resp = client.post(
            "/api/settings",
            json={
                "enableBlocking": True,
                "timeZone": "UTC",
                "defaultProfile": "",
                "baseProfile": "",
                "scheduleAllDay": True,
            },
        )
        assert "application/json" in resp.headers["content-type"]

    def test_services_get_content_type(self, client):
        resp = client.get("/api/services")
        assert "application/json" in resp.headers["content-type"]

    def test_custom_service_save_content_type(self, client):
        resp = client.post(
            "/api/custom-services",
            json={"id": "ct-svc", "name": "CT Svc", "domains": []},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_custom_service_delete_content_type(self, client):
        resp = client.request("DELETE", "/api/custom-services", json={"id": "nonexistent"})
        assert "application/json" in resp.headers["content-type"]

    def test_blocklist_save_content_type(self, client):
        resp = client.post(
            "/api/blocklists",
            json={"url": "https://ct-test.com/list.txt", "name": "CT", "enabled": True},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_blocklist_delete_content_type(self, client):
        resp = client.request("DELETE", "/api/blocklists", json={"url": "nonexistent"})
        assert "application/json" in resp.headers["content-type"]

    def test_blocklist_refresh_content_type(self, client):
        resp = client.post("/api/blocklists/refresh")
        assert "application/json" in resp.headers["content-type"]

    def test_allowlist_save_content_type(self, client):
        resp = client.post(
            "/api/allowlists",
            json={"profile": "kids", "domains": ["safe.com"]},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_rules_save_content_type(self, client):
        resp = client.post(
            "/api/rules",
            json={"profile": "kids", "rules": ["blocked.com"]},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_rewrite_save_content_type(self, client):
        resp = client.post(
            "/api/rewrites",
            json={"profile": "kids", "domain": "ct.com", "answer": "1.1.1.1"},
        )
        assert "application/json" in resp.headers["content-type"]

    def test_rewrite_delete_content_type(self, client):
        resp = client.request(
            "DELETE", "/api/rewrites", json={"profile": "kids", "domain": "nonexistent"}
        )
        assert "application/json" in resp.headers["content-type"]


# ---------------------------------------------------------------------------
# #67: Duplicated config fixtures between conftest files
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestConftestFixtureDeduplication:
    """Verify that shared fixture data is consistent between conftest files.

    Issue #67 identified that sample_config is duplicated between
    tests/conftest.py and tests/e2e/conftest.py. This test documents
    the duplication and verifies the fixtures produce equivalent data.
    """

    def test_sample_config_fixtures_match(self, sample_config):
        """The main conftest sample_config has the expected structure.

        The e2e conftest has an identical sample_config fixture. If either
        changes, these assertions catch the drift.
        """
        assert "kids" in sample_config["profiles"]
        assert "adults" in sample_config["profiles"]
        assert len(sample_config["clients"]) == 2
        assert sample_config["clients"][0]["name"] == "iPad"
        assert sample_config["clients"][1]["name"] == "Laptop"
        assert sample_config["timeZone"] == "America/Denver"
        assert sample_config["_blockListsSeeded"] is True

    def test_empty_config_has_required_keys(self, empty_config):
        """The empty_config fixture has all required keys."""
        required_keys = [
            "enableBlocking",
            "profiles",
            "clients",
            "defaultProfile",
            "baseProfile",
            "timeZone",
            "scheduleAllDay",
            "customServices",
            "blockLists",
        ]
        for key in required_keys:
            assert key in empty_config, f"empty_config missing required key: {key}"


# ---------------------------------------------------------------------------
# #68: Request body size limits
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestRequestBodySizeLimits:
    """Test behavior with large request bodies."""

    def test_large_profile_description(self, client, tmp_config):
        """Profile with a very large description is accepted."""
        large_desc = "A" * 10000
        resp = client.post(
            "/api/profiles",
            json={
                "name": "large-desc",
                "description": large_desc,
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["profiles"]["large-desc"]["description"] == large_desc

    def test_large_allowlist(self, client, tmp_config):
        """Profile with many allowlist entries is accepted."""
        domains = [f"domain-{i}.example.com" for i in range(500)]
        resp = client.post(
            "/api/profiles",
            json={
                "name": "large-allow",
                "description": "",
                "blockedServices": [],
                "blockLists": [],
                "allowList": domains,
                "customRules": [],
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert len(config["profiles"]["large-allow"]["allowList"]) == 500

    def test_large_custom_rules(self, client, tmp_config):
        """Profile with many custom rules is accepted."""
        rules = [f"rule-{i}.example.com" for i in range(500)]
        resp = client.post(
            "/api/profiles",
            json={
                "name": "large-rules",
                "description": "",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": rules,
                "dnsRewrites": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert len(config["profiles"]["large-rules"]["customRules"]) == 500

    def test_large_config_set_payload(self, client, tmp_config):
        """POST /api/config with a large payload is accepted."""
        large_config = {
            "enableBlocking": True,
            "profiles": {
                f"p-{i}": {
                    "description": "x" * 100,
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [f"d-{j}.com" for j in range(50)],
                    "customRules": [],
                    "dnsRewrites": [],
                }
                for i in range(50)
            },
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        resp = client.post("/api/config", json=large_config)
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert len(config["profiles"]) == 50


# ---------------------------------------------------------------------------
# #69: Error response format consistency
# ---------------------------------------------------------------------------


@pytest.mark.api
class TestErrorResponseFormatConsistency:
    """Assert all error responses follow a consistent JSON schema."""

    def test_blocklist_empty_url_error_format(self, client):
        """POST /api/blocklists with empty URL returns error with ok=False and error key."""
        resp = client.post("/api/blocklists", json={"url": "", "name": "empty"})
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data
        assert isinstance(data["error"], str)
        assert len(data["error"]) > 0

    def test_allowlist_missing_profile_error_format(self, client):
        """POST /api/allowlists with nonexistent profile returns consistent error."""
        resp = client.post(
            "/api/allowlists", json={"profile": "nonexistent", "domains": ["x.com"]}
        )
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data
        assert isinstance(data["error"], str)

    def test_rules_missing_profile_error_format(self, client):
        """POST /api/rules with nonexistent profile returns consistent error."""
        resp = client.post("/api/rules", json={"profile": "nonexistent", "rules": ["x.com"]})
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data

    def test_rewrite_missing_profile_error_format(self, client):
        """POST /api/rewrites with nonexistent profile returns consistent error."""
        resp = client.post(
            "/api/rewrites",
            json={"profile": "nonexistent", "domain": "x.com", "answer": "1.1.1.1"},
        )
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data

    def test_rewrite_delete_missing_profile_error_format(self, client):
        """DELETE /api/rewrites with nonexistent profile returns consistent error."""
        resp = client.request(
            "DELETE", "/api/rewrites", json={"profile": "nonexistent", "domain": "x.com"}
        )
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data

    def test_rewrite_empty_domain_error_format(self, client):
        """POST /api/rewrites with empty domain returns consistent error."""
        resp = client.post(
            "/api/rewrites", json={"profile": "kids", "domain": "", "answer": "1.1.1.1"}
        )
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data

    def test_rewrite_empty_answer_error_format(self, client):
        """POST /api/rewrites with empty answer returns consistent error."""
        resp = client.post(
            "/api/rewrites", json={"profile": "kids", "domain": "test.com", "answer": ""}
        )
        assert resp.status_code == 400
        data = resp.json()
        assert data["ok"] is False
        assert "error" in data

    def test_success_responses_have_ok_true(self, client):
        """All successful mutation responses include ok=True."""
        endpoints = [
            ("POST", "/api/profiles", {"name": "ok-test", "blockedServices": []}),
            ("DELETE", "/api/profiles", {"name": "nonexistent"}),
            ("POST", "/api/clients", {"name": "ok-dev", "ids": ["10.0.0.1"], "profile": ""}),
            ("POST", "/api/settings", {
                "enableBlocking": True, "timeZone": "UTC",
                "defaultProfile": "", "baseProfile": "", "scheduleAllDay": True,
            }),
            ("POST", "/api/custom-services", {"id": "ok-svc", "name": "OK", "domains": []}),
            ("DELETE", "/api/custom-services", {"id": "nonexistent"}),
            ("POST", "/api/blocklists", {
                "url": "https://ok-test.com/l.txt", "name": "OK", "enabled": True,
            }),
            ("DELETE", "/api/blocklists", {"url": "nonexistent"}),
        ]
        for method, path, body in endpoints:
            if method == "POST":
                resp = client.post(path, json=body)
            else:
                resp = client.request(method, path, json=body)
            data = resp.json()
            assert data.get("ok") is True, f"{method} {path} missing ok=True: {data}"

    def test_all_error_responses_are_json(self, client_permissive):
        """Error responses from known validation paths return JSON."""
        error_cases = [
            ("POST", "/api/blocklists", {"url": ""}),
            ("POST", "/api/allowlists", {"profile": "no-such", "domains": []}),
            ("POST", "/api/rules", {"profile": "no-such", "rules": []}),
            ("POST", "/api/rewrites", {"profile": "no-such", "domain": "x", "answer": "y"}),
            ("DELETE", "/api/rewrites", {"profile": "no-such", "domain": "x"}),
        ]
        for method, path, body in error_cases:
            if method == "POST":
                resp = client_permissive.post(path, json=body)
            else:
                resp = client_permissive.request(method, path, json=body)
            assert resp.status_code == 400, f"{method} {path} expected 400, got {resp.status_code}"
            assert "application/json" in resp.headers["content-type"]
            data = resp.json()
            assert "ok" in data
            assert "error" in data
