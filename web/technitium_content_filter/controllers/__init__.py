from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from litestar import Response
from mako.lookup import TemplateLookup

from .. import config

# #50: Enable default HTML escaping in Mako templates
templates = TemplateLookup(
    directories=[str(Path(__file__).parent.parent / "templates")],
    input_encoding="utf-8",
    default_filters=["h"],
)


def render(template_name: str, current: str = "", **kwargs: Any) -> Response[str]:
    tmpl = templates.get_template(template_name)
    html: str = tmpl.render(
        base_path=config.BASE_PATH.rstrip("/"), json=json, current=current, **kwargs
    )
    return Response(content=html, media_type="text/html; charset=utf-8")


def _json_error(error: str, status_code: int = 400) -> Response[Any]:
    return Response(
        content={"ok": False, "error": error},
        status_code=status_code,
        media_type="application/json",
    )


def _json_ok(**kwargs: Any) -> Response[Any]:
    return Response(
        content={"ok": True, **kwargs},
        media_type="application/json",
    )
