from __future__ import annotations

import os
import time
from collections import defaultdict

RATE_LIMIT_MAX = int(os.environ.get("RATE_LIMIT_MAX", "300"))
RATE_LIMIT_WINDOW = 60.0  # seconds


class RateLimiter:
    """Per-key sliding window rate limiter (#54)."""

    def __init__(
        self, max_requests: int = RATE_LIMIT_MAX, window: float = RATE_LIMIT_WINDOW
    ) -> None:
        self.max_requests = max_requests
        self.window = window
        self._buckets: dict[str, list[float]] = defaultdict(list)

    @property
    def buckets(self) -> dict[str, list[float]]:
        return self._buckets

    def check(self, key: str, *, max_requests: int | None = None) -> bool:
        """Return True if request is allowed, False if rate-limited."""
        now = time.monotonic()
        bucket = self._buckets[key]
        cutoff = now - self.window
        while bucket and bucket[0] < cutoff:
            bucket.pop(0)
        limit = max_requests if max_requests is not None else self.max_requests
        if len(bucket) >= limit:
            return False
        bucket.append(now)
        return True

    def clear(self) -> None:
        self._buckets.clear()


rate_limiter = RateLimiter()
