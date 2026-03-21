"""E2E tests for the regex rules tab on the profile detail page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRegexPageLoad:
    def test_both_textareas_present(self, page, live_server):
        """Block and allow textareas are present."""
        page.goto(f"{live_server}/profiles/kids#regex")
        page.locator("#tab-regex").wait_for(state="visible")
        assert page.locator("#regexBlockText").is_visible()
        assert page.locator("#regexAllowText").is_visible()


class TestRegexSave:
    def test_save_persists(self, page, live_server, config_path):
        """Saving regex rules persists to config."""
        page.goto(f"{live_server}/profiles/kids#regex")
        page.locator("#tab-regex").wait_for(state="visible")

        page.locator("#regexBlockText").fill(r"^ads?\d*\." + "\ntracking\\.")
        page.locator("#regexAllowText").fill(r"^safe\.")

        with page.expect_response("**/api/profiles/kids/regex"):
            page.locator("#saveRegexBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["regexBlockRules"] == [
            r"^ads?\d*\.",
            r"tracking\.",
        ]
        assert config["profiles"]["kids"]["regexAllowRules"] == [r"^safe\."]

    def test_invalid_shows_error(self, page, live_server):
        """Invalid regex pattern shows error toast."""
        page.goto(f"{live_server}/profiles/kids#regex")
        page.locator("#tab-regex").wait_for(state="visible")

        page.locator("#regexBlockText").fill("[invalid")

        with page.expect_response("**/api/profiles/kids/regex"):
            page.locator("#saveRegexBtn").click()

        # Error toast should appear
        page.locator("[role='alert']").wait_for(timeout=5000)
        assert "Invalid regex" in page.locator("[role='alert']").text_content()


class TestRegexPatternCount:
    def test_pattern_count_updates(self, page, live_server):
        """Pattern count updates as user types."""
        page.goto(f"{live_server}/profiles/kids#regex")
        page.locator("#tab-regex").wait_for(state="visible")

        page.locator("#regexBlockText").fill("pattern1\npattern2\n# comment")
        assert "2 patterns" in page.locator("#blockCount").text_content()

    def test_single_pattern_singular(self, page, live_server):
        """Single pattern shows singular form."""
        page.goto(f"{live_server}/profiles/kids#regex")
        page.locator("#tab-regex").wait_for(state="visible")

        page.locator("#regexAllowText").fill("one-pattern")
        assert "1 pattern" in page.locator("#allowCount").text_content()
