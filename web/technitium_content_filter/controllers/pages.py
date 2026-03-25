from __future__ import annotations

from typing import Any

from litestar import Controller, Response, get
from litestar.response import Redirect

from .. import config
from ..config import _as_list, _as_obj
from . import render


class PageController(Controller):
    path = "/"

    @get("/")
    async def dashboard(self) -> Response[str]:
        cfg = config.load_config()
        services = config.load_blocked_services()

        # #57: Dashboard stat computation
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

    @get("/profiles")
    async def profiles_page(self) -> Response[str]:
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

    @get("/profiles/{name:path}")
    async def profile_detail(self, name: str) -> Response[Any]:
        name = name.lstrip("/")
        cfg = config.load_config()
        profiles = cfg.get("profiles")
        if not isinstance(profiles, dict) or name not in profiles:
            base = config.BASE_PATH.rstrip("/")
            return Redirect(path=f"{base}/profiles", status_code=302)
        services = config.load_blocked_services()
        custom = cfg.get("customServices")
        all_services = {**services, **(_as_obj(custom) if isinstance(custom, dict) else {})}
        return render(
            "profile_detail.html",
            current="profiles",
            config=cfg,
            profile_name=name,
            profile=profiles[name],
            services=all_services,
        )

    @get("/clients")
    async def clients_page(self) -> Response[str]:
        cfg = config.load_config()
        return render("clients.html", current="clients", config=cfg)

    @get("/settings")
    async def settings_page(self) -> Response[str]:
        cfg = config.load_config()
        services = config.load_blocked_services()
        return render("settings.html", current="settings", config=cfg, services=services)


class RedirectController(Controller):
    path = "/"
    opt: dict[str, bool] = {"skip_auth": True}

    @get("/services")
    async def services_redirect(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/settings", status_code=301)

    @get("/filters/blocklists")
    async def filters_blocklists(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/settings", status_code=301)

    @get("/filters/allowlists")
    async def filters_allowlists(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/profiles", status_code=301)

    @get("/filters/services")
    async def filters_services(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/settings", status_code=301)

    @get("/filters/rules")
    async def filters_rules(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/profiles", status_code=301)

    @get("/filters/regex")
    async def filters_regex(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/profiles", status_code=301)

    @get("/filters/rewrites")
    async def filters_rewrites(self) -> Response[Any]:
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/profiles", status_code=301)
