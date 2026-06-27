#!/usr/bin/env python3
"""
cc-launcher — a tiny REST daemon that launches CC Director and controls windows.

Why this exists: the Claude Code agent runs cc-director's automated builds, but it
cannot launch the GUI itself — anything it spawns inherits its (non-TTY) process
tree, so cc-director's child `claude` processes break, and the agent otherwise has
to ask a human to relaunch. This daemon is registered with launchd, so it runs in
the clean Aqua GUI session OUTSIDE the agent's process tree. The agent POSTs to it
to start/stop/restart cc-director; because the daemon is the parent, cc-director
launches cleanly every time. That enables an edit -> rebuild -> /restart ->
screenshot iterate loop with no human in the middle.

Because it runs in the GUI session, the same daemon is the right place to drive
*any* app's windows (minimize / restore / maximize / focus) via the macOS
Accessibility API. NOTE: window control requires the daemon's interpreter
(/usr/bin/python3) to be granted Accessibility permission in
System Settings -> Privacy & Security -> Accessibility (one-time manual grant;
screenshots likewise need Screen Recording). Until granted, window ops error out.

Endpoints (localhost only):
  GET  /status                -> {running, pid, uptime_s, bin}
  POST /start                 -> start cc-director if not already running
  POST /stop                  -> stop the cc-director we started
  POST /restart               -> stop + start
  GET  /screenshot            -> capture the screen to a PNG, return its path
  GET  /logs?n=80             -> last n lines of the cc-director stdout/stderr log
  GET  /windows[?app=NAME]    -> list windows (all apps, or just NAME)
  POST /window/minimize       -> body {"app":"NAME"[,"window":1]}  minimize a window
  POST /window/restore        -> body {"app":"NAME"[,"window":1]}  un-minimize a window
  POST /window/maximize       -> body {"app":"NAME"[,"window":1]}  click the green zoom button
  POST /window/focus          -> body {"app":"NAME"[,"window":1]}  bring app+window to front

Config via environment (set in the launchd plist):
  CCD_BIN              absolute path to the cc-director binary to launch
  DOTNET_ROOT          .NET runtime location (framework-dependent build needs it)
  CCL_PORT             TCP port to listen on (default 8765)
  CCL_DIR              working/log directory (default: this script's dir)
"""
import json
import os
import signal
import subprocess
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = int(os.environ.get("CCL_PORT", "8765"))
WORKDIR = os.environ.get("CCL_DIR", HERE)
CCD_BIN = os.environ.get(
    "CCD_BIN",
    os.path.expanduser("~/ReposFred/devthrottle/local_builds/mac/cc-director-mac1"),
)
DOTNET_ROOT = os.environ.get("DOTNET_ROOT", os.path.expanduser("~/.dotnet"))

APP_LOG = os.path.join(WORKDIR, "cc-director.out.log")
SHOT_DIR = os.path.join(WORKDIR, "shots")
os.makedirs(SHOT_DIR, exist_ok=True)

# Tracks only the cc-director process WE launched (never the user's own).
_proc = None  # type: subprocess.Popen | None
_started_at = 0.0


def _is_running():
    global _proc
    if _proc is None:
        return False
    if _proc.poll() is None:
        return True
    _proc = None
    return False


