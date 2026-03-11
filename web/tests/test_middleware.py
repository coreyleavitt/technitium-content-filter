"""Tests for middleware: CSRF, request size limit, rate limiting."""

import time

import pytest


@pytest.mark.api
class TestCSRFMiddleware:
    def test_cross_origin_post_rejected(self, client):
        resp = client.post(
            "/api/config",
            json={"enableBlocking": True},
            headers={"origin": "https://evil.com", "host": "localhost"},
        )
        assert resp.status_code == 403
        assert "CSRF" in resp.json()["error"]

    def test_same_origin_post_allowed(self, client):
        resp = client.post(
            "/api/config",
            json={"enableBlocking": True},
            headers={"origin": "http://localhost", "host": "localhost"},
        )
        assert resp.status_code == 200

    def test_no_origin_header_allowed(self, client):
        resp = client.post("/api/config", json={"enableBlocking": True})
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
        import middleware

        # TestClient uses "testclient" as client IP
        bucket_key = "testclient"
        now = time.monotonic()
        middleware._rate_limit_buckets[bucket_key] = [now] * (middleware.RATE_LIMIT_MAX + 1)

        resp = client.get("/api/config")
        assert resp.status_code == 429
        assert "Rate limit" in resp.json()["error"]

    def test_under_rate_limit_passes(self, client):
        resp = client.get("/api/config")
        assert resp.status_code == 200

    def test_old_entries_expire(self, client):
        import middleware

        bucket_key = "testclient"
        old_time = time.monotonic() - middleware.RATE_LIMIT_WINDOW - 10
        middleware._rate_limit_buckets[bucket_key] = [old_time] * (middleware.RATE_LIMIT_MAX + 1)

        resp = client.get("/api/config")
        assert resp.status_code == 200
