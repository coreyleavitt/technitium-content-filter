import asyncio
import json
from unittest.mock import patch

import pytest
import respx
from httpx import Response
from litestar.testing import TestClient


@pytest.fixture(scope="session")
def event_loop_policy():
    """Force standard asyncio policy to avoid uvloop conflicts with E2E threads."""
    return asyncio.DefaultEventLoopPolicy()


class _CSRFClient:
    """Wraps TestClient to auto-include CSRF tokens in state-changing requests."""

    def __init__(self, inner: TestClient) -> None:
        self._inner = inner
        # Seed the CSRF cookie with an initial safe GET
        self._inner.get("/api/config")

    def _csrf_headers(self, headers: dict | None) -> dict:
        result = dict(headers) if headers else {}
        token = self._inner.cookies.get("csrftoken")
        if token and "x-csrftoken" not in {k.lower() for k in result}:
            result["x-csrftoken"] = token
        return result

    def get(self, *args, **kwargs):  # noqa: ANN002, ANN003
        return self._inner.get(*args, **kwargs)

    def post(self, *args, **kwargs):  # noqa: ANN002, ANN003
        kwargs["headers"] = self._csrf_headers(kwargs.get("headers"))
        return self._inner.post(*args, **kwargs)

    def put(self, *args, **kwargs):  # noqa: ANN002, ANN003
        kwargs["headers"] = self._csrf_headers(kwargs.get("headers"))
        return self._inner.put(*args, **kwargs)

    def delete(self, *args, **kwargs):  # noqa: ANN002, ANN003
        kwargs["headers"] = self._csrf_headers(kwargs.get("headers"))
        return self._inner.delete(*args, **kwargs)

    def patch(self, *args, **kwargs):  # noqa: ANN002, ANN003
        kwargs["headers"] = self._csrf_headers(kwargs.get("headers"))
        return self._inner.patch(*args, **kwargs)

    def request(self, method, *args, **kwargs):  # noqa: ANN001, ANN002, ANN003
        if method.upper() in ("POST", "PUT", "DELETE", "PATCH"):
            kwargs["headers"] = self._csrf_headers(kwargs.get("headers"))
        return self._inner.request(method, *args, **kwargs)

    def __getattr__(self, name):  # noqa: ANN001
        return getattr(self._inner, name)


@pytest.fixture(autouse=True)
def _clear_rate_limit():
    """Clear rate limiter state between tests to prevent cross-test interference."""
    from technitium_content_filter.rate_limiter import rate_limiter

    rate_limiter.clear()
    yield
    rate_limiter.clear()


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
    """Factory fixture for creating patched TestClients with CSRF support."""
    _patches: list = []
    _mocks: list = []

    def _factory(config_data=None, *, auth_disabled=True, raise_exceptions=True):
        if config_data is not None:
            tmp_config.write_text(json.dumps(config_data, indent=2))
        services_path = tmp_config.parent / "blocked-services.json"
        from technitium_content_filter.config import _hkdf_sha256

        # Compute derived tokens from the test token
        test_token = "test-token"
        auth_passthrough = _hkdf_sha256(test_token.encode(), b"navbar-auth").hex()
        patches = [
            patch("technitium_content_filter.config.CONFIG_PATH", tmp_config),
            patch("technitium_content_filter.config.BLOCKED_SERVICES_PATH", services_path),
            patch("technitium_content_filter.config.TECHNITIUM_API_TOKEN", test_token),
            patch("technitium_content_filter.config.AUTH_PASSTHROUGH_TOKEN", auth_passthrough),
            patch("technitium_content_filter.config.TECHNITIUM_URL", "http://technitium-mock:5380"),
            patch("technitium_content_filter.config.AUTH_DISABLED", auth_disabled),
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
        from technitium_content_filter.app import app

        inner = TestClient(app, raise_server_exceptions=raise_exceptions)
        return _CSRFClient(inner)

    yield _factory
    for p in reversed(_patches):
        p.stop()
    for m in _mocks:
        m.stop()


@pytest.fixture()
def client(make_client, sample_config):
    """TestClient with patched module globals."""
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
