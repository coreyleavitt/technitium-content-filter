"""E2E tests for the profiles page."""

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

    def test_schedule_grid(self, page, live_server):
        """Kids profile shows blocking schedule grid."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()
        cards = page.locator("#profilesList")
        assert cards.get_by_text("Blocking Schedule").is_visible()

    def test_empty_profiles(self, page, live_server_empty):
        """Empty config shows 'no profiles' message."""
        page.goto(f"{live_server_empty}/profiles")
        page.locator("#profilesList").wait_for()
        assert page.get_by_text("No profiles yet").is_visible()


class TestProfileCreate:
    def test_add_profile(self, page, live_server, config_path):
        """Create a new profile via modal."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#profileModal").wait_for(state="visible")

        assert page.locator("#modalTitle").text_content() == "Add Profile"

        page.locator("#profileName").fill("teenagers")
        page.locator("#profileDesc").fill("Teen profile")

        page.locator("input[name='blockedServices'][value='youtube']").check()

        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        assert page.get_by_text("teenagers").is_visible()
        assert page.get_by_text("Teen profile").is_visible()

        config = read_config(config_path)
        assert "teenagers" in config["profiles"]
        assert config["profiles"]["teenagers"]["blockedServices"] == ["youtube"]

    def test_add_profile_with_blocklist(self, page, live_server, config_path):
        """Profile can reference global blocklists."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#profileModal").wait_for(state="visible")
        page.locator("#profileName").fill("bl-test")

        page.locator("input[name='profileBlockLists']").first.check()

        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        assert "bl-test" in config["profiles"]
        assert len(config["profiles"]["bl-test"]["blockLists"]) == 1


class TestProfileEdit:
    def test_edit_profile(self, page, live_server, config_path):
        """Edit an existing profile's description."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.locator("#profilesList button:has-text('Edit')").first.click()
        page.locator("#profileModal").wait_for(state="visible")

        assert page.locator("#modalTitle").text_content() == "Edit Profile"
        assert page.locator("#profileName").input_value() == "kids"

        page.locator("#profileDesc").fill("Updated description")
        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["description"] == "Updated description"

    def test_edit_preserves_allowlist(self, page, live_server, config_path):
        """Editing a profile preserves allowList and customRules."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.locator("#profilesList button:has-text('Edit')").first.click()
        page.locator("#profileModal").wait_for(state="visible")

        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["allowList"] == [
            "khanacademy.org",
            "school.edu",
        ]
        assert config["profiles"]["kids"]["customRules"] == [
            "blocked.com",
            "@@exception.com",
        ]

    def test_edit_schedule(self, page, live_server, config_path):
        """Enable schedule and check days on adults profile."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.locator("#profilesList button:has-text('Edit')").nth(1).click()
        page.locator("#profileModal").wait_for(state="visible")

        page.locator("#enableSchedule").check()
        page.locator("#scheduleGrid").wait_for(state="visible")

        page.locator(".day-toggle[data-day='mon']").check()
        page.locator(".day-toggle[data-day='tue']").check()

        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        schedule = config["profiles"]["adults"]["schedule"]
        assert "mon" in schedule
        assert "tue" in schedule
        assert "wed" not in schedule

    def test_rename_profile(self, page, live_server, config_path):
        """Renaming a profile deletes the old and creates with the new name."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        # Edit adults profile
        page.locator("#profilesList button:has-text('Edit')").nth(1).click()
        page.locator("#profileModal").wait_for(state="visible")
        assert page.locator("#profileName").input_value() == "adults"

        page.locator("#profileName").fill("grown-ups")
        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        assert "adults" not in config["profiles"]
        assert "grown-ups" in config["profiles"]
        assert config["profiles"]["grown-ups"]["description"] == "Adult profile"

    def test_rename_cascades_client_reassignment(self, page, live_server, config_path):
        """Renaming a profile updates client assignments to the new name."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        # Edit kids profile (first profile, used by iPad)
        page.locator("#profilesList button:has-text('Edit')").first.click()
        page.locator("#profileModal").wait_for(state="visible")
        assert page.locator("#profileName").input_value() == "kids"

        page.locator("#profileName").fill("children")
        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        # Old profile should be deleted, new one created
        assert "kids" not in config["profiles"]
        assert "children" in config["profiles"]
        # The rename endpoint atomically updates client assignments to the new name
        ipad = next(c for c in config["clients"] if c["name"] == "iPad")
        assert ipad["profile"] == "children"

    def test_disable_schedule_clears_it(self, page, live_server, config_path):
        """Unchecking schedule checkbox saves schedule as null."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        # Edit kids profile (has a schedule)
        page.locator("#profilesList button:has-text('Edit')").first.click()
        page.locator("#profileModal").wait_for(state="visible")
        assert page.locator("#enableSchedule").is_checked()

        page.locator("#enableSchedule").uncheck()
        with page.expect_navigation():
            page.locator("#profileForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["profiles"]["kids"]["schedule"] is None


class TestProfileDelete:
    def test_delete_profile(self, page, live_server, config_path):
        """Delete a profile via confirm dialog."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.on("dialog", lambda dialog: dialog.accept())

        with page.expect_navigation():
            page.locator("#profilesList button:has-text('Delete')").nth(1).click()

        config = read_config(config_path)
        assert "adults" not in config["profiles"]

    def test_delete_cancel(self, page, live_server, config_path):
        """Dismissing the confirm dialog preserves the profile."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.on("dialog", lambda dialog: dialog.dismiss())
        page.locator("#profilesList button:has-text('Delete')").nth(1).click()

        config = read_config(config_path)
        assert "adults" in config["profiles"]


class TestProfileModal:
    def test_modal_opens_and_closes(self, page, live_server):
        """Modal can be opened and closed without saving."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#profileModal").wait_for(state="visible")

        page.locator("#profileModal button:has-text('Cancel')").click()
        # Modal uses Tailwind "hidden" class which sets display:none
        assert page.locator("#profileModal.hidden").count() == 1

    def test_edit_modal_prefills(self, page, live_server):
        """Edit modal pre-fills name, description, and service checkboxes."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.locator("#profilesList button:has-text('Edit')").first.click()
        page.locator("#profileModal").wait_for(state="visible")

        assert page.locator("#profileName").input_value() == "kids"
        assert page.locator("#profileDesc").input_value() == "Children's profile"

        yt = page.locator("input[name='blockedServices'][value='youtube']")
        tt = page.locator("input[name='blockedServices'][value='tiktok']")
        assert yt.is_checked()
        assert tt.is_checked()

    def test_custom_service_in_checkboxes(self, page, live_server):
        """Custom services appear in the service checkboxes."""
        page.goto(f"{live_server}/profiles")
        page.locator("#profilesList").wait_for()

        page.get_by_role("button", name="Add Profile").click()
        page.locator("#profileModal").wait_for(state="visible")

        assert page.locator("input[name='blockedServices'][value='my-streaming']").count() == 1
