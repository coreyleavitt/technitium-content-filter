"""E2E tests for the Regex Rules page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRegexPageLoad:
    def test_page_loads(self, page, live_server):
        """Regex rules page loads successfully."""
        page.goto(f"{live_server}/filters/regex")
        page.locator("#profilePicker").wait_for()
        assert page.locator("h1").text_content() == "Regex Rules"

    def test_profile_picker_loads(self, page, live_server):
        """Profile picker populates with profile names."""
        page.goto(f"{live_server}/filters/regex")
        page.locator("#profilePicker").wait_for()
        options = page.locator("#profilePicker option")
        texts = [options.nth(i).text_content() for i in range(options.count())]
        assert "kids" in texts
        assert "adults" in texts

    def test_empty_profiles(self, page, live_server_empty):
        """No profiles shows disabled picker."""
        page.goto(f"{live_server_empty}/filters/regex")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").is_disabled()

    def test_both_textareas_present(self, page, live_server):
        """Block and allow textareas are present."""
        page.goto(f"{live_server}/filters/regex")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#regexBlockText").is_visible()
        assert page.locator("#regexAllowText").is_visible()


class TestRegexSave:
    def test_save_persists(self, page, live_server, config_path):
        """Saving regex rules persists to config."""
        page.goto(f"{live_server}/filters/regex#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#regexBlockText").fill(r"^ads?\d*\." + "\ntracking\\.")
        page.locator("#regexAllowText").fill(r"^safe\.")

        with page.expect_response("**/api/regex-rules"):
            page.get_by_role("button", name="Save").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["regexBlockRules"] == [
            r"^ads?\d*\.",
            r"tracking\.",
        ]
        assert config["profiles"]["kids"]["regexAllowRules"] == [r"^safe\."]

    def test_invalid_shows_error(self, page, live_server):
        """Invalid regex pattern shows error toast."""
        page.goto(f"{live_server}/filters/regex#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#regexBlockText").fill("[invalid")

        with page.expect_response("**/api/regex-rules"):
            page.get_by_role("button", name="Save").click()

        # Error toast should appear
        page.locator("[role='alert']").wait_for(timeout=5000)
        assert "Invalid regex" in page.locator("[role='alert']").text_content()


class TestRegexPatternCount:
    def test_pattern_count_updates(self, page, live_server):
        """Pattern count updates as user types."""
        page.goto(f"{live_server}/filters/regex#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#regexBlockText").fill("pattern1\npattern2\n# comment")
        assert "2 patterns" in page.locator("#blockCount").text_content()

    def test_single_pattern_singular(self, page, live_server):
        """Single pattern shows singular form."""
        page.goto(f"{live_server}/filters/regex#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#regexAllowText").fill("one-pattern")
        assert "1 pattern" in page.locator("#allowCount").text_content()
