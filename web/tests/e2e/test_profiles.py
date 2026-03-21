"""E2E tests for the profiles page and profile detail page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestProfilesList:
    def test_profiles_rendered(self, page, live_server):
        """Profile cards render with names."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        cards = page.locator("#profilesList")
        assert cards.get_by_text("kids", exact=True).is_visible()
        assert cards.get_by_text("adults", exact=True).is_visible()

    def test_blocked_services_badges(self, page, live_server):
        """Kids profile shows YouTube and TikTok badges in the profile list."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        cards = page.locator("#profilesList")
        assert cards.get_by_text("YouTube").is_visible()
        assert cards.get_by_text("TikTok").is_visible()

    def test_profile_description(self, page, live_server):
        """Profile descriptions are rendered."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        assert page.get_by_text("Children's profile").is_visible()

    def test_filter_count_badges(self, page, live_server):
        """Profile cards show count badges for allowlist, rules, rewrites."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        cards = page.locator("#profilesList")
        assert cards.get_by_text("2 allowed").is_visible()
        assert cards.get_by_text("2 custom rules").is_visible()
        assert cards.get_by_text("1 rewrite").is_visible()
        assert cards.get_by_text("1 blocklist").is_visible()

    def test_cards_link_to_detail(self, page, live_server):
        """Clicking a profile card navigates to the detail page."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        page.locator("#profilesList a").first.click()
        page.wait_for_url("**/profiles/kids")

    def test_empty_profiles(self, page, live_server_empty):
        """Empty config shows 'no profiles' message."""
        page.goto(f"{live_server_empty}/profiles")
        page.locator("#profilesList").wait_for()
        assert page.get_by_text("No profiles yet").is_visible()


class TestProfileCreate:
    def test_add_profile(self, page, live_server, config_path):
        """Create a new profile via modal -- redirects to detail page."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#createModal").wait_for(state="visible")

        page.locator("#profileName").fill("teenagers")
        page.locator("#profileDesc").fill("Teen profile")

        with page.expect_navigation():
            page.locator("#createForm button[type='submit']").click()

        # Should redirect to the new profile's detail page
        page.wait_for_url("**/profiles/teenagers")

        config = read_config(config_path)
        assert "teenagers" in config["profiles"]
        assert config["profiles"]["teenagers"]["description"] == "Teen profile"


class TestProfileDetail:
    def test_detail_page_loads(self, page, live_server):
        """Profile detail page renders with profile name."""
        page.goto(f"{live_server}/profiles/kids")
        page.locator("#profileTitle").wait_for()
        assert page.locator("#profileTitle").text_content() == "kids"

    def test_edit_description(self, page, live_server, config_path):
        """Edit description on the overview tab."""
        page.goto(f"{live_server}/profiles/kids")
        page.locator("#profileDesc").wait_for()

        page.locator("#profileDesc").fill("Updated description")

        with page.expect_response("**/api/profiles"):
            page.locator("#overviewSaveBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["description"] == "Updated description"

    def test_edit_preserves_allowlist(self, page, live_server, config_path):
        """Saving overview preserves allowList and customRules."""
        page.goto(f"{live_server}/profiles/kids")
        page.locator("#profileDesc").wait_for()

        with page.expect_response("**/api/profiles"):
            page.locator("#overviewSaveBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["allowList"] == [
            "khanacademy.org",
            "school.edu",
        ]
        assert config["profiles"]["kids"]["customRules"] == [
            "blocked.com",
            "@@exception.com",
        ]

    def test_schedule(self, page, live_server, config_path):
        """Enable schedule and check days on adults profile."""
        page.goto(f"{live_server}/profiles/adults")
        page.locator("#profileDesc").wait_for()

        page.locator("#enableSchedule").check()
        page.locator("#scheduleGrid").wait_for(state="visible")

        page.locator(".day-toggle[data-day='mon']").check()
        page.locator(".day-toggle[data-day='tue']").check()

        with page.expect_response("**/api/profiles"):
            page.locator("#overviewSaveBtn").click()

        config = read_config(config_path)
        schedule = config["profiles"]["adults"]["schedule"]
        assert "mon" in schedule
        assert "tue" in schedule
        assert "wed" not in schedule

    def test_disable_schedule(self, page, live_server, config_path):
        """Unchecking schedule checkbox saves schedule as null."""
        page.goto(f"{live_server}/profiles/kids")
        page.locator("#profileDesc").wait_for()
        assert page.locator("#enableSchedule").is_checked()

        page.locator("#enableSchedule").uncheck()

        with page.expect_response("**/api/profiles"):
            page.locator("#overviewSaveBtn").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["schedule"] is None

    def test_rename_profile(self, page, live_server, config_path):
        """Renaming a profile navigates to the new detail page."""
        page.goto(f"{live_server}/profiles/adults")
        page.locator("#profileTitle").wait_for()

        page.locator("#renameBtn").click()
        page.locator("#renameModal").wait_for(state="visible")
        page.locator("#newProfileName").fill("grown-ups")

        with page.expect_navigation():
            page.locator("#renameForm button[type='submit']").click()

        page.wait_for_url("**/profiles/grown-ups")

        config = read_config(config_path)
        assert "adults" not in config["profiles"]
        assert "grown-ups" in config["profiles"]

    def test_rename_cascades_client_reassignment(self, page, live_server, config_path):
        """Renaming a profile updates client assignments."""
        page.goto(f"{live_server}/profiles/kids")
        page.locator("#profileTitle").wait_for()

        page.locator("#renameBtn").click()
        page.locator("#renameModal").wait_for(state="visible")
        page.locator("#newProfileName").fill("children")

        with page.expect_navigation():
            page.locator("#renameForm button[type='submit']").click()

        config = read_config(config_path)
        assert "kids" not in config["profiles"]
        assert "children" in config["profiles"]
        ipad = next(c for c in config["clients"] if c["name"] == "iPad")
        assert ipad["profile"] == "children"

    def test_delete_profile(self, page, live_server, config_path):
        """Delete a profile via confirm dialog."""
        page.goto(f"{live_server}/profiles/adults")
        page.locator("#profileTitle").wait_for()

        page.on("dialog", lambda dialog: dialog.accept())

        with page.expect_navigation():
            page.locator("#deleteBtn").click()

        page.wait_for_url("**/profiles")

        config = read_config(config_path)
        assert "adults" not in config["profiles"]

    def test_delete_cancel(self, page, live_server, config_path):
        """Dismissing confirm dialog preserves the profile."""
        page.goto(f"{live_server}/profiles/adults")
        page.locator("#profileTitle").wait_for()

        page.on("dialog", lambda dialog: dialog.dismiss())
        page.locator("#deleteBtn").click()

        config = read_config(config_path)
        assert "adults" in config["profiles"]


class TestProfileModal:
    def test_modal_opens_and_closes(self, page, live_server):
        """Create modal can be opened and closed without saving."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#createModal").wait_for(state="visible")

        page.locator("#createModal button:has-text('Cancel')").click()
        assert page.locator("#createModal.hidden").count() == 1
