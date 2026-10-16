"""QA for Factory Definition, phase 7: Eric's factory, on a throwaway Gateway and a REAL Director built from this tree.

The factory's folder is NOT in this repository (it is kept with the mission's private briefs); the rig takes its path.
Nothing of the folder's text is written into the transcript except what an agent quotes; its files are listed with
their sizes and hashes.

By the owner's word the factory starts with no check: one seat, one daily start at 08:00 America/Toronto, and the
agent checks the mailbox itself. The rig reuses the phase 5b rig (../../phase-5b/rig/run_qa.py) for everything it
starts and reads, on its own port, its own storage roots and its own scheduled task:
- a Gateway (Debug build of THIS tree) on port 7992, storage root under %TEMP%, Tailscale off, authentication ON,
  factory agents on in the rig's OWN config.json, serving this tree's Cockpit build from its wwwroot/c;
- a real Director: scripts\\local-build\\cc-director7.exe (SLOT 7), launched ONLY through a scheduled task of its own,
  `cc-director-launch-p7`, set up exactly as CLAUDE.md rule 0b sets up `cc-director-launch`, and stopped ONLY by its
  named shutdown signal. The rig registers the task at the start and unregisters it at the end.

What it does:
1. Imports the folder AS WRITTEN (every TO FILL: still open) as a person: version 1, a draft, with what changes.
2. Saves version 2: the same folder with ONE line changed - the start's time, moved from 08:00 to a few minutes from
   now (same zone), because the rig cannot wait for 08:00. What changes from 1 to 2 is printed. Version 2 is published
   as the rig's person.
3. The start fires: exactly one run and one session, started from its kit (its working folder the kit's work/, seat
   mail-desk, definition version 2), with the brief in its first prompt. The agent follows the brief as written: the
   instructions still say TO FILL:, so it opens no mailbox, writes one journal line and stops.
4. Pictures of the Cockpit, in headless Chrome signed in as the rig's person: the Definition tab, the Schedule screen
   with the start's runs, and the seat's page. (A picture of the draft is taken only if the factory has a page before
   its first publish; on main at 49f3fed5c it has none, and the transcript says so.)

Usage (from the repository root, after building the Gateway, the slot 7 Director and the Cockpit):
    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    powershell -NoProfile -File scripts\\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\\scripts\\local-build"
    (cd apps/cockpit && npm run build)
    uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" \\
        --with tzdata --with playwright python docs/qa/factory-definition/phase-7/eric-rig/run_qa.py <eric folder> <out dir>
"""

import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import traceback
import urllib.error
import zoneinfo
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("p5b", HERE.parents[1] / "phase-5b" / "rig" / "run_qa.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

FOLDER = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve()

p.TASK = "cc-director-launch-p7"
p.PORT = 7992
p.GW = f"http://127.0.0.1:{p.PORT}"
p.FACTORY = "eric"
p.RIG = Path(os.environ["TEMP"]) / "factory-definition-p7-eric"
p.GW_ROOT = p.RIG / "gateway-root"
p.DIR_ROOT = p.RIG / "director-root"
p.HOME = p.DIR_ROOT / "instances" / "default"
p.PLAIN = p.RIG / "repos" / "qa-plain"
SEAT = "mail-desk"
ZONE = "America/Toronto"
REAL_CRON = 'cron: "0 8 * * *"'
MARKER = "It is time for the 08:00 mail check"
BRIEF_MARK = "You are the Mail Desk of Eric's factory"
COCKPIT_DIST = p.REPO / "apps" / "cockpit" / "dist"

_shown = p.shown


def shown(text):
    """The phase 5b masking, and the private folder's path as <eric folder>."""
    text = str(text)
    for form in (str(FOLDER), str(FOLDER).replace("\\", "/"), json.dumps(str(FOLDER))[1:-1]):
        text = text.replace(form, "<eric folder>").replace(form.lower(), "<eric folder>")
    return _shown(text)


p.shown = shown


def ps(script, check=True):
    """The phase 5b helper, decoding the answer so that a process whose command line is not in this machine's code
    page (one was, on 2026-10-16) cannot end the run."""
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=180)
    if check and r.returncode != 0:
        raise SystemExit(f"powershell failed ({r.returncode}): {script}\n{r.stdout}{r.stderr}")
    return r.stdout


