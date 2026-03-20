"""API tests for regex rules endpoint."""

import pytest

from tests.conftest import read_config


@pytest.mark.api
class TestRegexRulesSave:
    def test_save_valid_regex_rules(self, client, tmp_config):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": [r"^ads?\d*\.", r"tracking\."],
                "regexAllowRules": [r"safe\.example\.com"],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["regexBlockRules"] == [
            r"^ads?\d*\.",
            r"tracking\.",
        ]
        assert config["profiles"]["kids"]["regexAllowRules"] == [r"safe\.example\.com"]

    def test_save_invalid_regex_returns_400(self, client):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": [r"[invalid"],
                "regexAllowRules": [],
            },
        )
        assert resp.status_code == 400
        assert "Invalid regex" in resp.json()["error"]

    def test_nonexistent_profile_returns_400(self, client):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "nonexistent",
                "regexBlockRules": [],
                "regexAllowRules": [],
            },
        )
        assert resp.status_code == 400
        assert "Profile not found" in resp.json()["error"]

    def test_clear_regex_rules(self, client, tmp_config):
        # First set some rules
        client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": [r"test\."],
                "regexAllowRules": [r"safe\."],
            },
        )
        # Then clear them
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": [],
                "regexAllowRules": [],
            },
        )
        assert resp.status_code == 200
        config = read_config(tmp_config)
        assert config["profiles"]["kids"]["regexBlockRules"] == []
        assert config["profiles"]["kids"]["regexAllowRules"] == []

    def test_comment_lines_pass_validation(self, client, tmp_config):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": ["# this is a comment", r"valid\."],
                "regexAllowRules": [],
            },
        )
        assert resp.status_code == 200

    def test_empty_lines_pass_validation(self, client, tmp_config):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": ["", "  ", r"valid\."],
                "regexAllowRules": [],
            },
        )
        assert resp.status_code == 200

    def test_invalid_allow_pattern_returns_400(self, client):
        resp = client.post(
            "/api/regex-rules",
            json={
                "profile": "kids",
                "regexBlockRules": [],
                "regexAllowRules": [r"(unclosed"],
            },
        )
        assert resp.status_code == 400
