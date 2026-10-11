"""Screenshot QA for the seat markers (Factory Control, step 2), against a throwaway Gateway built from this branch.

Runs in the foreground and owns everything it starts: a Gateway (CcDirector.Gateway, the desktop Gateway, Debug build of this tree)
on port 7899 with its own storage root under %TEMP%, auth and Tailscale off, factory agents on; and the Cockpit's
Vite dev server on port 5199 proxying the Gateway routes to it. Both are stopped when the run ends, pass or fail.

How the data was made, honestly:
- The two factories are registered through the real registry route (PUT /gateway/factory/registry).
- Their schedules are created through the real schedule route (POST /cron/jobs), linked to their seats.
- The schedules' RUNS are written straight into the rig's gateway.db (cron_runs), because the rig has no Director to
  start sessions. Each run carries one of the endings the Gateway route stamps when a session closes itself
  ("closed-itself") or when a person stops it ("stopped-by-you"). Everything after that - the markers, the flag and
  every word on screen - is the Gateway's own fold, read through GET /gateway/factories, rendered by the Cockpit.

Playwright as a library, deliberately: a repeatable scripted run against a throwaway local Gateway, not interactive
work in a signed-in browser profile.

Usage: python shoot.py <out dir>
"""
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import requests
from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[5]
OUT = Path(sys.argv[1]).resolve()
RIG = Path(os.environ["TEMP"]) / "seatmarkers-qa"
ROOT = RIG / "root"
GW_PORT = 7899
GW = f"http://127.0.0.1:{GW_PORT}"
UI_PORT = 5199
UI = f"http://127.0.0.1:{UI_PORT}"
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
MACHINE = os.environ["COMPUTERNAME"]
NOW = datetime.now(timezone.utc)


def wait_for(url, what, seconds=120):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if requests.get(url, timeout=3).status_code == 200:
                return
        except requests.RequestException:
            pass
        time.sleep(1)
    raise SystemExit(f"{what} did not answer at {url} within {seconds}s")


def check(r, what):
    if r.status_code >= 300:
        raise SystemExit(f"{what} FAILED: HTTP {r.status_code} {r.text[:400]}")
    return r


def register(factory, title, boss, seats):
    check(requests.put(f"{GW}/gateway/factory/registry", json={
        "factory": factory, "title": title, "folder": str(RIG / factory), "computer": MACHINE, "bossSeat": boss,
        "seats": [{"id": s, "name": role, "role": role, "briefFile": f"agents/{s}.yaml", "computer": MACHINE}
                  for s, role in seats],
    }), f"register {factory}")
    print(f"registered {factory}")


def schedule(factory, seat):
    r = check(requests.post(f"{GW}/cron/jobs", json={
        "name": f"{factory} - {seat}", "scheduleKind": "recurring", "cronExpression": "0 3 1 1 *", "timeZoneId": "UTC",
        "enabled": True, "factory": factory, "seat": seat,
        "target": {"machine": MACHINE}, "action": {"repoPath": str(RIG), "seed": "QA rig - never fires"},
    }), f"schedule {factory}/{seat}")
    job = r.json()
    print(f"schedule {factory}/{seat}: {job['id']}")
    return job["id"]


