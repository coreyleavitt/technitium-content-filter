"""E2E tests for the Custom Filtering Rules page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRulesProfilePicker:
    def test_profile_picker_loads(self, page, live_server):
        """Profile picker populates with profile names."""
        page.goto(f"{live_server}/filters/rules")
        page.locator("#profilePicker").wait_for()
        options = page.locator("#profilePicker option")
        texts = [options.nth(i).text_content() for i in range(options.count())]
        assert "kids" in texts
        assert "adults" in texts

    def test_hash_selects_profile(self, page, live_server):
        """URL hash pre-selects the correct profile."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").input_value() == "kids"

    def test_empty_profiles(self, page, live_server_empty):
        """No profiles shows disabled picker with placeholder."""
        page.goto(f"{live_server_empty}/filters/rules")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").is_disabled()
        option_text = page.locator("#profilePicker option").text_content()
        assert "No profiles available" in option_text

    def test_switch_profile_loads_rules(self, page, live_server):
        """Switching profile loads that profile's rules."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()
        lines = page.locator("#rulesText").input_value().splitlines()
        assert "blocked.com" in lines

        page.locator("#profilePicker").select_option("adults")
        # Adults has empty rules
        assert page.locator("#rulesText").input_value() == ""


class TestRulesContent:
    def test_loads_existing_rules(self, page, live_server):
        """Kids profile loads existing custom rules into textarea."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()
        lines = set(page.locator("#rulesText").input_value().splitlines())
        assert lines == {"blocked.com", "@@exception.com"}

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

        # Wait for API call to complete before reading config
        with page.expect_response("**/api/rules"):
            page.get_by_role("button", name="Save").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["customRules"] == [
            "new-block.com",
            "@@new-allow.com",
            "# comment",
        ]

    def test_save_empty_rules(self, page, live_server, config_path):
        """Saving empty textarea persists an empty array."""
        page.goto(f"{live_server}/filters/rules#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#rulesText").fill("")

        with page.expect_response("**/api/rules"):
            page.get_by_role("button", name="Save").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["customRules"] == []
