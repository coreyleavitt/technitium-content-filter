"""E2E tests for the custom filtering rules tab on the profile detail page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRulesContent:
    def test_loads_existing_rules(self, page, live_server):
        """Kids profile loads existing custom rules into textarea."""
        page.goto(f"{live_server}/profiles/kids#rules")
        page.locator("#tab-rules").wait_for(state="visible")
        lines = set(page.locator("#rulesText").input_value().splitlines())
        assert lines == {"blocked.com", "@@exception.com"}

    def test_rule_count_excludes_comments(self, page, live_server):
        """Rule count excludes comment lines."""
        page.goto(f"{live_server}/profiles/kids#rules")
        page.locator("#tab-rules").wait_for(state="visible")
        # 2 rules (blocked.com, @@exception.com), no comments
        assert page.locator("#ruleCount").text_content() == "2 rules"

    def test_comment_not_counted(self, page, live_server):
        """Adding a comment line doesn't increase rule count."""
        page.goto(f"{live_server}/profiles/kids#rules")
        page.locator("#tab-rules").wait_for(state="visible")

        page.locator("#rulesText").fill("blocked.com\n# this is a comment")
        assert page.locator("#ruleCount").text_content() == "1 rule"


class TestRulesSave:
    def test_save_rules(self, page, live_server, config_path):
        """Save modified rules persists to config."""
        page.goto(f"{live_server}/profiles/kids#rules")
        page.locator("#tab-rules").wait_for(state="visible")

        page.locator("#rulesText").fill("new-block.com\n@@new-allow.com\n# comment")

        # Wait for API call to complete before reading config
        with page.expect_response("**/api/profiles/kids/rules"):
            page.locator("#saveRulesBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["customRules"] == [
            "new-block.com",
            "@@new-allow.com",
            "# comment",
        ]

    def test_save_empty_rules(self, page, live_server, config_path):
        """Saving empty textarea persists an empty array."""
        page.goto(f"{live_server}/profiles/kids#rules")
        page.locator("#tab-rules").wait_for(state="visible")

        page.locator("#rulesText").fill("")

        with page.expect_response("**/api/profiles/kids/rules"):
            page.locator("#saveRulesBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["customRules"] == []
