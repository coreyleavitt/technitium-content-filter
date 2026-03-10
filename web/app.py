from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, cast

import httpx
from mako.lookup import TemplateLookup
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, RedirectResponse
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles

type JsonValue = str | int | float | bool | None | list[JsonValue] | dict[str, JsonValue]
type JsonObj = dict[str, JsonValue]

CONFIG_PATH = Path(os.environ.get("CONFIG_PATH", "/data/dnsApp.config"))
TECHNITIUM_URL = os.environ.get("TECHNITIUM_URL", "http://technitium:5380")
APP_NAME = os.environ.get("APP_NAME", "ParentalControlsApp")
BASE_PATH = os.environ.get("BASE_PATH", "/")


def _read_api_token() -> str:
    token_file = os.environ.get("TECHNITIUM_API_TOKEN_FILE")
    if token_file and Path(token_file).exists():
        return Path(token_file).read_text().strip()
    return os.environ.get("TECHNITIUM_API_TOKEN", "")


TECHNITIUM_API_TOKEN = _read_api_token()

# Read from plugin's app folder (shared volume) -- single source of truth
BLOCKED_SERVICES_PATH = Path(os.environ.get("CONFIG_PATH", "")).parent / "blocked-services.json"

templates = TemplateLookup(
    directories=[str(Path(__file__).parent / "templates")],
    input_encoding="utf-8",
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
    defaults = cast(list[JsonObj], json.loads(defaults_path.read_text()))
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


def load_config() -> JsonObj:
    if CONFIG_PATH.exists():
        config: JsonObj = cast(JsonObj, json.loads(CONFIG_PATH.read_text()))
        changed = _migrate_blocklists(config)
        changed = _seed_default_blocklists(config) or changed
        if changed:
            save_config(config)
        return config
    config = {
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
    _seed_default_blocklists(config)
    return config


def save_config(config: JsonObj) -> None:
    tmp = CONFIG_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(config, indent=2))
    tmp.rename(CONFIG_PATH)


async def reload_technitium_config(config: JsonObj) -> bool:
    """Push config to Technitium so the DNS app reloads without restart."""
    if not TECHNITIUM_API_TOKEN:
        return False
    try:
        async with httpx.AsyncClient() as client:
            resp = await client.post(
                f"{TECHNITIUM_URL}/api/apps/config/set",
                data={
                    "token": TECHNITIUM_API_TOKEN,
                    "name": APP_NAME,
                    "config": json.dumps(config, indent=2),
                },
            )
            return resp.status_code == 200
    except httpx.HTTPError:
        return False


def load_blocked_services() -> JsonObj:
    if BLOCKED_SERVICES_PATH.exists():
        return cast(JsonObj, json.loads(BLOCKED_SERVICES_PATH.read_text()))
    return {}


def render(template_name: str, current: str = "", **kwargs: Any) -> HTMLResponse:
    tmpl = templates.get_template(template_name)
    return HTMLResponse(
        tmpl.render(base_path=BASE_PATH.rstrip("/"), json=json, current=current, **kwargs)
    )


# --- Page Routes ---


async def dashboard(request: Request) -> HTMLResponse:
    config = load_config()
    services = load_blocked_services()
    return render(
        "dashboard.html",
        current="dashboard",
        config=config,
        services=services,
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
    config = cast(JsonObj, await request.json())
    save_config(config)
    reloaded = await reload_technitium_config(config)
    return JSONResponse({"ok": True, "reloaded": reloaded})


async def api_profile_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    name = _as_str(data.pop("name", ""))
    profiles = _as_obj(config.get("profiles") or {})
    profiles[name] = data
    config["profiles"] = profiles
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_profile_delete(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    name = _as_str(data.get("name", ""))
    profiles = config.get("profiles")
    if isinstance(profiles, dict):
        profiles.pop(name, None)
    clients = config.get("clients")
    if isinstance(clients, list):
        for client_val in clients:
            if isinstance(client_val, dict) and client_val.get("profile") == name:
                client_val["profile"] = ""
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_client_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
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

    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_client_delete(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    clients = _as_list(config.get("clients") or [])
    index = data.get("index")
    if isinstance(index, int) and 0 <= index < len(clients):
        clients.pop(index)
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_settings_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    config["enableBlocking"] = data.get("enableBlocking", True)
    config["defaultProfile"] = data.get("defaultProfile") or None
    config["baseProfile"] = data.get("baseProfile") or None
    config["timeZone"] = data.get("timeZone", "America/Denver")
    config["scheduleAllDay"] = data.get("scheduleAllDay", True)
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_services_get(request: Request) -> JSONResponse:
    config = load_config()
    services = load_blocked_services()
    custom = config.get("customServices")
    all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
    return JSONResponse(all_services)


async def api_custom_service_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    svc_id = _as_str(data.get("id", ""))
    custom = _as_obj(config.setdefault("customServices", {}))
    custom[svc_id] = {
        "name": data.get("name", svc_id),
        "domains": data.get("domains", []),
    }
    config["customServices"] = custom
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_custom_service_delete(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    custom = config.get("customServices")
    if isinstance(custom, dict):
        svc_id = _as_str(data.get("id", ""))
        custom.pop(svc_id, None)
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


# --- Filter API Routes ---


async def api_blocklist_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    blocklists = _as_list(config.setdefault("blockLists", []))
    url = _as_str(data.get("url", "")).strip()
    if not url:
        return JSONResponse({"ok": False, "error": "URL required"}, status_code=400)

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

    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_blocklist_delete(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
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
    save_config(config)
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
    data = cast(JsonObj, await request.json())
    config = load_config()
    profile = _get_profile(config, data)
    if profile is None:
        return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
    profile["allowList"] = data.get("domains", [])
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rules_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    profile = _get_profile(config, data)
    if profile is None:
        return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
    profile["customRules"] = data.get("rules", [])
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rewrite_save(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
    config = load_config()
    profile = _get_profile(config, data)
    if profile is None:
        return JSONResponse({"ok": False, "error": "Profile not found"}, status_code=400)
    rewrites = _as_list(profile.setdefault("dnsRewrites", []))
    domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
    answer = _as_str(data.get("answer", "")).strip()
    if not domain or not answer:
        return JSONResponse({"ok": False, "error": "Domain and answer required"}, status_code=400)

    # Update existing or add new
    for rw_val in rewrites:
        if isinstance(rw_val, dict) and _norm_domain(rw_val) == domain:
            rw_val["domain"] = domain
            rw_val["answer"] = answer
            break
    else:
        rewrites.append({"domain": domain, "answer": answer})

    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


async def api_rewrite_delete(request: Request) -> JSONResponse:
    data = cast(JsonObj, await request.json())
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
    save_config(config)
    await reload_technitium_config(config)
    return JSONResponse({"ok": True})


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

app = Starlette(routes=routes)
