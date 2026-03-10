"""API tests for global settings endpoint."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestSettingsSave:
    def test_update_all_settings(self, client, tmp_config):
        resp = client.post(
            "/api/settings",
            json={
                "enableBlocking": False,
                "timeZone": "America/New_York",
                "defaultProfile": "kids",
                "baseProfile": "adults",
                "scheduleAllDay": False,
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["enableBlocking"] is False
        assert config["timeZone"] == "America/New_York"
        assert config["defaultProfile"] == "kids"
        assert config["baseProfile"] == "adults"
        assert config["scheduleAllDay"] is False

    def test_empty_profile_becomes_none(self, client, tmp_config):
        client.post(
            "/api/settings",
            json={
                "enableBlocking": True,
                "timeZone": "UTC",
                "defaultProfile": "",
                "baseProfile": "",
                "scheduleAllDay": True,
            },
        )
        config = read_config(tmp_config)
        assert config["defaultProfile"] is None
        assert config["baseProfile"] is None

    def test_preserves_other_config_fields(self, client, tmp_config):
        """Settings save doesn't wipe profiles, clients, etc."""
        client.post(
            "/api/settings",
            json={
                "enableBlocking": False,
                "timeZone": "UTC",
                "defaultProfile": "",
                "baseProfile": "",
                "scheduleAllDay": True,
            },
        )
        config = read_config(tmp_config)
        assert "kids" in config["profiles"]
        assert len(config["clients"]) == 2
