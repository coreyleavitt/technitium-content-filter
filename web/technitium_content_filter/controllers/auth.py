from __future__ import annotations

import json
import time
from dataclasses import dataclass
from typing import Annotated, Any

import httpx
from litestar import Controller, Request, Response, get, post
from litestar.enums import RequestEncodingType
from litestar.params import Body
from litestar.response import Redirect

from .. import config
from ..rate_limiter import rate_limiter
from . import render


@dataclass
class LoginData:
    username: str = ""
    password: str = ""


_NO_AUTH: dict[str, bool] = {"skip_auth": True, "exclude_from_csrf": True}


class AuthController(Controller):
    path = "/"
    opt = _NO_AUTH

    @get("/login")
    async def login_page(self, request: Request[Any, Any, Any]) -> Response[Any]:
        if not config.AUTH_DISABLED and request.session.get("user"):
            base = config.BASE_PATH.rstrip("/")
            return Redirect(path=f"{base}/", status_code=302)  # type: ignore[return-value]
        return render("login.html", error="")

    @post("/login", status_code=200)
    async def login_submit(
        self,
        request: Request[Any, Any, Any],
        data: Annotated[LoginData, Body(media_type=RequestEncodingType.URL_ENCODED)],
    ) -> Response[Any]:
        username = data.username.strip()
        password = data.password

        if not username or not password:
            return render("login.html", error="Username and password are required")

        # Rate limit login attempts
        client_addr = request.scope.get("client")
        client_ip = client_addr[0] if client_addr else "unknown"
        if not rate_limiter.check(f"login:{client_ip}", max_requests=config.LOGIN_RATE_LIMIT):
            return render("login.html", error="Too many login attempts. Please wait.")

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
        return Redirect(path=f"{base}/", status_code=302)  # type: ignore[return-value]

    @post("/logout", status_code=200)
    async def logout(self, request: Request[Any, Any, Any]) -> Response[Any]:
        request.session.clear()
        base = config.BASE_PATH.rstrip("/")
        return Redirect(path=f"{base}/login", status_code=302)  # type: ignore[return-value]
