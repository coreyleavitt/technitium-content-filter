"""E2E tests for the DNS Blocklists filter page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestBlocklistsList:
    def test_blocklists_rendered(self, page, live_server):
        """Blocklist table renders existing entries."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()
        assert page.get_by_text("Ad List").is_visible()
        assert page.get_by_text("https://example.com/list.txt").is_visible()

    def test_enabled_status(self, page, live_server):
        """Enabled blocklist shows 'Enabled' status."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()
        table = page.locator("#blocklistsList")
        assert table.get_by_text("Enabled", exact=True).is_visible()

    def test_profile_badges(self, page, live_server):
        """Blocklist shows which profiles use it."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()
        # kids profile uses this blocklist
        table = page.locator("#blocklistsList")
        assert table.get_by_text("kids").is_visible()

    def test_empty_blocklists(self, page, live_server_empty):
        """Empty config shows 'no blocklists' message."""
        page.goto(f"{live_server_empty}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()
        assert page.get_by_text("No blocklists configured").is_visible()


class TestBlocklistCreate:
    def test_add_blocklist(self, page, live_server, config_path):
        """Add a new blocklist via modal."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.get_by_role("button", name="Add Blocklist").click()
        page.locator("#blocklistModal").wait_for(state="visible")
        assert page.locator("#blModalTitle").text_content() == "Add Blocklist"

        page.locator("#blName").fill("Test List")
        page.locator("#blUrl").fill("https://test.example.com/hosts.txt")
        page.locator("#blRefreshHours").fill("12")

        with page.expect_navigation():
            page.locator("#blocklistForm button[type='submit']").click()

        assert page.get_by_text("Test List").is_visible()

        config = read_config(config_path)
        added = next(
            bl for bl in config["blockLists"] if bl["url"] == "https://test.example.com/hosts.txt"
        )
        assert added["name"] == "Test List"
        assert added["refreshHours"] == 12


class TestBlocklistEdit:
    def test_edit_blocklist(self, page, live_server, config_path):
        """Edit existing blocklist -- URL field is readonly."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.locator("#blocklistsList button:has-text('Edit')").first.click()
        page.locator("#blocklistModal").wait_for(state="visible")

        assert page.locator("#blModalTitle").text_content() == "Edit Blocklist"
        # URL field should be readonly
        assert page.locator("#blUrl").get_attribute("readonly") is not None

        page.locator("#blName").fill("Updated Name")
        with page.expect_navigation():
            page.locator("#blocklistForm button[type='submit']").click()

        config = read_config(config_path)
        bl = next(bl for bl in config["blockLists"] if bl["url"] == "https://example.com/list.txt")
        assert bl["name"] == "Updated Name"

    def test_edit_prefills_values(self, page, live_server):
        """Edit modal pre-fills name, URL, enabled state."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.locator("#blocklistsList button:has-text('Edit')").first.click()
        page.locator("#blocklistModal").wait_for(state="visible")

        assert page.locator("#blName").input_value() == "Ad List"
        assert page.locator("#blUrl").input_value() == "https://example.com/list.txt"
        assert page.locator("#blEnabled").is_checked()


class TestBlocklistDelete:
    def test_delete_blocklist(self, page, live_server, config_path):
        """Delete a blocklist via confirm dialog."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.on("dialog", lambda dialog: dialog.accept())
        with page.expect_navigation():
            page.locator("#blocklistsList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert len(config["blockLists"]) == 0


class TestBlocklistDeleteCancel:
    def test_delete_cancel_preserves_blocklist(self, page, live_server, config_path):
        """Dismissing the confirm dialog preserves the blocklist."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        initial_config = read_config(config_path)
        initial_count = len(initial_config["blockLists"])

        page.on("dialog", lambda dialog: dialog.dismiss())
        page.locator("#blocklistsList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert len(config["blockLists"]) == initial_count


class TestBlocklistRefresh:
    def test_refresh_button(self, page, live_server):
        """Refresh All button changes text during refresh."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        btn = page.locator("#refreshBtn")
        assert btn.text_content().strip() == "Refresh All"
        btn.click()
        # After the API call completes, button resets
        page.wait_for_function(
            "document.getElementById('refreshBtn').textContent.trim() === 'Refresh All'"
        )

    def test_refresh_button_shows_loading_state(self, page, live_server):
        """Refresh button shows 'Refreshing...' text during the API call."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        btn = page.locator("#refreshBtn")
        assert btn.text_content().strip() == "Refresh All"

        # Use evaluate to click and immediately capture the text
        refreshing_text = page.evaluate("""() => {
            document.getElementById('refreshBtn').click();
            return document.getElementById('refreshBtn').textContent.trim();
        }""")
        assert refreshing_text == "Refreshing..."

        # Wait for it to reset
        page.wait_for_function(
            "document.getElementById('refreshBtn').textContent.trim() === 'Refresh All'"
        )


class TestBlocklistType:
    def test_type_badge_domain(self, page, live_server):
        """Default blocklist shows Domain badge."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()
        table = page.locator("#blocklistsList")
        assert table.get_by_text("Domain", exact=True).is_visible()

    def test_add_regex_blocklist_shows_badge(self, page, live_server, config_path):
        """Adding a regex blocklist shows Regex badge."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.get_by_role("button", name="Add Blocklist").click()
        page.locator("#blocklistModal").wait_for(state="visible")

        page.locator("#blType").select_option("regex")
        page.locator("#blName").fill("Regex Patterns")
        page.locator("#blUrl").fill("https://regex.example.com/patterns.txt")

        with page.expect_navigation():
            page.locator("#blocklistForm button[type='submit']").click()

        assert page.get_by_text("Regex", exact=True).is_visible()

        config = read_config(config_path)
        added = next(
            bl for bl in config["blockLists"]
            if bl["url"] == "https://regex.example.com/patterns.txt"
        )
        assert added["type"] == "regex"

    def test_type_dropdown_in_modal(self, page, live_server):
        """Type dropdown visible when adding a new blocklist."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.get_by_role("button", name="Add Blocklist").click()
        page.locator("#blocklistModal").wait_for(state="visible")

        type_select = page.locator("#blType")
        assert type_select.is_visible()
        assert not type_select.is_disabled()

    def test_type_disabled_on_edit(self, page, live_server):
        """Type dropdown is disabled when editing an existing blocklist."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.locator("#blocklistsList button:has-text('Edit')").first.click()
        page.locator("#blocklistModal").wait_for(state="visible")

        type_select = page.locator("#blType")
        assert type_select.is_disabled()


class TestBlocklistModal:
    def test_modal_opens_and_closes(self, page, live_server):
        """Modal can be opened and cancelled."""
        page.goto(f"{live_server}/filters/blocklists")
        page.locator("#blocklistsList").wait_for()

        page.get_by_role("button", name="Add Blocklist").click()
        page.locator("#blocklistModal").wait_for(state="visible")

        page.locator("#blocklistModal button:has-text('Cancel')").click()
        assert page.locator("#blocklistModal.hidden").count() == 1
