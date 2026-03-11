from __future__ import annotations

import asyncio
import hashlib
import ipaddress
import json
import logging
import os
import secrets
import time
from collections import defaultdict
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from datetime import datetime
from pathlib import Path
from typing import Any, cast
from urllib.parse import urlparse
from zoneinfo import ZoneInfo

import httpx
from mako.lookup import TemplateLookup
from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.middleware.sessions import SessionMiddleware
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, RedirectResponse, Response
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
_VALID_CONFIG_KEYS = frozenset(
    {
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
    }
)

# #54: Simple rate limiter state
_rate_limit_buckets: dict[str, list[float]] = defaultdict(list)
RATE_LIMIT_MAX = int(os.environ.get("RATE_LIMIT_MAX", "300"))
RATE_LIMIT_WINDOW = 60.0  # seconds

# #103: Authentication config
SESSION_EXPIRY = int(os.environ.get("SESSION_EXPIRY", "86400"))  # 24 hours
AUTH_DISABLED = os.environ.get("AUTH_DISABLED", "").lower() in ("1", "true", "yes")
LOGIN_RATE_LIMIT = 10  # max login attempts per minute per IP


def _get_session_secret() -> str:
    env_secret = os.environ.get("SESSION_SECRET", "")
    if env_secret:
        return env_secret
    if TECHNITIUM_API_TOKEN:
        return hashlib.sha256(f"content-filter-session:{TECHNITIUM_API_TOKEN}".encode()).hexdigest()
    return secrets.token_hex(32)


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
    defaults: list[JsonObj] = _validate_json_obj_list(json.loads(defaults_path.read_text()))
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


# #103: Auth paths that don't require authentication
_PUBLIC_PATHS = frozenset({"/login"})
_PUBLIC_PREFIXES = ("/static/",)


class AuthMiddleware:
    """Require authentication for all routes except login and static files (#103)."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or AUTH_DISABLED:
            await self.app(scope, receive, send)
            return

        path: str = scope["path"]
        if path in _PUBLIC_PATHS or any(path.startswith(p) for p in _PUBLIC_PREFIXES):
            await self.app(scope, receive, send)
            return

        session: dict[str, Any] = scope.get("session", {})
        user = session.get("user")
        login_time: float = session.get("login_time", 0)

        if not user or (time.time() - login_time > SESSION_EXPIRY):
            if "session" in scope:
                scope["session"].clear()
            if path.startswith("/api/"):
                resp: Response = JSONResponse(
                    {"ok": False, "error": "Authentication required"},
                    status_code=401,
                )
            else:
                base = BASE_PATH.rstrip("/")
                resp = RedirectResponse(url=f"{base}/login", status_code=302)
            await resp(scope, receive, send)
            return

        await self.app(scope, receive, send)


# --- Auth Routes ---


async def login_page(request: Request) -> HTMLResponse:
    if not AUTH_DISABLED and request.session.get("user"):
        base = BASE_PATH.rstrip("/")
        return RedirectResponse(url=f"{base}/", status_code=302)  # type: ignore[return-value]
    return render("login.html", error="")


async def login_submit(request: Request) -> HTMLResponse:
    form = await request.form()
    username = str(form.get("username", "")).strip()
    password = str(form.get("password", ""))

    if not username or not password:
        return render("login.html", error="Username and password are required")

    # Rate limit login attempts
    client_addr = request.scope.get("client")
    client_ip = client_addr[0] if client_addr else "unknown"
    login_bucket_key = f"login:{client_ip}"
    now = time.monotonic()
    bucket = _rate_limit_buckets[login_bucket_key]
    cutoff = now - RATE_LIMIT_WINDOW
    while bucket and bucket[0] < cutoff:
        bucket.pop(0)
    if len(bucket) >= LOGIN_RATE_LIMIT:
        return render("login.html", error="Too many login attempts. Please wait.")

    bucket.append(now)

    # Validate against Technitium DNS Server
    client = _http_client
    if client is None:
        client = httpx.AsyncClient(timeout=10.0)
        should_close = True
    else:
        should_close = False
    try:
        resp = await client.get(
            f"{TECHNITIUM_URL}/api/user/login",
            params={"user": username, "pass": password},
        )
        result = resp.json()
        if result.get("status") != "ok":
            error_msg = result.get("errorMessage", "Invalid credentials")
            return render("login.html", error=error_msg)
    except httpx.HTTPError:
        return render("login.html", error="Cannot reach DNS server. Please try again.")
    except (json.JSONDecodeError, KeyError):
        return render("login.html", error="Unexpected response from DNS server")
    finally:
        if should_close:
            await client.aclose()

    # Login successful
    request.session["user"] = username
    request.session["login_time"] = time.time()
    base = BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/", status_code=302)  # type: ignore[return-value]


async def logout(request: Request) -> RedirectResponse:
    request.session.clear()
    base = BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/login", status_code=302)


# --- Page Routes ---


async def dashboard(request: Request) -> HTMLResponse:
    config = load_config()
    services = load_blocked_services()

    # #57: Move dashboard stat computation from template into route handler
    all_clients = _as_list(config.get("clients") or [])
    profiles_dict = config.get("profiles")
    profiles_map = _as_obj(profiles_dict) if isinstance(profiles_dict, dict) else {}

    protected_clients = [
        c
        for c in all_clients
        if isinstance(c, dict) and c.get("profile") and c["profile"] in profiles_map
    ]
    unprotected_clients = [
        c
        for c in all_clients
        if isinstance(c, dict) and (not c.get("profile") or c["profile"] not in profiles_map)
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
            total_custom_rules += len(
                [
                    r
                    for r in rules
                    if isinstance(r, str) and r.strip() and not r.strip().startswith("#")
                ]
            )
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


# --- Domain Test Helpers (#118) ---

_DAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


def _domain_matches(domains: set[str], query: str) -> str | None:
    """Subdomain-walking match, mirroring C# DomainMatcher.Matches."""
    trimmed = query.rstrip(".").lower()
    current = trimmed
    while True:
        if current in domains:
            return current
        dot = current.find(".")
        if dot < 0 or dot == len(current) - 1:
            break
        current = current[dot + 1 :]
    return None


