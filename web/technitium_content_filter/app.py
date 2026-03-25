from __future__ import annotations

import os
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

import httpx
from litestar import Litestar, Response
from litestar.config.csrf import CSRFConfig
from litestar.exceptions import PermissionDeniedException
from litestar.middleware.session.client_side import CookieBackendConfig
from litestar.static_files import create_static_files_router

from . import config
from .controllers.auth import AuthController
from .controllers.clients import ClientController
from .controllers.pages import PageController, RedirectController
from .controllers.profiles import ProfileController
from .controllers.services import ServiceController
from .controllers.settings import SettingsController
from .guards import (
    NotAuthenticatedException,
    TokenRedirectException,
    auth_guard,
    not_authenticated_handler,
    token_redirect_handler,
)
from .middleware import RateLimitMiddleware, RequestSizeLimitMiddleware


# #42 / #58: Lifespan handler for httpx client lifecycle
@asynccontextmanager
async def lifespan(app_instance: Litestar) -> AsyncIterator[None]:
    config._http_client = httpx.AsyncClient(timeout=10.0)
    try:
        yield
    finally:
        await config._http_client.aclose()
        config._http_client = None


session_config = CookieBackendConfig(
    secret=config._get_session_secret().encode()[:32],
    max_age=config.SESSION_EXPIRY,
)

csrf_config = CSRFConfig(
    secret=config._get_session_secret(),
    cookie_httponly=False,
    cookie_samesite="lax",
)


def _permission_denied_handler(
    _: object, exc: PermissionDeniedException
) -> Response[dict[str, object]]:
    return Response(
        content={"ok": False, "error": str(exc.detail)},
        status_code=403,
        media_type="application/json",
    )


app = Litestar(
    route_handlers=[
        AuthController,
        PageController,
        RedirectController,
        ProfileController,
        ClientController,
        SettingsController,
        ServiceController,
        create_static_files_router(
            path="/static",
            directories=[Path(__file__).parent / "static"],
        ),
    ],
    guards=[auth_guard],
    middleware=[
        RequestSizeLimitMiddleware,
        session_config.middleware,
        RateLimitMiddleware,
    ],
    csrf_config=csrf_config,
    exception_handlers={
        NotAuthenticatedException: not_authenticated_handler,
        TokenRedirectException: token_redirect_handler,
        PermissionDeniedException: _permission_denied_handler,
    },
    lifespan=[lifespan],
    debug=os.environ.get("DEBUG", "").lower() in ("1", "true", "yes"),
)