p.ps = ps


def stop_director(pid):
    """The phase 5b stop, with ten minutes to exit: this machine's Director took over a minute (2026-10-16)."""
    reg = p.registration(pid)
    if reg is None:
        raise SystemExit(f"no instance registration names pid {pid}; it cannot be asked to stop")
    signal = f"Local\\cc-director-shutdown-{reg['DirectorId'].lower()}"
    p.ps(f"$e = [System.Threading.EventWaitHandle]::OpenExisting('{signal}'); $e.Set() | Out-Null; $e.Dispose()")
    p.wait_for(f"the slot 7 Director (pid {pid}) did not exit after its shutdown signal",
               lambda: pid not in p.director_processes(), seconds=600, every=3)
    p.record("the slot 7 Director stopped by its named shutdown signal",
             f"  signal set: {signal}\n  cc-director7 processes afterwards: {p.director_processes()}")
    print(f"slot 7 Director {pid} stopped by its named signal", flush=True)


p.stop_director = stop_director


def gateway_serving():
    try:
        p.get("/factory-definition-schemas", key="qa-not-enrolled")
        return True
    except urllib.error.HTTPError as exc:
        return exc.code in (401, 403)


# ===================================================================================================== the folder


def files_of(folder):
    return {f.relative_to(folder).as_posix(): f.read_bytes() for f in sorted(folder.rglob("*")) if f.is_file()}


def listing(title, files):
    p.record(title, "\n".join(f"  {rel}  {len(data)} bytes  sha256 {hashlib.sha256(data).hexdigest()}"
                              for rel, data in files.items()))


def rig_copy(at):
    """The folder with ONE line changed: the start's time, to `at` (local time in the same zone)."""
    target = p.RIG / "definitions" / "v-rig"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(FOLDER, target)
    seat_file = target / "seats" / f"{SEAT}.yaml"
    text = seat_file.read_text(encoding="utf-8")
    if text.count(REAL_CRON) != 1 or text.count(f"timeZone: {ZONE}") != 1:
        raise SystemExit(f"seats/{SEAT}.yaml does not hold {REAL_CRON} with timeZone {ZONE} exactly once")
    near = f'cron: "{at.minute} {at.hour} * * *"'
    seat_file.write_text(text.replace(REAL_CRON, near), encoding="utf-8", newline="\n")
    before, after = files_of(FOLDER), files_of(target)
    changed = [rel for rel in after if before.get(rel) != after[rel]]
    if changed != [f"seats/{SEAT}.yaml"] or set(before) != set(after):
        raise SystemExit(f"the rig changed more than the start's time: {changed}")
    diff = [(a, b) for a, b in zip(before[changed[0]].decode().splitlines(), after[changed[0]].decode().splitlines())
            if a != b]
    p.record("version 2, the rig's copy: the one line it changes (every other line and file is the folder's)",
             "\n".join(f"  seats/{SEAT}.yaml:  {a.strip()}  ->  {b.strip()}" for a, b in diff))
    listing("version 2, the rig's copy: every file", after)
    return str(target)


# ===================================================================================================== the steps


def step_import():
    p.note("STEP 1 - the folder AS WRITTEN, imported as a person, every TO FILL: still open: what the owner's own "
           "import sees.")
    listing("the folder as written: every file (sizes and hashes; the folder is private)", files_of(FOLDER))
    r = p.cli(["factory", "import", str(FOLDER), "--note", "Eric's factory as written, every TO FILL: still open",
               "--json"], 0)
    p.STATE["newest"] = json.loads(r.stdout)["version"]
    p.cli(["factory", "definition", "diff", p.FACTORY], 0)
    status, _ = p.raw("GET", f"/gateway/factories/{p.FACTORY}", None, None)
    return status


