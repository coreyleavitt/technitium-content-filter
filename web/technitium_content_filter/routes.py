from __future__ import annotations

import ipaddress
import json
import time
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import httpx
from mako.lookup import TemplateLookup
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, RedirectResponse

from . import config, filtering, middleware
from .config import (
    _VALID_CONFIG_KEYS,
    JsonObj,
    _as_list,
    _as_obj,
    _as_str,
    _norm_domain,
    _validate_json_obj,
)

# #50: Enable default HTML escaping in Mako templates
templates = TemplateLookup(
    directories=[str(Path(__file__).parent / "templates")],
    input_encoding="utf-8",
    default_filters=["h"],
)


def render(template_name: str, current: str = "", **kwargs: Any) -> HTMLResponse:
    tmpl = templates.get_template(template_name)
    return HTMLResponse(
        tmpl.render(base_path=config.BASE_PATH.rstrip("/"), json=json, current=current, **kwargs)
    )


# --- Auth Routes ---


async def login_page(request: Request) -> HTMLResponse:
    if not config.AUTH_DISABLED and request.session.get("user"):
        base = config.BASE_PATH.rstrip("/")
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
    bucket = middleware._rate_limit_buckets[login_bucket_key]
    cutoff = now - middleware.RATE_LIMIT_WINDOW
    while bucket and bucket[0] < cutoff:
        bucket.pop(0)
    if len(bucket) >= config.LOGIN_RATE_LIMIT:
        return render("login.html", error="Too many login attempts. Please wait.")

    bucket.append(now)

    # Validate against Technitium DNS Server
    client = config._http_client
    if client is None:
        client = httpx.AsyncClient(timeout=10.0)
        should_close = True
    else:
        should_close = False
    try:
        resp = await client.get(
            f"{config.TECHNITIUM_URL}/api/user/login",
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
    base = config.BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/", status_code=302)  # type: ignore[return-value]


async def logout(request: Request) -> RedirectResponse:
    request.session.clear()
    base = config.BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/login", status_code=302)


# --- Page Routes ---


async def dashboard(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    services = config.load_blocked_services()

    # #57: Move dashboard stat computation from template into route handler
    all_clients = _as_list(cfg.get("clients") or [])
    profiles_dict = cfg.get("profiles")
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
        config=cfg,
        services=services,
        protected_clients=protected_clients,
        unprotected_clients=unprotected_clients,
        total_blocked_services=total_blocked_services,
        total_custom_rules=total_custom_rules,
        total_allow_entries=total_allow_entries,
        total_rewrites=total_rewrites,
    )


async def profiles_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    services = config.load_blocked_services()
    custom = cfg.get("customServices")
    all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
    return render(
        "profiles.html",
        current="profiles",
        config=cfg,
        services=all_services,
    )


async def clients_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    return render(
        "clients.html",
        current="clients",
        config=cfg,
    )


async def services_redirect(request: Request) -> RedirectResponse:
    base = config.BASE_PATH.rstrip("/")
    return RedirectResponse(url=f"{base}/filters/services", status_code=301)


async def filters_blocklists_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    return render("filters_blocklists.html", current="filters-blocklists", config=cfg)


async def filters_allowlists_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    return render("filters_allowlists.html", current="filters-allowlists", config=cfg)


async def filters_services_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    services = config.load_blocked_services()
    return render(
        "filters_services.html", current="filters-services", config=cfg, services=services
    )


async def filters_rules_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    return render("filters_rules.html", current="filters-rules", config=cfg)


async def filters_rewrites_page(request: Request) -> HTMLResponse:
    cfg = config.load_config()
    return render("filters_rewrites.html", current="filters-rewrites", config=cfg)


# --- API Routes ---


async def api_config_get(request: Request) -> JSONResponse:
    return JSONResponse(config.load_config())


async def api_config_set(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    # #40: Log unknown config keys for auditing
    unknown_keys = set(data.keys()) - _VALID_CONFIG_KEYS
    if unknown_keys:
        config.logger.warning("Config set with unknown keys: %s", ", ".join(sorted(unknown_keys)))
    async with config.config_lock:
        try:
            config.save_config(data)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        reloaded = await config.reload_technitium_config(data)
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
    async with config.config_lock:
        cfg = config.load_config()
        profiles = _as_obj(cfg.get("profiles") or {})
        profiles[name] = data
        cfg["profiles"] = profiles
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_profile_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        name = _as_str(data.get("name", ""))
        profiles = cfg.get("profiles")
        if isinstance(profiles, dict):
            profiles.pop(name, None)
        # #48: Unassign clients when deleting a profile
        clients = cfg.get("clients")
        if isinstance(clients, list):
            for client_val in clients:
                if isinstance(client_val, dict) and client_val.get("profile") == name:
                    client_val["profile"] = ""
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
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
    async with config.config_lock:
        cfg = config.load_config()
        profiles = cfg.get("profiles")
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
        clients = cfg.get("clients")
        if isinstance(clients, list):
            for client_val in clients:
                if isinstance(client_val, dict) and client_val.get("profile") == old_name:
                    client_val["profile"] = new_name
        # Update defaultProfile/baseProfile references
        if cfg.get("defaultProfile") == old_name:
            cfg["defaultProfile"] = new_name
        if cfg.get("baseProfile") == old_name:
            cfg["baseProfile"] = new_name
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_client_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        clients = _as_list(cfg.setdefault("clients", []))
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
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_client_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        # #56: Use setdefault to get actual list reference
        clients = _as_list(cfg.setdefault("clients", []))
        index = data.get("index")
        if isinstance(index, int) and 0 <= index < len(clients):
            clients.pop(index)
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_settings_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        cfg["enableBlocking"] = data.get("enableBlocking", True)
        cfg["defaultProfile"] = data.get("defaultProfile") or None
        cfg["baseProfile"] = data.get("baseProfile") or None
        cfg["timeZone"] = data.get("timeZone", "America/Denver")
        cfg["scheduleAllDay"] = data.get("scheduleAllDay", True)
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_services_get(request: Request) -> JSONResponse:
    cfg = config.load_config()
    services = config.load_blocked_services()
    custom = cfg.get("customServices")
    all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
    return JSONResponse(all_services)


async def api_custom_service_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        svc_id = _as_str(data.get("id", ""))
        custom = _as_obj(cfg.setdefault("customServices", {}))
        custom[svc_id] = {
            "name": data.get("name", svc_id),
            "domains": data.get("domains", []),
        }
        cfg["customServices"] = custom
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_custom_service_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        custom = cfg.get("customServices")
        if isinstance(custom, dict):
            svc_id = _as_str(data.get("id", ""))
            custom.pop(svc_id, None)
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
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

    async with config.config_lock:
        cfg = config.load_config()
        blocklists = _as_list(cfg.setdefault("blockLists", []))

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
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_blocklist_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        url = _as_str(data.get("url", ""))
        cfg["blockLists"] = [
            bl
            for bl in _as_list(cfg.get("blockLists") or [])
            if not (isinstance(bl, dict) and bl.get("url") == url)
        ]
        # Remove URL references from profiles
        profiles = cfg.get("profiles")
        if isinstance(profiles, dict):
            for profile_val in profiles.values():
                if isinstance(profile_val, dict):
                    bls = profile_val.get("blockLists")
                    if isinstance(bls, list):
                        profile_val["blockLists"] = [u for u in bls if u != url]
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_blocklist_refresh(request: Request) -> JSONResponse:
    """Trigger Technitium to reload config which starts a blocklist refresh cycle."""
    cfg = config.load_config()
    reloaded = await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True, "reloaded": reloaded})


