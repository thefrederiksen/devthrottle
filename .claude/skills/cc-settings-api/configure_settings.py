#!/usr/bin/env python3
"""Read and write CC Director settings via a running Director's Control API.

Discovers a running Director from its instance registration files, then talks to the
loopback Control API:

    GET /settings        -> the whole config.json as JSON
    PUT /settings <obj>  -> deep-merges a partial patch into config.json (siblings preserved)

Because writes go through the running Director, gateway changes are applied live (the
Director re-registers with the gateway) - no app restart needed.

Usage:
    python configure_settings.py show
    python configure_settings.py get screenshots.source_directory
    python configure_settings.py set-screenshots "/Users/soren/Desktop"
    python configure_settings.py set-gateway --url http://gw-host:7878 \
        --advertised http://this-host:7879 [--token TOKEN]
    python configure_settings.py set <dotted.key> <value>

Auth is OFF by default (single-user trust boundary), so no token is needed. If a Director
has auth enabled you'll get a 401 - pass --director-token, or the script reads it from
config/director/gateway-token.txt.

ASCII-only output. No Unicode.
"""

import argparse
import json
import os
import sys
import urllib.request
import urllib.error
from pathlib import Path


def _local_app_data() -> Path:
    """Resolve the cc-director config base, honoring CC_DIRECTOR_ROOT like the app does."""
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return Path(override)
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA", "")
        if not base:
            raise RuntimeError("LOCALAPPDATA is not set; cannot locate cc-director config.")
        return Path(base) / "cc-director"
    # macOS/Linux: .NET maps LocalApplicationData to ~/.local/share
    return Path(os.path.expanduser("~")) / ".local" / "share" / "cc-director"


def _instances_dir() -> Path:
    return _local_app_data() / "config" / "director" / "instances"


def _token_file() -> Path:
    return _local_app_data() / "config" / "director" / "gateway-token.txt"


def _pid_alive(pid: int) -> bool:
    """True if a process with this pid exists."""
    if pid <= 0:
        return False
    if sys.platform == "win32":
        import ctypes
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ProcessLookupError):
        return False


def _ci_get(obj: dict, key: str):
    """Case-insensitive key lookup (instance files are PascalCase today)."""
    for k, v in obj.items():
        if k.lower() == key.lower():
            return v
    return None


def discover_control_endpoint() -> str:
    """Find the newest LIVE Director's loopback control endpoint, e.g. http://127.0.0.1:7883.

    Raises with a clear message if none is running.
    """
    d = _instances_dir()
    if not d.is_dir():
        raise RuntimeError(
            f"No Director instances directory at {d}. Is CC Director running?"
        )

    candidates = []
    for f in d.glob("*.json"):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        endpoint = _ci_get(data, "ControlEndpoint")
        pid = _ci_get(data, "Pid")
        started = _ci_get(data, "StartedAt") or ""
        if not endpoint or not isinstance(pid, int):
            continue
        if not _pid_alive(pid):
            continue
        candidates.append((started, endpoint))

    if not candidates:
        raise RuntimeError(
            f"Found no running Director in {d}. Start CC Director, then retry."
        )

    # Newest by StartedAt (ISO-8601 sorts lexically).
    candidates.sort(key=lambda c: c[0], reverse=True)
    return candidates[0][1].rstrip("/")


def _request(method: str, url: str, token: str | None, body: dict | None = None) -> dict:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            text = resp.read().decode("utf-8")
            return json.loads(text) if text.strip() else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")
        if e.code == 401:
            raise RuntimeError(
                "401 Unauthorized: this Director has auth enabled. Pass --director-token "
                f"or set the token at {_token_file()}."
            ) from e
        raise RuntimeError(f"{method} {url} failed: HTTP {e.code} {detail}") from e
    except urllib.error.URLError as e:
        raise RuntimeError(f"{method} {url} failed: {e.reason}") from e


def _resolve_token(explicit: str | None) -> str | None:
    if explicit:
        return explicit
    # Only used if a Director happens to have auth on; harmless to read if present.
    tf = _token_file()
    if tf.is_file():
        return tf.read_text(encoding="utf-8").strip() or None
    return None


def get_settings(token: str | None) -> dict:
    base = discover_control_endpoint()
    return _request("GET", f"{base}/settings", token)


def put_settings(patch: dict, token: str | None) -> dict:
    base = discover_control_endpoint()
    return _request("PUT", f"{base}/settings", token, body=patch)


def _dig(obj: dict, dotted: str):
    cur = obj
    for part in dotted.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None
        cur = cur[part]
    return cur


def _nest(dotted: str, value) -> dict:
    parts = dotted.split(".")
    out: dict = {}
    cur = out
    for p in parts[:-1]:
        cur[p] = {}
        cur = cur[p]
    cur[parts[-1]] = value
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Configure CC Director settings via REST.")
    parser.add_argument("--director-token", default=None, help="Bearer token (only if auth is on).")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("show", help="Print the full config.json.")

    p_get = sub.add_parser("get", help="Print one dotted-key value.")
    p_get.add_argument("key")

    p_set = sub.add_parser("set", help="Set one dotted-key value.")
    p_set.add_argument("key")
    p_set.add_argument("value")

    p_shots = sub.add_parser("set-screenshots", help="Set the screenshots source directory.")
    p_shots.add_argument("path")

    p_gw = sub.add_parser("set-gateway", help="Set gateway connection settings.")
    p_gw.add_argument("--url", required=True, help="Gateway base URL, e.g. http://gw-host:7878")
    p_gw.add_argument("--advertised", default=None,
                      help="This Director's reachable URL (gateway calls back here).")
    p_gw.add_argument("--token", default=None, help="Gateway shared token (optional).")

    args = parser.parse_args()
    token = _resolve_token(args.director_token)

    try:
        if args.command == "show":
            print(json.dumps(get_settings(token), indent=2))

        elif args.command == "get":
            value = _dig(get_settings(token), args.key)
            if value is None:
                print(f"(not set) {args.key}")
            else:
                print(value if isinstance(value, str) else json.dumps(value, indent=2))

        elif args.command == "set":
            merged = put_settings(_nest(args.key, args.value), token)
            print(f"OK set {args.key}")
            print(json.dumps(_dig(merged, args.key.split('.')[0]), indent=2))

        elif args.command == "set-screenshots":
            put_settings({"screenshots": {"source_directory": args.path}}, token)
            print(f"OK screenshots.source_directory = {args.path}")

        elif args.command == "set-gateway":
            gw = {"url": args.url}
            if args.advertised is not None:
                gw["tailnetEndpoint"] = args.advertised
            if args.token is not None:
                gw["token"] = args.token
            merged = put_settings({"gateway": gw}, token)
            print("OK gateway updated (Director re-registered live)")
            print(json.dumps(merged.get("gateway", {}), indent=2))

    except RuntimeError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
