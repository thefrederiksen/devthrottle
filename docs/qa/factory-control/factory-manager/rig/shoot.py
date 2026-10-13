"""Screenshot QA for the Factory Manager (Factory Control, step 5), against a throwaway Gateway built from this branch.

Runs in the foreground and owns everything it starts, and stops all of it when the run ends, pass or fail:
- a Gateway (CcDirector.Gateway, Debug build of this tree) on port 7898 with its own storage root under %TEMP%,
  Tailscale off, AUTH ON, factory agents on;
- a stand-in Director (rig/standin-director) that dials the Gateway's Director stream on this computer, answers the
  session creates, registers a session key for each session the way a real Director does, and writes down exactly
  what the Gateway sent;
- the Cockpit's Vite dev server on port 5198, proxying the Gateway routes to it.

Auth is on because the Factory Manager's talk is the owner's own: the route refuses anything but the owner's signed-in
browser or phone. The rig enrolls two devices by writing their rows into the rig's own gateway.db, the way brief 4's
rig does. The command-line steps run the real `cc-devthrottle` from this tree AS a session, with the session key the
stand-in registered for it - so resolve rights are proven through the session-key guard and the real route.

Playwright is used as a library here, not interactively: this is a repeatable QA script whose output is committed.

How the data was made, honestly:
- The factories are registered through the real registry route, their schedules through the real schedule route.
- The PROBLEM is a run written straight into the rig's cron_runs table, because the rig has no real sessions to
  report. Everything after that - the card, the talk, Handle the issue, the binding, resolve - is the product's code.

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
RIG = Path(os.environ["TEMP"]) / "factory-manager-qa"
ROOT = RIG / "root"
GW_PORT = 7898
GW = f"http://127.0.0.1:{GW_PORT}"
UI_PORT = 5198
UI = f"http://127.0.0.1:{UI_PORT}"
EXE = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0" / "CcDirector.Gateway.exe"
STANDIN = REPO / "docs" / "qa" / "factory-control" / "factory-manager" / "rig" / "standin-director" / "bin" / "Debug" / "net10.0" / "StandinDirector.exe"
CLI_PYTHON = Path(os.environ["CCDT_PYTHON"]) if "CCDT_PYTHON" in os.environ else Path(sys.executable)
CLI_DIR = REPO / "tools" / "cc-devthrottle"
MACHINE = os.environ["COMPUTERNAME"]
NOW = datetime.now(timezone.utc)
FOLDER = str(RIG / "factories")

BROWSER_KEY = "qa-browser-" + secrets.token_hex(16)
DIRECTOR_KEY = "qa-director-" + secrets.token_hex(16)
AUTH = {"Authorization": f"Bearer {BROWSER_KEY}"}

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
    """seats: (id, name, role) - the name is what the name check reads."""
    body = {"factory": factory, "title": title, "folder": str(RIG / factory), "computer": MACHINE,
            "seats": [{"id": s, "name": name, "role": role, "briefFile": f"agents/{s}.yaml", "computer": MACHINE}
                      for s, name, role in seats]}
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


def seed_problem(db, run_id, job_id, hours_ago, problem, reason):
    """One run whose result is a problem, as the result route or the sweep writes it."""
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
        "SessionId": str(uuid.uuid4()), "InfraStatus": "started", "TaskStatus": "stopped-by-you",
        "Result": "problem", "Problem": problem, "ResultReason": reason, "ResultUtc": stamp(fired + timedelta(minutes=12)),
    }
    missing = [k for k in row if k not in cols]
    if missing:
        raise SystemExit(f"cron_runs has no column(s) {missing}; its columns are {cols}")
    c.execute(f"INSERT INTO cron_runs ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
    c.commit()
    c.close()
    print(f"problem {run_id} on {job_id}: {problem}")


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


def session_of(talks, n):
    sid, key = (talks / f"create-{n}.session.txt").read_text(encoding="utf-8").split()
    return sid, key


def wait_for_session(sid):
    """The stand-in pushes every second; the session (and its key) reach the Gateway within a few seconds."""
    for _ in range(20):
        sessions = check(requests.get(f"{GW}/sessions", headers=AUTH), "GET /sessions").json()
        found = [x for x in sessions if x["sessionId"] == sid]
        if found:
            time.sleep(2)  # one more push, so the key registration that follows the snapshot has landed
            return found[0]
        time.sleep(1)
    raise SystemExit(f"the session {sid} never reached the Gateway's roster")


def draw_create(browser, title, opened, session, create, prompt, name):
    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    page.set_content(
        "<html><body style='font-family:Segoe UI,sans-serif;margin:24px;max-width:1200px'>"
        f"<h2>{html.escape(title)}</h2>"
        f"<p><b>The Cockpit moved to:</b> <code>{html.escape(opened.replace(UI, ''))}</code></p>"
        f"<p><b>The session on the Gateway:</b> {html.escape(session['name'])} ({html.escape(session['status'])})</p>"
        f"<p><b>Started on:</b> {html.escape(MACHINE)}, in <code>{html.escape(create.get('repoPath') or '')}</code>; "
        f"factory: {html.escape(str(create.get('factory')))}; controller: {html.escape(str(create.get('controllerSessionId')))}; "
        f"parent: {html.escape(str(create.get('parentSessionId')))}</p>"
        "<p><b>The first prompt</b> (as the Director received it):</p>"
        f"<pre style='white-space:pre-wrap;background:#f4f4f4;padding:12px;border:1px solid #ccc'>{html.escape(prompt)}</pre>"
        "</body></html>")
    shot(page, name)
    page.close()


def cli(transcript, as_session, args, expect_exit):
    """Run the real cc-devthrottle from this tree AS a session, and keep what it printed."""
    sid, key = as_session
    env = dict(os.environ, CC_GATEWAY_URL=GW, CC_GATEWAY_SESSION_KEY=key, CC_SESSION_ID=sid, NO_COLOR="1")
    # This tree's command line, imported the way its own tests import it (the installed one may be older).
    launch = ("import sys; sys.path[:0] = [sys.argv[1], sys.argv[2]]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; "
              "from src.cli import tool_main; tool_main()")
    r = subprocess.run([str(CLI_PYTHON), "-c", launch, str(CLI_DIR), str(CLI_DIR.parent), *args], cwd=str(CLI_DIR),
                       env=env, capture_output=True, text=True, timeout=120)
    shown = " ".join(a if " " not in a else f'"{a}"' for a in args)
    transcript.append(f"$ cc-devthrottle {shown}      (as session {sid})\n{r.stdout}{r.stderr}[exit {r.returncode}]\n")
    print(f"cli {shown}: exit {r.returncode}")
    if r.returncode != expect_exit:
        raise SystemExit(f"cc-devthrottle {shown} exited {r.returncode}, expected {expect_exit}:\n{r.stdout}{r.stderr}")


def main():
    if RIG.exists():
        shutil.rmtree(RIG)
    (ROOT / "config").mkdir(parents=True)
    (ROOT / "config" / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    Path(FOLDER).mkdir(parents=True)
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

        # Tallyhand passes the name check: its seats are named Boss and Scout, both role words.
        register("qa-tallyhand", "Tallyhand (QA)", "boss", [("boss", "Boss", "Boss"), ("scout", "Scout", "Scout")])
        # WarmForward has no boss, a seat whose role says CEO, and a seat named like a person.
        register("qa-warmforward", "WarmForward (QA)", None,
                 [("chief", "Chief", "CEO"), ("writer", "Nora Hale", "Writer")])
        schedule("qa-tallyhand", "boss", "Do the Tallyhand morning run.")
        writer_job = schedule("qa-warmforward", "writer", "Write the weekly homeowner summary.")
        seed_problem(db, WRITER_PROBLEM, writer_job, 3, "did-not-report",
                     "its session ended without reporting how the run went (stopped by you after 12 min)")

        ui = subprocess.Popen(["npx.cmd", "vite", "--port", str(UI_PORT), "--strictPort", "--host", "127.0.0.1"],
                              env=dict(os.environ, COCKPIT_PROXY_TARGET=GW), stdout=open(RIG / "vite.out.txt", "w"),
                              stderr=subprocess.STDOUT, cwd=str(REPO / "apps" / "cockpit"))
        wait_for(UI, "the Cockpit dev server")

        with sync_playwright() as p:
            browser = p.chromium.launch(args=["--disable-gpu", "--use-gl=swiftshader", "--disable-dev-shm-usage"])
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            prepare(page)

            # 1. The Factories tab: the Factory Manager card, not started, asking where it runs, with the name check.
            go(page, "/factories", "fa-fm-placement")
            shot(page, "1-factories-with-the-factory-manager-card")

            # 2. Choose the computer and a folder, press Talk to the Factory Manager.
            page.select_option("[data-testid='fa-fm-computer']", MACHINE)
            page.fill("[data-testid='fa-fm-folder']", FOLDER)
            page.click("[data-testid='fa-fm-talk']")
            page.wait_for_url("**/session/**", timeout=30000)
            opened = page.url
            page.close()
            first = session_of(talks, 1)
            session = wait_for_session(first[0])
            create = json.loads((talks / "create-1.json").read_text(encoding="utf-8"))
            prompt = (talks / "create-1-first-prompt.txt").read_text(encoding="utf-8")
            shutil.copy(talks / "create-1.json", OUT / "talk-create.json")
            shutil.copy(talks / "create-1-first-prompt.txt", OUT / "talk-first-prompt.txt")
            if first[0] not in opened:
                raise SystemExit(f"the Cockpit moved to {opened}, not the session the Director made ({first[0]})")
            draw_create(browser, "Talk to the Factory Manager: the session the Gateway started", opened, session, create,
                        prompt, "2-talk-started-the-factory-manager")

            # 3. The card again: it runs, and the button opens it rather than starting another.
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            prepare(page)
            go(page, "/factories", "fa-fm-saved")
            shot(page, "3-card-while-the-factory-manager-runs")
            page.click("[data-testid='fa-fm-talk']")
            page.wait_for_url("**/session/**", timeout=30000)
            reopened = page.url
            page.close()
            if first[0] not in reopened or (talks / "create-2.json").exists():
                raise SystemExit(f"pressing it again should open the running one ({first[0]}), not start another: {reopened}")
            (OUT / "open-again-navigated-to.txt").write_text(reopened.replace(UI, "") + "\n", encoding="utf-8")
            binding = check(requests.get(f"{GW}/gateway/factory-manager", headers=AUTH), "GET binding").json()
            (OUT / "binding-after-talk.json").write_text(json.dumps(binding, indent=2), encoding="utf-8")

            # 4. A factory with no boss: Handle the issue is now offered, and goes to the Factory Manager.
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            prepare(page)
            go(page, "/factories/qa-warmforward", f"fa-problem-{WRITER_PROBLEM}")
            shot(page, "4-factory-with-no-boss-offers-handle-the-issue")

            # 5. Handle the issue: a Factory Manager talk briefed on the problem, now the bound Factory Manager.
            page.locator(f"[data-testid='fa-problem-{WRITER_PROBLEM}'] [data-testid='fa-problem-handle']").click()
            page.wait_for_url("**/session/**", timeout=30000)
            opened = page.url
            page.close()
            second = session_of(talks, 2)
            session = wait_for_session(second[0])
            create = json.loads((talks / "create-2.json").read_text(encoding="utf-8"))
            prompt = (talks / "create-2-first-prompt.txt").read_text(encoding="utf-8")
            shutil.copy(talks / "create-2.json", OUT / "handle-create.json")
            shutil.copy(talks / "create-2-first-prompt.txt", OUT / "handle-first-prompt.txt")
            draw_create(browser, "Handle the issue on a factory with no boss: the Factory Manager talk", opened, session,
                        create, prompt, "5-handle-the-issue-went-to-the-factory-manager")

            # 6. The command line, AS sessions: the binding, the name check, and resolve rights.
            transcript = []
            cli(transcript, second, ["factory", "manager", "show"], 0)
            cli(transcript, second, ["factory", "name-check"], 0)
            # The first Factory Manager session is no longer the bound one, and is in no factory: refused.
            cli(transcript, first, ["run", "resolve", WRITER_PROBLEM, "I am not the bound Factory Manager any more"], 1)
            # The bound Factory Manager resolves a problem of a factory it is not in, and its reason is kept.
            cli(transcript, second, ["run", "resolve", WRITER_PROBLEM, "Restarted the writer's schedule; the summary went out at 14:10."], 0)
            text = "\n".join(transcript)
            (OUT / "command-line-transcript.txt").write_text(text, encoding="utf-8")
            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            page.set_content(
                "<html><body style='font-family:Segoe UI,sans-serif;margin:24px;max-width:1200px'>"
                "<h2>The command line, run as sessions against the rig Gateway</h2>"
                f"<pre style='white-space:pre-wrap;background:#111;color:#ddd;padding:12px'>{html.escape(text)}</pre>"
                "</body></html>")
            shot(page, "6-command-line-binding-name-check-and-resolve-rights")
            page.close()

            # 7. The factory page afterwards: the problem is gone.
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            prepare(page)
            go(page, "/factories/qa-warmforward", "factory-page")
            page.wait_for_timeout(1500)
            if page.locator(f"[data-testid='fa-problem-{WRITER_PROBLEM}']").count() != 0:
                raise SystemExit("the resolved problem is still on the page")
            shot(page, "7-resolved-by-the-factory-manager-gone")
            browser.close()

        after = check(requests.get(f"{GW}/cron/problems?state=all", headers=AUTH), "GET /cron/problems?state=all").json()
        (OUT / "gateway-problems-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
        for p_ in after["problems"]:
            print(f"  after: {p_['runId']} {p_['state']} by={p_.get('resolvedBy')} reason={p_.get('resolvedReason')}")
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
