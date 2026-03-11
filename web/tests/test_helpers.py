"""Tests for helper functions: session secret, validation, schedule timezone."""

import json
from unittest.mock import patch

import pytest


@pytest.mark.unit
class TestGetSessionSecret:
    def test_returns_env_secret_when_set(self):
        with (
            patch.dict("os.environ", {"SESSION_SECRET": "mysecret"}),
            patch("config.TECHNITIUM_API_TOKEN", "token"),
        ):
            from config import _get_session_secret

            assert _get_session_secret() == "mysecret"

    def test_derives_from_api_token(self):
        with (
            patch.dict("os.environ", {"SESSION_SECRET": ""}),
            patch("config.TECHNITIUM_API_TOKEN", "my-api-token"),
        ):
            from config import _get_session_secret

            result = _get_session_secret()
            assert len(result) == 64  # sha256 hex digest

    def test_generates_random_when_no_token(self):
        with (
            patch.dict("os.environ", {"SESSION_SECRET": ""}),
            patch("config.TECHNITIUM_API_TOKEN", ""),
        ):
            from config import _get_session_secret

            result = _get_session_secret()
            assert len(result) == 64  # secrets.token_hex(32)


@pytest.mark.unit
class TestValidateJsonObjList:
    def test_raises_on_non_list(self):
        from config import _validate_json_obj_list

        with pytest.raises(TypeError, match="Expected list"):
            _validate_json_obj_list("not a list")

    def test_filters_non_dict_items(self):
        from config import _validate_json_obj_list

        result = _validate_json_obj_list([{"a": 1}, "string", 42, {"b": 2}])
        assert result == [{"a": 1}, {"b": 2}]


@pytest.mark.unit
class TestCheckScheduleTimezone:
    def test_invalid_timezone_falls_back_to_utc(self, client, tmp_config):
        """Invalid timezone in config should fall back to UTC without error."""
        from tests.conftest import read_config

        config = read_config(tmp_config)
        config["timeZone"] = "Invalid/Timezone"
        config["scheduleAllDay"] = False
        config["defaultProfile"] = "kids"
        config["profiles"]["kids"]["schedule"] = {
            "mon": {"allDay": False, "start": "00:00", "end": "23:59"},
            "tue": {"allDay": False, "start": "00:00", "end": "23:59"},
            "wed": {"allDay": False, "start": "00:00", "end": "23:59"},
            "thu": {"allDay": False, "start": "00:00", "end": "23:59"},
            "fri": {"allDay": False, "start": "00:00", "end": "23:59"},
            "sat": {"allDay": False, "start": "00:00", "end": "23:59"},
            "sun": {"allDay": False, "start": "00:00", "end": "23:59"},
        }
        config["profiles"]["kids"]["customRules"] = ["blocked.com"]
        tmp_config.write_text(json.dumps(config, indent=2))

        resp = client.post("/api/test-domain", json={"domain": "blocked.com"})
        data = resp.json()
        # Should still work (falls back to UTC)
        assert data["ok"] is True
        assert data["verdict"] == "BLOCK"
