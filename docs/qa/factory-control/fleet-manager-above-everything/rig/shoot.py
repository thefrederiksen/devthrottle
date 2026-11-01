"""Screenshot QA: the Factory Manager is gone, and Handle the issue reaches the Fleet Manager.

Reuses the throwaway rig of docs/qa/factory-control/factory-manager/rig/shoot.py: a Gateway of this tree with auth on,
the stand-in Director, and the Cockpit's dev server, all started and stopped by this run. Playwright is a library here
because this is a repeatable script whose output is committed.

How the data was made, honestly: the factories and schedules go through the real routes. Three things are written
straight into the rig's tables, because the product no longer makes them: the PROBLEM (a run), the OLD MARK (four rows
of tenant_settings, the shape an account upgrades with) and the MISSING BOSS (registration refuses a factory with none). Everything after that is the product's code.

Usage: python shoot.py <out dir>     (CCDT_PYTHON names a Python that has the command line's packages.)
"""
import html
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parents[1] / "factory-manager" / "rig"))
import requests  # noqa: E402
import shoot as old  # noqa: E402
from playwright.sync_api import sync_playwright  # noqa: E402

OUT, RIG, ROOT, GW, UI, AUTH, MACHINE = old.OUT, old.RIG, old.ROOT, old.GW, old.UI, old.AUTH, old.MACHINE
PROBLEM = str(uuid.uuid4())
DEAD_MARK = "dead0000-0000-4000-8000-000000000001"


