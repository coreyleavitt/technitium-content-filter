"""Console entry point."""

from __future__ import annotations

import asyncio
from typing import cast

from hypercorn.asyncio import serve
from hypercorn.config import Config
from hypercorn.typing import ASGIFramework


def main() -> None:
    from .app import app

    hc = Config()
    hc.bind = ["0.0.0.0:8000"]  # noqa: S104
    asyncio.run(serve(cast(ASGIFramework, app), hc))
