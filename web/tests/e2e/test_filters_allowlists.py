"""E2E tests for the DNS Allowlists filter page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestAllowlistProfilePicker:
    def test_profile_picker_loads(self, page, live_server):
        """Profile picker populates with profile names."""
        page.goto(f"{live_server}/filters/allowlists")
        page.locator("#profilePicker").wait_for()
        options = page.locator("#profilePicker option")
        texts = [options.nth(i).text_content() for i in range(options.count())]
        assert "kids" in texts
        assert "adults" in texts

    def test_hash_selects_profile(self, page, live_server):
        """URL hash pre-selects the correct profile."""
        page.goto(f"{live_server}/filters/allowlists#kids")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").input_value() == "kids"

    def test_empty_profiles(self, page, live_server_empty):
        """No profiles shows disabled picker with placeholder."""
        page.goto(f"{live_server_empty}/filters/allowlists")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").is_disabled()
        option_text = page.locator("#profilePicker option").text_content()
        assert "No profiles available" in option_text


class TestAllowlistContent:
    def test_loads_existing_domains(self, page, live_server):
        """Kids profile loads existing allowlist domains into textarea."""
        page.goto(f"{live_server}/filters/allowlists#kids")
        page.locator("#profilePicker").wait_for()
        text = page.locator("#allowListText").input_value()
        assert "khanacademy.org" in text
        assert "school.edu" in text

    def test_domain_count(self, page, live_server):
        """Domain count label shows correct count."""
        page.goto(f"{live_server}/filters/allowlists#kids")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#domainCount").text_content() == "2 domains"

    def test_switch_profile_loads_data(self, page, live_server):
        """Switching profile loads that profile's allowlist."""
        page.goto(f"{live_server}/filters/allowlists#kids")
        page.locator("#profilePicker").wait_for()
        assert "khanacademy.org" in page.locator("#allowListText").input_value()

        page.locator("#profilePicker").select_option("adults")
        # Adults has empty allowlist
        assert page.locator("#allowListText").input_value() == ""


class TestAllowlistSave:
    def test_save_allowlist(self, page, live_server, config_path):
        """Save modified allowlist persists to config."""
        page.goto(f"{live_server}/filters/allowlists#kids")
        page.locator("#profilePicker").wait_for()

        page.locator("#allowListText").fill("new-domain.com\nanother.org")
        page.get_by_role("button", name="Save").click()

        # Wait for API call to complete
        page.wait_for_function("document.getElementById('domainCount').textContent === '2 domains'")

        config = read_config(config_path)
        assert config["profiles"]["kids"]["allowList"] == [
            "new-domain.com",
            "another.org",
        ]
