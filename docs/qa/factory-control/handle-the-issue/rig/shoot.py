"""Screenshot QA for Handle the issue (Factory Control, step 1b), against a throwaway Gateway built from this branch.

Runs in the foreground and owns everything it starts, and stops all of it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, the desktop Gateway, Debug build of this tree) on port 7899 with its own storage
  root under %TEMP%, Tailscale off, AUTH ON, factory agents on;
- a stand-in Director (rig/standin-director) that dials the Gateway's Director stream on this computer, answers the
  session create Handle the issue sends, and writes down exactly what the Gateway sent;
- the Cockpit's Vite dev server on port 5199, proxying the Gateway routes to it.

Auth is on because Handle the issue is the owner's own: the route refuses anything but the owner's signed-in browser
or phone. The rig enrolls two devices by writing their rows into the rig's own gateway.db - a "browser" (the Cockpit
in Playwright, and the seeding calls) and a "workstation" (the stand-in Director) - each a SHA-256 of a random key,
exactly the way the Gateway stores a device it enrolled itself.

How the data was made, honestly:
- The factories are registered through the real registry route, their schedules through the real schedule route.
- The PROBLEMS are runs written straight into the rig's cron_runs table, carrying the result columns a run's own
  report (or the Gateway's sweep) writes, because the rig has no real sessions to report. Everything after that -
  the problems fold, the page card, Handle the issue, Resolve - is the product's own code.

Usage: python shoot.py <out dir>
"""
import hashlib
import html
import json
import os
import secrets
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
RIG = Path(os.environ["TEMP"]) / "handle-issue-qa"
ROOT = RIG / "root"
GW_PORT = 7899
GW = f"http://127.0.0.1:{GW_PORT}"
UI_PORT = 5199
UI = f"http://127.0.0.1:{UI_PORT}"
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
STANDIN = REPO / "docs" / "qa" / "factory-control" / "handle-the-issue" / "rig" / "standin-director" / "bin" / "Debug" / "net10.0" / "StandinDirector.exe"
MACHINE = os.environ["COMPUTERNAME"]
NOW = datetime.now(timezone.utc)

BROWSER_KEY = "qa-browser-" + secrets.token_hex(16)
DIRECTOR_KEY = "qa-director-" + secrets.token_hex(16)
AUTH = {"Authorization": f"Bearer {BROWSER_KEY}"}

# The run ids the problems are seeded under, so the screenshots and the report can name them.
SCOUT_PROBLEM = str(uuid.uuid4())
SENDER_PROBLEM = str(uuid.uuid4())
WRITER_PROBLEM = str(uuid.uuid4())


def wait_for(url, what, seconds=120, headers=None):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if requests.get(url, timeout=3, headers=headers).status_code == 200:
                return
        except requests.RequestException:
            pass
        time.sleep(1)
    raise SystemExit(f"{what} did not answer 200 at {url} within {seconds}s")


def check(r, what):
    if r.status_code >= 300:
        raise SystemExit(f"{what} FAILED: HTTP {r.status_code} {r.text[:400]}")
    return r


