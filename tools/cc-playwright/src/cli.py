"""cc-playwright CLI - browser automation with trusted CDP events.

Drop-in replacement for cc-browser's command surface where reliable form fills,
trusted clicks, and dropdown selection are required. Uses Playwright Python
against a Chromium-family browser (Brave, Chrome, Edge, or Chromium) launched
with --remote-debugging-port, which produces isTrusted=true events that React
forms accept.

Connections
-----------
Multiple cc-playwright browser instances can run concurrently, each isolated by
a named connection. Pass --connection <name> before any subcommand:

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
import shutil
import signal
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

# Environment override for the browser executable. A caller can point
# cc-playwright at any Chromium-family browser without editing code.
BROWSER_ENV_VAR = "CC_PLAYWRIGHT_BROWSER"

# Names looked up on PATH via shutil.which (covers Linux package names and any
# browser already exposed on PATH on Windows or macOS).
BROWSER_WHICH_NAMES = [
    "brave",
    "brave-browser",
    "chrome",
    "google-chrome",
    "google-chrome-stable",
    "chromium",
    "chromium-browser",
    "msedge",
    "microsoft-edge",
]

# Common fixed install locations, Windows first (primary platform) then macOS
# (secondary target). Brave is preferred, then Chrome, then Edge.
WINDOWS_BROWSER_PATHS = [
    r"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
    r"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]
MACOS_BROWSER_PATHS = [
    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    "/Applications/Chromium.app/Contents/MacOS/Chromium",
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
    if not f.exists():
        return {}
    try:
        return json.loads(f.read_text(encoding="utf-8"))
    except (ValueError, OSError) as ex:
        # A corrupt state file must not silently look like "no state" - that
        # could start a second browser on top of a live one or hide the real
        # failure. Move the bad file aside (so the tool can recover on the next
        # run) and warn loudly in ASCII on stderr.
        moved = f.with_suffix(f.suffix + ".corrupt")
        try:
            if moved.exists():
                moved.unlink()
            f.rename(moved)
            hint = f"moved aside to {moved}"
        except OSError as move_ex:
            hint = f"could not move it aside: {move_ex}"
        print(
            f"WARNING: state file for connection '{connection}' is corrupt "
            f"({type(ex).__name__}: {ex}); {hint}. Starting from empty state. "
            f"Re-run 'cc-playwright --connection {connection} start' to recreate it.",
            file=sys.stderr,
        )
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

def _candidate_browser_paths() -> list[str]:
    """Fixed install locations to probe, ordered by platform and preference."""
    if sys.platform == "win32":
        return list(WINDOWS_BROWSER_PATHS)
    if sys.platform == "darwin":
        return list(MACOS_BROWSER_PATHS)
    # Linux and others rely on PATH lookup below; no fixed list here.
    return []


def _find_browser(override: str | None = None) -> str:
    """Locate a Chromium-family browser executable.

    Resolution order (first hit wins):
      1. --browser-path argument (override)
      2. CC_PLAYWRIGHT_BROWSER environment variable
      3. shutil.which() lookup for common browser names on PATH
      4. common fixed install paths for Brave, Chrome, and Edge
         (Windows primary, macOS secondary)

    A bad explicit override fails immediately with a clear ASCII message; an
    empty search fails with a message that names the override options.
    """
    if override:
        if Path(override).exists():
            return override
        raise SystemExit(
            f"Browser executable not found at --browser-path: {override}"
        )

    env_path = os.environ.get(BROWSER_ENV_VAR)
    if env_path:
        if Path(env_path).exists():
            return env_path
        raise SystemExit(
            f"Browser executable not found at {BROWSER_ENV_VAR}={env_path}"
        )

    for name in BROWSER_WHICH_NAMES:
        found = shutil.which(name)
        if found:
            return found

    for p in _candidate_browser_paths():
        if Path(p).exists():
            return p

    raise SystemExit(
        "No Chromium-family browser found (looked for Brave, Chrome, and Edge "
        "on PATH and in common install locations). Install one, or point "
        f"cc-playwright at a browser with --browser-path or the "
        f"{BROWSER_ENV_VAR} environment variable."
    )


def _browser_command_lines() -> list[str]:
    """Best-effort list of command lines for running Chromium-family browsers.

    Factored out so the profile-lock pre-check is unit-testable. Failure to
    enumerate is not fatal: it only means the early "busy profile" hint cannot
    be produced, and start falls back to the debug-port timeout. This is an
    advisory probe, not core logic, so a failed enumeration must not block start.
    """
    if sys.platform == "win32":
        out = subprocess.run(
            [
                "powershell", "-NoProfile", "-Command",
                "Get-CimInstance Win32_Process -Filter "
                "\"Name='brave.exe' or Name='chrome.exe' or Name='msedge.exe' "
                "or Name='chromium.exe'\" "
                "| Select-Object -ExpandProperty CommandLine",
            ],
            capture_output=True, text=True, timeout=10,
        )
    else:
        out = subprocess.run(
            ["ps", "-eo", "args"], capture_output=True, text=True, timeout=10,
        )
    return [ln for ln in out.stdout.splitlines() if ln.strip()]


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


def _debug_port_responds(port: int) -> bool:
    """Return True if a Chromium debug endpoint answers /json/version on this port.

    A positive result confirms the recorded browser is genuinely listening, which
    distinguishes our live browser from an unrelated process that the OS happened
    to hand the same PID after our browser died. This is an identity probe, not
    core logic, so any failure resolves to False.
    """
    if not port:
        return False
    import urllib.request
    try:
        with urllib.request.urlopen(
            f"http://localhost:{port}/json/version", timeout=1
        ) as r:
            data = json.loads(r.read())
        return bool(data.get("webSocketDebuggerUrl"))
    except Exception:
        return False


def _state_browser_alive(state: dict) -> bool:
    """Return True only if the recorded PID is alive AND is really our browser.

    Guards against PID reuse. After a launched browser dies the operating system
    can reassign its PID to an unrelated process; a bare PID-existence check would
    then make `start` falsely report already_running (refusing to launch) and let
    `stop` taskkill an innocent process. Identity is confirmed against the state
    we recorded at launch: the debug port must still answer, or a running browser
    must still hold the recorded profile_dir. If neither confirms, the recorded
    PID is treated as not-our-browser.
    """
    pid = state.get("pid")
    if not pid or not _is_running(pid):
        return False
    if _debug_port_responds(state.get("port")):
        return True
    profile_dir = state.get("profile_dir")
    if profile_dir and _dir_locked_by_running_browser(Path(profile_dir)):
        return True
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


def _norm_path(p) -> str:
    """Normalize a path string for substring matching across slash styles."""
    return str(p).replace("\\", "/").rstrip("/").lower()


def _dir_locked_by_running_browser(profile_dir: Path) -> bool:
    """Return True if a running browser has this user-data-dir open.

    Works on Windows (where Chromium leaves no SingletonLock file) by scanning
    running browser command lines for a matching --user-data-dir. This is what
    catches a cc-browser instance holding the profile, so the user gets the
    "close cc-browser first" hint immediately instead of waiting the full
    debug-port timeout.
    """
    needle = _norm_path(profile_dir)
    try:
        command_lines = _browser_command_lines()
    except (subprocess.SubprocessError, OSError):
        # Advisory probe only; if process enumeration fails we cannot detect a
        # busy profile here. Start still fails clearly later via the debug-port
        # timeout, so do not block on an enumeration failure.
        return False
    for line in command_lines:
        low = _norm_path(line)
        if "--user-data-dir" in low and needle in low:
            return True
    return False


def _looks_locked(profile_dir: Path) -> bool:
    """Best-effort detect that another browser instance has the profile open."""
    if not profile_dir.exists():
        return False
    # Brave/Chrome on POSIX leaves a SingletonLock symlink. Presence is a strong
    # positive signal.
    for marker in ("SingletonLock", "SingletonCookie", "SingletonSocket"):
        if (profile_dir / marker).exists():
            return True
    # Windows leaves no such file, so scan running browser command lines for a
    # process that already has this user-data-dir open.
    return _dir_locked_by_running_browser(profile_dir)


def _connect(connection: str):
    """Connect Playwright to the running browser for this connection.
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
            f"Could not connect to the browser for '{connection}' on port {port}: {e}"
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


