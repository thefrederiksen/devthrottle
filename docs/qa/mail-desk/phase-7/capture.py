"""The phase 7 screenshots of the Mail Desk page, on a THROWAWAY Gateway (docs/qa/mail-desk/phase-7/CONTRACT.md,
section 4). Runs in the foreground, owns everything it starts, and stops it when the run ends, pass or fail.

What it starts:
  - a Gateway built from this tree (CcDirector.Gateway.dll, Debug), with the Cockpit and the phone app built from this
    tree in its wwwroot/c and wwwroot/mobile; its own storage root and home under the rig folder (made empty for the
    run), SQLite, no Docker, Tailscale off, authentication ON, on a free local port, with an administrator token made
    for this run only. It can reach neither the hosted Gateway nor any mailbox: it holds no mailbox sign-in, and
    nothing on it checks or screens mail.
  - one browser device, enrolled by writing its key's hash into the throwaway gateway.db (as every QA rig here does).

What it does, on one Gateway and one account (email@devthrottle.com):
  1. empty: seed.py switches the Mail Desk on with no mailbox; pictures.
  2. on:    seed.py adds the three mailboxes, live mode, checks and decisions; pictures.
  3. off:   seed.py switches the Mail Desk off; pictures.
Pictures (shots.cjs): the Cockpit at 1440 wide, the phone app at 390 wide, and the phone's drawer, per scenario, plus
what each screen showed in seen-<scenario>.json. The Gateway's answer to GET /gateway/mail-desk/channels is kept in
answer-<scenario>.json.

Usage, from the repository root (Linux; .NET 10 at ~/.dotnet, Node 22 on the PATH):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    (cd apps/cockpit && npx vite build) && (cd apps/mobile && npx vite build)
    NODE_PATH=<the web-site skill's qa-tools>/node_modules python3 docs/qa/mail-desk/phase-7/capture.py <rig folder> <out folder>
The rig folder must be outside every git checkout; it holds the throwaway database and the device key.
"""

import hashlib
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
DLL = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.dll"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
MOBILE_DIST = REPO / "apps" / "mobile" / "dist"
DOTNET = Path.home() / ".dotnet" / "dotnet"
SCENARIOS = ("empty", "on", "off")

PERSON_KEY = "qa-person-" + uuid.uuid4().hex
ADMIN_TOKEN = "qa-admin-" + uuid.uuid4().hex


def say(message):
    print(f"[capture] {message}", flush=True)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def inside_git(folder):
    probe = folder
    while probe != probe.parent:
        if (probe / ".git").exists():
            return True
        probe = probe.parent
    return False


def wait_for(what, ready, seconds=180):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            value = ready()
            if value:
                return value
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError, sqlite3.Error):
            pass
        time.sleep(1)
    raise SystemExit(f"[capture] {what} within {seconds}s")