def start():
    global _proc, _started_at
    if _is_running():
        return {"ok": True, "already": True, "pid": _proc.pid}
    if not os.path.exists(CCD_BIN):
        return {"ok": False, "error": f"binary not found: {CCD_BIN}"}

    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT
    env["PATH"] = f"{DOTNET_ROOT}:{os.path.expanduser('~/.local/bin')}:{env.get('PATH','')}"

    logf = open(APP_LOG, "ab", buffering=0)
    logf.write(f"\n===== start {time.strftime('%Y-%m-%d %H:%M:%S')} {CCD_BIN} =====\n".encode())
    # start_new_session detaches into its own session/process group, so cc-director
    # and its child claude processes are fully independent of this daemon's controlling
    # terminal (there isn't one) -- exactly the clean launch we need.
    _proc = subprocess.Popen(
        [CCD_BIN],
        cwd=os.path.dirname(CCD_BIN),
        env=env,
        stdout=logf,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    _started_at = time.time()
    return {"ok": True, "pid": _proc.pid, "bin": CCD_BIN}


def stop():
    global _proc
    if not _is_running():
        return {"ok": True, "already_stopped": True}
    pid = _proc.pid
    try:
        # Signal the whole process group we created.
        os.killpg(os.getpgid(pid), signal.SIGTERM)
    except ProcessLookupError:
        pass
    for _ in range(50):
        if _proc.poll() is not None:
            break
        time.sleep(0.1)
    if _proc.poll() is None:
        try:
            os.killpg(os.getpgid(pid), signal.SIGKILL)
        except ProcessLookupError:
            pass
    _proc = None
    return {"ok": True, "stopped_pid": pid}


def status():
    return {
        "running": _is_running(),
        "pid": _proc.pid if _is_running() else None,
        "uptime_s": round(time.time() - _started_at, 1) if _is_running() else 0,
        "bin": CCD_BIN,
        "exists": os.path.exists(CCD_BIN),
    }


def screenshot():
    path = os.path.join(SHOT_DIR, f"shot-{time.strftime('%H%M%S')}.png")
    rc = subprocess.run(["/usr/sbin/screencapture", "-x", path]).returncode
    return {"ok": rc == 0, "path": path if rc == 0 else None, "rc": rc}


def logs(n):
    if not os.path.exists(APP_LOG):
        return {"ok": True, "lines": []}
    with open(APP_LOG, "rb") as f:
        data = f.read().decode("utf-8", "replace").splitlines()
    return {"ok": True, "lines": data[-n:]}


# ----------------------------------------------------------------------------
# Window control via the macOS Accessibility API (System Events / osascript).
# Requires Accessibility permission for /usr/bin/python3 (one-time, manual).
# ----------------------------------------------------------------------------

def _osa(script):
    """Run an AppleScript and return (rc, stdout, stderr). A missing Accessibility
    grant surfaces here as a non-zero rc with errAEEventNotPermitted in stderr."""
    p = subprocess.run(
        ["/usr/bin/osascript", "-e", script],
        capture_output=True, text=True,
    )
    return p.returncode, p.stdout.strip(), p.stderr.strip()


def _valid_app(name):
    # AppleScript string literals can't contain a double quote or newline; reject
    # rather than escape, so we never build a malformed or injected script.
    return isinstance(name, str) and bool(name) and '"' not in name and "\n" not in name


def list_windows(app=None):
    if app is not None and not _valid_app(app):
        return {"ok": False, "error": "invalid app name"}
    if app:
        script = (
            'tell application "System Events" to tell process "%s"\n'
            '  set out to ""\n'
            '  repeat with w in windows\n'
            '    set out to out & (name of w) & linefeed\n'
            '  end repeat\n'
            '  return out\n'
            'end tell' % app
        )
        rc, out, err = _osa(script)
        if rc != 0:
            return {"ok": False, "error": err or f"rc={rc}", "app": app}
        titles = [t for t in out.split("\n") if t != ""]
        return {"ok": True, "app": app, "windows": titles, "count": len(titles)}

    # All foreground apps and their window titles. "background only is false"
    # restricts to apps that can present UI.
    script = (
        'tell application "System Events"\n'
        '  set out to ""\n'
        '  repeat with p in (processes whose background only is false)\n'
        '    set pname to name of p\n'
        '    repeat with w in (windows of p)\n'
        '      set out to out & pname & "\\t" & (name of w) & linefeed\n'
        '    end repeat\n'
        '  end repeat\n'
        '  return out\n'
        'end tell'
    )
    rc, out, err = _osa(script)
    if rc != 0:
        return {"ok": False, "error": err or f"rc={rc}"}
    apps = {}
    for line in out.split("\n"):
        if not line:
            continue
        name, _, title = line.partition("\t")
        apps.setdefault(name, []).append(title)
    return {"ok": True, "apps": [{"app": k, "windows": v} for k, v in apps.items()]}


def window_op(app, window, action):
    """action: minimize | restore | maximize | focus."""
    if not _valid_app(app):
        return {"ok": False, "error": "invalid or missing app name"}
    try:
        window = int(window)
    except (TypeError, ValueError):
        return {"ok": False, "error": "window must be an integer index"}

    if action == "minimize":
        body = f'set value of attribute "AXMinimized" of window {window} to true'
    elif action == "restore":
        body = f'set value of attribute "AXMinimized" of window {window} to false'
    elif action == "maximize":
        # Press the green zoom button = exactly what clicking it does.
        body = (
            f'perform action "AXPress" of '
            f'(first button of window {window} whose subrole is "AXZoomButton")'
        )
    elif action == "focus":
        body = (
            'set frontmost to true\n'
            f'  perform action "AXRaise" of window {window}'
        )
    else:
        return {"ok": False, "error": f"unknown action: {action}"}

    script = (
        f'tell application "System Events" to tell process "{app}"\n'
        f'  {body}\n'
        f'end tell'
    )
    rc, out, err = _osa(script)
    if rc != 0:
        return {"ok": False, "error": err or f"rc={rc}", "app": app,
                "window": window, "action": action}
    return {"ok": True, "app": app, "window": window, "action": action}


class Handler(BaseHTTPRequestHandler):
    def _send(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8")) or {}
        except (ValueError, UnicodeDecodeError):
            return None  # signals a malformed body

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if u.path == "/status":
            self._send(status())
        elif u.path == "/screenshot":
            self._send(screenshot())
        elif u.path == "/logs":
            n = int(q.get("n", ["80"])[0])
            self._send(logs(n))
        elif u.path == "/windows":
            self._send(list_windows(q.get("app", [None])[0]))
        elif u.path in ("/", "/health"):
            self._send({"ok": True, "service": "cc-launcher", "port": PORT})
        else:
            self._send({"ok": False, "error": "not found"}, 404)

    def do_POST(self):
        u = urlparse(self.path)
        if u.path == "/start":
            self._send(start())
        elif u.path == "/stop":
            self._send(stop())
        elif u.path == "/restart":
            stop()
            time.sleep(0.5)
            self._send(start())
        elif u.path.startswith("/window/"):
            body = self._body()
            if body is None:
                self._send({"ok": False, "error": "invalid JSON body"}, 400)
                return
            action = u.path[len("/window/"):]
            self._send(window_op(body.get("app"), body.get("window", 1), action))
        else:
            self._send({"ok": False, "error": "not found"}, 404)

    def log_message(self, *a):
        pass  # quiet


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"cc-launcher listening on http://127.0.0.1:{PORT} (bin={CCD_BIN})", flush=True)
    srv.serve_forever()