def seed_runs(db, job_id, endings):
    """Write one run per ending, newest first, one a day, as the cron engine would have recorded it."""
    c = sqlite3.connect(db)
    cols = [row[1] for row in c.execute("PRAGMA table_info(cron_runs)")]
    # The account the schedule itself was written under, so the runs are that account's.
    tenant = c.execute("SELECT tenant_id FROM cron_jobs WHERE Id = ?", (job_id,)).fetchone()
    if tenant is None:
        raise SystemExit(f"no cron_jobs row for {job_id}")
    for i, ending in enumerate(reversed(endings)):
        fired = NOW - timedelta(days=len(endings) - i, hours=1)
        row = {
            "Id": str(uuid.uuid4()).upper(), "tenant_id": tenant[0], "JobId": job_id, "Sequence": i + 1,
            "ScheduledUtc": fired.strftime("%Y-%m-%d %H:%M:%S"), "FiredUtc": fired.strftime("%Y-%m-%d %H:%M:%S"),
            "Machine": MACHINE, "TargetDirectorId": "qa-rig-director", "SessionId": str(uuid.uuid4()),
            "InfraStatus": "started", "TaskStatus": ending,
        }
        missing = [k for k in row if k not in cols]
        if missing:
            raise SystemExit(f"cron_runs has no column(s) {missing}; its columns are {cols}")
        c.execute(f"INSERT INTO cron_runs ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
    c.commit()
    c.close()
    print(f"runs for {job_id}: {len(endings)}")


# The rig Gateway runs with auth off, so any device key is accepted; the Cockpit only needs to hold one to get past
# its sign-in screen. This is a placeholder for the rig, not a credential.
RIG_ACCOUNT = json.dumps([{"id": "qa-rig", "label": "QA rig", "email": None, "deviceKey": "qa-rig-no-auth",
                           "installId": "qa-rig-install"}])


def prepare(page, view):
    page.goto(UI)
    page.evaluate("([accounts, view]) => { localStorage.setItem('cc.accounts', accounts); "
                  "localStorage.setItem('cc.activeAccount', 'qa-rig'); "
                  "localStorage.setItem('cockpit.factoriesView', JSON.stringify(view)); }", [RIG_ACCOUNT, view])


def shoot(page, path, name, wait_for_testid):
    page.goto(f"{UI}{path}")
    try:
        page.wait_for_selector(f"[data-testid='{wait_for_testid}']", timeout=30000)
    except Exception:
        page.screenshot(path=str(RIG / f"FAILED-{name}.png"), full_page=True)
        print("FAILED page text:", page.inner_text("body")[:600])
        raise
    page.wait_for_timeout(800)
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
    print("saved", name)


def main():
    if RIG.exists():
        shutil.rmtree(RIG)
    (ROOT / "config").mkdir(parents=True)
    (ROOT / "config" / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(ROOT), CC_GATEWAY_NO_AUTH="1", CC_GATEWAY_NO_TAILSCALE="1")
    gw_log = open(RIG / "gateway.out.txt", "w")
    gw = subprocess.Popen([str(EXE), "--port", str(GW_PORT)], env=env, stdout=gw_log,
                          stderr=subprocess.STDOUT, cwd=str(EXE.parent))
    ui = None
    try:
        wait_for(f"{GW}/healthz", "the rig Gateway")
        # /healthz answers once the database is open; the routes answer 503 "starting" a little longer.
        wait_for(f"{GW}/gateway/factories", "the rig Gateway's factory routes")
        print("gateway up, pid", gw.pid)

        register("qa-tallyhand", "Tallyhand (QA)", "boss", [("boss", "Boss"), ("scout", "Scout")])
        register("qa-warmforward", "WarmForward (QA)", "boss", [("boss", "Boss"), ("writer", "Writer")])
        jobs = {
            "tally-boss": schedule("qa-tallyhand", "boss"),
            "tally-scout": schedule("qa-tallyhand", "scout"),
            "warm-boss": schedule("qa-warmforward", "boss"),
        }
        dbs = list(ROOT.rglob("gateway.db"))
        if len(dbs) != 1:
            raise SystemExit(f"expected one gateway.db under the rig root, found {dbs}")
        # Newest first. The Scout was stopped by you 3 times in its last 7 runs; the two bosses close themselves; the
        # Writer has no schedule and so no runs.
        seed_runs(dbs[0], jobs["tally-boss"], ["closed-itself"] * 6)
        seed_runs(dbs[0], jobs["tally-scout"], ["stopped-by-you", "closed-itself", "stopped-by-you", "closed-itself",
                                                "closed-itself", "stopped-by-you", "closed-itself"])
        seed_runs(dbs[0], jobs["warm-boss"], ["closed-itself"] * 5)

        listing = check(requests.get(f"{GW}/gateway/factories"), "GET /gateway/factories").json()
        (OUT / "gateway-factories-list.json").write_text(json.dumps(listing, indent=2), encoding="utf-8")
        seats = check(requests.get(f"{GW}/gateway/factories/qa-tallyhand/seats"), "GET seats").json()
        (OUT / "gateway-tallyhand-seats.json").write_text(json.dumps(seats, indent=2), encoding="utf-8")
        for row in listing["rows"]:
            print(f"  list {row['id']}: leftOpenText={row['leftOpenText']!r}")
        for row in seats["rows"]:
            print(f"  seat {row['seatId']}: {row['closingText']!r} ({row['closingTone']})")

        ui_env = dict(os.environ, COCKPIT_PROXY_TARGET=GW)
        ui_log = open(RIG / "vite.out.txt", "w")
        ui = subprocess.Popen(["npx.cmd", "vite", "--port", str(UI_PORT), "--strictPort", "--host", "127.0.0.1"],
                              env=ui_env, stdout=ui_log, stderr=subprocess.STDOUT, cwd=str(REPO / "apps" / "cockpit"))
        wait_for(UI, "the Cockpit dev server")

        with sync_playwright() as p:
            browser = p.chromium.launch()
            page = browser.new_page(viewport={"width": 1280, "height": 900})
            prepare(page, "table")
            shoot(page, "/factories", "1-factories-list-table", "fa-factories-list")
            prepare(page, "cards")
            shoot(page, "/factories", "2-factories-list-cards", "fa-factories-cards")
            shoot(page, "/factories/qa-tallyhand", "3-factory-page-header", "fa-page-left-open")
            shoot(page, "/factories/qa-tallyhand/seats", "4-tallyhand-seats", "fa-seats-table")
            shoot(page, "/factories/qa-warmforward/seats", "5-warmforward-seats", "fa-seats-table")
            browser.close()
    finally:
        if ui is not None:
            subprocess.run(["taskkill", "/PID", str(ui.pid), "/T", "/F"], capture_output=True)
            print("stopped the Cockpit dev server this run started, pid", ui.pid)
        gw.terminate()
        gw.wait(30)
        print("stopped the rig Gateway this run started, pid", gw.pid)


if __name__ == "__main__":
    main()