def step_publish():
    at = (datetime.now(timezone.utc) + timedelta(minutes=5)).replace(second=0, microsecond=0)
    local = at.astimezone(zoneinfo.ZoneInfo(ZONE))
    p.note(f"STEP 2 - version 2, the rig's copy (the start moved to {local:%H:%M} {ZONE}, {p.utc(at)} UTC), saved and "
           "published as the rig's person.")
    v = p.save(rig_copy(local), "the QA rig's copy: the start moved to a few minutes from now")
    p.cli(["factory", "definition", "diff", p.FACTORY, "--from", "1", "--to", str(v)], 0)
    p.publish(v)
    p.cli(["factory", "definition", "history", p.FACTORY], 0)
    jobs = [j for j in p.get("/cron/jobs")["jobs"] if j.get("factory") == p.FACTORY]
    if len(jobs) != 1:
        raise SystemExit(f"the publish made {len(jobs)} schedules for {p.FACTORY}, not one: {jobs}")
    p.cli(["schedule", "list"], 0)
    return jobs[0], at


def step_fire(db, job, at):
    p.note("STEP 3 - the start fires. Expected: exactly one run and one session, started from its kit, the brief in "
           "its first prompt; the agent follows the brief as written (instructions still TO FILL:): no mailbox opened, "
           "one journal line, done.")
    since = time.time()
    runs = "SELECT Id, JobName, Seat, FiredUtc, Result, InfraStatus, SessionId, ResultReason FROM cron_runs " \
           "WHERE Factory = ? ORDER BY FiredUtc"
    wait = (at - datetime.now(timezone.utc)).total_seconds() + 600
    # The session is found by its history row, not by the run's row: on this machine the Director once took longer
    # than the Gateway's 30 seconds to answer the start, and the run was recorded as not started while the session
    # did start (2026-10-16). The run's row is recorded below exactly as the Gateway keeps it.
    history_sql = "SELECT SessionId FROM session_history WHERE Factory = ? ORDER BY rowid"
    sid = p.wait_for("the start did not fire and start a session", lambda: next(
        (r[0] for r in p.query(db, history_sql, (p.FACTORY,))), None), seconds=int(wait), every=5, tend=True)
    p.rows(db, "STEP 3 - the run's row as the Gateway keeps it, once the session exists", runs, (p.FACTORY,))
    prompts = p.HOME / "factory-run" / sid / "prompts"
    sent = p.wait_for("the Director saved no first prompt in the kit", lambda: sorted(prompts.glob("*"))
                      if prompts.exists() and any(prompts.iterdir()) else None, seconds=300, every=2, tend=True)
    text = sent[0].read_text(encoding="utf-8", errors="replace")
    p.record("STEP 3 - the first prompt, as the Director saved it in the kit's prompts/ folder before sending it",
             "".join(f"  {x}\n" for x in text.splitlines()))
    p.record("STEP 3 - its first prompt holds the brief and the start's prompt",
             f"  the brief's first line ('{BRIEF_MARK}'): {BRIEF_MARK in text}\n"
             f"  the start's prompt ('{MARKER}'): {MARKER in text}")
    if BRIEF_MARK not in text or MARKER not in text:
        raise SystemExit("the first prompt does not hold both the brief and the start's prompt")
    p.folder_listing("STEP 3 - the session's kit folder while it runs "
                     "(<rig>/director-root/instances/default/factory-run/<session>)", p.HOME / "factory-run" / sid)
    path = p.wait_answer(MARKER, since, seconds=1200)
    p.summarize("STEP 3 - the session the start started", path)
    row = p.session_row("STEP 3 - the session the start started", sid)
    p.rows(db, "STEP 3 - its session history row", "SELECT SessionId, DirectorId, Factory, FactorySeat, "
           "FactoryDefinitionVersion, RepoPath FROM session_history WHERE SessionId = ? COLLATE NOCASE", (sid,))
    time.sleep(60)        # a second fire or a second session would show here
    found = p.rows(db, "STEP 3 - every run of the factory's schedule, a minute after the session finished", runs,
                   (p.FACTORY,))
    sessions = [s.get("sessionId") for s in p.listed_sessions() if s.get("factory") == p.FACTORY]
    history = p.query(db, "SELECT SessionId FROM session_history WHERE Factory = ?", (p.FACTORY,))
    p.record("STEP 3 - exactly one", f"  runs of the schedule: {len(found)}\n  sessions of the factory listed now: "
             f"{len(sessions)} {sessions}\n  session history rows of the factory: {len(history)}")
    if len(found) != 1 or len(history) != 1:
        raise SystemExit(f"expected one run and one session: {len(found)} runs, {len(history)} history rows")
    p.cli(["schedule", "runs", job["id"]], 0)
    p.cli(["factory", "journal", "list", "--factory", p.FACTORY, "--seat", SEAT], 0)
    return sid, row


