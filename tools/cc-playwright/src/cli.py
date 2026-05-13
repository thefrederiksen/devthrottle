"""cc-playwright CLI - browser automation with trusted CDP events.

Drop-in replacement for cc-browser's command surface where reliable form fills,
trusted clicks, and dropdown selection are required. Uses Playwright Python
against a Brave (Chromium) instance launched with --remote-debugging-port,
which produces isTrusted=true events that React forms accept.

Connections
-----------
Multiple cc-playwright Brave instances can run concurrently, each isolated by a
named connection. Pass --connection <name> before any subcommand:

    cc-playwright --connection linkedin start
    cc-playwright --connection linkedin navigate --url https://www.linkedin.com/feed/

Per-connection state lives in state/<name>.json. Each connection auto-allocates
its own debug port on first start. If --profile is not passed, a named
connection resolves to cc-director's connection dir (sharing cookies with
cc-browser); the implicit "default" connection uses cc-playwright's own profile.
"""
from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

STATE_DIR = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local"))) / "cc-playwright"
LEGACY_STATE_FILE = STATE_DIR / "state.json"
CONNECTIONS_STATE_DIR = STATE_DIR / "state"
DEFAULT_PROFILE_DIR = STATE_DIR / "profile"  # used by the implicit "default" connection
CC_DIRECTOR_CONNECTIONS_DIR = (
    Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local")))
    / "cc-director" / "connections"
)
DEFAULT_CONNECTION = "default"
PORT_SCAN_START = 9223
PORT_SCAN_END = 9999
BRAVE_PATHS = [
    r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    r"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
]


# ---------------- State ----------------

def _state_file(connection: str) -> Path:
    return CONNECTIONS_STATE_DIR / f"{connection}.json"


def _migrate_legacy_state_if_needed() -> None:
    """One-time: copy legacy state.json to state/default.json so existing
    cc-playwright Brave instances launched by older clients keep working."""
    if LEGACY_STATE_FILE.exists() and not _state_file(DEFAULT_CONNECTION).exists():
        try:
            CONNECTIONS_STATE_DIR.mkdir(parents=True, exist_ok=True)
            _state_file(DEFAULT_CONNECTION).write_text(
                LEGACY_STATE_FILE.read_text(encoding="utf-8"), encoding="utf-8"
            )
        except Exception:
            pass


def _load_state(connection: str) -> dict:
    _migrate_legacy_state_if_needed()
    f = _state_file(connection)
    if f.exists():
        try:
            return json.loads(f.read_text(encoding="utf-8"))
        except Exception:
            return {}
    return {}


def _save_state(connection: str, state: dict) -> None:
    CONNECTIONS_STATE_DIR.mkdir(parents=True, exist_ok=True)
    state = {**state, "connection": connection}
    _state_file(connection).write_text(json.dumps(state, indent=2), encoding="utf-8")
    # Keep legacy state.json in sync for the default connection so that any
    # client still pointed at the old file path keeps working until rebuilt.
    if connection == DEFAULT_CONNECTION:
        try:
            LEGACY_STATE_FILE.write_text(json.dumps(state, indent=2), encoding="utf-8")
        except Exception:
            pass


def _clear_state(connection: str) -> None:
    f = _state_file(connection)
    if f.exists():
        try:
            f.unlink()
        except Exception:
            pass
    if connection == DEFAULT_CONNECTION and LEGACY_STATE_FILE.exists():
        try:
            LEGACY_STATE_FILE.unlink()
        except Exception:
            pass


def _all_connections() -> list[str]:
    _migrate_legacy_state_if_needed()
    if not CONNECTIONS_STATE_DIR.exists():
        return []
    return sorted(p.stem for p in CONNECTIONS_STATE_DIR.glob("*.json"))


# ---------------- Helpers ----------------

def _find_brave() -> str:
    for p in BRAVE_PATHS:
        if Path(p).exists():
            return p
    raise SystemExit("Brave not found. Looked in: " + ", ".join(BRAVE_PATHS))


