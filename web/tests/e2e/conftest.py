"""Fixtures for Playwright e2e tests."""

import json
import threading
import time
from unittest.mock import patch

import pytest
import uvicorn


@pytest.fixture(scope="session")
def _services_path(tmp_path_factory):
    """Session-scoped blocked-services.json."""
    tmp = tmp_path_factory.mktemp("e2e")
    services_path = tmp / "blocked-services.json"
    services_path.write_text(
        json.dumps(
            {
                "youtube": {"name": "YouTube", "domains": ["youtube.com", "ytimg.com"]},
                "tiktok": {"name": "TikTok", "domains": ["tiktok.com", "tiktokcdn.com"]},
            }
        )
    )
    return services_path


@pytest.fixture()
def config_path(_services_path):
    """Per-test config file path (same directory as services)."""
    return _services_path.parent / "dnsApp.config"


@pytest.fixture()
def sample_config():
    """Full-featured config matching the parent conftest."""
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
def empty_config():
    """Minimal config with no profiles, clients, or blocklists."""
    return {
        "enableBlocking": True,
        "profiles": {},
        "clients": [],
        "defaultProfile": None,
        "baseProfile": None,
        "timeZone": "America/Denver",
        "scheduleAllDay": True,
        "customServices": {},
        "blockLists": [],
        "_blockListsSeeded": True,
    }


def _start_server(config_path, services_path, config_data):
    """Start a live uvicorn server, return (base_url, shutdown_fn)."""
    config_path.write_text(json.dumps(config_data, indent=2))

    import config as config_module

    patches = (
        patch.object(config_module, "CONFIG_PATH", config_path),
        patch.object(config_module, "BLOCKED_SERVICES_PATH", services_path),
        patch.object(config_module, "TECHNITIUM_API_TOKEN", ""),
        patch.object(config_module, "TECHNITIUM_URL", "http://localhost:19999"),
        patch.object(config_module, "AUTH_DISABLED", True),
    )
    for p in patches:
        p.start()

    from app import app as app_instance

    uv_config = uvicorn.Config(
        app=app_instance,
        host="127.0.0.1",
        port=0,
        log_level="warning",
    )
    server = uvicorn.Server(uv_config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()

    while not server.started:
        time.sleep(0.05)

    port = server.servers[0].sockets[0].getsockname()[1]

    def shutdown():
        server.should_exit = True
        thread.join(timeout=5)
        for p in patches:
            p.stop()

    return f"http://127.0.0.1:{port}", shutdown


@pytest.fixture()
def live_server(config_path, _services_path, sample_config):
    """Start the Starlette app with sample config, yield the base URL."""
    base_url, shutdown = _start_server(config_path, _services_path, sample_config)
    yield base_url
    shutdown()


@pytest.fixture()
def live_server_empty(config_path, _services_path, empty_config):
    """Start the Starlette app with empty config, yield the base URL."""
    base_url, shutdown = _start_server(config_path, _services_path, empty_config)
    yield base_url
    shutdown()


def read_config(config_path) -> dict:
    """Helper to read saved config from disk."""
    return json.loads(config_path.read_text())
