"""Unit tests for config loading, saving, and migrations."""

import json
from unittest.mock import patch

import pytest


@pytest.mark.unit
class TestLoadConfig:
    def test_no_file_returns_defaults(self, tmp_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import load_config

            config = load_config()

        assert config["enableBlocking"] is True
        assert config["profiles"] == {}
        assert config["clients"] == []
        assert config["blockLists"] == []
        assert config["timeZone"] == "UTC"
        assert config["scheduleAllDay"] is True

    def test_loads_from_file(self, tmp_config, sample_config):
        tmp_config.write_text(json.dumps(sample_config))
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import load_config

            config = load_config()

        assert config["enableBlocking"] is True
        assert "kids" in config["profiles"]
        assert len(config["clients"]) == 2

    def test_missing_fields_use_defaults(self, tmp_config):
        tmp_config.write_text(json.dumps({"enableBlocking": False, "_blockListsSeeded": True}))
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import load_config

            config = load_config()

        assert config["enableBlocking"] is False

    def test_corrupt_json_raises(self, tmp_config):
        tmp_config.write_text("not valid json{{{")
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import load_config

            with pytest.raises(json.JSONDecodeError):
                load_config()


@pytest.mark.unit
class TestSaveConfig:
    def test_writes_file(self, tmp_config, sample_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import save_config

            save_config(sample_config)

        saved = json.loads(tmp_config.read_text())
        assert saved == sample_config

    def test_atomic_write_no_partial(self, tmp_config):
        """Temp file should not remain after successful save."""
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import save_config

            save_config({"test": True})

        tmp_file = tmp_config.with_suffix(".tmp")
        assert not tmp_file.exists()
        assert tmp_config.exists()

    def test_overwrites_existing(self, tmp_config, sample_config):
        tmp_config.write_text(json.dumps(sample_config))
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import save_config

            save_config({"enableBlocking": False})

        saved = json.loads(tmp_config.read_text())
        assert saved == {"enableBlocking": False}

    def test_indented_output(self, tmp_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import save_config

            save_config({"key": "value", "nested": {"a": 1}})

        text = tmp_config.read_text()
        assert "\n" in text
        assert "  " in text


@pytest.mark.unit
class TestMigrateBlocklists:
    def test_objects_to_global_urls(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {
                "kids": {
                    "blockLists": [
                        {
                            "url": "https://list1.txt",
                            "name": "List 1",
                            "enabled": True,
                            "refreshHours": 24,
                        },
                    ]
                }
            },
            "blockLists": [],
        }
        changed = _migrate_blocklists(config)

        assert changed is True
        assert len(config["blockLists"]) == 1
        assert config["blockLists"][0]["url"] == "https://list1.txt"
        assert config["profiles"]["kids"]["blockLists"] == ["https://list1.txt"]

    def test_deduplicates_across_profiles(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {
                "kids": {"blockLists": [{"url": "https://same.txt"}]},
                "teens": {"blockLists": [{"url": "https://same.txt"}]},
            },
            "blockLists": [],
        }
        _migrate_blocklists(config)

        assert len(config["blockLists"]) == 1

    def test_string_urls_not_migrated(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {"kids": {"blockLists": ["https://already-migrated.txt"]}},
            "blockLists": [],
        }
        changed = _migrate_blocklists(config)

        assert changed is False
        assert config["profiles"]["kids"]["blockLists"] == ["https://already-migrated.txt"]

    def test_idempotent(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {"kids": {"blockLists": [{"url": "https://list.txt"}]}},
            "blockLists": [],
        }
        _migrate_blocklists(config)
        first = json.dumps(config, sort_keys=True)

        _migrate_blocklists(config)
        second = json.dumps(config, sort_keys=True)

        assert first == second

    def test_empty_url_skipped(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {
            "profiles": {"kids": {"blockLists": [{"url": ""}, {"url": "https://good.txt"}]}},
            "blockLists": [],
        }
        _migrate_blocklists(config)

        assert config["profiles"]["kids"]["blockLists"] == ["https://good.txt"]

    def test_no_profiles_no_error(self):
        from technitium_content_filter.config import _migrate_blocklists

        config = {"profiles": {}, "blockLists": []}
        changed = _migrate_blocklists(config)

        assert changed is False


@pytest.mark.unit
class TestSeedDefaultBlocklists:
    def test_seeds_on_first_run(self, tmp_config):
        defaults_path = tmp_config.parent / "default-blocklists.json"
        defaults_path.write_text(
            json.dumps(
                [
                    {
                        "url": "https://default1.txt",
                        "name": "Default 1",
                        "enabled": False,
                        "refreshHours": 24,
                    },
                ]
            )
        )
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import _seed_default_blocklists

            config = {"blockLists": []}
            changed = _seed_default_blocklists(config)

        assert changed is True
        assert len(config["blockLists"]) == 1
        assert config["_blockListsSeeded"] is True

    def test_skips_when_already_seeded(self, tmp_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import _seed_default_blocklists

            config = {"blockLists": [], "_blockListsSeeded": True}
            changed = _seed_default_blocklists(config)

        assert changed is False

    def test_deduplicates_existing_urls(self, tmp_config):
        defaults_path = tmp_config.parent / "default-blocklists.json"
        defaults_path.write_text(
            json.dumps(
                [
                    {"url": "https://existing.txt", "name": "New Name"},
                    {"url": "https://new.txt", "name": "New"},
                ]
            )
        )
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import _seed_default_blocklists

            config = {
                "blockLists": [{"url": "https://existing.txt", "name": "Old Name"}],
            }
            _seed_default_blocklists(config)

        assert len(config["blockLists"]) == 2
        # Existing entry not replaced
        assert config["blockLists"][0]["name"] == "Old Name"

    def test_no_defaults_file_returns_false(self, tmp_config):
        with patch("technitium_content_filter.config.CONFIG_PATH", tmp_config):
            from technitium_content_filter.config import _seed_default_blocklists

            config = {"blockLists": []}
            changed = _seed_default_blocklists(config)

        assert changed is False
