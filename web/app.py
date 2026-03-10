from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from collections import defaultdict
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, cast
from urllib.parse import urlparse

import httpx
from mako.lookup import TemplateLookup
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, RedirectResponse
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles
from starlette.types import ASGIApp, Receive, Scope, Send

type JsonValue = str | int | float | bool | None | list[JsonValue] | dict[str, JsonValue]
type JsonObj = dict[str, JsonValue]

CONFIG_PATH = Path(os.environ.get("CONFIG_PATH", "/data/dnsApp.config"))
TECHNITIUM_URL = os.environ.get("TECHNITIUM_URL", "http://technitium:5380")
APP_NAME = os.environ.get("APP_NAME", "ContentFilter")
BASE_PATH = os.environ.get("BASE_PATH", "/")
MAX_REQUEST_BODY = 1_048_576  # 1 MB (#43)

logger = logging.getLogger("parental-controls")

# #39: asyncio lock for config read-modify-write
config_lock = asyncio.Lock()

# #42: Module-level httpx client, initialized in lifespan
_http_client: httpx.AsyncClient | None = None

# #40: Allowlist of valid top-level config keys
_VALID_CONFIG_KEYS = frozenset({
    "enableBlocking",
    "profiles",
    "clients",
    "defaultProfile",
    "baseProfile",
    "timeZone",
    "scheduleAllDay",
    "customServices",
    "blockLists",
    "_blockListsSeeded",
    "settings",
})

# #54: Simple rate limiter state
_rate_limit_buckets: dict[str, list[float]] = defaultdict(list)
RATE_LIMIT_MAX = int(os.environ.get("RATE_LIMIT_MAX", "300"))
RATE_LIMIT_WINDOW = 60.0  # seconds


def _read_api_token() -> str:
    token_file = os.environ.get("TECHNITIUM_API_TOKEN_FILE")
    if token_file and Path(token_file).exists():
        return Path(token_file).read_text().strip()
    return os.environ.get("TECHNITIUM_API_TOKEN", "")


TECHNITIUM_API_TOKEN = _read_api_token()

# #53: Derive BLOCKED_SERVICES_PATH consistently from CONFIG_PATH
BLOCKED_SERVICES_PATH = Path(
    os.environ.get("BLOCKED_SERVICES_PATH", str(CONFIG_PATH.parent / "blocked-services.json"))
)

# #50: Enable default HTML escaping in Mako templates
templates = TemplateLookup(
    directories=[str(Path(__file__).parent / "templates")],
    input_encoding="utf-8",
    default_filters=["h"],
)


def _as_obj(val: JsonValue) -> JsonObj:
    """Narrow a JsonValue to JsonObj. Raises TypeError if not a dict."""
    if isinstance(val, dict):
        return val
    raise TypeError(f"Expected dict, got {type(val).__name__}")


def _as_list(val: JsonValue) -> list[JsonValue]:
    """Narrow a JsonValue to list. Raises TypeError if not a list."""
    if isinstance(val, list):
        return val
    raise TypeError(f"Expected list, got {type(val).__name__}")


def _as_str(val: JsonValue) -> str:
    """Narrow a JsonValue to str, with fallback to empty string."""
    return val if isinstance(val, str) else ""


def _norm_domain(rw: JsonObj) -> str:
    """Normalize a rewrite entry's domain for comparison."""
    return _as_str(rw.get("domain", "")).lower().rstrip(".")


def _validate_json_obj(data: object) -> JsonObj:
    """Validate that data is a JSON object (dict). Raises TypeError otherwise."""
    if not isinstance(data, dict):
        raise TypeError(f"Expected JSON object, got {type(data).__name__}")
    return cast(JsonObj, data)


def _migrate_blocklists(config: JsonObj) -> bool:
    """Migrate per-profile BlockListConfig objects to global blockLists + URL strings."""
    migrated = False
    global_lists = _as_list(config.setdefault("blockLists", []))
    global_urls: set[str] = {
        _as_str(bl["url"]) for bl in global_lists if isinstance(bl, dict) and bl.get("url")
    }

    profiles = config.get("profiles")
    if not isinstance(profiles, dict):
        return False

    for profile_val in profiles.values():
        profile = _as_obj(profile_val)
        bls = profile.get("blockLists")
        if not isinstance(bls, list):
            continue
        new_bls: list[JsonValue] = []
        for entry in bls:
            if isinstance(entry, dict):
                url = _as_str(entry.get("url", ""))
                if url and url not in global_urls:
                    global_lists.append(entry)
                    global_urls.add(url)
                if url:
                    new_bls.append(url)
                migrated = True
            elif isinstance(entry, str):
                new_bls.append(entry)
        profile["blockLists"] = new_bls

    return migrated