def enroll_devices(db):
    """Write the two device rows the rig uses, the way the Gateway stores a device it enrolled (a SHA-256 of the key)."""
    # /healthz answers once the database is open; its later migrations may still be running, so wait for the table.
    deadline = time.time() + 120
    while True:
        c = sqlite3.connect(db)
        cols = {row[1]: row for row in c.execute("PRAGMA table_info(device_credentials)")}
        if cols:
            break
        c.close()
        if time.time() > deadline:
            raise SystemExit("the rig gateway.db has no device_credentials table after 120s")
        time.sleep(1)
    tenant_col = "tenant_id" if "tenant_id" in cols else "TenantId"
    for device_id, key, device_type in (("qa-browser", BROWSER_KEY, "browser"), ("qa-director", DIRECTOR_KEY, "workstation")):
        row = {
            "DeviceId": device_id, "MachineName": MACHINE, "DeviceKeyHash": hashlib.sha256(key.encode()).hexdigest(),
            "KeyPrefix": key[:8], "KeyLast4": key[-4:], "IssuedAtUtc": NOW.strftime("%Y-%m-%d %H:%M:%S"),
            "Status": "active", "Platform": "windows", "DeviceType": device_type, tenant_col: "local",
        }
        missing = [k for k in row if k not in cols]
        if missing:
            raise SystemExit(f"device_credentials has no column(s) {missing}; its columns are {list(cols)}")
        c.execute(f"INSERT INTO device_credentials ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
    c.commit()
    c.close()
    print("enrolled the rig's browser and workstation devices")


def register(factory, title, boss, seats):
    body = {"factory": factory, "title": title, "folder": str(RIG / factory), "computer": MACHINE,
            "seats": [{"id": s, "name": role, "role": role, "briefFile": f"agents/{s}.yaml", "computer": MACHINE}
                      for s, role in seats]}
    if boss is not None:
        body["bossSeat"] = boss
    check(requests.put(f"{GW}/gateway/factory/registry", json=body, headers=AUTH), f"register {factory}")
    print(f"registered {factory}")


def schedule(factory, seat, seed):
    r = check(requests.post(f"{GW}/cron/jobs", headers=AUTH, json={
        "name": f"{factory} - {seat}", "scheduleKind": "recurring", "cronExpression": "0 3 1 1 *", "timeZoneId": "UTC",
        "enabled": True, "factory": factory, "seat": seat,
        "target": {"machine": MACHINE}, "action": {"repoPath": str(RIG / factory), "seed": seed},
    }), f"schedule {factory}/{seat}")
    job = r.json()
    print(f"schedule {factory}/{seat}: {job['id']}")
    return job["id"]


def seed_problem(db, run_id, job_id, hours_ago, problem, reason, session=True):
    """One run whose result is a problem, as the result route (problem = reported) or the sweep writes it."""
    c = sqlite3.connect(db)
    cols = [row[1] for row in c.execute("PRAGMA table_info(cron_runs)")]
    tenant = c.execute("SELECT tenant_id FROM cron_jobs WHERE Id = ?", (job_id,)).fetchone()
    if tenant is None:
        raise SystemExit(f"no cron_jobs row for {job_id}")
    seq = (c.execute("SELECT MAX(Sequence) FROM cron_runs WHERE JobId = ?", (job_id,)).fetchone()[0] or 0) + 1
    fired = NOW - timedelta(hours=hours_ago)
    stamp = lambda t: t.strftime("%Y-%m-%d %H:%M:%S")
    row = {
        "Id": run_id.upper(), "tenant_id": tenant[0], "JobId": job_id, "Sequence": seq,
        "ScheduledUtc": stamp(fired), "FiredUtc": stamp(fired), "Machine": MACHINE, "TargetDirectorId": "qa-standin-director",
        "SessionId": str(uuid.uuid4()) if session else None,
        "InfraStatus": "started" if session else "not-started", "TaskStatus": "stopped-by-you" if session else "no-session",
        "Result": "problem", "Problem": problem, "ResultReason": reason, "ResultUtc": stamp(fired + timedelta(minutes=12)),
    }
    missing = [k for k in row if k not in cols]
    if missing:
        raise SystemExit(f"cron_runs has no column(s) {missing}; its columns are {cols}")
    c.execute(f"INSERT INTO cron_runs ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
    c.commit()
    c.close()
    print(f"problem {run_id} on {job_id}: {problem}")


# The rig Gateway runs with auth on; the Cockpit holds the rig's browser key as its signed-in account.
def prepare(page):
    page.goto(UI)
    accounts = json.dumps([{"id": "qa-rig", "label": "QA rig", "email": None, "deviceKey": BROWSER_KEY, "installId": "qa-rig-install"}])
    page.evaluate("(accounts) => { localStorage.setItem('cc.accounts', accounts); localStorage.setItem('cc.activeAccount', 'qa-rig'); }", accounts)


def shot(page, name, full_page=True):
    page.wait_for_timeout(800)
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=full_page)
    print("saved", name)


def go(page, path, testid):
    page.goto(f"{UI}{path}")
    try:
        page.wait_for_selector(f"[data-testid='{testid}']", timeout=30000)
    except Exception:
        page.screenshot(path=str(RIG / "FAILED.png"), full_page=True)
        print("FAILED page text:", page.inner_text("body")[:800])
        raise


def main():
    if RIG.exists():
        shutil.rmtree(RIG)
    (ROOT / "config").mkdir(parents=True)
    (ROOT / "config" / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    talks = RIG / "talks"
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    env.pop("CC_GATEWAY_NO_AUTH", None)
    gw = subprocess.Popen([str(EXE), "--port", str(GW_PORT)], env=env, stdout=open(RIG / "gateway.out.txt", "w"),
                          stderr=subprocess.STDOUT, cwd=str(EXE.parent))
    standin = None
    ui = None
    try:
        wait_for(f"{GW}/healthz", "the rig Gateway")
        dbs = list(ROOT.rglob("gateway.db"))
        if len(dbs) != 1:
            raise SystemExit(f"expected one gateway.db under the rig root, found {dbs}")
        db = dbs[0]
        enroll_devices(db)
        wait_for(f"{GW}/gateway/factories", "the rig Gateway's factory routes", headers=AUTH)
        print("gateway up, pid", gw.pid)

        standin = subprocess.Popen([str(STANDIN), GW, MACHINE, str(talks), DIRECTOR_KEY], stdin=subprocess.PIPE,
                                   stdout=open(RIG / "standin.out.txt", "w"), stderr=subprocess.STDOUT)
        deadline = time.time() + 30
        while not any(d.get("directorId") == "qa-standin-director" for d in requests.get(f"{GW}/directors", headers=AUTH).json()):
            if time.time() > deadline:
                raise SystemExit("the stand-in Director did not appear in GET /directors within 30s; see standin.out.txt")
            time.sleep(1)
        print("stand-in Director connected")

        register("qa-tallyhand", "Tallyhand (QA)", "boss", [("boss", "Boss"), ("scout", "Scout"), ("sender", "Sender")])
        register("qa-warmforward", "WarmForward (QA)", None, [("writer", "Writer")])
        boss_job = schedule("qa-tallyhand", "boss", "Do the Tallyhand morning run: read the feed, post the numbers, report how it went.")
        scout_job = schedule("qa-tallyhand", "scout", "Scan the supplier feed for new parts.")
        sender_job = schedule("qa-tallyhand", "sender", "Send the day's quotes.")
        writer_job = schedule("qa-warmforward", "writer", "Write the weekly homeowner summary.")
        seed_problem(db, SCOUT_PROBLEM, scout_job, 5, "reported",
                     "The supplier feed returned 503 three times; nothing was imported.")
        seed_problem(db, SENDER_PROBLEM, sender_job, 9, "did-not-run", "it was due and never started a session", session=False)
        seed_problem(db, WRITER_PROBLEM, writer_job, 3, "did-not-report",
                     "its session ended without reporting how the run went (stopped by you after 12 min)")

        problems = check(requests.get(f"{GW}/cron/problems", headers=AUTH), "GET /cron/problems").json()
        (OUT / "gateway-problems-before.json").write_text(json.dumps(problems, indent=2), encoding="utf-8")
        print(f"open problems before: {problems['count']}")

        ui = subprocess.Popen(["npx.cmd", "vite", "--port", str(UI_PORT), "--strictPort", "--host", "127.0.0.1"],
                              env=dict(os.environ, COCKPIT_PROXY_TARGET=GW), stdout=open(RIG / "vite.out.txt", "w"),
                              stderr=subprocess.STDOUT, cwd=str(REPO / "apps" / "cockpit"))
        wait_for(UI, "the Cockpit dev server")

        with sync_playwright() as p:
            browser = p.chromium.launch(args=["--disable-gpu", "--use-gl=swiftshader", "--disable-dev-shm-usage"])
            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            prepare(page)

            # 1. A factory with open problems.
            go(page, "/factories/qa-tallyhand", f"fa-problem-{SCOUT_PROBLEM}")
            shot(page, "1-factory-with-open-problems")

            # 2. Handle the issue opens the briefed talk.
            page.locator(f"[data-testid='fa-problem-{SCOUT_PROBLEM}'] [data-testid='fa-problem-handle']").click()
            page.wait_for_url("**/session/**", timeout=30000)
            opened = page.url
            print("Handle the issue navigated to", opened)
            # The talk's session page, and the Sessions list showing it, hold a live terminal; the stand-in Director
            # has no real session behind it, and headless Chromium crashes capturing either. So the proof of this
            # step is what Handle the issue sent to the boss's computer, exactly as the stand-in Director received
            # it, drawn on a plain page - plus the address the Cockpit moved to.
            page.close()
            (OUT / "handle-the-issue-navigated-to.txt").write_text(opened.replace(UI, "") + "\n", encoding="utf-8")
            creates = sorted(talks.glob("create-*-first-prompt.txt"))
            if len(creates) != 1:
                raise SystemExit(f"expected one create at the stand-in Director, found {creates}")
            shutil.copy(creates[0], OUT / "handle-the-issue-first-prompt.txt")
            shutil.copy(talks / "create-1.json", OUT / "handle-the-issue-create.json")
            create = json.loads((talks / "create-1.json").read_text(encoding="utf-8"))
            prompt = creates[0].read_text(encoding="utf-8")
            print("the talk's first prompt:", len(prompt), "characters")
            # The stand-in Director pushes what it holds every two seconds, so the new session reaches the roster
            # within a few seconds of the create.
            talk = []
            for _ in range(15):
                sessions = check(requests.get(f"{GW}/sessions", headers=AUTH), "GET /sessions").json()
                talk = [x for x in sessions if x["sessionId"] in opened]
                if talk:
                    break
                time.sleep(1)
            if len(talk) != 1:
                raise SystemExit(f"the session the Cockpit moved to is not on the Gateway: {opened}")
            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            page.set_content(
                "<html><body style='font-family:Segoe UI,sans-serif;margin:24px;max-width:1200px'>"
                "<h2>Handle the issue: the talk the Gateway opened</h2>"
                f"<p><b>The Cockpit moved to:</b> <code>{html.escape(opened.replace(UI, ''))}</code></p>"
                f"<p><b>The session on the Gateway:</b> {html.escape(talk[0]['name'])} ({html.escape(talk[0]['status'])})</p>"
                f"<p><b>Started on:</b> {html.escape(create.get('repoPath') or '')}</p>"
                "<p><b>The first prompt the boss is given</b> (as the Director received it):</p>"
                f"<pre style='white-space:pre-wrap;background:#f4f4f4;padding:12px;border:1px solid #ccc'>{html.escape(prompt)}</pre>"
                "</body></html>")
            shot(page, "2-handle-the-issue-opened-the-briefed-talk")
            page.close()
            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            prepare(page)

            # 3. Resolve with no reason is refused, in the Gateway's words.
            go(page, "/factories/qa-tallyhand", f"fa-problem-{SENDER_PROBLEM}")
            item = page.locator(f"[data-testid='fa-problem-{SENDER_PROBLEM}']")
            item.locator("[data-testid='fa-problem-resolve']").click()
            item.locator("[data-testid='fa-problem-resolve-send']").click()
            item.locator("[data-testid='fa-problem-error']").wait_for(timeout=15000)
            shot(page, "3-resolve-with-no-reason-refused")

            # 4. Resolve with a reason - it disappears.
            item.locator("[data-testid='fa-problem-reason']").fill("The schedule was paused by mistake on Friday; switched it back on.")
            item.locator("[data-testid='fa-problem-resolve-send']").click()
            page.wait_for_selector(f"[data-testid='fa-problem-{SENDER_PROBLEM}']", state="detached", timeout=15000)
            shot(page, "4-resolved-with-a-reason-gone")

            # 5. A factory with no boss.
            go(page, "/factories/qa-warmforward", "fa-problem-unavailable")
            shot(page, "5-factory-with-no-boss")
            browser.close()

        after = check(requests.get(f"{GW}/cron/problems?state=all", headers=AUTH), "GET /cron/problems?state=all").json()
        (OUT / "gateway-problems-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
        for p_ in after["problems"]:
            print(f"  after: {p_['runId']} {p_['state']} {p_.get('stateText')}")
    finally:
        if ui is not None:
            subprocess.run(["taskkill", "/PID", str(ui.pid), "/T", "/F"], capture_output=True)
            print("stopped the Cockpit dev server this run started, pid", ui.pid)
        if standin is not None:
            standin.stdin.close()
            standin.wait(30)
            print("stopped the stand-in Director this run started, pid", standin.pid)
        gw.terminate()
        gw.wait(30)
        print("stopped the rig Gateway this run started, pid", gw.pid)


if __name__ == "__main__":
    main()
