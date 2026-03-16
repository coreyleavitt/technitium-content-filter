"""Tests for reload_technitium_config and token reading."""

import json
import os
from unittest.mock import patch

import pytest
import respx
from httpx import Response


@pytest.mark.unit
class TestReloadTechnitiumConfig:
    @pytest.mark.asyncio
    async def test_reload_success(self):
        """Successful reload returns True."""
        with (
            patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", "test-token"),
            patch("technitium_content_filter.config.TECHNITIUM_URL", "http://mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            from technitium_content_filter.config import reload_technitium_config

            result = await reload_technitium_config({"enableBlocking": True})

        assert result is True

    @pytest.mark.asyncio
    async def test_reload_server_error(self):
        """Technitium returning 500 causes reload to return False."""
        with (
            patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", "test-token"),
            patch("technitium_content_filter.config.TECHNITIUM_URL", "http://mock:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://mock:5380/api/apps/config/set").mock(
                return_value=Response(500, text="Internal Server Error")
            )
            from technitium_content_filter.config import reload_technitium_config

            result = await reload_technitium_config({"enableBlocking": True})

        assert result is False

    @pytest.mark.asyncio
    async def test_reload_no_token(self):
        """No API token means reload is skipped."""
        with patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", ""):
            from technitium_content_filter.config import reload_technitium_config

            result = await reload_technitium_config({"enableBlocking": True})

        assert result is False

    @pytest.mark.asyncio
    async def test_reload_network_error(self):
        """Network error (httpx.HTTPError) returns False."""
        import httpx as httpx_mod

        with (
            patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", "test-token"),
            patch("technitium_content_filter.config.TECHNITIUM_URL", "http://unreachable:5380"),
            respx.mock(assert_all_called=False) as mock,
        ):
            mock.post("http://unreachable:5380/api/apps/config/set").mock(
                side_effect=httpx_mod.ConnectError("Connection refused")
            )
            from technitium_content_filter.config import reload_technitium_config

            result = await reload_technitium_config({"enableBlocking": True})

        assert result is False


@pytest.mark.unit
class TestReadApiToken:
    def test_reads_from_file(self, tmp_path):
        token_file = tmp_path / "token.txt"
        token_file.write_text("  my-secret-token  \n")
        with patch.dict(os.environ, {"TECHNITIUM_API_TOKEN_FILE": str(token_file)}):
            from technitium_content_filter.config import _read_api_token

            assert _read_api_token() == "my-secret-token"

    def test_falls_back_to_env_var(self):
        with patch.dict(os.environ, {"TECHNITIUM_API_TOKEN": "env-token"}, clear=False):
            # Ensure no token file set
            env = os.environ.copy()
            env.pop("TECHNITIUM_API_TOKEN_FILE", None)
            with patch.dict(os.environ, env, clear=True):
                from technitium_content_filter.config import _read_api_token

                assert _read_api_token() == "env-token"

    def test_missing_file_falls_back(self, tmp_path):
        with patch.dict(
            os.environ,
            {
                "TECHNITIUM_API_TOKEN_FILE": str(tmp_path / "nonexistent.txt"),
                "TECHNITIUM_API_TOKEN": "fallback",
            },
        ):
            from technitium_content_filter.config import _read_api_token

            assert _read_api_token() == "fallback"

    def test_no_config_returns_empty(self):
        env = os.environ.copy()
        env.pop("TECHNITIUM_API_TOKEN_FILE", None)
        env.pop("TECHNITIUM_API_TOKEN", None)
        with patch.dict(os.environ, env, clear=True):
            from technitium_content_filter.config import _read_api_token

            assert _read_api_token() == ""


@pytest.mark.unit
class TestLoadBlockedServices:
    def test_missing_file_returns_empty(self, tmp_path):
        svc_patch = "technitium_content_filter.config.BLOCKED_SERVICES_PATH"
        with patch(svc_patch, tmp_path / "nonexistent.json"):
            from technitium_content_filter.config import load_blocked_services

            assert load_blocked_services() == {}

    def test_loads_from_file(self, tmp_path):
        services_file = tmp_path / "services.json"
        services_file.write_text(json.dumps({"youtube": {"name": "YouTube", "domains": []}}))
        with patch("technitium_content_filter.config.BLOCKED_SERVICES_PATH", services_file):
            from technitium_content_filter.config import load_blocked_services

            result = load_blocked_services()
            assert "youtube" in result


@pytest.mark.unit
class TestJsonNarrowing:
    def test_as_obj_raises_on_non_dict(self):
        from technitium_content_filter.config import _as_obj

        with pytest.raises(TypeError, match="Expected dict"):
            _as_obj("not a dict")

    def test_as_list_raises_on_non_list(self):
        from technitium_content_filter.config import _as_list

        with pytest.raises(TypeError, match="Expected list"):
            _as_list("not a list")

    def test_as_str_returns_empty_for_non_str(self):
        from technitium_content_filter.config import _as_str

        assert _as_str(42) == ""
        assert _as_str(None) == ""
        assert _as_str("hello") == "hello"


@pytest.mark.unit
class TestSeedBlocklistsEdgeCases:
    def test_seed_when_blocklists_not_a_list(self, tmp_path):
        """When blockLists is not a list, seed creates it as a list."""
        defaults_path = tmp_path / "default-blocklists.json"
        defaults_path.write_text(
            json.dumps(
                [
                    {
                        "url": "https://default.txt",
                        "name": "Default",
                        "enabled": False,
                        "refreshHours": 24,
                    },
                ]
            )
        )
        config_path = tmp_path / "dnsApp.config"

        with patch("technitium_content_filter.config.CONFIG_PATH", config_path):
            from technitium_content_filter.config import _seed_default_blocklists

            config: dict[str, object] = {"blockLists": "not-a-list"}
            changed = _seed_default_blocklists(config)

        assert changed is True
        assert isinstance(config["blockLists"], list)


@pytest.mark.unit
class TestLoadConfigMigrationSave:
    def test_migration_triggers_save(self, tmp_path):
        """When migration changes config, load_config writes it back to disk."""
        config_path = tmp_path / "dnsApp.config"
        old_config = {
            "enableBlocking": True,
            "profiles": {
                "kids": {
                    "blockLists": [
                        {
                            "url": "https://list.txt",
                            "name": "List",
                            "enabled": True,
                            "refreshHours": 24,
                        }
                    ]
                }
            },
            "clients": [],
            "_blockListsSeeded": True,
        }
        config_path.write_text(json.dumps(old_config))

        with patch("technitium_content_filter.config.CONFIG_PATH", config_path):
            from technitium_content_filter.config import load_config

            config = load_config()

        # After migration, profile blockLists should be strings
        assert config["profiles"]["kids"]["blockLists"] == ["https://list.txt"]
        # File should have been re-saved with migrated format
        on_disk = json.loads(config_path.read_text())
        assert on_disk["profiles"]["kids"]["blockLists"] == ["https://list.txt"]
        assert len(on_disk["blockLists"]) == 1