def _prune_to_single_tab(port: int, target_url: str | None) -> dict:
    """Close every tab except one matching target_url. Used after start to
    cancel Brave's session-restore (which would otherwise drag old tabs in
    every launch and confuse multi-tab automation).

    If target_url is given: keep one tab whose host matches; if no tab
    matches, navigate the most recently focused tab to target_url.
    If target_url is None: keep the most recently focused tab.

    Returns {"closed": N, "kept_url": "..."}.
    """
    from playwright.sync_api import sync_playwright

    pw = sync_playwright().start()
    closed = 0
    kept_url = None
    try:
        browser = pw.chromium.connect_over_cdp(f"http://localhost:{port}")
        if not browser.contexts:
            return {"closed": 0, "kept_url": None}
        ctx = browser.contexts[0]
        if not ctx.pages:
            return {"closed": 0, "kept_url": None}

        target_host = None
        if target_url:
            try:
                target_host = urlparse(target_url).netloc.lower() or None
            except Exception:
                target_host = None

        keep = None
        if target_host:
            matches = [
                p for p in ctx.pages
                if urlparse(p.url).netloc.lower() == target_host
            ]
            if matches:
                keep = matches[-1]
        if keep is None:
            keep = ctx.pages[-1]
            if target_url and keep.url != target_url:
                try:
                    keep.goto(target_url, wait_until="domcontentloaded", timeout=20_000)
                except Exception:
                    pass

        for p in list(ctx.pages):
            if p is keep:
                continue
            try:
                p.close()
                closed += 1
            except Exception:
                pass
        kept_url = keep.url
    finally:
        pw.stop()
    return {"closed": closed, "kept_url": kept_url}


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


