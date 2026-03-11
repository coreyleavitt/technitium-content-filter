"""E2E tests for the DNS Rewrites filter page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestRewritesProfilePicker:
    def test_profile_picker_loads(self, page, live_server):
        """Profile picker populates with profile names."""
        page.goto(f"{live_server}/filters/rewrites")
        page.locator("#profilePicker").wait_for()
        options = page.locator("#profilePicker option")
        texts = [options.nth(i).text_content() for i in range(options.count())]
        assert "kids" in texts
        assert "adults" in texts

    def test_default_profile_selected(self, page, live_server):
        """First profile is auto-selected on load."""
        page.goto(f"{live_server}/filters/rewrites")
        page.locator("#profilePicker").wait_for()
        selected = page.locator("#profilePicker").input_value()
        assert selected in ("kids", "adults")

    def test_hash_selects_profile(self, page, live_server):
        """URL hash pre-selects the correct profile."""
        page.goto(f"{live_server}/filters/rewrites#adults")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").input_value() == "adults"

    def test_empty_profiles(self, page, live_server_empty):
        """No profiles shows disabled picker with placeholder text."""
        page.goto(f"{live_server_empty}/filters/rewrites")
        page.locator("#profilePicker").wait_for()
        assert page.locator("#profilePicker").is_disabled()
        # "No profiles available" is an <option> inside the select
        option_text = page.locator("#profilePicker option").text_content()
        assert "No profiles available" in option_text


class TestRewritesList:
    def test_existing_rewrites_rendered(self, page, live_server):
        """Kids profile shows its existing rewrite in the table."""
        page.goto(f"{live_server}/filters/rewrites#kids")
        page.locator("#rewritesBody").wait_for()
        assert page.get_by_text("search.com").is_visible()
        assert page.get_by_text("safesearch.google.com").is_visible()

    def test_empty_rewrites_message(self, page, live_server):
        """Adults profile with no rewrites shows empty message."""
        page.goto(f"{live_server}/filters/rewrites#adults")
        page.locator("#rewritesBody").wait_for()
        assert page.get_by_text("No rewrites configured").is_visible()

    def test_switch_profile_updates_table(self, page, live_server):
        """Switching profile via picker updates the rewrite table."""
        page.goto(f"{live_server}/filters/rewrites#kids")
        page.locator("#rewritesBody").wait_for()
        assert page.get_by_text("search.com").is_visible()

        page.locator("#profilePicker").select_option("adults")
        assert page.get_by_text("No rewrites configured").is_visible()


class TestRewriteAdd:
    def test_add_rewrite_inline(self, page, live_server, config_path):
        """Add a rewrite via inline form -- table updates without reload."""
        page.goto(f"{live_server}/filters/rewrites#kids")
        page.locator("#rewritesBody").wait_for()

        page.locator("#newDomain").fill("example.com")
        page.locator("#newAnswer").fill("1.2.3.4")
        page.locator("#addRewriteForm button[type='submit']").click()

        # Table should update without page reload (SPA-style)
        page.locator("#rewritesBody >> text=example.com").wait_for()
        assert page.get_by_text("1.2.3.4").is_visible()

        # Form should be cleared
        assert page.locator("#newDomain").input_value() == ""
        assert page.locator("#newAnswer").input_value() == ""

        # Verify persisted to disk
        config = read_config(config_path)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        added = next(r for r in rewrites if r["domain"] == "example.com")
        assert added["answer"] == "1.2.3.4"

    def test_upsert_rewrite(self, page, live_server, config_path):
        """Adding same domain again updates the answer (upsert)."""
        page.goto(f"{live_server}/filters/rewrites#kids")
        page.locator("#rewritesBody").wait_for()

        # Add first
        page.locator("#newDomain").fill("test.local")
        page.locator("#newAnswer").fill("10.0.0.1")
        page.locator("#addRewriteForm button[type='submit']").click()
        page.locator("#rewritesBody >> text=test.local").wait_for()

        # Upsert with different answer
        page.locator("#newDomain").fill("test.local")
        page.locator("#newAnswer").fill("10.0.0.2")
        page.locator("#addRewriteForm button[type='submit']").click()

        # Should show updated answer, not duplicated rows
        page.locator("#rewritesBody >> text=10.0.0.2").wait_for()
        assert page.locator("#rewritesBody >> text=test.local").count() == 1


class TestRewriteDelete:
    def test_delete_rewrite_inline(self, page, live_server, config_path):
        """Delete a rewrite -- table updates without reload."""
        page.goto(f"{live_server}/filters/rewrites#kids")
        page.locator("#rewritesBody").wait_for()
        body = page.locator("#rewritesBody")
        assert body.get_by_text("search.com", exact=True).is_visible()

        page.on("dialog", lambda dialog: dialog.accept())
        body.locator("button:has-text('Delete')").first.click()

        # Row should disappear -- table shows empty message
        page.get_by_text("No rewrites configured").wait_for()

        config = read_config(config_path)
        rewrites = config["profiles"]["kids"]["dnsRewrites"]
        assert not any(r["domain"] == "search.com" for r in rewrites)
