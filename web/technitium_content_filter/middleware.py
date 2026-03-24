from __future__ import annotations

import json as _json
from typing import Any

from . import config
from .rate_limiter import rate_limiter


class _ASGIResponse:
    """Minimal ASGI JSON response for use in raw middleware."""

    def __init__(self, content: dict[str, Any], status_code: int = 200) -> None:
        self._body = _json.dumps(content).encode()
        self._status = status_code

    async def __call__(self, scope: Any, receive: Any, send: Any) -> None:
        await send(
            {
                "type": "http.response.start",
                "status": self._status,
                "headers": [
                    [b"content-type", b"application/json"],
                    [b"content-length", str(len(self._body)).encode()],
                ],
            }
        )
        await send({"type": "http.response.body", "body": self._body})


class RequestSizeLimitMiddleware:
    """Reject request bodies over MAX_REQUEST_BODY bytes (#43)."""

    def __init__(self, app: Any) -> None:
        self.app = app

    async def __call__(self, scope: Any, receive: Any, send: Any) -> None:
        if scope["type"] == "http":
            headers = dict(scope.get("headers", []))
            content_length = headers.get(b"content-length", b"0")
            try:
                if int(content_length) > config.MAX_REQUEST_BODY:
                    response = _ASGIResponse({"ok": False, "error": "Request body too large"}, 413)
                    await response(scope, receive, send)
                    return
            except (ValueError, TypeError):
                pass
        await self.app(scope, receive, send)


class RateLimitMiddleware:
    """Simple per-IP rate limiting for API endpoints (#54)."""

    def __init__(self, app: Any) -> None:
        self.app = app

    async def __call__(self, scope: Any, receive: Any, send: Any) -> None:
        if scope["type"] == "http" and scope["path"].startswith("/api/"):
            client_addr = scope.get("client")
            client_ip = client_addr[0] if client_addr else "unknown"
            if not rate_limiter.check(client_ip):
                response = _ASGIResponse({"ok": False, "error": "Rate limit exceeded"}, 429)
                await response(scope, receive, send)
                return
        await self.app(scope, receive, send)
