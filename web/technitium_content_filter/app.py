from __future__ import annotations

import os
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

import httpx
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.middleware.sessions import SessionMiddleware
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles

from . import config
from .middleware import (
    AuthMiddleware,
    CSRFMiddleware,
    RateLimitMiddleware,
    RequestSizeLimitMiddleware,
)
from .routes import (
    _redirect_to_profiles,
    _redirect_to_settings,
    api_blocklist_delete,
    api_blocklist_refresh,
    api_blocklist_save,
    api_client_delete,
    api_client_save,
    api_config_get,
    api_config_set,
    api_custom_service_delete,
    api_custom_service_save,
    api_profile_allowlist_save,
    api_profile_delete,
    api_profile_get,
    api_profile_regex_save,
    api_profile_rename,
    api_profile_rewrite_delete,
    api_profile_rewrite_save,
    api_profile_rules_save,
    api_profile_save,
    api_services_get,
    api_settings_save,
    api_test_domain,
    clients_page,
    dashboard,
    login_page,
    login_submit,
    logout,
    profile_detail_page,
    profiles_page,
    settings_page,
)


# #42 / #58: Lifespan handler for httpx client lifecycle
@asynccontextmanager
async def lifespan(app_instance: Starlette) -> AsyncIterator[None]:
    config._http_client = httpx.AsyncClient(timeout=10.0)
    try:
        yield
    finally:
        await config._http_client.aclose()
        config._http_client = None


routes = [
    Route("/login", login_page, methods=["GET"]),
    Route("/login", login_submit, methods=["POST"]),
    Route("/logout", logout, methods=["POST"]),
    Route("/", dashboard),
    Route("/profiles", profiles_page),
    Route("/profiles/{name:path}", profile_detail_page),
    Route("/clients", clients_page),
    Route("/settings", settings_page),
    # Legacy redirects
    Route("/services", _redirect_to_settings),
    Route("/filters/blocklists", _redirect_to_settings),
    Route("/filters/allowlists", _redirect_to_profiles),
    Route("/filters/services", _redirect_to_settings),
    Route("/filters/rules", _redirect_to_profiles),
    Route("/filters/regex", _redirect_to_profiles),
    Route("/filters/rewrites", _redirect_to_profiles),
    # API routes
    Route("/api/config", api_config_get, methods=["GET"]),
    Route("/api/config", api_config_set, methods=["POST"]),
    Route("/api/profiles", api_profile_save, methods=["POST"]),
    Route("/api/profiles", api_profile_delete, methods=["DELETE"]),
    Route("/api/profiles/rename", api_profile_rename, methods=["POST"]),
    Route("/api/profiles/{name:path}/allowlist", api_profile_allowlist_save, methods=["POST"]),
    Route("/api/profiles/{name:path}/rules", api_profile_rules_save, methods=["POST"]),
    Route("/api/profiles/{name:path}/regex", api_profile_regex_save, methods=["POST"]),
    Route("/api/profiles/{name:path}/rewrites", api_profile_rewrite_save, methods=["POST"]),
    Route("/api/profiles/{name:path}/rewrites", api_profile_rewrite_delete, methods=["DELETE"]),
    Route("/api/profiles/{name:path}", api_profile_get, methods=["GET"]),
    Route("/api/clients", api_client_save, methods=["POST"]),
    Route("/api/clients", api_client_delete, methods=["DELETE"]),
    Route("/api/settings", api_settings_save, methods=["POST"]),
    Route("/api/services", api_services_get, methods=["GET"]),
    Route("/api/custom-services", api_custom_service_save, methods=["POST"]),
    Route("/api/custom-services", api_custom_service_delete, methods=["DELETE"]),
    Route("/api/blocklists", api_blocklist_save, methods=["POST"]),
    Route("/api/blocklists", api_blocklist_delete, methods=["DELETE"]),
    Route("/api/blocklists/refresh", api_blocklist_refresh, methods=["POST"]),
    Route("/api/test-domain", api_test_domain, methods=["POST"]),
    Mount("/static", StaticFiles(directory=str(Path(__file__).parent / "static")), name="static"),
]

# #44: Set debug=False, controllable via env var
app = Starlette(
    routes=routes,
    debug=os.environ.get("DEBUG", "").lower() in ("1", "true", "yes"),
    lifespan=lifespan,
    middleware=[
        Middleware(RequestSizeLimitMiddleware),
        Middleware(
            SessionMiddleware,
            secret_key=config._get_session_secret(),
            max_age=config.SESSION_EXPIRY,
        ),
        Middleware(AuthMiddleware),
        Middleware(CSRFMiddleware),
        Middleware(RateLimitMiddleware),
    ],
)
