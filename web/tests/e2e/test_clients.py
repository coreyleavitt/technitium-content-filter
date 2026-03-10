"""E2E tests for the clients page."""

import pytest

from tests.e2e.conftest import _start_server, read_config

pytestmark = pytest.mark.e2e


class TestClientsList:
    def test_clients_rendered(self, page, live_server):
        """Client table renders device names."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()
        table = page.locator("#clientsList")
        assert table.get_by_text("iPad").is_visible()
        assert table.get_by_text("Laptop", exact=True).is_visible()

    def test_profile_badges(self, page, live_server):
        """Clients show their assigned profile badges."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()
        table = page.locator("#clientsList")
        assert table.get_by_text("kids", exact=True).is_visible()
        assert table.get_by_text("adults", exact=True).is_visible()

    def test_identifier_badges(self, page, live_server):
        """Client identifiers are shown as badges."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()
        assert page.get_by_text("192.168.1.10").is_visible()
        assert page.get_by_text("laptop.dns").is_visible()

    def test_empty_clients(self, page, live_server_empty):
        """Empty config shows 'no clients' message."""
        page.goto(f"{live_server_empty}/clients")
        page.locator("#clientsList").wait_for()
        assert page.get_by_text("No clients configured").is_visible()


class TestClientCreate:
    def test_add_client(self, page, live_server, config_path):
        """Add a new client via modal."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.get_by_role("button", name="Add Client").click()
        page.locator("#clientModal").wait_for(state="visible")
        assert page.locator("#clientModalTitle").text_content() == "Add Client"

        page.locator("#clientName").fill("Gaming PC")
        page.locator("#clientIds").fill("192.168.1.50\n10.0.0.5")
        page.locator("#clientProfile").select_option("kids")

        with page.expect_navigation():
            page.locator("#clientForm button[type='submit']").click()

        assert page.get_by_text("Gaming PC").is_visible()

        config = read_config(config_path)
        added = next(c for c in config["clients"] if c["name"] == "Gaming PC")
        assert added["ids"] == ["192.168.1.50", "10.0.0.5"]
        assert added["profile"] == "kids"

    def test_add_client_no_profile(self, page, live_server, config_path):
        """Add a client without a profile assignment."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.get_by_role("button", name="Add Client").click()
        page.locator("#clientModal").wait_for(state="visible")

        page.locator("#clientName").fill("Guest Device")
        page.locator("#clientIds").fill("192.168.1.99")

        with page.expect_navigation():
            page.locator("#clientForm button[type='submit']").click()

        config = read_config(config_path)
        added = next(c for c in config["clients"] if c["name"] == "Guest Device")
        assert added["profile"] == ""


class TestClientEdit:
    def test_edit_client(self, page, live_server, config_path):
        """Edit existing client's profile assignment."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.locator("#clientsList button:has-text('Edit')").first.click()
        page.locator("#clientModal").wait_for(state="visible")

        assert page.locator("#clientModalTitle").text_content() == "Edit Client"
        assert page.locator("#clientName").input_value() == "iPad"

        page.locator("#clientProfile").select_option("adults")
        with page.expect_navigation():
            page.locator("#clientForm button[type='submit']").click()

        config = read_config(config_path)
        ipad = next(c for c in config["clients"] if c["name"] == "iPad")
        assert ipad["profile"] == "adults"

    def test_edit_modal_prefills_ids(self, page, live_server):
        """Edit modal pre-fills identifiers textarea."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.locator("#clientsList button:has-text('Edit')").nth(1).click()
        page.locator("#clientModal").wait_for(state="visible")

        ids_text = page.locator("#clientIds").input_value()
        assert "192.168.1.20" in ids_text
        assert "laptop.dns" in ids_text


class TestClientDelete:
    def test_delete_client(self, page, live_server, config_path):
        """Delete a client via confirm dialog."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        initial_config = read_config(config_path)
        initial_count = len(initial_config["clients"])

        page.on("dialog", lambda dialog: dialog.accept())
        with page.expect_navigation():
            page.locator("#clientsList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert len(config["clients"]) == initial_count - 1

    def test_delete_cancel(self, page, live_server, config_path):
        """Dismissing confirm preserves the client."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        initial_config = read_config(config_path)
        initial_count = len(initial_config["clients"])

        page.on("dialog", lambda dialog: dialog.dismiss())
        page.locator("#clientsList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert len(config["clients"]) == initial_count


class TestClientEmptyIdentifiers:
    def test_empty_ids_prevents_submission(self, page, live_server):
        """Submitting with empty identifiers does not navigate away."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.get_by_role("button", name="Add Client").click()
        page.locator("#clientModal").wait_for(state="visible")

        page.locator("#clientName").fill("Empty Device")
        # Leave identifiers empty
        page.locator("#clientForm button[type='submit']").click()

        # Modal should still be visible (no navigation occurred)
        assert page.locator("#clientModal").is_visible()
        assert "/clients" in page.url

    def test_whitespace_ids_prevents_submission(self, page, live_server):
        """Submitting with only whitespace/blank lines does not navigate away."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.get_by_role("button", name="Add Client").click()
        page.locator("#clientModal").wait_for(state="visible")

        page.locator("#clientName").fill("Whitespace Device")
        page.locator("#clientIds").fill("   \n\n   \n")
        page.locator("#clientForm button[type='submit']").click()

        # Modal should still be visible (no navigation occurred)
        assert page.locator("#clientModal").is_visible()


class TestClientMissingProfile:
    @pytest.fixture()
    def live_server_missing_profile(self, config_path, _services_path):
        """Start app with a client referencing a non-existent profile."""
        config = {
            "enableBlocking": True,
            "profiles": {
                "adults": {
                    "description": "Adult profile",
                    "blockedServices": [],
                    "blockLists": [],
                    "allowList": [],
                    "customRules": [],
                    "dnsRewrites": [],
                },
            },
            "clients": [
                {
                    "name": "Orphaned Device",
                    "ids": ["192.168.1.50"],
                    "profile": "deleted-profile",
                },
            ],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "America/Denver",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        base_url, shutdown = _start_server(config_path, _services_path, config)
        yield base_url
        shutdown()

    def test_missing_profile_badge(self, page, live_server_missing_profile):
        """Client with non-existent profile shows '(missing)' badge."""
        page.goto(f"{live_server_missing_profile}/clients")
        page.locator("#clientsList").wait_for()
        assert page.get_by_text("(missing)").is_visible()
        assert page.get_by_text("deleted-profile").is_visible()


class TestClientModal:
    def test_modal_opens_and_closes(self, page, live_server):
        """Modal can be opened and cancelled."""
        page.goto(f"{live_server}/clients")
        page.locator("#clientsList").wait_for()

        page.get_by_role("button", name="Add Client").click()
        page.locator("#clientModal").wait_for(state="visible")

        page.locator("#clientModal button:has-text('Cancel')").click()
        assert page.locator("#clientModal.hidden").count() == 1