# ===================================================================================================== pictures


def pictures(shots):
    """Headless Chrome, signed in with the rig person's key (phase 6's way), one full-page picture each."""
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        browser = pw.chromium.launch(channel="chrome", headless=True)
        try:
            context = browser.new_context(viewport={"width": 1280, "height": 1400})
            context.add_cookies([{"name": "cc-gateway-token", "value": p.PERSON_KEY, "url": p.GW}])
            page = context.new_page()
            page.goto(p.GW + "/", wait_until="domcontentloaded")
            page.evaluate("""(key) => {
                localStorage.setItem('cc.accounts', JSON.stringify([
                  { id: 'rig', label: 'Rig', email: null, deviceKey: key, installId: 'rig' }]));
                localStorage.setItem('cc.activeAccount', 'rig');
                localStorage.setItem('cc.deviceKey', key);
            }""", p.PERSON_KEY)
            for name, address, selector, then in shots:
                page.goto(p.GW + address, wait_until="networkidle")
                page.wait_for_selector(selector, timeout=30000)
                if then:
                    then(page)
                time.sleep(2)
                page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
                p.record(f"picture {name}.png", f"  {address}")
                print(f"picture {name}.png", flush=True)
            context.close()
        finally:
            browser.close()


def open_runs(page):
    """The Schedule screen hides factory schedules until asked; the seat's row then opens its recent runs."""
    page.click("text=Show factory schedules")
    page.click(f"text=seat: {SEAT}")
    page.wait_for_selector("text=Recent runs", timeout=30000)


