"""Tests for authentication (#103)."""

from unittest.mock import patch

import pytest
import respx
from httpx import Response


@pytest.mark.unit
class TestAuthRedirects:
    """Unauthenticated requests are redirected or rejected."""

    def test_page_redirects_to_login(self, client_with_auth):
        resp = client_with_auth.get("/", follow_redirects=False)
        assert resp.status_code == 302
        assert "/login" in resp.headers["location"]

    def test_profiles_redirects_to_login(self, client_with_auth):
        resp = client_with_auth.get("/profiles", follow_redirects=False)
        assert resp.status_code == 302
        assert "/login" in resp.headers["location"]

    def test_api_returns_401(self, client_with_auth):
        resp = client_with_auth.get("/api/config")
        assert resp.status_code == 401
        data = resp.json()
        assert data["ok"] is False
        assert "Authentication required" in data["error"]

    def test_api_post_returns_401(self, client_with_auth):
        resp = client_with_auth.post(
            "/api/settings",
            json={"enableBlocking": True},
        )
        assert resp.status_code == 401

    def test_static_files_accessible(self, client_with_auth):
        resp = client_with_auth.get("/static/js/common.js")
        assert resp.status_code == 200

    def test_login_page_accessible(self, client_with_auth):
        resp = client_with_auth.get("/login")
        assert resp.status_code == 200
        assert b"Sign in" in resp.content


@pytest.mark.unit
class TestLoginFlow:
    """Login and logout flows."""

    def test_login_success(self, client_with_auth):
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(200, json={"status": "ok", "token": "abc123"})
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            resp = client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "admin"},
                follow_redirects=False,
            )
        assert resp.status_code == 302
        assert resp.headers["location"].endswith("/")

        # Now authenticated -- can access dashboard
        resp = client_with_auth.get("/", follow_redirects=False)
        assert resp.status_code == 200

    def test_login_failure(self, client_with_auth):
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(
                    200, json={"status": "error", "errorMessage": "Invalid password"}
                )
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            resp = client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "wrong"},
            )
        assert resp.status_code == 200
        assert b"Invalid password" in resp.content

    def test_login_empty_fields(self, client_with_auth):
        resp = client_with_auth.post(
            "/login",
            data={"username": "", "password": ""},
        )
        assert resp.status_code == 200
        assert b"required" in resp.content

    def test_login_technitium_unreachable(self, client_with_auth):
        with respx.mock(assert_all_called=False) as mock:
            import httpx as httpx_module

            mock.get("http://technitium-mock:5380/api/user/login").mock(
                side_effect=httpx_module.ConnectError("Connection refused")
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            resp = client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "admin"},
            )
        assert resp.status_code == 200
        assert b"Cannot reach DNS server" in resp.content

    def test_logout(self, client_with_auth):
        # Login first
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(200, json={"status": "ok", "token": "abc123"})
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "admin"},
                follow_redirects=False,
            )

        # Verify authenticated
        resp = client_with_auth.get("/", follow_redirects=False)
        assert resp.status_code == 200

        # Logout
        resp = client_with_auth.post("/logout", follow_redirects=False)
        assert resp.status_code == 302
        assert "/login" in resp.headers["location"]

        # Verify no longer authenticated
        resp = client_with_auth.get("/", follow_redirects=False)
        assert resp.status_code == 302
        assert "/login" in resp.headers["location"]


@pytest.mark.unit
class TestSessionExpiry:
    """Session expiry behavior."""

    def test_expired_session_redirects(self, client_with_auth):
        # Login first
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(200, json={"status": "ok", "token": "abc123"})
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "admin"},
                follow_redirects=False,
            )

        # Expire the session by setting SESSION_EXPIRY to 0
        with patch("config.SESSION_EXPIRY", 0):
            resp = client_with_auth.get("/", follow_redirects=False)
            assert resp.status_code == 302
            assert "/login" in resp.headers["location"]


@pytest.mark.unit
class TestAuthDisabled:
    """When AUTH_DISABLED is set, all routes are accessible."""

    def test_pages_accessible_without_login(self, client):
        # The `client` fixture has AUTH_DISABLED=True
        resp = client.get("/")
        assert resp.status_code == 200

    def test_api_accessible_without_login(self, client):
        resp = client.get("/api/config")
        assert resp.status_code == 200


@pytest.mark.unit
class TestAlreadyAuthenticated:
    """Authenticated user visiting login page gets redirected."""

    def test_login_page_redirects_when_authenticated(self, client_with_auth):
        # Login first
        with respx.mock(assert_all_called=False) as mock:
            mock.get("http://technitium-mock:5380/api/user/login").mock(
                return_value=Response(200, json={"status": "ok", "token": "abc123"})
            )
            mock.post("http://technitium-mock:5380/api/apps/config/set").mock(
                return_value=Response(200, json={"status": "ok"})
            )
            client_with_auth.post(
                "/login",
                data={"username": "admin", "password": "admin"},
                follow_redirects=False,
            )

        resp = client_with_auth.get("/login", follow_redirects=False)
        assert resp.status_code == 302
        assert resp.headers["location"].endswith("/")
