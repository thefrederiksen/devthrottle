"""Screenshots and real Gateway answers for Factory Seats, phase 1, from a throwaway local Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    python docs/qa/factory-seats/phase-1/capture.py

It needs Playwright for Python and Google Chrome. With uv, and nothing installed into the machine's own Python:

    uv run --no-project --with playwright --with typer --with click --with rich --with requests \
        --with ./tools/cc_storage --with ./tools/cc_shared --with ./tools/cc-devthrottle \
        python docs/qa/factory-seats/phase-1/capture.py

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, which is where the Gateway serves it from.
  2. Starts the Gateway on port 7991 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's Gateway nor the owner's factories or schedules.
  3. Registers three demonstration factories: one with a seat that has a schedule, a seat with two and a boss with
     none; one nothing is scheduled for; and one whose only schedule is switched off.
  4. Opens the Cockpit in headless Chrome (Playwright as a library: a repeatable script, not interactive work),
     signed in with the scratch Gateway's own token, and takes the screenshots into this folder, at 1280 and at 375
     pixels wide. At 375 the Cockpit's menu is collapsed, so the picture shows the page.
  5. Writes every Gateway answer the report quotes into answers.json, exactly as the Gateway sent it.
  6. Stops the Gateway it started. The scratch data folder is left for inspection and is not in the repository.

ONE SCREENSHOT IS NOT FROM THE GATEWAY'S OWN DATA, and its file name says so: seat-schedule-missing-*.png. A seat that
names a schedule the Gateway does not have cannot be produced on a real Gateway since issue #3650, because a seat's
schedule list is derived from the schedules themselves. The page still has to draw that case correctly, so for that one
picture the browser is handed the Scout's real page with its schedule replaced by exactly what the Gateway's fold
returns for a missing schedule (FactorySeatPageFoldTests.Page_ASeatWhoseScheduleIsMissing_SaysSo_AndOffersNothingToPress).
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
PORT = 7991
BASE = f"http://127.0.0.1:{PORT}"
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "seat-demo"
TALK_ONLY = "talk-only-demo"
PAUSED = "paused-demo"

# Every Gateway answer the report quotes, by name, exactly as sent.
ANSWERS = {}


def log(msg):
    print(f"[seats-capture] {msg}", flush=True)


def request(method, path, token, body=None):
    """One call. Returns (status, parsed body or text); never raises on an HTTP status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, text = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, text = exc.code, exc.read().decode("utf-8", "replace")
    try:
        return status, (json.loads(text) if text else None)
    except json.JSONDecodeError:
        return status, text


def call(method, path, token, body=None):
    """A call that must succeed: anything but a 2XX stops the run with the Gateway's answer."""
    status, answer = request(method, path, token, body)
    if not 200 <= status < 300:
        raise SystemExit(f"{method} {path} failed: HTTP {status} {answer}")
    return answer


def keep(name, method, path, status, answer):
    ANSWERS[name] = {"request": f"{method} {path}", "status": status, "body": answer}
    log(f"answer kept: {name} -> HTTP {status}")


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
    raise SystemExit(f"the Gateway did not answer on port {PORT} within two minutes")


def find_token(root):
    for path in Path(root).rglob("gateway-token.txt"):
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    raise SystemExit(f"no gateway-token.txt under {root}: the Gateway did not write its token")


def wait_until_ready(token):
    # The Gateway listens before its database is open and answers 503 "starting" until then.
    for _ in range(120):
        status, _ = request("GET", "/cron/jobs", token)
        if 200 <= status < 300:
            return
        if status not in (0, 503):
            raise SystemExit(f"GET /cron/jobs answered HTTP {status} while waiting for readiness")
        time.sleep(1)
    raise SystemExit("the Gateway was not ready within two minutes")