def seed_old_mark(db):
    c = sqlite3.connect(db)
    cols = [r[1] for r in c.execute("PRAGMA table_info(tenant_settings)")]
    tenant = "tenant_id" if "tenant_id" in cols else "TenantId"
    for key, value in (("factory_manager_session_id", DEAD_MARK), ("factory_manager_machine", "devlinux"),
                       ("factory_manager_folder", "/home/soren/factories"),
                       ("factory_manager_earlier_session_ids", DEAD_MARK)):
        row = {tenant: "local", "Key": key, "Value": value, "UpdatedAtUtc": old.NOW.strftime("%Y-%m-%d %H:%M:%S")}
        c.execute(f"INSERT INTO tenant_settings ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
    c.commit()
    rows = c.execute("SELECT Key, Value FROM tenant_settings WHERE Key LIKE 'factory_manager%'").fetchall()
    c.close()
    return rows


def press_handle(browser):
    page = browser.new_page(viewport={"width": 1280, "height": 1100})
    old.prepare(page)
    old.go(page, "/factories/qa-warmforward", f"fa-problem-{PROBLEM}")
    with page.expect_response(lambda r: r.url.endswith("/handle"), timeout=60000) as answer:
        page.locator(f"[data-testid='fa-problem-{PROBLEM}'] [data-testid='fa-problem-handle']").click()
    if not answer.value.ok:
        raise SystemExit(f"Handle the issue answered {answer.value.status}: {answer.value.text()}")
    page.wait_for_url("**/session/**", timeout=30000)
    opened = page.url
    page.close()
    return opened


def main():
    if RIG.exists():
        shutil.rmtree(RIG)
    (ROOT / "config").mkdir(parents=True)
    (ROOT / "config" / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)
    talks = RIG / "talks"
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    env.pop("CC_GATEWAY_NO_AUTH", None)
    env.pop("CC_GATEWAY_DB_CONNECTION", None)
    gw = subprocess.Popen([str(old.EXE), "--port", str(old.GW_PORT)], env=env, stdout=open(RIG / "gateway.out.txt", "w"),
                          stderr=subprocess.STDOUT, cwd=str(old.EXE.parent))
    standin = ui = None
    try:
        old.wait_for(f"{GW}/healthz", "the rig Gateway")
        deadline = time.time() + 120
        dbs = []
        while not dbs and time.time() < deadline:  # The database is created after the health route first answers.
            dbs = list(ROOT.rglob("gateway.db"))
            time.sleep(1)
        if len(dbs) != 1:
            raise SystemExit(f"expected one gateway.db, found {dbs}")
        db = dbs[0]
        deadline = time.time() + 120
        while True:  # The Gateway holds the database while it migrates at start.
            try:
                old.enroll_devices(db)
                break
            except sqlite3.OperationalError as ex:
                if "locked" not in str(ex) or time.time() > deadline:
                    raise
                time.sleep(2)
        old.wait_for(f"{GW}/gateway/factories", "the factory routes", headers=AUTH)
        stored = seed_old_mark(db)

        standin = subprocess.Popen([str(old.STANDIN), GW, MACHINE, str(talks), old.DIRECTOR_KEY], stdin=subprocess.PIPE,
                                   stdout=open(RIG / "standin.out.txt", "w"), stderr=subprocess.STDOUT)
        deadline = time.time() + 30
        while not any(d.get("directorId") == "qa-standin-director" for d in requests.get(f"{GW}/directors", headers=AUTH).json()):
            if time.time() > deadline:
                raise SystemExit("the stand-in Director did not connect")
            time.sleep(1)

        old.register("qa-tallyhand", "Tallyhand (QA)", "boss", [("boss", "Boss", "Boss")])
        # Registration now refuses a factory with no boss, so a boss-less one exists only as an older record: register it
        # with a boss, then take the boss off in the rig's table, which is the shape such a record has.
        old.register("qa-warmforward", "WarmForward (QA)", "boss",
                     [("boss", "Boss", "Boss"), ("chief", "Chief", "Chief"), ("writer", "Writer", "Writer")])
        c = sqlite3.connect(db)
        taken = c.execute("UPDATE factory_registry SET BossSeat = NULL WHERE Factory = 'qa-warmforward'").rowcount
        c.commit()
        c.close()
        if taken != 1:
            raise SystemExit(f"expected to take the boss off one row, changed {taken}")
        job = old.schedule("qa-warmforward", "writer", "Write the weekly homeowner summary.")
        old.seed_problem(db, PROBLEM, job, 3, "did-not-report", "its session ended without reporting how the run went")
        # A run keeps the factory it ran in; the older rig's row predates that column.
        c = sqlite3.connect(db)
        c.execute("UPDATE cron_runs SET Factory = 'qa-warmforward' WHERE JobId = ?", (job,))
        c.commit()
        c.close()

        ui = subprocess.Popen(["npx.cmd", "vite", "--port", str(old.UI_PORT), "--strictPort", "--host", "127.0.0.1"],
                              env=dict(os.environ, COCKPIT_PROXY_TARGET=GW), stdout=open(RIG / "vite.out.txt", "w"),
                              stderr=subprocess.STDOUT, cwd=str(old.REPO / "apps" / "cockpit"))
        old.wait_for(UI, "the Cockpit dev server")

        retired = {path: requests.get(f"{GW}{path}", headers=AUTH).status_code
                   for path in ("/gateway/factory-manager", "/gateway/factory-agents/factory-manager")}

        with sync_playwright() as p:
            browser = p.chromium.launch(args=["--disable-gpu", "--use-gl=swiftshader", "--disable-dev-shm-usage"])

            # 1. The Factories screen, for an account that still holds the old mark: three tabs and no card.
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            old.prepare(page)
            page.goto(f"{UI}/factories")
            page.wait_for_selector("text=Talk to the boss", timeout=30000)
            old.shot(page, "1-factories-tab-with-no-factory-manager")
            tabs = page.locator("[role=tab]").all_inner_texts()
            if tabs != ["Factories", "Activity", "Reports"] or "Factory Manager" in page.inner_text("body"):
                raise SystemExit(f"the screen still offers it: tabs={tabs}")

            # 2. A factory with no boss says where Handle the issue goes.
            old.go(page, "/factories/qa-warmforward", f"fa-problem-{PROBLEM}")
            old.shot(page, "2-factory-with-no-boss-handle-goes-to-the-fleet-manager")
            if "goes to the Fleet Manager" not in page.inner_text("body"):
                raise SystemExit("the factory page does not say the problem goes to the Fleet Manager")
            page.close()

            # 3. No Fleet Manager runs and the account has no computer for one: the press says so in the service's own
            # sentence, and keeps nothing. (That a start is taken when one can start is the unit tests'.)
            page = browser.new_page(viewport={"width": 1280, "height": 1100})
            old.prepare(page)
            old.go(page, "/factories/qa-warmforward", f"fa-problem-{PROBLEM}")
            refused = None
            with page.expect_response(lambda r: r.url.endswith("/handle"), timeout=60000) as answer:
                page.locator(f"[data-testid='fa-problem-{PROBLEM}'] [data-testid='fa-problem-handle']").click()
            refused = (answer.value.status, answer.value.json()["error"])
            old.shot(page, "3-with-no-fleet-manager-it-says-why")
            page.close()
            if refused[0] != 409:
                raise SystemExit(f"expected the refusal, got {refused}")

            # 4. The owner has a Fleet Manager: a session the owner started and marked, as Settings does.
            old.check(requests.post(f"{GW}/directors/qa-standin-director/sessions", headers=AUTH, json={
                "repoPath": str(RIG / "qa-tallyhand"), "agent": "ClaudeCode", "name": "Fleet Manager"}), "start a session")
            first = old.session_of(talks, 1)
            session = old.wait_for_session(first[0])
            old.check(requests.put(f"{GW}/gateway/fleet-manager", headers=AUTH, json={"sessionId": first[0]}), "mark it")

            # 5. Handle the issue reaches it, opens it, and starts nothing.
            opened = press_handle(browser)
            if first[0] not in opened:
                raise SystemExit(f"the Cockpit opened {opened}, not the Fleet Manager {first[0]}")

            # 6. Pressed again while it waits: nothing new is started, and the event is not doubled.
            again = press_handle(browser)
            if first[0] not in again or (talks / "create-2.json").exists():
                raise SystemExit(f"a second press should open the same Fleet Manager: {again}")

            transcript = []
            old.cli(transcript, first, ["fleet", "events"], 0)
            old.cli(transcript, first, ["factory", "name-check"], 0)
            old.cli(transcript, first, ["factory", "manager", "show"], 2)
            # The marked Fleet Manager resolves a problem of a factory it is not in, and its reason is kept.
            old.cli(transcript, first, ["run", "resolve", PROBLEM, "Restarted the writer's schedule; the summary went out."], 0)
            text = "\n".join(transcript)
            (OUT / "command-line-transcript.txt").write_text(text, encoding="utf-8")

            events = old.check(requests.get(f"{GW}/gateway/fleet-manager/events?status=all", headers=AUTH), "GET events").json()
            problems = [e for e in events["events"] if e["kind"] == "problem"]
            if len(problems) != 1:
                raise SystemExit(f"expected one problem event after two presses, found {len(problems)}")
            (OUT / "problem-event.json").write_text(json.dumps(problems[0], indent=2), encoding="utf-8")

            page = browser.new_page(viewport={"width": 1280, "height": 1000})
            page.set_content(
                "<html><body style='font-family:Segoe UI,sans-serif;margin:24px;max-width:1200px'>"
                "<h2>Handle the issue reached the Fleet Manager</h2>"
                f"<p><b>Cockpit opened:</b> <code>{html.escape(opened.replace(UI, ''))}</code> on both presses</p>"
                f"<p><b>Marked Fleet Manager:</b> {html.escape(first[0])} ({html.escape(session['name'])})</p>"
                f"<p><b>With no Fleet Manager, the press answered {refused[0]}:</b> {html.escape(refused[1])}</p>"
                "<p><b>The event it was sent:</b></p>"
                f"<pre style='white-space:pre-wrap;background:#f4f4f4;padding:12px'>{html.escape(problems[0]['words'])}</pre>"
                "<p><b>The command line, run as that session:</b></p>"
                f"<pre style='white-space:pre-wrap;background:#111;color:#ddd;padding:12px'>{html.escape(text)}</pre>"
                "</body></html>")
            old.shot(page, "4-handle-the-issue-reached-the-fleet-manager")
            page.close()
            browser.close()

        after = old.check(requests.get(f"{GW}/cron/problems?state=all", headers=AUTH), "problems").json()
        (OUT / "gateway-problems-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
        c = sqlite3.connect(db)
        kept = c.execute("SELECT Key, Value FROM tenant_settings WHERE Key LIKE 'factory_manager%'").fetchall()
        c.close()
        if sorted(kept) != sorted(stored):
            raise SystemExit(f"the stored rows changed: {stored} -> {kept}")
        (OUT / "old-mark-rows-after.json").write_text(json.dumps({"rows": kept, "retiredRoutes": retired}, indent=2), encoding="utf-8")
        print("PASS", retired, [(x["state"], x.get("resolvedBy")) for x in after["problems"]])
    finally:
        if ui is not None:
            subprocess.run(["taskkill", "/PID", str(ui.pid), "/T", "/F"], capture_output=True)
        if standin is not None:
            standin.stdin.close()
            standin.wait(30)
        gw.terminate()
        gw.wait(30)
        print("stopped everything this run started")


if __name__ == "__main__":
    main()