def _seed_default_blocklists(config: JsonObj) -> bool:
    """Seed default blocklists (all disabled) on first run."""
    if config.get("_blockListsSeeded"):
        return False
    defaults_path = CONFIG_PATH.parent / "default-blocklists.json"
    if not defaults_path.exists():
        return False
    raw_blocklists = config.get("blockLists")
    existing_urls: set[str] = {
        _as_str(bl["url"])
        for bl in (raw_blocklists if isinstance(raw_blocklists, list) else [])
        if isinstance(bl, dict)
    }
    defaults: list[JsonObj] = _validate_json_obj_list(
        json.loads(defaults_path.read_text())
    )
    new_lists: list[JsonValue] = [
        bl for bl in defaults if _as_str(bl.get("url", "")) not in existing_urls
    ]
    blocklists = config.get("blockLists")
    if isinstance(blocklists, list):
        blocklists.extend(new_lists)
    else:
        config["blockLists"] = new_lists
    config["_blockListsSeeded"] = True
    return True


def _validate_json_obj_list(data: object) -> list[JsonObj]:
    """Validate that data is a list of dicts."""
    if not isinstance(data, list):
        raise TypeError(f"Expected list, got {type(data).__name__}")
    result: list[JsonObj] = []
    for item in data:
        if isinstance(item, dict):
            result.append(item)
    return result