def job(name, cron, factory, seat, prompt):
    return {
        "name": name,
        "enabled": True,
        "scheduleKind": "recurring",
        "cronExpression": cron,
        "timeZoneId": "America/Toronto",
        "target": {"machine": "RIG_MACHINE"},
        "action": {"repoPath": "/home/rig/" + factory, "seed": prompt},
        "preventOverlap": True,
        "notifyOn": "none",
        "factory": factory,
        "seat": seat,
    }


def register(token, factory, title, seats, goal=None):
    body = {
        "factory": factory,
        "title": title,
        "folder": "/home/rig/" + factory,
        "computer": "RIG_MACHINE",
        "bossSeat": "boss",
        "seats": seats,
    }
    if goal is not None:
        body.update({"goalText": goal, "goalFile": "GOAL.md", "goalApprovedOn": "2026-10-04"})
    call("PUT", "/gateway/factory/registry", token, body)


def seed(token):
    """The three factories. Returns the ids of the Scout's schedule and the Writer's two."""
    register(token, FACTORY, "Seat Demo", [
        {"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"},
        {"id": "scout", "name": "Scout", "role": "Scout", "briefFile": "seats/scout.yaml"},
        {"id": "writer", "name": "Writer", "role": "Writer", "briefFile": "seats/writer.yaml"},
    ], goal="Publish one useful page a week and keep every page we publish correct.")
    scout = call("POST", "/cron/jobs", token, job("Seat Demo Factory - Scout - morning scan", "15 6 * * *", FACTORY, "scout", "Morning scan."))
    morning = call("POST", "/cron/jobs", token, job("Seat Demo Factory - Writer - morning draft", "0 9 * * *", FACTORY, "writer", "Morning draft."))
    evening = call("POST", "/cron/jobs", token, job("Seat Demo Factory - Writer - evening review", "0 18 * * *", FACTORY, "writer", "Evening review."))
    paused = dict(evening, enabled=False)
    call("PUT", f"/cron/jobs/{evening['id']}", token, paused)

    # What the seats have left behind: a talk with the boss, and one run of the Scout.
    for row in (
        {"factory": FACTORY, "factoryAgent": "boss", "outcome": "talked", "what": "decided: one page a week, checked before it goes out"},
        {"factory": FACTORY, "factoryAgent": "scout", "outcome": "done", "what": "found 3 pages with a broken link", "subject": "broken links"},
    ):
        status, answer = request("POST", "/gateway/factory/activity", token, row)
        log(f"activity row for {row['factoryAgent']}: HTTP {status}")
        if not 200 <= status < 300:
            raise SystemExit(f"could not write the activity row: HTTP {status} {answer}")

    # A factory nothing is scheduled for, and one whose only schedule is switched off: TALK ONLY beside PAUSED.
    register(token, TALK_ONLY, "Talk Only Demo", [
        {"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"},
        {"id": "helper", "name": "Helper", "role": "Helper", "briefFile": "seats/helper.yaml"},
    ], goal="Answer the owner when asked.")
    register(token, PAUSED, "Paused Demo", [
        {"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"},
    ])
    off = call("POST", "/cron/jobs", token, job("Paused Demo Factory - Boss - morning run", "0 7 * * *", PAUSED, "boss", "Morning run."))
    call("PUT", f"/cron/jobs/{off['id']}", token, dict(off, enabled=False))

    # A plain schedule in no factory, for the refusal "there is nothing to take off".
    plain = call("POST", "/cron/jobs", token, dict(job("Daily job search", "0 8 * * *", FACTORY, "scout", "Search."), factory=None, seat=None))
    return scout, morning, evening, plain


def link(token, schedule):
    """Put a schedule back on the Scout, so the next pass starts from the same state."""
    current = call("GET", f"/cron/jobs/{schedule['id']}", token)
    call("PUT", f"/cron/jobs/{schedule['id']}", token, dict(current, factory=FACTORY, seat="scout"))


def shot(page, name, suffix):
    page.screenshot(path=str(OUT / f"{name}-{suffix}.png"), full_page=True)
    log(f"{name}-{suffix}.png")


