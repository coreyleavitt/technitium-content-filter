import json
from unittest.mock import patch

import pytest
import respx
from httpx import Response
from starlette.testclient import TestClient


@pytest.fixture(autouse=True)
def _clear_rate_limit():
    """Clear rate limiter state between tests to prevent cross-test interference."""
    import middleware

    middleware._rate_limit_buckets.clear()
    yield
    middleware._rate_limit_buckets.clear()


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
def make_client(tmp_config):
    """Factory fixture for creating patched TestClients."""
    _patches: list = []
    _mocks: list = []

    def _factory(config_data=None, *, auth_disabled=True, raise_exceptions=True):
        if config_data is not None:
            tmp_config.write_text(json.dumps(config_data, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"
        patches = [
            patch("config.CONFIG_PATH", tmp_config),
            patch("config.BLOCKED_SERVICES_PATH", services_path),
            patch("config.TECHNITIUM_API_TOKEN", "test-token"),
            patch("config.TECHNITIUM_URL", "http://technitium-mock:5380"),
            patch("config.AUTH_DISABLED", auth_disabled),
        ]
        for p in patches:
            p.start()
        _patches.extend(patches)
        mock = respx.mock(assert_all_called=False)
        mock.start()
        mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
            return_value=Response(200, json={"status": "ok"})
        )
        _mocks.append(mock)
        from app import app

        return TestClient(app, raise_server_exceptions=raise_exceptions)

    yield _factory
    for p in reversed(_patches):
        p.stop()
    for m in _mocks:
        m.stop()


@pytest.fixture()
def client(make_client, sample_config):
    """Starlette TestClient with patched module globals."""
    return make_client(sample_config)


@pytest.fixture()
def client_empty(make_client):
    """TestClient with empty config (first-run scenario)."""
    return make_client(None)


@pytest.fixture()
def client_permissive(make_client, sample_config):
    """TestClient that returns 500 instead of raising on server errors."""
    return make_client(sample_config, raise_exceptions=False)


@pytest.fixture()
def client_with_auth(make_client, sample_config):
    """TestClient with auth ENABLED (not bypassed)."""
    return make_client(sample_config, auth_disabled=False)


def read_config(tmp_config) -> dict:
    """Helper to read saved config from disk."""
    return json.loads(tmp_config.read_text())
