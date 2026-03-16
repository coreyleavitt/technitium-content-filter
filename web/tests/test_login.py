"""Tests for login error branches (rate limiting, server errors)."""

import time

import httpx as httpx_mod
import pytest
import respx
from httpx import Response


@pytest.mark.api
class TestLoginRateLimit:
    def test_login_rate_limited(self, client_with_auth):
        from technitium_content_filter import config as config_module
        from technitium_content_filter import middleware

        # TestClient uses "testclient" as client IP
        now = time.monotonic()
        middleware._rate_limit_buckets["login:testclient"] = [now] * (
            config_module.LOGIN_RATE_LIMIT + 1
        )

        resp = client_with_auth.post(
            "/login",
            data={"username": "admin", "password": "pass"},
        )
        assert resp.status_code == 200
        assert "Too many login attempts" in resp.text


@pytest.mark.api
class TestLoginServerErrors:
    def test_dns_server_unreachable(self, client_with_auth):
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                side_effect=httpx_mod.ConnectError("Connection refused")
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            resp = client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "pass"},
            )
        assert resp.status_code == 200
        assert "Cannot reach DNS server" in resp.text

    def test_dns_server_bad_json(self, client_with_auth):
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(200, text="not json")
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            resp = client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "pass"},
            )
        assert resp.status_code == 200
        assert "Unexpected response" in resp.text