def wait(page, selector, what):
    try:
        page.wait_for_selector(selector, timeout=30000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "seats-rig-debug.png"
        page.screenshot(path=str(debug), full_page=True)
        raise SystemExit(f"{what} did not appear ({selector}); the page is in {debug}")


def capture(token, scout):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        try:
            # Tall enough that a whole page is in one picture: the Cockpit scrolls inside its own frame, so a
            # "full page" screenshot is only ever as tall as the window.
            for width, height, suffix in ((1280, 1300, "desktop"), (375, 2300, "phone")):
                context = browser.new_context(viewport={"width": width, "height": height})
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
                        // At phone width the menu is collapsed, as a reader on a narrow window keeps it, so the
                        // picture shows the page rather than the menu.
                        if (window.innerWidth < 600) localStorage.setItem('cockpit.railCollapsed', 'true');
                    }""",
                    token,
                )

                # 1. The Factories cards: a factory nothing is scheduled for is TALK ONLY, beside one that is PAUSED.
                page.goto(f"{BASE}/factories", wait_until="networkidle")
                wait(page, "[data-testid=fa-group-talk]", "the Talk only group")
                wait(page, "[data-testid=fa-group-paused]", "the Paused group")
                shot(page, "factories-talk-only-beside-paused", suffix)

                page.goto(f"{BASE}/factories/{TALK_ONLY}", wait_until="networkidle")
                wait(page, "[data-testid=fa-page-status-line]", "the talk-only factory's status line")
                shot(page, "factory-page-talk-only", suffix)

                # 2. The Seats tab: every row opens its seat, the boss with no schedule included.
                page.goto(f"{BASE}/factories/{FACTORY}/seats", wait_until="networkidle")
                wait(page, "[data-testid=fa-seat-open-none-boss]", "Open seat on the boss, who has no schedule")
                shot(page, "seats-tab-open-seat", suffix)

                # 3. A seat with a schedule, opened from its row.
                page.click("[data-testid^=fa-seat-open-][data-testid$=-scout]")
                wait(page, "[data-testid=fa-seat-schedule-" + scout["id"] + "]", "the Scout's schedule on its page")
                shot(page, "seat-with-a-schedule", suffix)

                # 4. A seat with two schedules, one of them paused.
                page.goto(f"{BASE}/factories/{FACTORY}/seats/writer", wait_until="networkidle")
                wait(page, "[data-testid=fa-seat-starts] .fa-seat-start + .fa-seat-start", "the Writer's two schedules")
                shot(page, "seat-with-two-schedules-one-paused", suffix)

                # 5. A seat with none, opened from its row: the case the mission exists for.
                page.goto(f"{BASE}/factories/{FACTORY}/seats", wait_until="networkidle")
                page.click("[data-testid=fa-seat-open-none-boss]")
                wait(page, "[data-testid=fa-seat-talk-only]", "the boss's talk-only sentence")
                shot(page, "seat-with-no-schedule", suffix)

                # 6. The older address of a seat lands on its seat page.
                page.goto(f"{BASE}/factories/{FACTORY}/agents/boss", wait_until="networkidle")
                wait(page, "[data-testid=factory-seat-page]", "the seat page behind the old agent address")
                landed = page.url.replace(BASE, "")
                if landed != f"/factories/{FACTORY}/seats/boss":
                    raise SystemExit(f"the old agent address landed on {landed}, not on the seat page")
                ANSWERS[f"old-agent-address-{suffix}"] = {"opened": f"/factories/{FACTORY}/agents/boss", "landed": landed}

                # 7. A seat the factory does not have: the Gateway's sentence, never a blank page.
                page.goto(f"{BASE}/factories/{FACTORY}/seats/owner-session", wait_until="networkidle")
                wait(page, "[role=alert]", "the refusal for a seat the factory does not have")
                shot(page, "seat-the-factory-does-not-have", suffix)

                # 8. A seat whose schedule the Gateway does not have. NOT the Gateway's own data - see the top of this file.
                status, real = request("GET", f"/gateway/factories/{FACTORY}/seats/scout", token)
                missing = dict(real, startsWord="Schedule missing", startsTone="amber")
                missing["starts"] = dict(real["starts"], talkOnlyText=None, schedules=[{
                    "jobId": "cj_gone",
                    "name": None,
                    "whenText": "Schedule cj_gone is missing",
                    "shiftText": "No run in the next 7 days",
                    "pausedText": None,
                    "missingText": "This seat names a schedule the Gateway does not have, so nothing runs from it.",
                    "editLabel": None,
                    "paused": False,
                    "toggleLabel": None,
                    "unlink": None,
                }])
                pattern = f"**/gateway/factories/{FACTORY}/seats/scout"
                page.route(pattern, lambda route: route.fulfill(status=200, content_type="application/json", body=json.dumps(missing)))
                page.goto(f"{BASE}/factories/{FACTORY}/seats/scout", wait_until="networkidle")
                wait(page, "[data-testid=fa-seat-schedule-cj_gone]", "the missing schedule's row")
                shot(page, "seat-schedule-missing-NOT-GATEWAY-DATA", suffix)
                page.unroute(pattern)

                # 9. Take off this seat. Before: the confirm, in the Gateway's sentences.
                page.goto(f"{BASE}/factories/{FACTORY}/seats/scout", wait_until="networkidle")
                wait(page, f"[data-testid=fa-seat-unlink-{scout['id']}]", "Take off this seat")
                page.click(f"[data-testid=fa-seat-unlink-{scout['id']}]")
                wait(page, "[role=alertdialog]", "the confirm")
                shot(page, "take-off-1-confirm", suffix)

                # 10. A failure case the owner can really meet: the schedule was taken off somewhere else while the
                #     confirm was open. The Gateway refuses the second one, and its sentence stays in the confirm.
                status, answer = request("POST", f"/cron/jobs/{scout['id']}/unlink", token)
                keep(f"unlink-by-the-account-{suffix}", "POST", f"/cron/jobs/{scout['id']}/unlink", status, answer)
                if status != 200:
                    raise SystemExit(f"taking the schedule off through the route failed: HTTP {status} {answer}")
                page.click("[role=alertdialog] button:has-text('Take it off')")
                wait(page, ".ui-confirm-error", "the refusal in the confirm")
                shot(page, "take-off-refused-already-off", suffix)
                status, answer = request("POST", f"/cron/jobs/{scout['id']}/unlink", token)
                keep(f"unlink-again-refused-{suffix}", "POST", f"/cron/jobs/{scout['id']}/unlink", status, answer)
                page.click("[role=alertdialog] button:has-text('Cancel')")

                # 11. Put it back, then take it off from the page. After: what the Gateway said, and the seat is talk only.
                link(token, scout)
                page.goto(f"{BASE}/factories/{FACTORY}/seats/scout", wait_until="networkidle")
                wait(page, f"[data-testid=fa-seat-unlink-{scout['id']}]", "Take off this seat, after it was put back")
                page.click(f"[data-testid=fa-seat-unlink-{scout['id']}]")
                wait(page, "[role=alertdialog]", "the confirm")
                page.click("[role=alertdialog] button:has-text('Take it off')")
                wait(page, "[data-testid=fa-notice]", "what the Gateway said it did")
                wait(page, "[data-testid=fa-seat-talk-only]", "the seat reading talk only afterwards")
                shot(page, "take-off-2-after", suffix)
                status, after = request("GET", f"/cron/jobs/{scout['id']}", token)
                keep(f"schedule-after-take-off-{suffix}", "GET", f"/cron/jobs/{scout['id']}", status,
                     {k: after.get(k) for k in ("id", "name", "enabled", "factory", "seat", "cronExpression")})

                # 12. The schedule that kept running is not hidden: it is listed outside any factory.
                page.goto(f"{BASE}/factories", wait_until="networkidle")
                wait(page, f"[data-testid=fa-outside-{scout['id']}]", "the schedule under Schedules outside any factory")
                page.locator("[data-testid=fa-outside]").screenshot(path=str(OUT / f"take-off-3-listed-outside-any-factory-{suffix}.png"))
                log(f"take-off-3-listed-outside-any-factory-{suffix}.png")

                link(token, scout)
                context.close()
        finally:
            browser.close()


def command_line(token, scout, plain):
    """The same act from the command line, against the same Gateway: the answer on success and on a refusal."""
    # The tool is pointed at the scratch Gateway and given its token, so it can reach nothing else.
    env = dict(os.environ, CC_GATEWAY_URL=BASE, CC_GATEWAY_SESSION_KEY=token, NO_COLOR="1")
    runs = []
    for label, args, expected in (
        ("take a schedule off its seat", ["schedule", "unlink", scout["id"]], 0),
        ("take it off a second time", ["schedule", "unlink", scout["id"]], 1),
        ("a schedule that is on no seat", ["schedule", "unlink", plain["id"]], 1),
        ("a schedule that does not exist", ["schedule", "unlink", "cj_nowhere"], 1),
    ):
        done = subprocess.run([sys.executable, "-m", "cc_devthrottle.cli", *args], env=env, capture_output=True, text=True, timeout=120)
        # A run that ended any other way is not evidence of anything - a tool that could not even start exits 1 too.
        if done.returncode != expected or "Traceback" in done.stderr:
            raise SystemExit(f"cc-devthrottle {' '.join(args)} exited {done.returncode}, expected {expected}:\n{done.stdout}\n{done.stderr}")
        runs.append({
            "what": label,
            "command": "cc-devthrottle " + " ".join(args),
            "exit": done.returncode,
            "stdout": done.stdout.strip(),
            "stderr": done.stderr.strip(),
        })
        log(f"cc-devthrottle {' '.join(args)} -> exit {done.returncode}")
    ANSWERS["command-line"] = runs
    link(token, scout)


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

    root = tempfile.mkdtemp(prefix="seats-rig-")
    # The scratch Gateway's own config.json: the factory area switched on (it is off by default) and the account's
    # display time zone pinned, so the shifts read the same on any machine.
    (Path(root) / "config").mkdir(parents=True, exist_ok=True)
    (Path(root) / "config" / "config.json").write_text(
        json.dumps({"factoryAgents": {"enabled": True}, "time_zone": "America/Toronto"}), encoding="utf-8")
    # The file watcher polls: a machine running many sessions has no file-watch slots left, and the Gateway would
    # stop at start-up reading its own settings file.
    env = dict(os.environ, CC_DIRECTOR_ROOT=root, CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1")
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    proc = subprocess.Popen(["dotnet", str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env)
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        log("Gateway answered; token read from its own data folder")
        wait_until_ready(token)
        scout, _, _, plain = seed(token)

        # The answers the report quotes that need no browser.
        for name, method, path in (
            ("seat-page-with-a-schedule", "GET", f"/gateway/factories/{FACTORY}/seats/scout"),
            ("seat-page-with-no-schedule", "GET", f"/gateway/factories/{FACTORY}/seats/boss"),
            ("seat-the-factory-does-not-have", "GET", f"/gateway/factories/{FACTORY}/seats/owner-session"),
            ("seat-of-a-factory-that-is-not-registered", "GET", "/gateway/factories/nowhere/seats/boss"),
            ("unlink-a-schedule-on-no-seat", "POST", f"/cron/jobs/{plain['id']}/unlink"),
            ("unlink-a-schedule-that-does-not-exist", "POST", "/cron/jobs/cj_nowhere/unlink"),
        ):
            status, answer = request(method, path, token)
            keep(name, method, path, status, answer)
        status, listing = request("GET", "/gateway/factories", token)
        keep("factories-list-status-words", "GET", "/gateway/factories", status,
             [{k: r.get(k) for k in ("id", "statusWord", "statusRank", "statusLine")} for r in listing["rows"]])

        capture(token, scout)
        command_line(token, scout, plain)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log("Gateway stopped")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