def _rewrite_matches(rewrites: dict[str, str], query: str) -> tuple[str, str] | None:
    """Subdomain-walking rewrite lookup, returns (matched_domain, answer) or None."""
    trimmed = query.rstrip(".").lower()
    current = trimmed
    while True:
        if current in rewrites:
            return (current, rewrites[current])
        dot = current.find(".")
        if dot < 0 or dot == len(current) - 1:
            break
        current = current[dot + 1 :]
    return None


def _resolve_client_profile(config: JsonObj, client_ip: str) -> tuple[str | None, str | None, str]:
    """Resolve client IP to (profile_name, client_name, method)."""
    clients = _as_list(config.get("clients") or [])
    ip = ipaddress.ip_address(client_ip)

    # Priority 1: Exact IP match
    for client in clients:
        if not isinstance(client, dict):
            continue
        for cid in _as_list(client.get("ids") or []):
            cid_str = _as_str(cid)
            if "/" not in cid_str:
                try:
                    if ip == ipaddress.ip_address(cid_str):
                        return (
                            _as_str(client.get("profile", "")),
                            _as_str(client.get("name", "")),
                            f"exact IP match ({cid_str})",
                        )
                except ValueError:
                    continue

    # Priority 2: CIDR longest prefix
    best_profile: str | None = None
    best_name: str | None = None
    best_prefix = -1
    best_cidr = ""
    for client in clients:
        if not isinstance(client, dict):
            continue
        for cid in _as_list(client.get("ids") or []):
            cid_str = _as_str(cid)
            if "/" in cid_str:
                try:
                    network = ipaddress.ip_network(cid_str, strict=False)
                    if ip in network and network.prefixlen > best_prefix:
                        best_profile = _as_str(client.get("profile", ""))
                        best_name = _as_str(client.get("name", ""))
                        best_prefix = network.prefixlen
                        best_cidr = cid_str
                except ValueError:
                    continue

    if best_profile is not None:
        return (best_profile, best_name, f"CIDR match ({best_cidr})")

    # Priority 3: Default profile
    default = _as_str(config.get("defaultProfile", "") or "")
    if default:
        return (default, None, "default profile")

    return (None, None, "no match")


