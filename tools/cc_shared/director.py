"""Shared helpers for tools that talk to their OWN Director's Control API (issue #705).

A cc-director session is launched with two environment values these tools rely on:

  CC_DIRECTOR_API  - the base URL of this session's own Director Control API (loopback)
  CC_SESSION_ID    - this session's GUID

For the helpers in THIS module, cc-devthrottle only ever calls its own Director (loopback). The
Director relays to the Gateway on its behalf, so these helpers need neither the Gateway URL nor the
fleet token - the fleet token stays on the Director.

Note: the separate schedule path (schedule_ops.py / the `schedule-*` commands) is deliberately
different - it resolves the configured gateway.url and gateway.token and calls the Gateway's
cron surface directly. That path therefore does need the Gateway URL and token; the
"never needs the Gateway URL or the fleet token" rule above applies only to the Director-relay
helpers defined here, not to schedule_ops.
"""

import json
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional


class DirectorError(RuntimeError):
    """A clear, user-facing failure talking to the Director (no stack traces for the agent)."""


def director_base_url() -> str:
    url = os.environ.get("CC_DIRECTOR_API", "").strip()
    if not url:
        raise DirectorError(
            "CC_DIRECTOR_API is not set. These tools only work inside a cc-director session."
        )
    return url.rstrip("/")


def session_id() -> Optional[str]:
    sid = os.environ.get("CC_SESSION_ID", "").strip()
    return sid or None


def _token() -> Optional[str]:
    # The loopback Control API needs NO token in the default configuration. In LAN mode the
    # Director's own standalone token (gateway-token.txt) is accepted; send it best-effort if
    # present. The fleet token is deliberately never read here - it stays on the Director.
    local = os.environ.get("LOCALAPPDATA", "")
    if not local:
        return None
    token_file = Path(local) / "cc-director" / "config" / "director" / "gateway-token.txt"
    try:
        if token_file.is_file():
            value = token_file.read_text(encoding="utf-8").strip()
            return value or None
    except OSError:
        return None
    return None


def _extract_error(body: str) -> Optional[str]:
    try:
        obj = json.loads(body)
    except (ValueError, TypeError):
        return None
    if isinstance(obj, dict):
        return obj.get("error") or obj.get("Error")
    return None


def _request(method: str, path: str, body: Optional[dict] = None, timeout: float = 30) -> Any:
    url = f"{director_base_url()}/{path.lstrip('/')}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    token = _token()
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as err:
        detail = ""
        try:
            detail = err.read().decode("utf-8")
        except OSError:
            detail = ""
        raise DirectorError(_extract_error(detail) or f"HTTP {err.code} from the Director") from err
    except urllib.error.URLError as err:
        raise DirectorError(
            f"Cannot reach the Director at {director_base_url()}: {err.reason}"
        ) from err


def get_json(path: str) -> Any:
    return _request("GET", path)


def post_json(path: str, body: dict, timeout: float = 30) -> Any:
    return _request("POST", path, body, timeout=timeout)


def patch_json(path: str, body: dict, timeout: float = 30) -> Any:
    return _request("PATCH", path, body, timeout=timeout)


def delete(path: str, timeout: float = 30) -> Any:
    return _request("DELETE", path, None, timeout=timeout)


def field(dto: Dict[str, Any], *keys: str, default: str = "") -> str:
    """Read the first present key from a session DTO, tolerating camelCase or PascalCase."""
    for key in keys:
        if key in dto and dto[key] is not None:
            return str(dto[key])
    return default


def short_id(session_guid: str) -> str:
    return session_guid[:8] if len(session_guid) > 8 else session_guid


def resolve_target(sessions: List[Dict[str, Any]], query: str) -> List[Dict[str, Any]]:
    """Resolve a user-typed target to matching sessions.

    Issue #821: an exactly-three-digit token (the session number, 100-999 from issue #820) is
    matched against session numbers first; if one or more active sessions hold that number, those
    are returned. Numbers are unique among active sessions (#820), so this normally yields exactly
    one match. When no active session holds the number, resolution falls back to id / name matching
    as before, so a three-digit token that happens to be an id prefix still resolves.

    Otherwise a full id match wins outright; failing that, match by id prefix OR by exact
    (case-insensitive) name. Returns the de-duplicated list of matches so the caller can detect
    ambiguity (more than one) or no match (empty).
    """
    q = query.strip().lower()
    if not q:
        return []

    if q.isdigit() and 100 <= int(q) <= 999:
        wanted = str(int(q))
        by_number = [s for s in sessions if field(s, "number", "Number") == wanted]
        if by_number:
            return by_number

    for s in sessions:
        if field(s, "sessionId", "SessionId").lower() == q:
            return [s]

    matches: Dict[str, Dict[str, Any]] = {}
    for s in sessions:
        sid = field(s, "sessionId", "SessionId")
        name = field(s, "name", "Name")
        if sid.lower().startswith(q) or name.lower() == q:
            matches[sid] = s
    return list(matches.values())