def _get_profile(cfg: JsonObj, data: JsonObj) -> JsonObj | None:
    """Look up a profile by name from request data. Returns None if not found."""
    profile_name = _as_str(data.get("profile", ""))
    profiles = cfg.get("profiles")
    if not isinstance(profiles, dict) or profile_name not in profiles:
        return None
    profile = profiles[profile_name]
    return _as_obj(profile) if isinstance(profile, dict) else None


async def api_allowlist_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        profile = _get_profile(cfg, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        profile["allowList"] = data.get("domains", [])
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_rules_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        profile = _get_profile(cfg, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        profile["customRules"] = data.get("rules", [])
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_rewrite_save(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        profile = _get_profile(cfg, data)
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
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
    return JSONResponse({"ok": True})


async def api_rewrite_delete(request: Request) -> JSONResponse:
    data = _validate_json_obj(await request.json())
    async with config.config_lock:
        cfg = config.load_config()
        profile = _get_profile(cfg, data)
        if profile is None:
            return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
        domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
        profile["dnsRewrites"] = [
            rw
            for rw in _as_list(profile.get("dnsRewrites") or [])
            if not (isinstance(rw, dict) and _norm_domain(rw) == domain)
        ]
        try:
            config.save_config(cfg)
        except OSError as exc:
            return JSONResponse(
                {"ok": False, "error": f"Failed to save config: {exc}"},
                status_code=500,
            )
        await config.reload_technitium_config(cfg)
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

    cfg = config.load_config()
    services = config.load_blocked_services()
    custom_services = cfg.get("customServices") or {}
    steps: list[dict[str, str]] = []

    # Step 1: Global blocking check
    if not cfg.get("enableBlocking", True):
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
        profile_name, client_name, method = filtering._resolve_client_profile(cfg, client_ip)
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
        default_profile = _as_str(cfg.get("defaultProfile", "") or "")
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
    base_profile_name = _as_str(cfg.get("baseProfile", "") or "")
    profiles = cfg.get("profiles") or {}
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
    rw_match = filtering._rewrite_matches(rewrites, domain)
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
    allow_match = filtering._domain_matches(allowed, domain)
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
    schedule_active, schedule_detail = filtering._check_schedule_active(profile, cfg)
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

    block_match = filtering._domain_matches(blocked, domain)
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
