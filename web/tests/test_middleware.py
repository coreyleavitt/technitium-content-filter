"""Tests for middleware: CSRF, request size limit, rate limiting."""

import time

import pytest


@pytest.mark.api
class TestCSRFProtection:
    def test_post_without_csrf_token_rejected(self, client):
        resp = client._inner.post("/api/config", json={"enableBlocking": True})
        assert resp.status_code == 403

    def test_post_with_valid_csrf_token_accepted(self, client):
        resp = client.post("/api/config", json={"enableBlocking": True})
        assert resp.status_code == 200

    def test_get_sets_csrf_cookie(self, client):
        resp = client.get("/api/config")
        assert "csrftoken" in resp.cookies or "csrftoken" in client._inner.cookies

    def test_invalid_csrf_token_rejected(self, client):
        resp = client._inner.post(
            "/api/config",
            json={"enableBlocking": True},
            headers={"x-csrftoken": "bad-token"},
        )
        assert resp.status_code == 403

    def test_get_request_does_not_require_token(self, client):
        resp = client._inner.get("/api/config")
        assert resp.status_code == 200

    def test_login_excluded_from_csrf(self, client_with_auth):
        resp = client_with_auth._inner.post(
            "/login",
            data={"username": "", "password": ""},
        )
        # Should get 200 (rendered form with error), not 403
        assert resp.status_code == 200


@pytest.mark.api
class TestRequestSizeLimitMiddleware:
    def test_oversized_request_rejected(self, client):
        resp = client.post(
            "/api/config",
            content=b"x" * 2_000_000,
            headers={"content-type": "application/json", "content-length": "2000000"},
        )
        assert resp.status_code == 413
        assert "too large" in resp.json()["error"]

    def test_normal_request_passes(self, client):
        resp = client.post("/api/config", json={"enableBlocking": True})
        assert resp.status_code == 200


@pytest.mark.api
class TestRateLimitMiddleware:
    def test_rate_limit_exceeded(self, client):
        from technitium_content_filter.rate_limiter import rate_limiter

        # TestClient uses "testclient" as client IP
        bucket_key = "testclient"
        now = time.monotonic()
        rate_limiter.buckets[bucket_key] = [now] * (rate_limiter.max_requests + 1)

        resp = client.get("/api/config")
        assert resp.status_code == 429
        assert "Rate limit" in resp.json()["error"]

    def test_under_rate_limit_passes(self, client):
        resp = client.get("/api/config")
        assert resp.status_code == 200

    def test_old_entries_expire(self, client):
        from technitium_content_filter.rate_limiter import rate_limiter

        bucket_key = "testclient"
        old_time = time.monotonic() - rate_limiter.window - 10
        rate_limiter.buckets[bucket_key] = [old_time] * (rate_limiter.max_requests + 1)

        resp = client.get("/api/config")
        assert resp.status_code == 200
