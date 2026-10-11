"""Screenshots of the shifts on the screens (Factory Control, step 3), from a throwaway local Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    python docs/qa/factory-control/shifts/capture.py

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, which is where the Gateway serves it from.
  2. Starts the Gateway on port 7979 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's Gateway nor the owner's factories or schedules.
  3. Registers one demonstration factory with three seats and gives them schedules, one of which starts inside the
     last 30 minutes of the morning shift and has never run - the case the warning is for.
  4. Opens the Cockpit in headless Edge (Playwright as a library: a repeatable script, not interactive work),
     signed in with the scratch Gateway's own token, and takes the screenshots into this folder.
  5. Stops the Gateway it started. The scratch data folder is left for inspection and is not in the repository.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
PORT = 7979
BASE = f"http://127.0.0.1:{PORT}"
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "shift-demo"


def log(msg):
    print(f"[shifts-capture] {msg}", flush=True)


def call(method, path, token, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            text = res.read().decode("utf-8")
            return json.loads(text) if text else None
    except urllib.error.HTTPError as exc:
        raise SystemExit(f"{method} {path} failed: HTTP {exc.code} {exc.read().decode('utf-8', 'replace')}")


def wait_for_gateway(proc):
    for _ in range(120):
        if proc.poll() is not None:
            raise SystemExit(f"the Gateway exited with code {proc.returncode} before it answered")
        try:
            urllib.request.urlopen(BASE + "/", timeout=2)
            return
        except urllib.error.HTTPError:
            return  # it answered, even if with a refusal
        except OSError:
            time.sleep(1)
    raise SystemExit("the Gateway did not answer on port 7979 within two minutes")


def find_token(root):
    for path in Path(root).rglob("gateway-token.txt"):
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    raise SystemExit(f"no gateway-token.txt under {root}: the Gateway did not write its token")


def wait_until_ready(token):
    # The Gateway listens before its database is open and answers 503 "starting" until then.
    for _ in range(120):
        req = urllib.request.Request(BASE + "/cron/jobs")
        req.add_header("Authorization", f"Bearer {token}")
        try:
            with urllib.request.urlopen(req, timeout=5):
                return
        except urllib.error.HTTPError as exc:
            if exc.code != 503:
                raise SystemExit(f"GET /cron/jobs answered HTTP {exc.code} while waiting for readiness")
        except OSError:
            pass
        time.sleep(1)
    raise SystemExit("the Gateway was not ready within two minutes")


def job(name, cron, seat, prompt):
    return {
        "name": f"Shift Demo Factory - {name}",
        "enabled": True,
        "scheduleKind": "recurring",
        "cronExpression": cron,
        "timeZoneId": "America/Toronto",
        "target": {"machine": "RIG_MACHINE"},
        "action": {"repoPath": "D:\\shift-demo", "seed": prompt},
        "preventOverlap": True,
        "notifyOn": "none",
        "factory": FACTORY,
        "seat": seat,
    }


def seed(token):
    call("PUT", "/gateway/factory/registry", token, {
        "factory": FACTORY,
        "title": "Shift Demo",
        "folder": "D:\\shift-demo",
        "computer": "RIG_MACHINE",
        "bossSeat": "boss",
        "seats": [
            {"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.md"},
            {"id": "scout", "name": "Scout", "role": "Scout", "briefFile": "seats/scout.md"},
            {"id": "writer", "name": "Writer", "role": "Writer", "briefFile": "seats/writer.md"},
        ],
    })
    created = [
        call("POST", "/cron/jobs", token, job("Boss - morning run", "15 6 * * *", "boss", "Morning run.")),
        call("POST", "/cron/jobs", token, job("Scout - night sweep", "0 2 * * *", "scout", "Night sweep.")),
        call("POST", "/cron/jobs", token, job("Scout - evening sweep", "0 18 * * *", "scout", "Evening sweep.")),
        # Inside the last 30 minutes of the morning shift, and never run: the warning's no-history case.
        call("POST", "/cron/jobs", token, job("Writer - afternoon post", "45 15 * * *", "writer", "Afternoon post.")),
    ]
    for c in created:
        log(f"schedule {c['id']} '{c['name']}': {c.get('shiftText')} warning={c.get('shiftWarning')!r}")
    return created


def capture(token, warned_name):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        try:
            for width, suffix in ((1280, "desktop"), (375, "phone")):
                context = browser.new_context(viewport={"width": width, "height": 900})
                # A page load is let through by the cc-gateway-token cookie; the app's calls carry the stored key.
                context.add_cookies([{"name": "cc-gateway-token", "value": token, "url": BASE}])
                page = context.new_page()
                page.goto(BASE + "/", wait_until="domcontentloaded")
                page.evaluate(
                    """(token) => {
                        localStorage.setItem('cc.accounts', JSON.stringify([
                          { id: 'rig', label: 'Rig', email: null, deviceKey: token, installId: 'rig' }]));
                        localStorage.setItem('cc.activeAccount', 'rig');
                        localStorage.setItem('cc.deviceKey', token);
                        localStorage.setItem('schedule.showFactorySchedules', '1');
                    }""",
                    token,
                )
                page.goto(f"{BASE}/factories/{FACTORY}/seats", wait_until="networkidle")
                try:
                    page.wait_for_selector("[data-testid=fa-seats-table] .fa-shift-head", timeout=30000)
                except Exception:
                    debug = Path(tempfile.gettempdir()) / "shifts-rig-debug.png"
                    page.screenshot(path=str(debug), full_page=True)
                    raise SystemExit(f"the Seats tab did not show its shift groups; the page is in {debug}")
                page.screenshot(path=str(OUT / f"seats-grouped-by-shift-{suffix}.png"), full_page=True)
                log(f"seats-grouped-by-shift-{suffix}.png")

                page.goto(f"{BASE}/schedule", wait_until="networkidle")
                page.wait_for_selector("[data-testid^=sched-shift-]", timeout=30000)
                page.screenshot(path=str(OUT / f"schedule-shift-column-{suffix}.png"), full_page=True)
                log(f"schedule-shift-column-{suffix}.png")
                page.get_by_text(warned_name, exact=False).first.click()
                page.wait_for_selector("[data-testid=sched-shift-warning]", timeout=30000)
                page.screenshot(path=str(OUT / f"schedule-shift-warning-{suffix}.png"), full_page=True)
                log(f"schedule-shift-warning-{suffix}.png")
                context.close()
        finally:
            browser.close()


def main():
    if not (GATEWAY_BIN / "CcDirector.Gateway.dll").exists():
        raise SystemExit(f"build the Gateway first: {GATEWAY_BIN} has no CcDirector.Gateway.dll")
    if not (COCKPIT_DIST / "index.html").exists():
        raise SystemExit(f"build the Cockpit first: {COCKPIT_DIST} has no index.html (cd apps/cockpit && npm run build)")
    target = GATEWAY_BIN / "wwwroot" / "c"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(COCKPIT_DIST, target)
    log(f"Cockpit copied to {target}")

    root = tempfile.mkdtemp(prefix="shifts-rig-")
    # The scratch Gateway's own config.json: the factory area switched on (it is off by default) and the account's
    # display time zone pinned, so the shifts read the same on any machine.
    (Path(root) / "config").mkdir(parents=True, exist_ok=True)
    (Path(root) / "config" / "config.json").write_text(
        json.dumps({"factoryAgents": {"enabled": True}, "time_zone": "America/Toronto"}), encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=root, CC_GATEWAY_NO_TAILSCALE="1")
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    proc = subprocess.Popen(["dotnet", str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env)
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        log("Gateway answered; token read from its own data folder")
        wait_until_ready(token)
        created = seed(token)
        warned = [c for c in created if c.get("shiftWarning")]
        if len(warned) != 1:
            raise SystemExit(f"expected exactly one warned schedule, got {len(warned)}")
        capture(token, warned[0]["name"].split(" - ", 1)[1])
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log("Gateway stopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
