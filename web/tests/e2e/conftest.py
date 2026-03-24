"""Fixtures for Playwright e2e tests."""

import asyncio
import json
import socket
import threading
import time
from unittest.mock import patch

import pytest
from hypercorn.asyncio import serve
from hypercorn.config import Config as HypercornConfig


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


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
    """Start a live Hypercorn server, return (base_url, shutdown_fn)."""
    config_path.write_text(json.dumps(config_data, indent=2))

    from technitium_content_filter import config as config_module

    patches = (
        patch.object(config_module, "CONFIG_PATH", config_path),
        patch.object(config_module, "BLOCKED_SERVICES_PATH", services_path),
        patch.object(config_module, "TECHNITIUM_API_TOKEN", ""),
        patch.object(config_module, "TECHNITIUM_URL", "http://localhost:19999"),
        patch.object(config_module, "AUTH_DISABLED", True),
    )
    for p in patches:
        p.start()

    from technitium_content_filter.app import app as app_instance

    port = _find_free_port()
    hc = HypercornConfig()
    hc.bind = [f"127.0.0.1:{port}"]
    hc.loglevel = "WARNING"

    stop_event = threading.Event()

    async def _shutdown_trigger() -> None:
        while not stop_event.is_set():
            await asyncio.sleep(0.1)

    def _run() -> None:
        # Use subprocess-like isolation: new_event_loop without set_event_loop
        # to avoid corrupting the main thread's event loop state
        loop = asyncio.new_event_loop()
        try:
            loop.run_until_complete(
                serve(app_instance, hc, shutdown_trigger=_shutdown_trigger)  # type: ignore[arg-type]
            )
        finally:
            try:
                pending = asyncio.all_tasks(loop)
                for task in pending:
                    task.cancel()
                if pending:
                    loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
                loop.run_until_complete(loop.shutdown_asyncgens())
            finally:
                loop.close()

    thread = threading.Thread(target=_run, daemon=True)
    thread.start()

    # Wait for server to be ready
    import httpx

    for _ in range(100):
        try:
            httpx.get(f"http://127.0.0.1:{port}/login", timeout=1.0)
            break
        except (httpx.ConnectError, httpx.ReadError):
            time.sleep(0.05)

    def shutdown() -> None:
        stop_event.set()
        thread.join(timeout=5)
        for p in patches:
            p.stop()

    return f"http://127.0.0.1:{port}", shutdown


@pytest.fixture()
def live_server(config_path, _services_path, sample_config):
    """Start the app with sample config, yield the base URL."""
    base_url, shutdown = _start_server(config_path, _services_path, sample_config)
    yield base_url
    shutdown()


@pytest.fixture()
def live_server_empty(config_path, _services_path, empty_config):
    """Start the app with empty config, yield the base URL."""
    base_url, shutdown = _start_server(config_path, _services_path, empty_config)
    yield base_url
    shutdown()


def read_config(config_path) -> dict:
    """Helper to read saved config from disk."""
    return json.loads(config_path.read_text())
