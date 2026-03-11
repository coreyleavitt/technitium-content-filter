from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import secrets
from pathlib import Path
from typing import cast

import httpx

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


def _validate_json_obj_list(data: object) -> list[JsonObj]:
    """Validate that data is a list of dicts."""
    if not isinstance(data, list):
        raise TypeError(f"Expected list, got {type(data).__name__}")
    result: list[JsonObj] = []
    for item in data:
        if isinstance(item, dict):
            result.append(item)
    return result


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
