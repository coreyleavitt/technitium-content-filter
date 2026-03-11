from __future__ import annotations

import ipaddress
from datetime import datetime
from zoneinfo import ZoneInfo

from config import JsonObj, _as_list, _as_str

_DAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]


def _domain_matches(domains: set[str], query: str) -> str | None:
    """Subdomain-walking match, mirroring C# DomainMatcher.Matches."""
    trimmed = query.rstrip(".").lower()
    current = trimmed
    while True:
        if current in domains:
            return current
        dot = current.find(".")
        if dot < 0 or dot == len(current) - 1:
            break
        current = current[dot + 1 :]
    return None


def _rewrite_matches(rewrites: dict[str, str], query: str) -> tuple[str, str] | None:
    """Subdomain-walking rewrite lookup, returns (matched_domain, answer) or None."""
    trimmed = query.rstrip(".").lower()
    current = trimmed
    while True:
        if current in rewrites:
            return (current, rewrites[current])
        dot = current.find(".")
        if dot < 0 or dot == len(current) - 1:
            break
        current = current[dot + 1 :]
    return None


def _resolve_client_profile(config: JsonObj, client_ip: str) -> tuple[str | None, str | None, str]:
    """Resolve client IP to (profile_name, client_name, method)."""
    clients = _as_list(config.get("clients") or [])
    ip = ipaddress.ip_address(client_ip)

    # Priority 1: Exact IP match
    for client in clients:
        if not isinstance(client, dict):
            continue
        for cid in _as_list(client.get("ids") or []):
            cid_str = _as_str(cid)
            if "/" not in cid_str:
                try:
                    if ip == ipaddress.ip_address(cid_str):
                        return (
                            _as_str(client.get("profile", "")),
                            _as_str(client.get("name", "")),
                            f"exact IP match ({cid_str})",
                        )
                except ValueError:
                    continue

    # Priority 2: CIDR longest prefix
    best_profile: str | None = None
    best_name: str | None = None
    best_prefix = -1
    best_cidr = ""
    for client in clients:
        if not isinstance(client, dict):
            continue
        for cid in _as_list(client.get("ids") or []):
            cid_str = _as_str(cid)
            if "/" in cid_str:
                try:
                    network = ipaddress.ip_network(cid_str, strict=False)
                    if ip in network and network.prefixlen > best_prefix:
                        best_profile = _as_str(client.get("profile", ""))
                        best_name = _as_str(client.get("name", ""))
                        best_prefix = network.prefixlen
                        best_cidr = cid_str
                except ValueError:
                    continue

    if best_profile is not None:
        return (best_profile, best_name, f"CIDR match ({best_cidr})")

    # Priority 3: Default profile
    default = _as_str(config.get("defaultProfile", "") or "")
    if default:
        return (default, None, "default profile")

    return (None, None, "no match")


def _check_schedule_active(profile: JsonObj, config: JsonObj) -> tuple[bool, str]:
    """Check if blocking is active now for the profile's schedule."""
    schedule = profile.get("schedule")
    if not schedule or not isinstance(schedule, dict) or len(schedule) == 0:
        return (True, "no schedule configured (always active)")

    tz_str = _as_str(config.get("timeZone", "UTC") or "UTC")
    try:
        tz = ZoneInfo(tz_str)
    except (KeyError, ValueError):
        tz = ZoneInfo("UTC")

    now = datetime.now(tz)
    day_key = _DAY_KEYS[now.weekday()]

    window = schedule.get(day_key)
    if not window or not isinstance(window, dict):
        return (True, f"no schedule entry for {day_key} (active by default)")

    schedule_all_day = bool(config.get("scheduleAllDay", True))
    if schedule_all_day or window.get("allDay"):
        return (True, f"schedule active all day on {day_key}")

    start_str = _as_str(window.get("start", ""))
    end_str = _as_str(window.get("end", ""))
    if not start_str or not end_str:
        return (True, "schedule window missing start/end (active by default)")

    current_minutes = now.hour * 60 + now.minute
    sh, sm = (int(x) for x in start_str.split(":"))
    eh, em = (int(x) for x in end_str.split(":"))
    start_min = sh * 60 + sm
    end_min = eh * 60 + em

    if start_min <= end_min:
        active = start_min <= current_minutes <= end_min
    else:
        active = current_minutes >= start_min or current_minutes <= end_min

    time_now = now.strftime("%H:%M")
    if active:
        return (True, f"within schedule window {start_str}-{end_str} (now: {time_now} {tz_str})")
    return (False, f"outside schedule window {start_str}-{end_str} (now: {time_now} {tz_str})")