def _is_running(pid: int) -> bool:
    if not pid:
        return False
    if sys.platform == "win32":
        try:
            out = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                capture_output=True, text=True, timeout=5,
            )
            return str(pid) in out.stdout
        except Exception:
            return False
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def _port_in_use(port: int) -> bool:
    """Return True if the port is bound by something on localhost."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.2)
        try:
            s.bind(("127.0.0.1", port))
            return False
        except OSError:
            return True


def _find_free_port(preferred: int | None = None) -> int:
    """Return a free TCP port on localhost. Prefers `preferred` if supplied
    and free; otherwise scans PORT_SCAN_START..PORT_SCAN_END and skips ports
    already used by other cc-playwright connections."""
    used_ports = set()
    for name in _all_connections():
        st = _load_state(name)
        p = st.get("port")
        pid = st.get("pid")
        if p and pid and _is_running(pid):
            used_ports.add(p)

    if preferred and preferred not in used_ports and not _port_in_use(preferred):
        return preferred

    for port in range(PORT_SCAN_START, PORT_SCAN_END + 1):
        if port in used_ports:
            continue
        if _port_in_use(port):
            continue
        return port
    raise SystemExit(
        f"No free port available between {PORT_SCAN_START} and {PORT_SCAN_END}"
    )


def _resolve_profile_dir(connection: str, override: str | None) -> Path:
    """Resolve the user-data-dir for a connection.

    --profile takes precedence. Otherwise:
      - "default" connection -> cc-playwright's own profile dir
      - any other name        -> cc-director/connections/<name> (shares cookies
                                  with cc-browser's connection of the same name)
    """
    if override:
        return Path(override)
    if connection == DEFAULT_CONNECTION:
        return DEFAULT_PROFILE_DIR
    return CC_DIRECTOR_CONNECTIONS_DIR / connection


def _looks_locked(profile_dir: Path) -> bool:
    """Best-effort detect that another browser instance has the profile open."""
    if not profile_dir.exists():
        return False
    # Brave/Chrome on POSIX leaves a SingletonLock symlink. Windows uses file
    # handles, so absence of these files does NOT mean unlocked. Presence is
    # a strong positive signal though.
    for marker in ("SingletonLock", "SingletonCookie", "SingletonSocket"):
        if (profile_dir / marker).exists():
            return True
    return False


def _connect(connection: str):
    """Connect Playwright to running Brave for this connection.
    Returns (playwright, browser, context, page). The page is chosen by
    matching the connection's pinned_host (set on `start --url`); falls back
    to the most recent tab if no match exists."""
    from playwright.sync_api import sync_playwright

    state = _load_state(connection)
    port = state.get("port")
    pid = state.get("pid")

    if not pid or not _is_running(pid):
        raise SystemExit(
            f"cc-playwright '{connection}' is not running. Start it first:\n"
            f"  cc-playwright --connection {connection} start"
        )

    pw = sync_playwright().start()
    try:
        browser = pw.chromium.connect_over_cdp(f"http://localhost:{port}")
    except Exception as e:
        pw.stop()
        raise SystemExit(
            f"Could not connect to Brave for '{connection}' on port {port}: {e}"
        )

    if not browser.contexts:
        ctx = browser.new_context()
    else:
        ctx = browser.contexts[0]

    if not ctx.pages:
        page = ctx.new_page()
    else:
        page = _select_pinned_page(ctx, state)

    return pw, browser, ctx, page


def _select_pinned_page(ctx, state: dict):
    """Return the page best matching the connection's pinned_host.
    If multiple match, pick the most recently focused (last in list).
    If none match, fall back to ctx.pages[-1]."""
    pinned_host = state.get("pinned_host")
    if pinned_host:
        matches = []
        for p in ctx.pages:
            try:
                if urlparse(p.url).netloc.lower() == pinned_host.lower():
                    matches.append(p)
            except Exception:
                continue
        if matches:
            return matches[-1]
    return ctx.pages[-1]


def _ok(data: Any) -> None:
    print(json.dumps(data, indent=2, default=str))


def _err(msg: str, code: int = 1) -> None:
    print(json.dumps({"error": msg}), file=sys.stderr)
    sys.exit(code)


def _host_of(url: str | None) -> str | None:
    if not url:
        return None
    try:
        return urlparse(url).netloc or None
    except Exception:
        return None


# ---------------- Commands ----------------

def cmd_start(args: argparse.Namespace) -> None:
    connection = args.connection
    state = _load_state(connection)
    pid = state.get("pid")
    if pid and _is_running(pid):
        _ok({
            "status": "already_running",
            "connection": connection,
            "pid": pid,
            "port": state.get("port"),
            "profile_dir": state.get("profile_dir"),
        })
        return

    profile_dir = _resolve_profile_dir(connection, args.profile)
    profile_dir.mkdir(parents=True, exist_ok=True)

    if _looks_locked(profile_dir):
        _err(
            f"Profile dir appears locked by another browser instance: {profile_dir}\n"
            f"If cc-browser has the '{connection}' connection open, close it first."
        )

    port = _find_free_port(preferred=args.port)
    brave = _find_brave()

    cmd = [
        brave,
        f"--remote-debugging-port={port}",
        f"--user-data-dir={profile_dir}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-features=BraveRewards,BraveWallet,BraveAds",
    ]
    if args.url:
        cmd.append(args.url)

    proc = subprocess.Popen(
        cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if sys.platform == "win32" else 0,
    )

    # Wait up to 15s for debug port to respond
    import urllib.request
    deadline = time.time() + 15
    ws_endpoint = None
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://localhost:{port}/json/version", timeout=1) as r:
                data = json.loads(r.read())
                ws_endpoint = data.get("webSocketDebuggerUrl")
                break
        except Exception:
            time.sleep(0.3)

    if not ws_endpoint:
        proc.kill()
        _err(
            f"Brave failed to expose debug port {port} within 15s for "
            f"connection '{connection}'. Profile dir: {profile_dir}. "
            f"If cc-browser has the same profile open, close it and retry."
        )

    new_state = {
        "connection": connection,
        "pid": proc.pid,
        "port": port,
        "profile_dir": str(profile_dir),
        "ws_endpoint": ws_endpoint,
        "started_at": time.time(),
        "pinned_host": _host_of(args.url),
        "pinned_url": args.url,
    }
    _save_state(connection, new_state)
    _ok({"status": "started", **new_state})


def cmd_stop(args: argparse.Namespace) -> None:
    connection = args.connection
    state = _load_state(connection)
    pid = state.get("pid")
    if not pid or not _is_running(pid):
        _clear_state(connection)
        _ok({"status": "not_running", "connection": connection})
        return
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)], capture_output=True)
    else:
        os.kill(pid, 15)
    _clear_state(connection)
    _ok({"status": "stopped", "connection": connection, "pid": pid})


def cmd_status(args: argparse.Namespace) -> None:
    connection = args.connection
    state = _load_state(connection)
    pid = state.get("pid")
    running = bool(pid and _is_running(pid))
    _ok({"running": running, "connection": connection, **state})


def cmd_list(args: argparse.Namespace) -> None:
    rows = []
    for name in _all_connections():
        st = _load_state(name)
        pid = st.get("pid")
        rows.append({
            "connection": name,
            "running": bool(pid and _is_running(pid)),
            "pid": pid,
            "port": st.get("port"),
            "profile_dir": st.get("profile_dir"),
            "pinned_host": st.get("pinned_host"),
        })
    _ok({"connections": rows})


def cmd_navigate(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        page.goto(args.url, wait_until="domcontentloaded", timeout=args.timeout * 1000)
        # Update pinned_host so subsequent commands lock to whichever site we
        # last navigated to. This keeps multi-tab profiles from drifting.
        st = _load_state(args.connection)
        st["pinned_host"] = _host_of(args.url)
        st["pinned_url"] = args.url
        _save_state(args.connection, st)
        _ok({"url": page.url, "title": page.title()})
    finally:
        pw.stop()


def _locate(page, args: argparse.Namespace):
    if args.selector:
        return page.locator(args.selector).first
    if args.text:
        return page.get_by_text(args.text, exact=False).first
    if args.role:
        return page.get_by_role(args.role, name=args.text).first
    raise SystemExit("Need --selector, --text, or --role")


def cmd_click(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = _locate(page, args)
        loc.click(timeout=args.timeout * 1000)
        _ok({"clicked": True})
    finally:
        pw.stop()


def cmd_fill(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = page.locator(args.selector).first
        loc.fill(args.value, timeout=args.timeout * 1000)
        actual = loc.input_value()
        _ok({"filled": args.selector, "value": actual})
    finally:
        pw.stop()


def cmd_type(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = page.locator(args.selector).first
        loc.press_sequentially(args.text, delay=args.delay, timeout=args.timeout * 1000)
        actual = loc.input_value()
        _ok({"typed": args.text, "value": actual})
    finally:
        pw.stop()


def cmd_press(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        if args.selector:
            page.locator(args.selector).first.press(args.key, timeout=args.timeout * 1000)
        else:
            page.keyboard.press(args.key)
        _ok({"pressed": args.key})
    finally:
        pw.stop()


def cmd_select(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = page.locator(args.selector).first
        if args.value:
            result = loc.select_option(value=args.value)
        elif args.label:
            result = loc.select_option(label=args.label)
        elif args.index is not None:
            result = loc.select_option(index=args.index)
        else:
            raise SystemExit("Need --value, --label, or --index")
        _ok({"selected": result})
    finally:
        pw.stop()


def cmd_check(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = page.locator(args.selector).first
        if args.uncheck:
            loc.uncheck(timeout=args.timeout * 1000)
        else:
            loc.check(timeout=args.timeout * 1000)
        _ok({"checked": not args.uncheck})
    finally:
        pw.stop()


def cmd_evaluate(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        result = page.evaluate(args.fn)
        _ok({"result": result})
    finally:
        pw.stop()


def cmd_screenshot(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        out = Path(args.output) if args.output else STATE_DIR / f"screenshot-{int(time.time())}.png"
        out.parent.mkdir(parents=True, exist_ok=True)
        page.screenshot(path=str(out), full_page=args.full_page)
        _ok({"screenshot": str(out)})
    finally:
        pw.stop()


def cmd_info(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        _ok({
            "connection": args.connection,
            "url": page.url,
            "title": page.title(),
            "viewport": page.viewport_size,
            "pages": len(ctx.pages),
        })
    finally:
        pw.stop()


def cmd_snapshot(args: argparse.Namespace) -> None:
    """Return interactive elements (buttons, inputs, links) with selector hints."""
    pw, browser, ctx, page = _connect(args.connection)
    try:
        items = page.evaluate(
            """() => {
                const tags = ['button', 'input', 'textarea', 'select', 'a'];
                const all = [];
                tags.forEach(tag => {
                    document.querySelectorAll(tag).forEach((el, i) => {
                        const rect = el.getBoundingClientRect();
                        if (rect.width === 0 || rect.height === 0) return;
                        const r = el.getBoundingClientRect();
                        all.push({
                            tag: el.tagName,
                            text: (el.textContent || '').trim().slice(0, 80),
                            placeholder: el.placeholder || '',
                            name: el.name || '',
                            id: el.id || '',
                            type: el.type || '',
                            href: el.href || '',
                            value: (el.value || '').slice(0, 60),
                            label: el.labels?.[0]?.textContent?.trim().slice(0, 80) || '',
                            aria: el.getAttribute('aria-label') || '',
                            visible: rect.width > 0 && rect.height > 0,
                            x: Math.round(r.x), y: Math.round(r.y),
                        });
                    });
                });
                return all;
            }"""
        )
        _ok({"url": page.url, "title": page.title(), "elements": items})
    finally:
        pw.stop()


def cmd_wait(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        if args.selector:
            page.wait_for_selector(args.selector, timeout=args.timeout * 1000)
            _ok({"waited": args.selector})
        elif args.text:
            page.get_by_text(args.text, exact=False).first.wait_for(timeout=args.timeout * 1000)
            _ok({"waited": args.text})
        elif args.networkidle:
            page.wait_for_load_state("networkidle", timeout=args.timeout * 1000)
            _ok({"waited": "networkidle"})
        else:
            raise SystemExit("Need --selector, --text, or --networkidle")
    finally:
        pw.stop()


def cmd_tabs(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        tabs = [{"index": i, "url": p.url, "title": p.title()} for i, p in enumerate(ctx.pages)]
        _ok({"tabs": tabs})
    finally:
        pw.stop()


def cmd_new_tab(args: argparse.Namespace) -> None:
    pw, browser, ctx, page = _connect(args.connection)
    try:
        new_page = ctx.new_page()
        if args.url:
            new_page.goto(args.url, wait_until="domcontentloaded")
        _ok({"opened": True, "url": new_page.url, "index": len(ctx.pages) - 1})
    finally:
        pw.stop()


# ---------------- Argparse ----------------

def main() -> None:
    p = argparse.ArgumentParser(prog="cc-playwright", description=__doc__)
    p.add_argument(
        "--connection", "-c",
        default=os.environ.get("CC_PLAYWRIGHT_CONNECTION", DEFAULT_CONNECTION),
        help=(
            f"Connection name. Defaults to '{DEFAULT_CONNECTION}' "
            f"(or env CC_PLAYWRIGHT_CONNECTION). Each connection has its own "
            f"port, profile, and state file."
        ),
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("start", help="Launch Brave with remote debugging")
    s.add_argument("--profile", help=(
        "Override profile dir. Default for the implicit 'default' connection "
        "is cc-playwright's own profile; any named connection defaults to "
        "%LOCALAPPDATA%/cc-director/connections/<name> (shares with cc-browser)."
    ))
    s.add_argument("--port", type=int, help=(
        f"Preferred debug port. Auto-allocated from {PORT_SCAN_START} upward "
        "if not given or already in use."
    ))
    s.add_argument("--url", help="Open this URL after launch (also pins the tab)")
    s.set_defaults(func=cmd_start)

    s = sub.add_parser("stop", help="Kill this connection's Brave instance")
    s.set_defaults(func=cmd_stop)

    s = sub.add_parser("status", help="Show this connection's running state")
    s.set_defaults(func=cmd_status)

    s = sub.add_parser("list", help="List all known connections and their state")
    s.set_defaults(func=cmd_list)

    s = sub.add_parser("navigate", help="Navigate to URL")
    s.add_argument("--url", required=True)
    s.add_argument("--timeout", type=int, default=30)
    s.set_defaults(func=cmd_navigate)

    s = sub.add_parser("click", help="Click an element (trusted event)")
    s.add_argument("--selector")
    s.add_argument("--text")
    s.add_argument("--role")
    s.add_argument("--timeout", type=int, default=10)
    s.set_defaults(func=cmd_click)

    s = sub.add_parser("fill", help="Fill an input/textarea (trusted)")
    s.add_argument("--selector", required=True)
    s.add_argument("--value", required=True)
    s.add_argument("--timeout", type=int, default=10)
    s.set_defaults(func=cmd_fill)

    s = sub.add_parser("type", help="Type with real keystrokes (slower; useful for autocompletes)")
    s.add_argument("--selector", required=True)
    s.add_argument("--text", required=True)
    s.add_argument("--delay", type=int, default=20)
    s.add_argument("--timeout", type=int, default=30)
    s.set_defaults(func=cmd_type)

    s = sub.add_parser("press", help="Press a key (e.g. Enter, Tab, Control+a)")
    s.add_argument("--key", required=True)
    s.add_argument("--selector")
    s.add_argument("--timeout", type=int, default=10)
    s.set_defaults(func=cmd_press)

    s = sub.add_parser("select", help="Select an option from a <select> dropdown")
    s.add_argument("--selector", required=True)
    s.add_argument("--value")
    s.add_argument("--label")
    s.add_argument("--index", type=int)
    s.set_defaults(func=cmd_select)

    s = sub.add_parser("check", help="Check or uncheck a checkbox/radio")
    s.add_argument("--selector", required=True)
    s.add_argument("--uncheck", action="store_true")
    s.add_argument("--timeout", type=int, default=10)
    s.set_defaults(func=cmd_check)

    s = sub.add_parser("evaluate", help="Run JavaScript and return the result")
    s.add_argument("--fn", required=True)
    s.set_defaults(func=cmd_evaluate)

    s = sub.add_parser("screenshot", help="Capture screenshot")
    s.add_argument("--output")
    s.add_argument("--full-page", action="store_true")
    s.set_defaults(func=cmd_screenshot)

    s = sub.add_parser("info", help="Current URL, title, viewport")
    s.set_defaults(func=cmd_info)

    s = sub.add_parser("snapshot", help="List interactive elements")
    s.add_argument("--full", action="store_true")
    s.set_defaults(func=cmd_snapshot)

    s = sub.add_parser("wait", help="Wait for selector/text/networkidle")
    s.add_argument("--selector")
    s.add_argument("--text")
    s.add_argument("--networkidle", action="store_true")
    s.add_argument("--timeout", type=int, default=30)
    s.set_defaults(func=cmd_wait)

    s = sub.add_parser("tabs", help="List tabs")
    s.set_defaults(func=cmd_tabs)

    s = sub.add_parser("new-tab", help="Open a new tab")
    s.add_argument("--url")
    s.set_defaults(func=cmd_new_tab)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