def _cc_director_config_path() -> Path:
    """Path to cc-director's config.json (holds the configured screenshots dir)."""
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        root = Path(override)
    elif sys.platform == "win32":
        root = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".local"))) / "cc-director"
    elif sys.platform == "darwin":
        root = Path.home() / "Library" / "Application Support" / "cc-director"
    else:
        root = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local" / "share"))) / "cc-director"
    return root / "config" / "config.json"


def _configured_screenshots_dir() -> Path | None:
    """Return the screenshots directory configured in cc-director, or None.

    Reads screenshots.source_directory from cc-director's config.json. Returns
    None when there is no Director config (cc-playwright can run standalone), so
    the caller can pick its own default in that case.
    """
    config_path = _cc_director_config_path()
    if not config_path.exists():
        return None
    try:
        data = json.loads(config_path.read_text(encoding="utf-8"))
    except (ValueError, OSError):
        return None
    section = data.get("screenshots")
    if isinstance(section, dict):
        value = section.get("source_directory")
        if value:
            return Path(value)
    return None


def _wait_for_exit(pid: int, timeout: float) -> bool:
    """Poll until the process exits or the timeout elapses. True if it exited."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not _is_running(pid):
            return True
        time.sleep(0.2)
    return not _is_running(pid)


def _terminate_process(pid: int, grace_seconds: float = 6.0) -> str:
    """Stop a process, preferring a graceful close to protect the profile.

    Tries a normal terminate first (taskkill without /F on Windows, SIGTERM on
    POSIX), which lets Chromium flush and unlock its profile cleanly. Only if
    the process is still alive after grace_seconds does it force-kill the single
    recorded PID (no /T tree-kill). Returns the path taken: "graceful",
    "forced", or "already_exited".
    """
    if not _is_running(pid):
        return "already_exited"

    if sys.platform == "win32":
        subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)
    else:
        try:
            os.kill(pid, signal.SIGTERM)
        except OSError:
            return "already_exited"

    if _wait_for_exit(pid, grace_seconds):
        return "graceful"

    # Still alive - force only this PID.
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    else:
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            pass
    _wait_for_exit(pid, 3.0)
    return "forced"


# ---------------- Commands ----------------

def cmd_start(args: argparse.Namespace) -> None:
    connection = args.connection
    state = _load_state(connection)
    pid = state.get("pid")
    if _state_browser_alive(state):
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
    browser = _find_browser(getattr(args, "browser_path", None))

    cmd = [
        browser,
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
            f"The browser failed to expose debug port {port} within 15s for "
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

    # Cancel Brave's session-restore: close every tab except the one matching
    # --url (or, if no --url, just keep the most recently focused tab). Without
    # this, every restart accumulates stale tabs from previous sessions and
    # confuses tab-pinning. Best-effort -- failures here don't fail the start.
    prune_info = {"closed": 0, "kept_url": None}
    try:
        # Small grace period so Brave finishes opening session-restore tabs
        # before we prune them.
        time.sleep(1.5)
        prune_info = _prune_to_single_tab(port, args.url)
    except Exception as e:
        prune_info = {"closed": 0, "kept_url": None, "prune_error": str(e)}

    _ok({"status": "started", **new_state, "tabs_pruned": prune_info})


def cmd_stop(args: argparse.Namespace) -> None:
    connection = args.connection
    state = _load_state(connection)
    pid = state.get("pid")
    if not _state_browser_alive(state):
        # Either nothing is recorded, the PID is gone, or the PID is alive but
        # is not our browser (PID reuse). In every case clear the stale record
        # and report not_running rather than killing an unidentified process.
        _clear_state(connection)
        _ok({"status": "not_running", "connection": connection})
        return
    method = _terminate_process(pid)
    _clear_state(connection)
    _ok({"status": "stopped", "connection": connection, "pid": pid, "method": method})


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
        # input_value() only works on <input>/<textarea>/<select>. For
        # contenteditable elements it raises. Typing already succeeded by
        # this point, so swallow the inspection failure and report ok.
        try:
            actual = loc.input_value()
        except Exception:
            actual = None
        _ok({"typed": args.text, "value": actual})
    finally:
        pw.stop()


def cmd_set_files(args: argparse.Namespace) -> None:
    """Attach files to a file input (or any element bound to one).

    Uses Playwright's set_input_files which handles both <input type=file>
    and elements that expose a hidden file input via the file-chooser API.
    Multiple --path flags attach multiple files."""
    pw, browser, ctx, page = _connect(args.connection)
    try:
        loc = page.locator(args.selector).first
        files = [str(Path(p)) for p in args.path]
        for f in files:
            if not Path(f).exists():
                raise SystemExit(f"File does not exist: {f}")
        loc.set_input_files(files, timeout=args.timeout * 1000)
        _ok({"set_files": files, "selector": args.selector})
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
        if args.output:
            out = Path(args.output)
        else:
            # Default to the configured screenshots location so captures land
            # where the rest of cc-director puts them, not in the tool state dir.
            base = _configured_screenshots_dir() or STATE_DIR
            out = base / f"screenshot-{int(time.time())}.png"
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


_SNAPSHOT_INTERACTIVE_JS = """() => {
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

# Fuller page picture used only by snapshot --full: headings, landmark regions,
# and a slice of the visible body text. Deliberately different from (and on top
# of) the interactive-element list so callers get more than the same data back.
_SNAPSHOT_FULL_JS = """() => {
    const headings = [];
    document.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(h => {
        const rect = h.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) return;
        const t = (h.textContent || '').trim().slice(0, 120);
        if (t) headings.push({tag: h.tagName, text: t});
    });
    const landmarks = [];
    document.querySelectorAll(
        'nav,main,header,footer,aside,[role=navigation],[role=main],[role=banner],[role=contentinfo],[role=search]'
    ).forEach(el => {
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) return;
        landmarks.push({
            tag: el.tagName,
            role: el.getAttribute('role') || '',
            aria: el.getAttribute('aria-label') || '',
        });
    });
    const bodyText = (document.body ? (document.body.innerText || '') : '').trim();
    return {
        headings: headings,
        landmarks: landmarks,
        meta_description:
            (document.querySelector('meta[name=description]') || {}).content || '',
        text: bodyText.slice(0, 5000),
        text_length: bodyText.length,
    };
}"""


def cmd_snapshot(args: argparse.Namespace) -> None:
    """Return interactive elements (buttons, inputs, links) with selector hints.

    With --full, also include a fuller page picture: headings, landmark
    regions, the meta description, and a slice of the visible body text.
    """
    pw, browser, ctx, page = _connect(args.connection)
    try:
        items = page.evaluate(_SNAPSHOT_INTERACTIVE_JS)
        result = {
            "url": page.url,
            "title": page.title(),
            "full": bool(args.full),
            "elements": items,
        }
        if args.full:
            extra = page.evaluate(_SNAPSHOT_FULL_JS)
            result["headings"] = extra["headings"]
            result["landmarks"] = extra["landmarks"]
            result["meta_description"] = extra["meta_description"]
            result["text"] = extra["text"]
            result["text_length"] = extra["text_length"]
        _ok(result)
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


def cmd_close_tabs(args: argparse.Namespace) -> None:
    """Close every tab in this connection except one. Useful when Brave's
    session-restore brings back stale tabs you want gone."""
    state = _load_state(args.connection)
    pid = state.get("pid")
    port = state.get("port")
    if not pid or not _is_running(pid):
        _err(f"cc-playwright '{args.connection}' is not running")
    target = args.url or state.get("pinned_url")
    info = _prune_to_single_tab(port, target)
    _ok({"connection": args.connection, **info})


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
    # Every cc-* tool must answer `--version` - the installer and the Tools-page health check run it,
    # and without it argparse fails with "the following arguments are required: cmd" (a false FAIL).
    # The version action short-circuits before the required-subcommand check. Keep in sync with
    # src/__init__.py / pyproject.toml (hardcoded so a bad import can never break the whole CLI).
    p.add_argument("--version", action="version", version="cc-playwright 0.1.0")
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

    s = sub.add_parser("start", help="Launch the browser with remote debugging")
    s.add_argument("--profile", help=(
        "Override profile dir. Default for the implicit 'default' connection "
        "is cc-playwright's own profile; any named connection defaults to "
        "%%LOCALAPPDATA%%/cc-director/connections/<name> (shares with cc-browser)."
    ))
    s.add_argument("--port", type=int, help=(
        f"Preferred debug port. Auto-allocated from {PORT_SCAN_START} upward "
        "if not given or already in use."
    ))
    s.add_argument("--url", help="Open this URL after launch (also pins the tab)")
    s.add_argument("--browser-path", help=(
        "Path to the browser executable to launch. Overrides the "
        f"{BROWSER_ENV_VAR} environment variable and auto-discovery. "
        "Accepts any Chromium-family browser (Brave, Chrome, Edge)."
    ))
    s.set_defaults(func=cmd_start)

    s = sub.add_parser("stop", help="Kill this connection's browser instance")
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

    s = sub.add_parser("set-files", help=(
        "Attach files to a file input or file-chooser-bound element"
    ))
    s.add_argument("--selector", required=True)
    s.add_argument("--path", required=True, action="append", help=(
        "Path to a file to attach. Repeat for multiple files."
    ))
    s.add_argument("--timeout", type=int, default=15)
    s.set_defaults(func=cmd_set_files)

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

    s = sub.add_parser("close-tabs", help=(
        "Close every tab except one. If --url given, keep the tab matching "
        "that URL (navigating if needed); otherwise keep the most recent."
    ))
    s.add_argument("--url", help="Target URL to keep; defaults to pinned_url")
    s.set_defaults(func=cmd_close_tabs)

    s = sub.add_parser("new-tab", help="Open a new tab")
    s.add_argument("--url")
    s.set_defaults(func=cmd_new_tab)

    args = p.parse_args()
    # Single catch-all at the entry point (where try/except is allowed per the
    # coding standard) so every command holds the JSON error contract. Without
    # this, an uncaught Playwright TimeoutError (or any other error) inside an
    # action command would dump a raw traceback instead of {"error": ...}.
    try:
        args.func(args)
    except SystemExit as se:
        # Two kinds of SystemExit reach here and they must NOT be treated alike:
        #   * _err() already printed a {"error": ...} JSON line and exited with an
        #     integer code - se.code is that int (or None). Let it through so we
        #     do not print the error twice.
        #   * The discovery/connect/locate helpers raise SystemExit("plain text")
        #     with a string payload. Reshape that into the same {"error": ...}
        #     JSON contract before exiting, so a consumer sees one error shape
        #     regardless of which failure fired.
        if isinstance(se.code, str):
            _err(se.code)
        raise
    except KeyboardInterrupt:
        _err("interrupted", code=130)
    except Exception as ex:
        _err(f"{type(ex).__name__}: {ex}")


if __name__ == "__main__":
    main()
