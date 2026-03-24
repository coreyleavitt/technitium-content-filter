from __future__ import annotations

from typing import Any

from litestar import Controller, Response, get, post

from .. import config
from ..config import _VALID_CONFIG_KEYS
from . import _json_error, _json_ok


class SettingsController(Controller):
    path = "/api"

    @get("/config")
    async def config_get(self) -> dict[str, Any]:
        return config.load_config()

    @post("/config", status_code=200)
    async def config_set(self, data: dict[str, Any]) -> Response[Any]:
        # #40: Log unknown config keys for auditing
        unknown_keys = set(data.keys()) - _VALID_CONFIG_KEYS
        if unknown_keys:
            config.logger.warning(
                "Config set with unknown keys: %s", ", ".join(sorted(unknown_keys))
            )
        async with config.config_lock:
            try:
                config.save_config(data)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            reloaded = await config.reload_technitium_config(data)
        return _json_ok(reloaded=reloaded)

    @post("/settings", status_code=200)
    async def settings_save(self, data: dict[str, Any]) -> Response[Any]:
        async with config.config_lock:
            cfg = config.load_config()
            cfg["enableBlocking"] = data.get("enableBlocking", True)
            cfg["defaultProfile"] = data.get("defaultProfile") or None
            cfg["baseProfile"] = data.get("baseProfile") or None
            cfg["timeZone"] = data.get("timeZone", "America/Denver")
            cfg["scheduleAllDay"] = data.get("scheduleAllDay", True)
            if "allowTxtBlockingReport" in data:
                cfg["allowTxtBlockingReport"] = data["allowTxtBlockingReport"]
            if "blockingAddresses" in data:
                cfg["blockingAddresses"] = data["blockingAddresses"]
            try:
                config.save_config(cfg)
            except OSError as exc:
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()
