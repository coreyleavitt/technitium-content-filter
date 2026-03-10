"""E2E tests for the Blocked Services filter page."""

import pytest

from tests.e2e.conftest import read_config

pytestmark = pytest.mark.e2e


class TestServicesList:
    def test_custom_services_rendered(self, page, live_server):
        """Custom services section renders existing entries."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()
        assert page.get_by_text("My Streaming").is_visible()
        assert page.get_by_text("stream.example.com").is_visible()

    def test_builtin_services_shown(self, page, live_server):
        """Built-in services section shows YouTube and TikTok."""
        page.goto(f"{live_server}/filters/services")
        services = page.locator("summary:has-text('Built-in Services')").locator("..")
        assert services.get_by_text("YouTube", exact=True).is_visible()
        assert services.get_by_text("TikTok", exact=True).is_visible()

    def test_empty_custom_services(self, page, live_server_empty):
        """Empty config shows 'no custom services' message."""
        page.goto(f"{live_server_empty}/filters/services")
        page.locator("#customServicesList").wait_for()
        assert page.get_by_text("No custom services defined").is_visible()


class TestServiceCreate:
    def test_add_custom_service(self, page, live_server, config_path):
        """Add a new custom service via modal."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.get_by_role("button", name="Add Custom Service").click()
        page.locator("#serviceModal").wait_for(state="visible")

        page.locator("#serviceId").fill("my-games")
        page.locator("#serviceName").fill("My Games")
        page.locator("#serviceDomains").fill("game1.com\ngame2.com")

        with page.expect_navigation():
            page.locator("#serviceForm button[type='submit']").click()

        assert page.get_by_text("My Games").is_visible()

        config = read_config(config_path)
        assert "my-games" in config["customServices"]
        assert config["customServices"]["my-games"]["domains"] == [
            "game1.com",
            "game2.com",
        ]

    def test_builtin_id_conflict(self, page, live_server):
        """Alert prevents saving a custom service with a built-in ID."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.get_by_role("button", name="Add Custom Service").click()
        page.locator("#serviceModal").wait_for(state="visible")

        page.locator("#serviceId").fill("youtube")
        page.locator("#serviceName").fill("Fake YouTube")
        page.locator("#serviceDomains").fill("fake.com")

        alert_text = []
        page.on("dialog", lambda d: (alert_text.append(d.message), d.accept()))
        page.locator("#serviceForm button[type='submit']").click()

        # Should have triggered an alert about conflicting ID
        page.wait_for_function("true", timeout=2000)  # let dialog handler fire
        assert any("youtube" in t for t in alert_text)


class TestServiceEdit:
    def test_edit_custom_service(self, page, live_server, config_path):
        """Edit an existing custom service."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.locator("#customServicesList button:has-text('Edit')").first.click()
        page.locator("#serviceModal").wait_for(state="visible")

        assert page.locator("#serviceModalTitle").text_content() == "Edit Custom Service"
        # Service ID is readonly when editing
        assert page.locator("#serviceId").get_attribute("readonly") is not None

        page.locator("#serviceName").fill("Updated Streaming")
        with page.expect_navigation():
            page.locator("#serviceForm button[type='submit']").click()

        config = read_config(config_path)
        assert config["customServices"]["my-streaming"]["name"] == "Updated Streaming"


class TestServiceDelete:
    def test_delete_custom_service(self, page, live_server, config_path):
        """Delete a custom service via confirm dialog."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.on("dialog", lambda dialog: dialog.accept())
        with page.expect_navigation():
            page.locator("#customServicesList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert "my-streaming" not in config["customServices"]


class TestServiceDeleteCancel:
    def test_delete_cancel_preserves_service(self, page, live_server, config_path):
        """Dismissing the confirm dialog preserves the custom service."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.on("dialog", lambda dialog: dialog.dismiss())
        page.locator("#customServicesList button:has-text('Delete')").first.click()

        config = read_config(config_path)
        assert "my-streaming" in config["customServices"]


class TestServiceModal:
    def test_modal_opens_and_closes(self, page, live_server):
        """Modal can be opened and cancelled."""
        page.goto(f"{live_server}/filters/services")
        page.locator("#customServicesList").wait_for()

        page.get_by_role("button", name="Add Custom Service").click()
        page.locator("#serviceModal").wait_for(state="visible")

        page.locator("#serviceModal button:has-text('Cancel')").click()
        assert page.locator("#serviceModal.hidden").count() == 1
