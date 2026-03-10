import json
from unittest.mock import patch

import pytest
import respx
from httpx import Response
from starlette.testclient import TestClient


@pytest.fixture()
def tmp_config(tmp_path):
    """Temporary config directory with config path and blocked-services.json."""
    config_path = tmp_path / "dnsApp.config"
    services_path = tmp_path / "blocked-services.json"
    services_path.write_text(
        json.dumps(
            {
                "youtube": {"name": "YouTube", "domains": ["youtube.com", "ytimg.com"]},
                "tiktok": {"name": "TikTok", "domains": ["tiktok.com", "tiktokcdn.com"]},
            }
        )
    )
    return config_path


@pytest.fixture()
def empty_config():
    """Minimal valid config."""
    return {
        "enableBlocking": True,
        "profiles": {},
        "clients": [],
        "defaultProfile": None,
        "baseProfile": None,
        "timeZone": "UTC",
        "scheduleAllDay": True,
        "customServices": {},
        "blockLists": [],
    }


@pytest.fixture()
def sample_config():
    """Full-featured config with profiles, clients, blocklists, rewrites."""
    return {
        "enableBlocking": True,
        "profiles": {
            "kids": {
                "description": "Children's profile",
                "blockedServices": ["youtube", "tiktok"],
                "blockLists": ["https://example.com/list.txt"],
                "allowList": ["khanacademy.org", "school.edu"],
                "customRules": ["blocked.com", "@@exception.com"],
                "dnsRewrites": [
                    {"domain": "search.com", "answer": "safesearch.google.com"},
                ],
                "schedule": {
                    "mon": {"allDay": False, "start": "08:00", "end": "20:00"},
                },
            },
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
            {"name": "iPad", "ids": ["192.168.1.10"], "profile": "kids"},
            {"name": "Laptop", "ids": ["192.168.1.20", "laptop.dns"], "profile": "adults"},
        ],
        "defaultProfile": "adults",
        "baseProfile": None,
        "timeZone": "America/Denver",
        "scheduleAllDay": False,
        "customServices": {
            "my-streaming": {
                "name": "My Streaming",
                "domains": ["stream.example.com"],
            },
        },
        "blockLists": [
            {
                "url": "https://example.com/list.txt",
                "name": "Ad List",
                "enabled": True,
                "refreshHours": 24,
            },
        ],
        "_blockListsSeeded": True,
    }


@pytest.fixture()
def client(tmp_config, sample_config):
    """Starlette TestClient with patched module globals."""
    tmp_config.write_text(json.dumps(sample_config, indent=2))
    services_path = tmp_config.parent / "blocked-services.json"

    with (
        patch("app.CONFIG_PATH", tmp_config),
        patch("app.BLOCKED_SERVICES_PATH", services_path),
        patch("app.TECHNITIUM_API_TOKEN", "test-token"),
        patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
        respx.mock(assert_all_called=False) as mock,
    ):
        mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
            return_value=Response(200, json={"status": "ok"})
        )
        from app import app

        yield TestClient(app, raise_server_exceptions=True)


@pytest.fixture()
def client_empty(tmp_config, empty_config):
    """TestClient with empty config (first-run scenario)."""
    # Don't write config file -- simulates first run
    services_path = tmp_config.parent / "blocked-services.json"

    with (
        patch("app.CONFIG_PATH", tmp_config),
        patch("app.BLOCKED_SERVICES_PATH", services_path),
        patch("app.TECHNITIUM_API_TOKEN", "test-token"),
        patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
        respx.mock(assert_all_called=False) as mock,
    ):
        mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
            return_value=Response(200, json={"status": "ok"})
        )
        from app import app

        yield TestClient(app, raise_server_exceptions=True)


@pytest.fixture()
def client_permissive(tmp_config, sample_config):
    """TestClient that returns 500 instead of raising on server errors."""
    tmp_config.write_text(json.dumps(sample_config, indent=2))
    services_path = tmp_config.parent / "blocked-services.json"

    with (
        patch("app.CONFIG_PATH", tmp_config),
        patch("app.BLOCKED_SERVICES_PATH", services_path),
        patch("app.TECHNITIUM_API_TOKEN", "test-token"),
        patch("app.TECHNITIUM_URL", "http://technitium-mock:5380"),
        respx.mock(assert_all_called=False) as mock,
    ):
        mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
            return_value=Response(200, json={"status": "ok"})
        )
        from app import app

        yield TestClient(app, raise_server_exceptions=False)


def read_config(tmp_config) -> dict:
    """Helper to read saved config from disk."""
    return json.loads(tmp_config.read_text())
