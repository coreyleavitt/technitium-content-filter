from __future__ import annotations

import ipaddress
from typing import Any
from urllib.parse import urlparse

from litestar import Controller, Response, delete, get, post

from .. import config, filtering
from ..config import _as_list, _as_obj, _as_str
from . import _json_error, _json_ok


def _validate_blocklist_url(url: str) -> str | None:
    """Validate blocklist URL scheme. Returns error message or None if valid (#49)."""
    parsed = urlparse(url)
    if parsed.scheme not in ("http", "https"):
        return "Only http:// and https:// URLs are allowed"
    return None


_VALID_BLOCKLIST_TYPES = {"domain", "regex"}


class ServiceController(Controller):
    path = "/api"

    @get("/services")
    async def services_get(self) -> dict[str, Any]:
        cfg = config.load_config()
        services = config.load_blocked_services()
        custom = cfg.get("customServices")
        return {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}

    @post("/custom-services", status_code=200)
    async def custom_service_save(self, data: dict[str, Any]) -> Response[Any]:
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @delete("/custom-services", status_code=200)
    async def custom_service_delete(self, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            custom = cfg.get("customServices")
            if isinstance(custom, dict):
                svc_id = _as_str(data.get("id", ""))
                custom.pop(svc_id, None)
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/blocklists", status_code=200)
    async def blocklist_save(self, data: dict[str, Any]) -> Response[Any]:
        url = _as_str(data.get("url", "")).strip()
        if not url:
            return _json_error("URL required")
        # #49: Validate URL scheme
        url_error = _validate_blocklist_url(url)
        if url_error:
            return _json_error(url_error)

        bl_type = _as_str(data.get("type", "domain")).strip()
        if bl_type not in _VALID_BLOCKLIST_TYPES:
            valid = sorted(_VALID_BLOCKLIST_TYPES)
            return _json_error(f"Invalid type: must be one of {valid}")

        async with config.config_lock:
            cfg = config.load_config()
            blocklists = _as_list(cfg.setdefault("blockLists", []))

            # Update existing or add new
            for bl_val in blocklists:
                if isinstance(bl_val, dict) and bl_val.get("url") == url:
                    bl_val["name"] = data.get("name", "")
                    bl_val["enabled"] = data.get("enabled", True)
                    bl_val["refreshHours"] = data.get("refreshHours", 24)
                    bl_val["type"] = bl_type
                    break
            else:
                blocklists.append(
                    {
                        "url": url,
                        "name": data.get("name", ""),
                        "enabled": data.get("enabled", True),
                        "refreshHours": data.get("refreshHours", 24),
                        "type": bl_type,
                    }
                )

            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @delete("/blocklists", status_code=200)
    async def blocklist_delete(self, data: dict[str, Any]) -> Response[Any]:
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @post("/blocklists/refresh", status_code=200)
    async def blocklist_refresh(self) -> Response[Any]:
        """Trigger Technitium to reload config which starts a blocklist refresh cycle."""
        cfg = config.load_config()
        reloaded = await config.reload_technitium_config(cfg)
        return _json_ok(reloaded=reloaded)

    @post("/test-domain", status_code=200)
    async def test_domain(self, data: dict[str, Any]) -> Response[Any]:
        """Simulate the filtering pipeline for a domain (#118)."""
        domain = _as_str(data.get("domain", "")).strip().lower().rstrip(".")
        if not domain:
            return _json_error("Domain is required")

        client_ip = _as_str(data.get("clientIp", "")).strip()
        if client_ip:
            try:
                ipaddress.ip_address(client_ip)
            except ValueError:
                return _json_error(f"Invalid IP address: {client_ip}")

        cfg = config.load_config()
        services = config.load_blocked_services()
        custom_services = cfg.get("customServices") or {}
        steps: list[dict[str, str]] = []

        def _result(
            verdict: str,
            profile: str | None = None,
            rewrite_answer: str | None = None,
            **extra: Any,
        ) -> Response[Any]:
            return Response(
                content={
                    "ok": True,
                    "verdict": verdict,
                    "profile": profile,
                    "rewriteAnswer": rewrite_answer,
                    "steps": steps,
                    **extra,
                },
                media_type="application/json",
            )

        # Step 1: Global blocking check
        if not cfg.get("enableBlocking", True):
            steps.append(
                {
                    "step": "Blocking enabled",
                    "result": "ALLOW",
                    "detail": "Global blocking is disabled",
                }
            )
            return _result("ALLOW")
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
                        "detail": (
                            f'No client IP provided, using default profile "{default_profile}"'
                        ),
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
            return _result("ALLOW")
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
            return _result("ALLOW", profile=profile_name)

        # Build merged sets (profile + base profile)
        base_profile = (
            profiles.get(base_profile_name)
            if base_profile_name
            and base_profile_name != profile_name
            and isinstance(profiles, dict)
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
            return _result("REWRITE", profile=profile_name, rewrite_answer=rw_match[1])
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
            return _result("ALLOW", profile=profile_name)
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

        # Step 6: Regex allow check
        regex_allow_patterns: list[str] = []
        for src in [profile, base_profile]:
            if src and isinstance(src, dict):
                for p in _as_list(src.get("regexAllowRules") or []):
                    p_str = _as_str(p).strip()
                    if p_str and not p_str.startswith("#"):
                        regex_allow_patterns.append(p_str)
        regex_allow_match = filtering._regex_matches(regex_allow_patterns, domain)
        if regex_allow_match:
            steps.append(
                {
                    "step": "Regex allow",
                    "result": "ALLOW",
                    "detail": f"Domain matches regex allow pattern: {regex_allow_match}",
                }
            )
            return _result("ALLOW", profile=profile_name)
        steps.append(
            {
                "step": "Regex allow",
                "result": "PASS",
                "detail": (
                    f"No regex allow match ({len(regex_allow_patterns)} pattern"
                    f"{'s' if len(regex_allow_patterns) != 1 else ''} checked)"
                ),
            }
        )

        # Step 7: Schedule check
        schedule_active, schedule_detail = filtering._check_schedule_active(profile, cfg)
        if not schedule_active:
            steps.append(
                {
                    "step": "Schedule",
                    "result": "ALLOW",
                    "detail": f"Blocking inactive: {schedule_detail}",
                }
            )
            return _result("ALLOW", profile=profile_name)
        steps.append({"step": "Schedule", "result": "PASS", "detail": schedule_detail})

        # Step 8: Build blocked domains (services + custom rules)
        blocked: set[str] = set()
        blocklist_urls: list[str] = []

        for src in [profile, base_profile]:
            if not src or not isinstance(src, dict):
                continue
            # Blocked services -> expand to domains
            for svc_id in _as_list(src.get("blockedServices") or []):
                svc_id_str = _as_str(svc_id)
                svc = services.get(svc_id_str) if isinstance(services, dict) else None
                if svc and isinstance(svc, dict):
                    for sd in _as_list(svc.get("domains") or []):
                        blocked.add(_as_str(sd).lower())
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
            return _result("BLOCK", profile=profile_name)

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
                    f"No match in {len(blocked)} blocked domain(s)"
                    f" from services/rules.{blocklist_note}"
                ),
            }
        )

        # Step 9: Regex block check
        regex_block_patterns: list[str] = []
        regex_blocklist_urls: list[str] = []
        global_blocklists = _as_list(cfg.get("blockLists") or [])
        regex_bl_urls_set: set[str] = set()
        for bl_item in global_blocklists:
            if (
                isinstance(bl_item, dict)
                and bl_item.get("type") == "regex"
                and bl_item.get("enabled")
            ):
                regex_bl_urls_set.add(_as_str(bl_item.get("url", "")))
        for src in [profile, base_profile]:
            if src and isinstance(src, dict):
                for p in _as_list(src.get("regexBlockRules") or []):
                    p_str = _as_str(p).strip()
                    if p_str and not p_str.startswith("#"):
                        regex_block_patterns.append(p_str)
                for bl_url in _as_list(src.get("blockLists") or []):
                    bl_url_str = _as_str(bl_url)
                    if bl_url_str in regex_bl_urls_set and bl_url_str not in regex_blocklist_urls:
                        regex_blocklist_urls.append(bl_url_str)
        regex_block_match = filtering._regex_matches(regex_block_patterns, domain)
        if regex_block_match:
            steps.append(
                {
                    "step": "Regex block",
                    "result": "BLOCK",
                    "detail": f"Domain matches regex block pattern: {regex_block_match}",
                }
            )
            return _result("BLOCK", profile=profile_name)

        regex_bl_note = ""
        if regex_blocklist_urls:
            regex_bl_note = (
                f" Note: {len(regex_blocklist_urls)} remote regex blocklist(s) assigned but"
                " not checked (only available in DNS plugin memory)"
            )
        steps.append(
            {
                "step": "Regex block",
                "result": "PASS",
                "detail": (
                    f"No regex block match ({len(regex_block_patterns)} inline pattern"
                    f"{'s' if len(regex_block_patterns) != 1 else ''} checked).{regex_bl_note}"
                ),
            }
        )

        # Step 10: Default allow
        steps.append(
            {
                "step": "Default",
                "result": "ALLOW",
                "detail": "No rules matched, domain is allowed",
            }
        )
        return _result(
            "ALLOW",
            profile=profile_name,
            blocklistUrls=blocklist_urls if blocklist_urls else None,
        )
