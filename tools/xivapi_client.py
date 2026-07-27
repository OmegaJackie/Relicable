"""Thin client for the XIVAPI v2 ("Boilmaster") read-only REST API.

Vendored and trimmed from OmegaJackie/XIVAPI-GUI (xivapi/client.py, MIT) so the
Relicable build tooling is self-contained -- no need to have XIVAPI-GUI cloned or
pip-installed to regenerate/validate the leve tables. If you DO have that package
importable, `from xivapi import XIVAPIClient` is a drop-in replacement.

Reference: https://v2.xivapi.com/docs/welcome/
Every endpoint is read-only and needs no authentication. Knows nothing about the
GUI, so it is importable and scriptable on its own.

Difference from the upstream client: a small retry/backoff around transient
network / 5xx errors, because a full table regen makes ~100 sequential calls and
one flaky response should not abort the build.
"""

from __future__ import annotations

import time
from typing import Any, Iterable, Optional, Union

import requests

BASE_URL = "https://v2.xivapi.com/api"

# (connect, read) timeout in seconds.
DEFAULT_TIMEOUT = (5.0, 20.0)

StrList = Union[str, Iterable[str], None]


class XIVAPIError(Exception):
    """Raised when the API returns a non-2xx response or a request fails."""

    def __init__(self, code: int, message: str):
        self.code = code
        self.message = message
        super().__init__(f"HTTP {code}: {message}")


class XIVAPIClient:
    """Blocking HTTP client for the XIVAPI v2 API."""

    def __init__(
        self,
        base_url: str = BASE_URL,
        timeout: Union[float, tuple] = DEFAULT_TIMEOUT,
        session: Optional[requests.Session] = None,
        max_retries: int = 3,
        retry_backoff: float = 1.5,
    ):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.max_retries = max_retries
        self.retry_backoff = retry_backoff
        self.session = session or requests.Session()
        self.session.headers.setdefault(
            "User-Agent", "Relicable-leve-tooling/1.0 (build tool; derived from XIVAPI-Explorer)"
        )

    # ------------------------------------------------------------------ #
    # Low-level plumbing
    # ------------------------------------------------------------------ #
    def _request(self, path: str, params: Optional[dict] = None) -> requests.Response:
        url = f"{self.base_url}{path}"
        last_exc: Optional[Exception] = None
        for attempt in range(self.max_retries):
            try:
                resp = self.session.get(
                    url, params=self._clean(params), timeout=self.timeout
                )
            except requests.RequestException as exc:
                last_exc = XIVAPIError(0, f"Network error: {exc}")
            else:
                if resp.ok:
                    return resp
                # Retry only on transient server-side failures; 4xx is a real error.
                if resp.status_code < 500:
                    raise self._error_from(resp)
                last_exc = self._error_from(resp)
            time.sleep(self.retry_backoff * (attempt + 1))
        assert last_exc is not None
        raise last_exc

    def _get_json(self, path: str, params: Optional[dict] = None) -> Any:
        resp = self._request(path, params)
        try:
            return resp.json()
        except ValueError as exc:
            raise XIVAPIError(resp.status_code, "Malformed JSON in response") from exc

    @staticmethod
    def _clean(params: Optional[dict]) -> Optional[dict]:
        if not params:
            return None
        return {k: v for k, v in params.items() if v not in (None, "", [])}

    @staticmethod
    def _error_from(resp: requests.Response) -> XIVAPIError:
        code = resp.status_code
        message = resp.reason or "Request failed"
        try:
            data = resp.json()
        except ValueError:
            data = None
        if isinstance(data, dict):
            code = data.get("code", code)
            message = data.get("message", message)
        return XIVAPIError(code, message)

    @staticmethod
    def _join(value: StrList) -> Optional[str]:
        if value is None:
            return None
        if isinstance(value, str):
            return value
        return ",".join(str(v) for v in value)

    # ------------------------------------------------------------------ #
    # Endpoints
    # ------------------------------------------------------------------ #
    def versions(self) -> list[dict]:
        """List available game/schema versions (newest first)."""
        return self._get_json("/version").get("versions", [])

    def row(
        self,
        sheet: str,
        row: Union[int, str],
        *,
        fields: StrList = None,
        language: Optional[str] = None,
        version: Optional[str] = None,
    ) -> dict:
        """Read a single row from a sheet with (optionally) filtered fields."""
        params = {
            "fields": self._join(fields),
            "language": language,
            "version": version,
        }
        return self._get_json(f"/sheet/{sheet}/{row}", params=params)

    def rows(
        self,
        sheet: str,
        *,
        fields: StrList = None,
        limit: int = 100,
        after: Optional[int] = None,
        language: Optional[str] = None,
        version: Optional[str] = None,
    ) -> dict:
        """List rows of a sheet (paginated). `after` is the last row id seen."""
        params = {
            "fields": self._join(fields),
            "limit": limit,
            "after": after,
            "language": language,
            "version": version,
        }
        return self._get_json(f"/sheet/{sheet}", params=params)

    def search(
        self,
        query: str,
        sheets: StrList,
        *,
        fields: StrList = None,
        language: Optional[str] = None,
        limit: int = 50,
        cursor: Optional[str] = None,
        version: Optional[str] = None,
    ) -> dict:
        """Execute a search query across one or more sheets."""
        if cursor:
            params = {"cursor": cursor, "limit": limit}
        else:
            params = {
                "query": query,
                "sheets": self._join(sheets),
                "fields": self._join(fields),
                "language": language,
                "limit": limit,
                "version": version,
            }
        return self._get_json("/search", params=params)
