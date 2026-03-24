from __future__ import annotations

from typing import Any

from litestar import Controller, Response, delete, post

from .. import config
from ..config import JsonObj, _as_list
from . import _json_error, _json_ok


class ClientController(Controller):
    path = "/api/clients"

    @post("/", status_code=200)
    async def save(self, data: dict[str, Any]) -> Response[Any]:
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()

    @delete("/", status_code=200)
    async def delete_client(self, data: dict[str, Any]) -> Response[Any]:
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
                return _json_error(f"Failed to save config: {exc}", 500)
            await config.reload_technitium_config(cfg)
        return _json_ok()