def read_channels(gateway):
    """GET /gateway/mail-desk/channels as the person: (status, body)."""
    req = urllib.request.Request(gateway + "/gateway/mail-desk/channels",
                                 headers={"Authorization": f"Bearer {PERSON_KEY}", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as res:
            return res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8")


def enroll(db):
    c = sqlite3.connect(db)
    now = datetime.now(timezone.utc).replace(tzinfo=None).strftime("%Y-%m-%d %H:%M:%S")
    c.execute("INSERT INTO device_credentials (DeviceId, MachineName, DeviceKeyHash, KeyPrefix, KeyLast4, IssuedAtUtc, "
              "Status, Platform, DeviceType, TenantId) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
              ("qa-phase-7-browser", "qa-machine", hashlib.sha256(PERSON_KEY.encode("utf-8")).hexdigest(),
               PERSON_KEY[:8], PERSON_KEY[-4:], now, "active", "linux", "browser", "local"))
    c.commit()
    c.close()


def stage_shells():
    for dist, name in ((COCKPIT_DIST, "c"), (MOBILE_DIST, "mobile")):
        if not (dist / "index.html").exists():
            raise SystemExit(f"[capture] build it first: {dist / 'index.html'} does not exist")
        target = DLL.parent / "wwwroot" / name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(dist, target)
    say("staged this tree's Cockpit and phone app builds into the Gateway's wwwroot/c and wwwroot/mobile")


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    rig, out = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    if inside_git(rig):
        raise SystemExit(f"[capture] {rig} is inside a git checkout; the rig folder holds a device key and must not be")
    for needed in (DLL, DOTNET):
        if not needed.exists():
            raise SystemExit(f"[capture] {needed} does not exist; build the Gateway first (see the usage)")
    if not os.environ.get("NODE_PATH"):
        raise SystemExit("[capture] set NODE_PATH to the web-site skill's qa-tools node_modules (Playwright 1.60.0)")
    stage_shells()

    if rig.exists():
        shutil.rmtree(rig)
    (rig / "root" / "config").mkdir(parents=True)
    (rig / "home").mkdir()
    out.mkdir(parents=True, exist_ok=True)
    port = free_port()
    gateway = f"http://127.0.0.1:{port}"
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(rig / "root"), HOME=str(rig / "home"), CC_GATEWAY_NO_TAILSCALE="1",
               ADMIN_SERVICE_TOKEN=ADMIN_TOKEN, DOTNET_ROOT=str(DOTNET.parent), DOTNET_USE_POLLING_FILE_WATCHER="1")
    # Nothing of this session's own Gateway, nor any other service token, reaches the throwaway one.
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN",
              "CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY"):
        env.pop(k, None)
    log = open(rig / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(DOTNET), str(DLL), "--port", str(port)], env=env, stdout=log,
                          stderr=subprocess.STDOUT, cwd=str(DLL.parent))
    say(f"throwaway Gateway pid {gw.pid} on {gateway}, storage root <rig>/root (empty for this run)")
    try:
        def database():
            if gw.poll() is not None:
                raise SystemExit("[capture] the throwaway Gateway exited; see <rig>/gateway.out.txt")
            return next(iter((rig / "root").rglob("gateway.db")), None)

        db = wait_for("the throwaway Gateway wrote no gateway.db", database)
        wait_for("the throwaway gateway.db did not get its tables", lambda: {
            "device_credentials", "tenants", "mail_desk_channels", "mail_desk_channel_checks"} <= {
            r[0] for r in sqlite3.connect(db).execute("SELECT name FROM sqlite_master WHERE type='table'")})
        enroll(db)
        # 503 "starting" until the Gateway is ready to serve.
        status = wait_for("the throwaway Gateway did not become ready",
                          lambda: next((code for code, _ in [read_channels(gateway)] if code != 503), None))
        say(f"ready; the Mail Desk read before any seeding answered {status} (the switch is off by default)")

        child_env = dict(env, QA_PERSON_KEY=PERSON_KEY, QA_ADMIN_TOKEN=ADMIN_TOKEN, HOME=os.environ["HOME"])
        for scenario in SCENARIOS:
            say(f"--- scenario {scenario}")
            subprocess.run([sys.executable, str(HERE / "seed.py"), "--gateway", gateway, "--db", str(db),
                            "--scenario", scenario], env=child_env, check=True)
            code, body = read_channels(gateway)
            (out / f"answer-{scenario}.json").write_text(
                json.dumps({"status": code, "body": json.loads(body) if body else None}, indent=2) + "\n", encoding="utf-8")
            say(f"GET /gateway/mail-desk/channels answered {code}")
            subprocess.run(["node", str(HERE / "shots.cjs"), gateway, str(out), scenario], env=child_env, check=True)
    finally:
        gw.terminate()
        try:
            gw.wait(60)
        except subprocess.TimeoutExpired:
            gw.kill()
            gw.wait(30)
        log.close()
        say(f"throwaway Gateway stopped (exit {gw.returncode})")


if __name__ == "__main__":
    main()