def load_config() -> JsonObj:
    if CONFIG_PATH.exists():
        config = _validate_json_obj(json.loads(CONFIG_PATH.read_text()))
        changed = _migrate_blocklists(config)
        changed = _seed_default_blocklists(config) or changed
        if changed:
            save_config(config)
        return config
    default_config: JsonObj = {
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
    _seed_default_blocklists(default_config)
    return default_config


def save_config(config: JsonObj) -> None:
    """Atomically write config to disk. Raises OSError on failure (#52)."""
    tmp = CONFIG_PATH.with_suffix(".tmp")
    try:
        tmp.write_text(json.dumps(config, indent=2))
        tmp.rename(CONFIG_PATH)
    except OSError:
        # Clean up temp file on failure
        tmp.unlink(missing_ok=True)
        raise


async def reload_technitium_config(config: JsonObj) -> bool:
    """Push config to Technitium so the DNS app reloads without restart."""
    if not TECHNITIUM_API_TOKEN:
        return False
    client = _http_client
    # #42: Use shared client if available, fall back to temporary client
    if client is None:
        client = httpx.AsyncClient(timeout=10.0)
        should_close = True
    else:
        should_close = False
    try:
        resp = await client.post(
            f"{TECHNITIUM_URL}/api/apps/config/set",
            data={
                "token": TECHNITIUM_API_TOKEN,
                "name": APP_NAME,
                "config": json.dumps(config, indent=2),
            },
        )
        if resp.status_code != 200:
            logger.warning(
                "Technitium reload failed: status=%d body=%s",
                resp.status_code,
                resp.text[:200],
            )
            return False
        return True
    except httpx.HTTPError as exc:
        logger.warning("Technitium reload error: %s", exc)
        return False
    finally:
        if should_close:
            await client.aclose()


def load_blocked_services() -> JsonObj:
    if BLOCKED_SERVICES_PATH.exists():
        return _validate_json_obj(json.loads(BLOCKED_SERVICES_PATH.read_text()))
    return {}


def render(template_name: str, current: str = "", **kwargs: Any) -> HTMLResponse:
    tmpl = templates.get_template(template_name)
    return HTMLResponse(
        tmpl.render(base_path=BASE_PATH.rstrip("/"), json=json, current=current, **kwargs)
    )


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


# --- Middleware ---

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
                if int(content_length) > MAX_REQUEST_BODY:
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


# --- Page Routes ---


async def dashboard(request: Request) -> HTMLResponse:
    config = load_config()
    services = load_blocked_services()

    # #57: Move dashboard stat computation from template into route handler
    all_clients = _as_list(config.get("clients") or [])
    profiles_dict = config.get("profiles")
    profiles_map = _as_obj(profiles_dict) if isinstance(profiles_dict, dict) else {}

    protected_clients = [
        c for c in all_clients
        if isinstance(c, dict) and c.get("profile") and c["profile"] in profiles_map
    ]
    unprotected_clients = [
        c for c in all_clients
        if isinstance(c, dict)
        and (not c.get("profile") or c["profile"] not in profiles_map)
    ]
    total_blocked_services: set[str] = set()
    total_custom_rules = 0
    total_allow_entries = 0
    total_rewrites = 0
    for p_val in profiles_map.values():
        if not isinstance(p_val, dict):
            continue
        svc_list = p_val.get("blockedServices")
        if isinstance(svc_list, list):
            total_blocked_services.update(str(s) for s in svc_list if isinstance(s, str))
        rules = p_val.get("customRules")
        if isinstance(rules, list):
            total_custom_rules += len([
                r for r in rules
                if isinstance(r, str) and r.strip() and not r.strip().startswith("#")
            ])
        allow = p_val.get("allowList")
        if isinstance(allow, list):
            total_allow_entries += len([a for a in allow if isinstance(a, str) and a.strip()])
        rw = p_val.get("dnsRewrites")
        if isinstance(rw, list):
            total_rewrites += len(rw)

    return render(
        "dashboard.html",
        current="dashboard",
        config=config,
        services=services,
        protected_clients=protected_clients,
        unprotected_clients=unprotected_clients,
        total_blocked_services=total_blocked_services,
        total_custom_rules=total_custom_rules,
        total_allow_entries=total_allow_entries,
        total_rewrites=total_rewrites,
    )


async def profiles_page(request: Request) -> HTMLResponse:
    config = load_config()
    services = load_blocked_services()
    custom = config.get("customServices")
    all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
    return render(
        "profiles.html",
        current="profiles",
        config=config,
        services=all_services,
    )


async def clients_page(request: Request) -> HTMLResponse:
    config = load_config()
    return render(
        "clients.html",
        current="clients",
        config=config,
    )


async def services_redirect(request: Request) -> RedirectResponse:
    base = BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/filters/services", status_code=301)


async def filters_blocklists_page(request: Request) -> HTMLResponse:
    config = load_config()
    return render("filters_blocklists.html", current="filters-blocklists", config=config)


async def filters_allowlists_page(request: Request) -> HTMLResponse:
    config = load_config()
    return render("filters_allowlists.html", current="filters-allowlists", config=config)


async def filters_services_page(request: Request) -> HTMLResponse:
    config = load_config()
    services = load_blocked_services()
    return render(
        "filters_services.html", current="filters-services", config=config, services=services
    )


async def filters_rules_page(request: Request) -> HTMLResponse:
    config = load_config()
    return render("filters_rules.html", current="filters-rules", config=config)


async def filters_rewrites_page(request: Request) -> HTMLResponse:
    config = load_config()
    return render("filters_rewrites.html", current="filters-rewrites", config=config)


# --- API Routes ---


async def api_config_get(request: Request) -> JSONResponse:
    return JSONResponse(load_config())


async def api_config_set(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    # #40: Log unknown config keys for auditing
    unknown_keys = set(data.keys()) - _VALID_CONFIG_KEYS
    if unknown_keys:
        logger.warning("Config set with unknown keys: %s", ", ".join(sorted(unknown_keys)))
    async with config_lock:
        try:
            save_config(data)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        reloaded = await reload_technitium_config(data)
    return JSONResponse({"ok": True, "reloaded": reloaded})


async def api_profile_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    name = _as_str(data.pop("name", ""))
    # #46: Reject whitespace-only profile names (but allow when name key is absent)
    if name and not name.strip():
        return JSONResponse(
            {"ok": False, "error": "Profile name must not be only whitespace"},
            status_code=400,
        )
    name = name.strip()
    async with config_lock:
        config = load_config()
        profiles = _as_obj(config.get("profiles") or {})
        profiles[name] = data
        config["profiles"] = profiles
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_profile_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        name = _as_str(data.get("name", ""))
        profiles = config.get("profiles")
        if isinstance(profiles, dict):
            profiles.pop(name, None)
        # #48: Unassign clients when deleting a profile
        clients = config.get("clients")
        if isinstance(clients, list):
            for client_val in clients:
                if isinstance(client_val, dict) and client_val.get("profile") == name:
                    client_val["profile"] = ""
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_profile_rename(request: Request) -> JSONResponse:
    """Atomically rename a profile and update client assignments."""
    data = _validate_json_obj(await request.json())
    old_name = _as_str(data.get("old_name", "")).strip()
    new_name = _as_str(data.get("new_name", "")).strip()
    if not old_name or not new_name:
        return JSONResponse(
            {"ok": False, "error": "old_name and new_name are required"},
            status_code=400,
        )
    if old_name == new_name:
        return JSONResponse({"ok": True})
    async with config_lock:
        config = load_config()
        profiles = config.get("profiles")
        if not isinstance(profiles, dict) or old_name not in profiles:
            return JSONResponse(
                {"ok": False, "error": f"Profile '{old_name}' not found"},
                status_code=404,
            )
        if new_name in profiles:
            return JSONResponse(
                {"ok": False, "error": f"Profile '{new_name}' already exists"},
                status_code=409,
            )
        # Rename profile key
        profiles[new_name] = profiles.pop(old_name)
        # Update client assignments
        clients = config.get("clients")
        if isinstance(clients, list):
            for client_val in clients:
                if isinstance(client_val, dict) and client_val.get("profile") == old_name:
                    client_val["profile"] = new_name
        # Update defaultProfile/baseProfile references
        if config.get("defaultProfile") == old_name:
            config["defaultProfile"] = new_name
        if config.get("baseProfile") == old_name:
            config["baseProfile"] = new_name
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_client_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        clients = _as_list(config.setdefault("clients", []))
        index = data.get("index")

        client_obj: JsonObj = {
            "name": data.get("name", ""),
            "ids": data.get("ids", []),
            "profile": data.get("profile", ""),
        }

        if isinstance(index, int) and 0 <= index < len(clients):
            clients[index] = client_obj
        else:
            clients.append(client_obj)

        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_client_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        # #56: Use setdefault to get actual list reference
        clients = _as_list(config.setdefault("clients", []))
        index = data.get("index")
        if isinstance(index, int) and 0 <= index < len(clients):
            clients.pop(index)
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_settings_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        config["enableBlocking"] = data.get("enableBlocking", True)
        config["defaultProfile"] = data.get("defaultProfile") or None
        config["baseProfile"] = data.get("baseProfile") or None
        config["timeZone"] = data.get("timeZone", "America/Denver")
        config["scheduleAllDay"] = data.get("scheduleAllDay", True)
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_services_get(request: Request) -> JSONResponse:
    config = load_config()
    services = load_blocked_services()
    custom = config.get("customServices")
    all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
    return JSONResponse(all_services)


async def api_custom_service_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        svc_id = _as_str(data.get("id", ""))
        custom = _as_obj(config.setdefault("customServices", {}))
        custom[svc_id] = {
            "name": data.get("name", svc_id),
            "domains": data.get("domains", []),
        }
        config["customServices"] = custom
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_custom_service_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        custom = config.get("customServices")
        if isinstance(custom, dict):
            svc_id = _as_str(data.get("id", ""))
            custom.pop(svc_id, None)
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


# --- Filter API Routes ---


def _validate_blocklist_url(url: str) -> str | None:
    """Validate blocklist URL scheme. Returns error message or None if valid (#49)."""
    parsed = urlparse(url)
    if parsed.scheme not in ("http", "https"):
        return "Only http:// and https:// URLs are allowed"
    return None


async def api_blocklist_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    url = _as_str(data.get("url", "")).strip()
    if not url:
        return JSONResponse({"ok": False, "error": "URL required"}, status_code=400)
    # #49: Validate URL scheme
    url_error = _validate_blocklist_url(url)
    if url_error:
        return JSONResponse({"ok": False, "error": url_error}, status_code=400)

    async with config_lock:
        config = load_config()
        blocklists = _as_list(config.setdefault("blockLists", []))

        # Update existing or add new
        for bl_val in blocklists:
            if isinstance(bl_val, dict) and bl_val.get("url") == url:
                bl_val["name"] = data.get("name", "")
                bl_val["enabled"] = data.get("enabled", True)
                bl_val["refreshHours"] = data.get("refreshHours", 24)
                break
        else:
            blocklists.append(
                {
                    "url": url,
                    "name": data.get("name", ""),
                    "enabled": data.get("enabled", True),
                    "refreshHours": data.get("refreshHours", 24),
                }
            )

        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_blocklist_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        url = _as_str(data.get("url", ""))
        config["blockLists"] = [
            bl
            for bl in _as_list(config.get("blockLists") or [])
            if not (isinstance(bl, dict) and bl.get("url") == url)
        ]
        # Remove URL references from profiles
        profiles = config.get("profiles")
        if isinstance(profiles, dict):
            for profile_val in profiles.values():
                if isinstance(profile_val, dict):
                    bls = profile_val.get("blockLists")
                    if isinstance(bls, list):
                        profile_val["blockLists"] = [u for u in bls if u != url]
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_blocklist_refresh(request: Request) -> JSONResponse:
    """Trigger Technitium to reload config which starts a blocklist refresh cycle."""
    config = load_config()
    reloaded = await reload_technitium_config(config)
    return JSONResponse({"ok": True, "reloaded": reloaded})


def _get_profile(config: JsonObj, data: JsonObj) -> JsonObj | None:
    """Look up a profile by name from request data. Returns None if not found."""
    profile_name = _as_str(data.get("profile", ""))
    profiles = config.get("profiles")
    if not isinstance(profiles, dict) or profile_name not in profiles:
        return None
    profile = profiles[profile_name]
    return _as_obj(profile) if isinstance(profile, dict) else None


async def api_allowlist_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        profile = _get_profile(config, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        profile["allowList"] = data.get("domains", [])
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rules_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        profile = _get_profile(config, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        profile["customRules"] = data.get("rules", [])
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rewrite_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        profile = _get_profile(config, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        rewrites = _as_list(profile.setdefault("dnsRewrites", []))
        domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
        answer = _as_str(data.get("answer", "")).strip()
        if not domain or not answer:
            return JSONResponse(
                {"ok": False, "error": "Domain and answer required"}, status_code=400
            )

        # Update existing or add new
        for rw_val in rewrites:
            if isinstance(rw_val, dict) and _norm_domain(rw_val) == domain:
                rw_val["domain"] = domain
                rw_val["answer"] = answer
                break
        else:
            rewrites.append({"domain": domain, "answer": answer})

        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rewrite_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config_lock:
        config = load_config()
        profile = _get_profile(config, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
        profile["dnsRewrites"] = [
            rw
            for rw in _as_list(profile.get("dnsRewrites") or [])
            if not (isinstance(rw, dict) and _norm_domain(rw) == domain)
        ]
        try:
            save_config(config)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await reload_technitium_config(config)
    return JSONResponse({"ok": True})


# #42 / #58: Lifespan handler for httpx client lifecycle
@asynccontextmanager
async def lifespan(app_instance: Starlette) -> AsyncIterator[None]:
    global _http_client  # noqa: PLW0603
    _http_client = httpx.AsyncClient(timeout=10.0)
    try:
        yield
    finally:
        await _http_client.aclose()
        _http_client = None


routes = [
    Route("/", dashboard),
    Route("/profiles", profiles_page),
    Route("/clients", clients_page),
    Route("/services", services_redirect),
    Route("/filters/blocklists", filters_blocklists_page),
    Route("/filters/allowlists", filters_allowlists_page),
    Route("/filters/services", filters_services_page),
    Route("/filters/rules", filters_rules_page),
    Route("/filters/rewrites", filters_rewrites_page),
    Route("/api/config", api_config_get, methods=["GET"]),
    Route("/api/config", api_config_set, methods=["POST"]),
    Route("/api/profiles", api_profile_save, methods=["POST"]),
    Route("/api/profiles", api_profile_delete, methods=["DELETE"]),
    Route("/api/profiles/rename", api_profile_rename, methods=["POST"]),
    Route("/api/clients", api_client_save, methods=["POST"]),
    Route("/api/clients", api_client_delete, methods=["DELETE"]),
    Route("/api/settings", api_settings_save, methods=["POST"]),
    Route("/api/services", api_services_get, methods=["GET"]),
    Route("/api/custom-services", api_custom_service_save, methods=["POST"]),
    Route("/api/custom-services", api_custom_service_delete, methods=["DELETE"]),
    Route("/api/blocklists", api_blocklist_save, methods=["POST"]),
    Route("/api/blocklists", api_blocklist_delete, methods=["DELETE"]),
    Route("/api/blocklists/refresh", api_blocklist_refresh, methods=["POST"]),
    Route("/api/allowlists", api_allowlist_save, methods=["POST"]),
    Route("/api/rules", api_rules_save, methods=["POST"]),
    Route("/api/rewrites", api_rewrite_save, methods=["POST"]),
    Route("/api/rewrites", api_rewrite_delete, methods=["DELETE"]),
    Mount("/static", StaticFiles(directory=str(Path(__file__).parent / "static")), name="static"),
]

# #44: Set debug=False, controllable via env var
app = Starlette(
    routes=routes,
    debug=os.environ.get("DEBUG", "").lower() in ("1", "true", "yes"),
    lifespan=lifespan,
    middleware=[
        Middleware(RequestSizeLimitMiddleware),
        Middleware(CSRFMiddleware),
        Middleware(RateLimitMiddleware),
    ],
)
