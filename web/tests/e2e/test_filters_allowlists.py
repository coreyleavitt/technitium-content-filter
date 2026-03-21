"""E2E tests for the allowlist tab on the profile detail page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestAllowlistContent:
    def test_loads_existing_domains(self, page, live_server):
        """Kids profile loads existing allowlist domains into textarea."""
        page.goto(f"{live_server}/profiles/kids#allowlist")
        page.locator("#tab-allowlist").wait_for(state="visible")
        lines = set(page.locator("#allowListText").input_value().splitlines())
        assert lines == {"khanacademy.org", "school.edu"}

    def test_domain_count(self, page, live_server):
        """Domain count label shows correct count."""
        page.goto(f"{live_server}/profiles/kids#allowlist")
        page.locator("#tab-allowlist").wait_for(state="visible")
        assert page.locator("#domainCount").text_content() == "2 domains"


class TestAllowlistSave:
    def test_save_allowlist(self, page, live_server, config_path):
        """Save modified allowlist persists to config."""
        page.goto(f"{live_server}/profiles/kids#allowlist")
        page.locator("#tab-allowlist").wait_for(state="visible")

        page.locator("#allowListText").fill("new-domain.com\nanother.org")
        page.locator("#saveAllowlistBtn").click()

        # Wait for API call to complete
        page.wait_for_function("document.getElementById('domainCount').textContent === '2 domains'")

        config = read_config(config_path)
        assert config["profiles"]["kids"]["allowList"] == [
            "new-domain.com",
            "another.org",
        ]

    def test_save_empty_allowlist(self, page, live_server, config_path):
        """Saving empty textarea persists an empty array."""
        page.goto(f"{live_server}/profiles/kids#allowlist")
        page.locator("#tab-allowlist").wait_for(state="visible")

        page.locator("#allowListText").fill("")
        page.locator("#saveAllowlistBtn").click()

        page.wait_for_function("document.getElementById('domainCount').textContent === '0 domains'")

        config = read_config(config_path)
        assert config["profiles"]["kids"]["allowList"] == []
