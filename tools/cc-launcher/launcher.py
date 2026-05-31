#!/usr/bin/env python3
"""
cc-director-launcher — a tiny REST daemon that starts/stops CC Director.

Why this exists: the Claude Code agent runs cc-director's automated builds, but it
cannot launch the GUI itself — anything it spawns inherits its (non-TTY) process
tree, so cc-director's child `claude` processes break, and the agent otherwise has
to ask a human to relaunch. This daemon is registered with launchd, so it runs in
the clean Aqua GUI session OUTSIDE the agent's process tree. The agent POSTs to it
to start/stop/restart cc-director; because the daemon is the parent, cc-director
launches cleanly every time. That enables an edit -> rebuild -> /restart ->
screenshot iterate loop with no human in the middle.

Endpoints (localhost only):
  GET  /status                -> {running, pid, uptime_s, bin}
  POST /start                 -> start cc-director if not already running
  POST /stop                  -> stop the cc-director we started
  POST /restart               -> stop + start
  GET  /screenshot            -> capture the screen to a PNG, return its path
  GET  /logs?n=80             -> last n lines of the cc-director stdout/stderr log

Config via environment (set in the launchd plist):
  CCD_BIN              absolute path to the cc-director binary to launch
  DOTNET_ROOT          .NET runtime location (framework-dependent build needs it)
  CCD_LAUNCHER_PORT    TCP port to listen on (default 8765)
  CCD_LAUNCHER_DIR     working/log directory (default: this script's dir)
"""
import json
import os
import signal
import subprocess
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = int(os.environ.get("CCD_LAUNCHER_PORT", "8765"))
WORKDIR = os.environ.get("CCD_LAUNCHER_DIR", HERE)
CCD_BIN = os.environ.get(
    "CCD_BIN",
    os.path.expanduser("~/ReposFred/cc-director/local_builds/mac/cc-director-mac1"),
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


class Handler(BaseHTTPRequestHandler):
    def _send(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urlparse(self.path)
        if u.path == "/status":
            self._send(status())
        elif u.path == "/screenshot":
            self._send(screenshot())
        elif u.path == "/logs":
            n = int(parse_qs(u.query).get("n", ["80"])[0])
            self._send(logs(n))
        elif u.path in ("/", "/health"):
            self._send({"ok": True, "service": "cc-director-launcher", "port": PORT})
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
        else:
            self._send({"ok": False, "error": "not found"}, 404)

    def log_message(self, *a):
        pass  # quiet


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"cc-director-launcher listening on http://127.0.0.1:{PORT} (bin={CCD_BIN})", flush=True)
    srv.serve_forever()