# ===================================================================================================== main


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for needed in (p.EXE, p.DIRECTOR_EXE, COCKPIT_DIST / "index.html", FOLDER / "factory.yaml"):
        if not needed.exists():
            raise SystemExit(f"build it first: {needed} does not exist")
    if p.director_processes():
        raise SystemExit("a cc-director7 from this tree is already running; this rig starts its own and never stacks one")
    if p.ps(f"Get-ScheduledTask -TaskName '{p.TASK}' -ErrorAction SilentlyContinue | ForEach-Object {{ $_.TaskName }}",
            check=False).strip():
        raise SystemExit(f"{p.TASK} is already registered: another run of this rig may be going. It is left untouched.")
    if p.RIG.exists() and p.rig_processes():
        raise SystemExit(f"processes still run from {p.RIG}: {p.rig_processes()}; wait for them to end")
    if p.RIG.exists():
        p.remove_tree(p.RIG)
    p.GW_ROOT.mkdir(parents=True)
    (p.GW_ROOT / "config").mkdir()
    (p.GW_ROOT / "config" / "config.json").write_text('{ "factoryAgents": { "enabled": true } }\n', encoding="utf-8")
    cockpit = p.EXE.parent / "wwwroot" / "c"
    if cockpit.exists():
        shutil.rmtree(cockpit)
    shutil.copytree(COCKPIT_DIST, cockpit)
    p.STATE.update(newest=0, published=0)
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(p.GW_ROOT), CC_GATEWAY_NO_TAILSCALE="1")
    for k in ("CC_GATEWAY_AUTH", "CC_GATEWAY_NO_AUTH", "REPORT_SERVICE_TOKEN", "NOTIFY_OWNER_SERVICE_TOKEN"):
        env.pop(k, None)
    log = open(p.RIG / "gateway.out.txt", "w", encoding="utf-8")
    gw = subprocess.Popen([str(p.EXE), "--port", str(p.PORT)], env=env, stdout=log, stderr=subprocess.STDOUT,
                          cwd=str(p.EXE.parent))
    director = None
    task_changed = False
    try:
        p.wait_for("the rig Gateway wrote no gateway.db",
                   lambda: bool(list(p.GW_ROOT.rglob("gateway.db"))) or gw.poll() is not None)
        if gw.poll() is not None:
            raise SystemExit("the rig Gateway exited; see gateway.out.txt")
        db = list(p.GW_ROOT.rglob("gateway.db"))[0]
        p.wait_for("the rig gateway.db did not get its tables", lambda: {"device_credentials", "cron_runs",
                   "session_history"} <= {r[0] for r in p.query(db, "SELECT name FROM sqlite_master WHERE type='table'")})
        # The Gateway answers 503 while it is still starting, and its database is busy then; enroll only once it
        # serves (a 401 to an unknown key is serving).
        p.wait_for("the rig Gateway did not finish starting", gateway_serving, seconds=600, every=2)
        p.enroll(db)
        p.wait_for("the rig Gateway did not answer", lambda: p.get("/factory-definition-schemas") is not None)

        p.shared_task_target("before this run")
        wrapper = p.prepare_director(p.DIRECTOR_KEY)
        p.set_task("cmd.exe", f'/c "{wrapper}"', str(p.DIRECTOR_EXE.parent))
        task_changed = True
        p.ps(f"Start-ScheduledTask -TaskName '{p.TASK}'")
        director = p.wait_for("the slot 7 Director did not start from the task",
                              lambda: (p.director_processes() or [None])[0], 60)
        # Starts on this machine took three minutes, then over twenty, to register (2026-10-16), so the wait is thirty.
        reg = p.wait_for("the slot 7 Director wrote no instance registration", lambda: p.registration(director), 1800)
        p.wait_for("the slot 7 Director did not connect to the rig Gateway", lambda: [
            d for d in p.get("/directors") if d.get("directorId") == reg["DirectorId"]], 120)
        p.note(f"Gateway: {p.GW}, built from this tree, storage root <rig>\\gateway-root made empty for this run, "
               f"serving this tree's Cockpit build. The slot 7 Director (pid {director}, id {reg['DirectorId']}) was "
               f"started by its own {p.TASK} scheduled task through <rig>\\start-director7.cmd, storage root "
               f"<rig>\\director-root, and connected on <machine>.")

        page_status = step_import()
        if page_status == 200:
            pictures([("01-definition-tab-draft-as-imported", f"/factories/{p.FACTORY}/definition",
                       "[data-testid=fa-def-state]", None)])
        else:
            p.record("no picture of the draft before the first publish",
                     f"  GET /gateway/factories/{p.FACTORY} answered {page_status} before the first publish: the "
                     "factory has no page to open until its first publish registers it")
        job, at = step_publish()
        step_fire(db, job, at)
        pictures([
            ("02-definition-tab-published", f"/factories/{p.FACTORY}/definition", "[data-testid=fa-def-state]", None),
            ("03-schedule-runs", "/schedule", "text=Show factory schedules", open_runs),
            ("04-seat-page", f"/factories/{p.FACTORY}/seats/{SEAT}", "[data-testid=factory-seat-page]", None),
        ])
    except BaseException:
        traceback.print_exc()           # before the cleanup below, so a cleanup failure cannot hide it
        raise
    finally:
        try:
            for s in p.listed_sessions() if gw.poll() is None else []:
                if s.get("factory") == p.FACTORY:
                    p.stop_session(s.get("sessionId"))
        except (urllib.error.URLError, OSError) as exc:
            print(f"could not list sessions to stop them: {exc}", flush=True)
        try:
            if director is not None and director in p.director_processes():
                p.wait_for("the slot 7 Director never registered, so it cannot be signalled",
                           lambda: p.registration(director), 1800)
                p.wait_for_tool_install(director)
                p.stop_director(director)
        finally:
            if task_changed:
                p.unregister_task()
            p.shared_task_target("after this run")
            gw.terminate()
            try:
                gw.wait(timeout=30)
            except subprocess.TimeoutExpired:
                gw.kill()
            log.close()
            (OUT / "transcript.txt").write_text("\n".join(p.TRANSCRIPT), encoding="utf-8", newline="\n")
            print(f"wrote {OUT / 'transcript.txt'}")


if __name__ == "__main__":
    main()
