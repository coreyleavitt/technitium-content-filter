"""E2E tests for the dashboard page."""

import pytest

from tests.e2e.conftest import _start_server, read_config

pytestmark = pytest.mark.e2e


class TestDashboardLoad:
    def test_protection_active_banner(self, page, live_server):
        """Dashboard shows 'Protection Active' when blocking is enabled."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        assert page.get_by_text("Protection Active").is_visible()

    def test_stats_cards(self, page, live_server):
        """Stats cards show correct counts from sample config."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        # Scope to the card containing "Total Clients"
        clients_card = page.locator("dt:has-text('Total Clients')").locator("..")
        assert clients_card.locator("dd").text_content().strip() == "2"

    def test_client_overview_table(self, page, live_server):
        """Client overview table renders device names."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        table = page.locator("table")
        assert table.get_by_text("iPad").is_visible()
        assert table.get_by_text("Laptop", exact=True).is_visible()

    def test_profile_summary(self, page, live_server):
        """Profiles section shows profile names."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        section = page.locator("h3:has-text('Profiles')").locator("..").locator("..")
        assert section.get_by_text("kids").is_visible()
        assert section.get_by_text("adults").is_visible()

    def test_services_blocked_count(self, page, live_server):
        """Services Blocked stat card shows correct count."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        card = page.locator("dt:has-text('Services Blocked')").locator("..")
        assert card.locator("dd").text_content().strip() == "2"


class TestProtectionToggle:
    def test_toggle_disables_protection(self, page, live_server, config_path):
        """Clicking the toggle disables blocking and reloads the page."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        assert page.get_by_text("Protection Active").is_visible()

        with page.expect_navigation():
            page.locator("#toggleBlocking").click()

        page.wait_for_load_state("networkidle")
        assert page.get_by_text("Protection Disabled").is_visible()
        config = read_config(config_path)
        assert config["enableBlocking"] is False

    def test_toggle_enables_protection(self, page, live_server, config_path):
        """Toggle from disabled back to enabled."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")
        with page.expect_navigation():
            page.locator("#toggleBlocking").click()
        page.wait_for_load_state("networkidle")
        assert page.get_by_text("Protection Disabled").is_visible()

        with page.expect_navigation():
            page.locator("#toggleBlocking").click()
        page.wait_for_load_state("networkidle")
        assert page.get_by_text("Protection Active").is_visible()
        config = read_config(config_path)
        assert config["enableBlocking"] is True


class TestSettings:
    def test_save_default_profile(self, page, live_server, config_path):
        """Settings form saves default profile."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")

        page.locator("summary:has-text('Settings')").click()
        page.locator("#settingsForm").wait_for(state="visible")

        page.locator("#defaultProfile").select_option("kids")
        with page.expect_navigation():
            page.locator("#settingsForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["defaultProfile"] == "kids"

    def test_save_base_profile(self, page, live_server, config_path):
        """Settings form saves base profile."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")

        page.locator("summary:has-text('Settings')").click()
        page.locator("#settingsForm").wait_for(state="visible")

        page.locator("#baseProfile").select_option("kids")
        with page.expect_navigation():
            page.locator("#settingsForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["baseProfile"] == "kids"

    def test_save_schedule_all_day(self, page, live_server, config_path):
        """Settings form saves scheduleAllDay toggle."""
        page.goto(f"{live_server}/")
        page.wait_for_load_state("networkidle")

        page.locator("summary:has-text('Settings')").click()
        page.locator("#settingsForm").wait_for(state="visible")

        # Sample config has scheduleAllDay=False, toggle it on
        page.locator("#scheduleAllDay").check()
        with page.expect_navigation():
            page.locator("#settingsForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["scheduleAllDay"] is True


class TestDashboardEmpty:
    def test_empty_dashboard(self, page, live_server_empty):
        """Dashboard with no clients/profiles shows empty state messages."""
        page.goto(f"{live_server_empty}/")
        page.wait_for_load_state("networkidle")
        assert page.get_by_text("No clients configured yet").is_visible()
        assert page.get_by_text("No profiles configured yet").is_visible()


class TestTimezoneAutoDetect:
    @pytest.fixture()
    def live_server_utc(self, config_path, _services_path):
        """Start app with timeZone set to UTC to trigger auto-detect."""
        config = {
            "enableBlocking": True,
            "profiles": {},
            "clients": [],
            "defaultProfile": None,
            "baseProfile": None,
            "timeZone": "UTC",
            "scheduleAllDay": True,
            "customServices": {},
            "blockLists": [],
            "_blockListsSeeded": True,
        }
        base_url, shutdown = _start_server(config_path, _services_path, config)
        yield base_url
        shutdown()

    def test_utc_timezone_triggers_auto_detect(self, browser, live_server_utc, config_path):
        """When timeZone is UTC, dashboard auto-detects browser TZ and saves it."""
        # Use an explicit non-UTC timezone so the auto-detect replaces UTC
        context = browser.new_context(timezone_id="America/New_York")
        tz_page = context.new_page()
        try:
            tz_page.goto(f"{live_server_utc}/")
            tz_page.wait_for_load_state("networkidle")

            config = read_config(config_path)
            # Browser timezone should replace UTC
            assert config["timeZone"] != "UTC"
        finally:
            context.close()


class TestNavigation:
    def test_nav_to_profiles(self, page, live_server):
        page.goto(f"{live_server}/")
        # Scope to desktop nav to avoid matching mobile nav duplicate
        desktop_nav = page.locator("nav .hidden.md\\:flex")
        desktop_nav.get_by_role("link", name="Profiles").click()
        page.wait_for_load_state("networkidle")
        assert "/profiles" in page.url

    def test_nav_to_clients(self, page, live_server):
        page.goto(f"{live_server}/")
        # Scope to desktop nav to avoid matching mobile nav duplicate
        desktop_nav = page.locator("nav .hidden.md\\:flex")
        desktop_nav.get_by_role("link", name="Clients").click()
        page.wait_for_load_state("networkidle")
        assert "/clients" in page.url

    def test_filters_dropdown(self, page, live_server):
        """Filters dropdown opens and shows sub-links."""
        page.goto(f"{live_server}/")
        page.locator("#filtersDropdown button").click()
        dropdown = page.locator("#filtersMenu")
        dropdown.wait_for(state="visible")
        # Scope assertions to the desktop dropdown menu to avoid mobile nav duplicates
        assert dropdown.get_by_role("link", name="DNS Blocklists").is_visible()
        assert dropdown.get_by_role("link", name="DNS Allowlists").is_visible()
        assert dropdown.get_by_role("link", name="Blocked Services").is_visible()
        assert dropdown.get_by_role("link", name="Custom Filtering Rules").is_visible()
        assert dropdown.get_by_role("link", name="DNS Rewrites").is_visible()
