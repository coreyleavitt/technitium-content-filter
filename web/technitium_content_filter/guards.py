from __future__ import annotations

import hmac
import time
from typing import Any
from urllib.parse import parse_qs

from litestar import Request, Response
from litestar.connection import ASGIConnection
from litestar.handlers import BaseRouteHandler
from litestar.response import Redirect

from . import config


class NotAuthenticatedException(Exception):
    """Raised when authentication is required but not present."""

    def __init__(self, *, is_api: bool = False) -> None:
        self.is_api = is_api
        super().__init__()


class TokenRedirectException(Exception):
    """Raised after successful token auto-login to strip the token from URL."""

    def __init__(self, *, path: str) -> None:
        self.path = path
        super().__init__()


def auth_guard(connection: ASGIConnection[Any, Any, Any, Any], handler: BaseRouteHandler) -> None:
    """Guard that enforces authentication. Opt out with ``opt={"skip_auth": True}``."""
    if config.AUTH_DISABLED:
        return

    # Allow opting out per-handler or per-controller
    if handler.opt.get("skip_auth"):
        return

    path: str = connection.scope["path"]
    # Static files are always accessible
    if path.startswith("/static/"):
        return

    user = connection.session.get("user")
    login_time: float = connection.session.get("login_time", 0)
    authenticated = bool(user) and (time.time() - login_time <= config.SESSION_EXPIRY)

    if not authenticated:
        # Try token auto-login via derived auth passthrough token
        query_string = connection.scope.get("query_string", b"").decode()
        params = parse_qs(query_string)
        token_values = params.get("token", [])
        if (
            token_values
            and config.AUTH_PASSTHROUGH_TOKEN
            and hmac.compare_digest(token_values[0], config.AUTH_PASSTHROUGH_TOKEN)
        ):
            connection.session["user"] = "admin"
            connection.session["login_time"] = time.time()
            raise TokenRedirectException(path=path)

        # Not authenticated -- clear stale session and reject
        connection.session.clear()
        raise NotAuthenticatedException(is_api=path.startswith("/api/"))


def not_authenticated_handler(
    _request: Request[Any, Any, Any], exc: NotAuthenticatedException
) -> Response[Any]:
    base = config.BASE_PATH.rstrip("/")
    if exc.is_api:
        return Response(
            content={"ok": False, "error": "Authentication required"},
            status_code=401,
            media_type="application/json",
        )
    return Redirect(path=f"{base}/login", status_code=302)  # type: ignore[return-value]


def token_redirect_handler(
    _request: Request[Any, Any, Any], exc: TokenRedirectException
) -> Response[Any]:
    base = config.BASE_PATH.rstrip("/")
    return Redirect(path=f"{base}{exc.path}", status_code=302)  # type: ignore[return-value]
