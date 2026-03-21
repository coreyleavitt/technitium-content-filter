from __future__ import annotations

import hmac
import os
import time
from collections import defaultdict
from typing import Any
from urllib.parse import parse_qs, urlparse

from starlette.responses import JSONResponse, RedirectResponse, Response
from starlette.types import ASGIApp, Receive, Scope, Send

from . import config

# #54: Simple rate limiter state
_rate_limit_buckets: dict[str, list[float]] = defaultdict(list)
RATE_LIMIT_MAX = int(os.environ.get("RATE_LIMIT_MAX", "300"))
RATE_LIMIT_WINDOW = 60.0  # seconds


def _check_rate_limit(client_ip: str) -> bool:
    """Return True if request is allowed, False if rate-limited (#54)."""
    now = time.monotonic()
    bucket = _rate_limit_buckets[client_ip]
    # Remove old entries
    cutoff = now - RATE_LIMIT_WINDOW
    while bucket and bucket[0] < cutoff:
        bucket.pop(0)
    if len(bucket) >= RATE_LIMIT_MAX:
        return False
    bucket.append(now)
    return True


class CSRFMiddleware:
    """CSRF protection via Origin header checking for state-changing requests (#47)."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] == "http" and scope["method"] in ("POST", "PUT", "DELETE", "PATCH"):
            headers = dict(scope.get("headers", []))
            host = headers.get(b"host", b"").decode()
            origin = headers.get(b"origin", b"").decode()
            if origin:
                parsed = urlparse(origin)
                origin_host = parsed.netloc or parsed.path
                if origin_host and origin_host != host:
                    response = JSONResponse(
                        {"ok": False, "error": "CSRF validation failed"},
                        status_code=403,
                    )
                    await response(scope, receive, send)
                    return
        await self.app(scope, receive, send)


class RequestSizeLimitMiddleware:
    """Reject request bodies over MAX_REQUEST_BODY bytes (#43)."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] == "http":
            headers = dict(scope.get("headers", []))
            content_length = headers.get(b"content-length", b"0")
            try:
                if int(content_length) > config.MAX_REQUEST_BODY:
                    response = JSONResponse(
                        {"ok": False, "error": "Request body too large"},
                        status_code=413,
                    )
                    await response(scope, receive, send)
                    return
            except (ValueError, TypeError):
                pass
        await self.app(scope, receive, send)


class RateLimitMiddleware:
    """Simple per-IP rate limiting for API endpoints (#54)."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] == "http" and scope["path"].startswith("/api/"):
            client_addr = scope.get("client")
            client_ip = client_addr[0] if client_addr else "unknown"
            if not _check_rate_limit(client_ip):
                response = JSONResponse(
                    {"ok": False, "error": "Rate limit exceeded"},
                    status_code=429,
                )
                await response(scope, receive, send)
                return
        await self.app(scope, receive, send)


# #103: Auth paths that don't require authentication
_PUBLIC_PATHS = frozenset({"/login"})
_PUBLIC_PREFIXES = ("/static/",)


class AuthMiddleware:
    """Require authentication for all routes except login and static files (#103)."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or config.AUTH_DISABLED:
            await self.app(scope, receive, send)
            return

        path: str = scope["path"]
        if path in _PUBLIC_PATHS or any(path.startswith(p) for p in _PUBLIC_PREFIXES):
            await self.app(scope, receive, send)
            return

        session: dict[str, Any] = scope.get("session", {})
        user = session.get("user")
        login_time: float = session.get("login_time", 0)

        # Auto-login via Technitium API token in query string
        if not user or (time.time() - login_time > config.SESSION_EXPIRY):
            query_string = scope.get("query_string", b"").decode()
            params = parse_qs(query_string)
            token_values = params.get("token", [])
            if (
                token_values
                and config.AUTH_PASSTHROUGH_TOKEN
                and hmac.compare_digest(token_values[0], config.AUTH_PASSTHROUGH_TOKEN)
            ):
                if "session" in scope:
                    scope["session"]["user"] = "admin"
                    scope["session"]["login_time"] = time.time()
                # Redirect to strip the token from the URL
                base = config.BASE_PATH.rstrip("/")
                resp: Response = RedirectResponse(url=f"{base}{path}", status_code=302)
                await resp(scope, receive, send)
                return

        if not user or (time.time() - login_time > config.SESSION_EXPIRY):
            if "session" in scope:
                scope["session"].clear()
            if path.startswith("/api/"):
                resp = JSONResponse(
                    {"ok": False, "error": "Authentication required"},
                    status_code=401,
                )
            else:
                base = config.BASE_PATH.rstrip("/")
                resp = RedirectResponse(url=f"{base}/login", status_code=302)
            await resp(scope, receive, send)
            return

        await self.app(scope, receive, send)
