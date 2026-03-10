"""E2E tests for the Custom Filtering Rules page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRulesContent:
    def test_loads_existing_rules(self, page, live_server):
        """Kids profile loads existing custom rules into textarea."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()
        text = page.locator("#rulesText").input_value()
        assert "blocked.com" in text
        assert "@@exception.com" in text

    def test_rule_count_excludes_comments(self, page, live_server):
        """Rule count excludes comment lines."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()
        # 2 rules (blocked.com, @@exception.com), no comments
        assert page.locator("#ruleCount").text_content() == "2 rules"

    def test_comment_not_counted(self, page, live_server):
        """Adding a comment line doesn't increase rule count."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#rulesText").fill("blocked.com\n# this is a comment")
        assert page.locator("#ruleCount").text_content() == "1 rule"


class TestRulesSave:
    def test_save_rules(self, page, live_server, config_path):
        """Save modified rules persists to config."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#rulesText").fill("new-block.com\n@@new-allow.com\n# comment")
        page.get_by_role("button", name="Save").click()

        # Wait for API call to complete -- count updates after save
        page.wait_for_function("document.getElementById('ruleCount').textContent === '2 rules'")

        config = read_config(config_path)
        assert config["profiles"]["kids"]["customRules"] == [
            "new-block.com",
            "@@new-allow.com",
            "# comment",
        ]