def _check_schedule_active(profile: JsonObj, config: JsonObj) -> tuple[bool, str]:
    """Check if blocking is active now for the profile's schedule."""
    schedule = profile.get("schedule")
    if not schedule or not isinstance(schedule, dict) or len(schedule) == 0:
        return (True, "no schedule configured (always active)")

    tz_str = _as_str(config.get("timeZone", "UTC") or "UTC")
    try:
        tz = ZoneInfo(tz_str)
    except (KeyError, ValueError):
        tz = ZoneInfo("UTC")

    now = datetime.now(tz)
    day_key = _DAY_KEYS[now.weekday()]

    window = schedule.get(day_key)
    if not window or not isinstance(window, dict):
        return (True, f"no schedule entry for {day_key} (active by default)")

    schedule_all_day = bool(config.get("scheduleAllDay", True))
    if schedule_all_day or window.get("allDay"):
        return (True, f"schedule active all day on {day_key}")

    start_str = _as_str(window.get("start", ""))
    end_str = _as_str(window.get("end", ""))
    if not start_str or not end_str:
        return (True, "schedule window missing start/end (active by default)")

    current_minutes = now.hour * 60 + now.minute
    sh, sm = (int(x) for x in start_str.split(":"))
    eh, em = (int(x) for x in end_str.split(":"))
    start_min = sh * 60 + sm
    end_min = eh * 60 + em

    if start_min <= end_min:
        active = start_min <= current_minutes <= end_min
    else:
        active = current_minutes >= start_min or current_minutes <= end_min

    time_now = now.strftime("%H:%M")
    if active:
        return (True, f"within schedule window {start_str}-{end_str} (now: {time_now} {tz_str})")
    return (False, f"outside schedule window {start_str}-{end_str} (now: {time_now} {tz_str})")


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


# --- Domain Test Endpoint (#118) ---


