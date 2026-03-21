"""E2E tests for XSS prevention via escapeHtml."""

import pytest

from tests.e2e.conftest import _start_server

pytestmark = pytest.mark.e2e


@pytest.fixture()
def live_server_xss(config_path, _services_path):
    """Start the app with config containing HTML/XSS payloads in names.

    Uses angle brackets and HTML attributes that escapeHtml must neutralize.
    Avoids </script> in values since that breaks the JSON script tag in templates.
    """
    xss_config = {
        "enableBlocking": True,
        "profiles": {
            "<b>bold</b>": {
                "description": "XSS profile",
                "blockedServices": [],
                "blockLists": [],
                "allowList": [],
                "customRules": [],
                "dnsRewrites": [],
            },
        },
        "clients": [
            {
                "name": '<img src=x onerror="alert(1)">',
                "ids": ["192.168.1.10"],
                "profile": "<b>bold</b>",
            },
        ],
        "defaultProfile": None,
        "baseProfile": None,
        "timeZone": "America/Denver",
        "scheduleAllDay": True,
        "customServices": {
            "xss-svc": {
                "name": '<b onmouseover="alert(1)">Hover</b>',
                "domains": ["example.com"],
            },
        },
        "blockLists": [],
        "_blockListsSeeded": True,
    }
    base_url, shutdown = _start_server(config_path, _services_path, xss_config)
    yield base_url
    shutdown()


class TestXssProfileName:
    def test_html_in_profile_name_is_escaped(self, page, live_server_xss):
        """Profile name containing HTML tags is rendered as text, not executed."""
        page.goto(f"{live_server_xss}/profiles")
        page.locator("#profilesList").wait_for()
        content = page.locator("#profilesList").inner_html()
        # The <b> tag should be escaped, not rendered as an actual bold element
        assert "&lt;b&gt;bold&lt;/b&gt;" in content


class TestXssClientName:
    def test_html_in_client_name_is_escaped(self, page, live_server_xss):
        """Client name with HTML is rendered safely."""
        page.goto(f"{live_server_xss}/clients")
        page.locator("#clientsList").wait_for()
        content = page.locator("#clientsList").inner_html()
        # The img tag should not be rendered as HTML
        assert "<img " not in content


class TestXssServiceName:
    def test_html_in_service_name_is_escaped(self, page, live_server_xss):
        """Custom service name with HTML is rendered safely."""
        page.goto(f"{live_server_xss}/settings")
        page.locator("#customServicesList").wait_for()
        content = page.locator("#customServicesList").inner_html()
        # The <b> tag should not be rendered as actual HTML element
        assert "<b " not in content
