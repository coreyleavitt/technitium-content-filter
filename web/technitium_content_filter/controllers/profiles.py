from __future__ import annotations

import re
from typing import Any

from litestar import Controller, Response, delete, get, post

from .. import config
from ..config import (
    JsonObj,
    _as_list,
    _as_obj,
    _as_str,
    _norm_domain,
)
from . import _json_error, _json_ok

_OVERVIEW_FIELDS = frozenset(
    {"description", "blockedServices", "blockLists", "schedule", "blockingAddresses"}
)


def _get_profile_by_name(cfg: JsonObj, name: str) -> JsonObj | None:
    """Look up a profile by name. Returns None if not found."""
    profiles = cfg.get("profiles")
    if not isinstance(profiles, dict) or name not in profiles:
        return None
    profile = profiles[name]
    return _as_obj(profile) if isinstance(profile, dict) else None


class ProfileController(Controller):
    path = "/api/profiles"

    @post("/", status_code=200)
    async def save(self, data: dict[str, Any]) -> Response[Any]:
        name = _as_str(data.pop("name", ""))
        # #46: Reject whitespace-only profile names
        if name and not name.strip():
            return _json_error("Profile name must not be only whitespace")
        name = name.strip()
        async with config.config_lock:
            cfg = config.load_config()
            profiles = _as_obj(cfg.get("profiles") or {})
            existing = (
                _as_obj(profiles[name])
                if name in profiles and isinstance(profiles[name], dict)
                else {}
            )
            # Merge: only overview fields from request, preserve filter data
            merged: JsonObj = {**existing}
            for key in _OVERVIEW_FIELDS:
                if key in data:
                    merged[key] = data[key]
            profiles[name] = merged
            cfg["profiles"] = profiles
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @delete("/", status_code=200)
    async def delete_profile(self, data: dict[str, Any]) -> Response[Any]:
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/rename", status_code=200)
    async def rename(self, data: dict[str, Any]) -> Response[Any]:
        """Atomically rename a profile and update client assignments."""
        old_name = _as_str(data.get("old_name", "")).strip()
        new_name = _as_str(data.get("new_name", "")).strip()
        if not old_name or not new_name:
            return _json_error("old_name and new_name are required")
        if old_name == new_name:
            return _json_ok()
        async with config.config_lock:
            cfg = config.load_config()
            profiles = cfg.get("profiles")
            if not isinstance(profiles, dict) or old_name not in profiles:
                return _json_error(f"Profile '{old_name}' not found", 404)
            if new_name in profiles:
                return _json_error(f"Profile '{new_name}' already exists", 409)
            profiles[new_name] = profiles.pop(old_name)
            clients = cfg.get("clients")
            if isinstance(clients, list):
                for client_val in clients:
                    if isinstance(client_val, dict) and client_val.get("profile") == old_name:
                        client_val["profile"] = new_name
            if cfg.get("defaultProfile") == old_name:
                cfg["defaultProfile"] = new_name
            if cfg.get("baseProfile") == old_name:
                cfg["baseProfile"] = new_name
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @get("/{name:str}")
    async def get_profile(self, name: str) -> Response[Any]:
        """Return a single profile plus global blocklist metadata."""
        cfg = config.load_config()
        profile = _get_profile_by_name(cfg, name)
        if profile is None:
            return _json_error("Profile not found", 404)
        return Response(
            content={
                "ok": True,
                "profile": profile,
                "blockLists": cfg.get("blockLists", []),
            },
            media_type="application/json",
        )

    @post("/{name:str}/allowlist", status_code=200)
    async def allowlist_save(self, name: str, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            profile = _get_profile_by_name(cfg, name)
            if profile is None:
                return _json_error("Profile not found")
            profile["allowList"] = data.get("domains", [])
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/{name:str}/rules", status_code=200)
    async def rules_save(self, name: str, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            profile = _get_profile_by_name(cfg, name)
            if profile is None:
                return _json_error("Profile not found")
            profile["customRules"] = data.get("rules", [])
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/{name:str}/regex", status_code=200)
    async def regex_save(self, name: str, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            profile = _get_profile_by_name(cfg, name)
            if profile is None:
                return _json_error("Profile not found")
            block_rules = data.get("regexBlockRules", [])
            allow_rules = data.get("regexAllowRules", [])
            if not isinstance(block_rules, list) or not isinstance(allow_rules, list):
                return _json_error("regexBlockRules and regexAllowRules must be lists")
            # Validate each pattern at save time
            for pattern in block_rules + allow_rules:
                if not isinstance(pattern, str):
                    return _json_error("All patterns must be strings")
                trimmed = pattern.strip()
                if not trimmed or trimmed.startswith("#"):
                    continue
                try:
                    re.compile(trimmed)
                except re.error as exc:
                    return _json_error(f"Invalid regex pattern '{trimmed}': {exc}")
            profile["regexBlockRules"] = block_rules
            profile["regexAllowRules"] = allow_rules
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/{name:str}/rewrites", status_code=200)
    async def rewrite_save(self, name: str, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            profile = _get_profile_by_name(cfg, name)
            if profile is None:
                return _json_error("Profile not found")
            rewrites = _as_list(profile.setdefault("dnsRewrites", []))
            domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
            answer = _as_str(data.get("answer", "")).strip()
            if not domain or not answer:
                return _json_error("Domain and answer required")
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @delete("/{name:str}/rewrites", status_code=200)
    async def rewrite_delete(self, name: str, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            profile = _get_profile_by_name(cfg, name)
            if profile is None:
                return _json_error("Profile not found")
            domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
            profile["dnsRewrites"] = [
                rw
                for rw in _as_list(profile.get("dnsRewrites") or [])
                if not (isinstance(rw, dict) and _norm_domain(rw) == domain)
            ]
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()