async def api_test_domain(request: Request) -> JSONResponse:
    """Simulate the filtering pipeline for a domain, optionally from a client IP."""
    data = _validate_json_obj(await request.json())
    domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
    if not domain:
        return JSONResponse({"ok": False, "error": "Domain is required"}, status_code=400)

    client_ip = _as_str(data.get("clientIp", "")).strip()
    if client_ip:
        try:
            ipaddress.ip_address(client_ip)
        except ValueError:
            return JSONResponse(
                {"ok": False, "error": f"Invalid IP address: {client_ip}"},
                status_code=400,
            )

    config = load_config()
    services = load_blocked_services()
    custom_services = config.get("customServices") or {}
    steps: list[dict[str, str]] = []

    # Step 1: Global blocking check
    if not config.get("enableBlocking", True):
        steps.append(
            {
                "step": "Blocking enabled",
                "result": "ALLOW",
                "detail": "Global blocking is disabled",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "ALLOW",
                "profile": None,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )
    steps.append(
        {
            "step": "Blocking enabled",
            "result": "PASS",
            "detail": "Global blocking is active",
        }
    )

    # Step 2: Resolve client to profile
    profile_name: str | None = None
    client_name: str | None = None
    method = ""
    if client_ip:
        profile_name, client_name, method = _resolve_client_profile(config, client_ip)
        client_detail = f"{method}"
        if client_name:
            client_detail += f" - client: {client_name}"
        if profile_name:
            steps.append(
                {
                    "step": "Client resolution",
                    "result": "PASS",
                    "detail": f'Resolved to profile "{profile_name}" via {client_detail}',
                }
            )
        else:
            steps.append(
                {
                    "step": "Client resolution",
                    "result": "PASS",
                    "detail": f"No profile resolved ({method})",
                }
            )
    else:
        default_profile = _as_str(config.get("defaultProfile", "") or "")
        if default_profile:
            profile_name = default_profile
            steps.append(
                {
                    "step": "Client resolution",
                    "result": "PASS",
                    "detail": f'No client IP provided, using default profile "{default_profile}"',
                }
            )
        else:
            steps.append(
                {
                    "step": "Client resolution",
                    "result": "PASS",
                    "detail": "No client IP provided, no default profile set",
                }
            )

    # Step 3: Profile lookup / base profile fallback
    base_profile_name = _as_str(config.get("baseProfile", "") or "")
    profiles = config.get("profiles") or {}
    if not isinstance(profiles, dict):
        profiles = {}

    if not profile_name and base_profile_name:
        profile_name = base_profile_name
        steps.append(
            {
                "step": "Profile fallback",
                "result": "PASS",
                "detail": f'Using base profile "{base_profile_name}"',
            }
        )
    elif not profile_name:
        steps.append(
            {
                "step": "Profile fallback",
                "result": "ALLOW",
                "detail": "No profile assigned and no base profile configured",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "ALLOW",
                "profile": None,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )
    else:
        steps.append(
            {
                "step": "Profile fallback",
                "result": "PASS",
                "detail": f'Using profile "{profile_name}"',
            }
        )

    profile = profiles.get(profile_name) if isinstance(profiles, dict) else None
    if not profile or not isinstance(profile, dict):
        steps.append(
            {
                "step": "Profile lookup",
                "result": "ALLOW",
                "detail": f'Profile "{profile_name}" not found in config',
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "ALLOW",
                "profile": profile_name,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )

    # Build merged sets (profile + base profile)
    base_profile = (
        profiles.get(base_profile_name)
        if base_profile_name and base_profile_name != profile_name and isinstance(profiles, dict)
        else None
    )

    # Build rewrites dict
    rewrites: dict[str, str] = {}
    if base_profile and isinstance(base_profile, dict):
        for rw in _as_list(base_profile.get("dnsRewrites") or []):
            if isinstance(rw, dict):
                d = _as_str(rw.get("domain", "")).lower().rstrip(".")
                if d:
                    rewrites[d] = _as_str(rw.get("answer", ""))
    for rw in _as_list(profile.get("dnsRewrites") or []):
        if isinstance(rw, dict):
            d = _as_str(rw.get("domain", "")).lower().rstrip(".")
            if d:
                rewrites[d] = _as_str(rw.get("answer", ""))

    # Step 4: DNS rewrite check
    rw_match = _rewrite_matches(rewrites, domain)
    if rw_match:
        steps.append(
            {
                "step": "DNS rewrite",
                "result": "REWRITE",
                "detail": f"Domain matches rewrite: {rw_match[0]} -> {rw_match[1]}",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "REWRITE",
                "profile": profile_name,
                "rewriteAnswer": rw_match[1],
                "steps": steps,
            }
        )
    steps.append(
        {
            "step": "DNS rewrite",
            "result": "PASS",
            "detail": (
                f"No rewrite match ({len(rewrites)} rewrite"
                f"{'s' if len(rewrites) != 1 else ''} checked)"
            ),
        }
    )

    # Build allowlist
    allowed: set[str] = set()
    if base_profile and isinstance(base_profile, dict):
        for al_entry in _as_list(base_profile.get("allowList") or []):
            allowed.add(_as_str(al_entry).lower().rstrip("."))
    for al_entry in _as_list(profile.get("allowList") or []):
        allowed.add(_as_str(al_entry).lower().rstrip("."))
    # Also add @@-prefixed custom rules as allows
    for rule_src in [profile, base_profile]:
        if rule_src and isinstance(rule_src, dict):
            for rule in _as_list(rule_src.get("customRules") or []):
                r = _as_str(rule).strip()
                if r.startswith("@@"):
                    allowed.add(r[2:].lower().rstrip("."))

    # Step 5: Allowlist check
    allow_match = _domain_matches(allowed, domain)
    if allow_match:
        steps.append(
            {
                "step": "Allowlist",
                "result": "ALLOW",
                "detail": f"Domain matches allowlist entry: {allow_match}",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "ALLOW",
                "profile": profile_name,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )
    steps.append(
        {
            "step": "Allowlist",
            "result": "PASS",
            "detail": (
                f"No allowlist match ({len(allowed)} entr"
                f"{'ies' if len(allowed) != 1 else 'y'} checked)"
            ),
        }
    )

    # Step 6: Schedule check
    schedule_active, schedule_detail = _check_schedule_active(profile, config)
    if not schedule_active:
        steps.append(
            {
                "step": "Schedule",
                "result": "ALLOW",
                "detail": f"Blocking inactive: {schedule_detail}",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "ALLOW",
                "profile": profile_name,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )
    steps.append(
        {
            "step": "Schedule",
            "result": "PASS",
            "detail": schedule_detail,
        }
    )

    # Step 7: Build blocked domains (services + custom rules)
    blocked: set[str] = set()
    blocklist_urls: list[str] = []

    for src in [profile, base_profile]:
        if not src or not isinstance(src, dict):
            continue
        # Blocked services -> expand to domains
        for svc_id in _as_list(src.get("blockedServices") or []):
            svc_id_str = _as_str(svc_id)
            # Check built-in services
            svc = services.get(svc_id_str) if isinstance(services, dict) else None
            if svc and isinstance(svc, dict):
                for sd in _as_list(svc.get("domains") or []):
                    blocked.add(_as_str(sd).lower())
            # Check custom services
            cs = custom_services.get(svc_id_str) if isinstance(custom_services, dict) else None
            if cs and isinstance(cs, dict):
                for sd in _as_list(cs.get("domains") or []):
                    blocked.add(_as_str(sd).lower())
        # Custom rules (non-@@ ones)
        for rule in _as_list(src.get("customRules") or []):
            r = _as_str(rule).strip()
            if r and not r.startswith("@@") and not r.startswith("#"):
                blocked.add(r.lower().rstrip("."))
        # Collect blocklist URLs
        for bl in _as_list(src.get("blockLists") or []):
            bl_str = _as_str(bl)
            if bl_str and bl_str not in blocklist_urls:
                blocklist_urls.append(bl_str)

    block_match = _domain_matches(blocked, domain)
    if block_match:
        steps.append(
            {
                "step": "Block check",
                "result": "BLOCK",
                "detail": f"Domain matches blocked entry: {block_match}",
            }
        )
        return JSONResponse(
            {
                "ok": True,
                "verdict": "BLOCK",
                "profile": profile_name,
                "rewriteAnswer": None,
                "steps": steps,
            }
        )

    blocklist_note = ""
    if blocklist_urls:
        blocklist_note = (
            f" Note: {len(blocklist_urls)} remote blocklist(s) assigned but"
            " not checked (only available in DNS plugin memory)"
        )
    steps.append(
        {
            "step": "Block check",
            "result": "PASS",
            "detail": (
                f"No match in {len(blocked)} blocked domain(s) from services/rules.{blocklist_note}"
            ),
        }
    )

    # Step 8: Default allow
    steps.append(
        {
            "step": "Default",
            "result": "ALLOW",
            "detail": "No rules matched, domain is allowed",
        }
    )
    return JSONResponse(
        {
            "ok": True,
            "verdict": "ALLOW",
            "profile": profile_name,
            "rewriteAnswer": None,
            "steps": steps,
            "blocklistUrls": blocklist_urls if blocklist_urls else None,
        }
    )


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
    Route("/login", login_page, methods=["GET"]),
    Route("/login", login_submit, methods=["POST"]),
    Route("/logout", logout, methods=["POST"]),
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
        Middleware(SessionMiddleware, secret_key=_get_session_secret(), max_age=SESSION_EXPIRY),
        Middleware(AuthMiddleware),
        Middleware(CSRFMiddleware),
        Middleware(RateLimitMiddleware),
    ],
)
