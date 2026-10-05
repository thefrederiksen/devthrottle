"""Take the team Reports screenshots for docs/proof/teams-2309 (devthrottle_internal#2309).

One foreground command, run from the repository root:

    python docs/proof/teams-2309/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway unit-test binaries, starts the proof rig (TeamReportsProofRig: a
hosted Gateway on 127.0.0.1:7913 with Teams released, and one team "DevThrottle" in which tech@mindzie.com, a Developer,
wrote "Signup page rewrite", sent it to docs@mindzie.com, a Collaborator, who commented on it), drives a headless browser
at desktop width and at 390 px phone width as each of the two people, writes a stop file and waits for the rig to stop.

Playwright is used as a library here, deliberately (the same choice as docs/proof/teams-2306): the screenshots must be
repeatable by a reviewer, and the rig's accounts are seeded into fresh browser contexts rather than into a signed-in
persona browser.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2309-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"
COMMENT = "Looks good. Can we keep the old page for a week for anyone who has it bookmarked?"


def run(cmd, cwd):
    print(f"[run] {' '.join(cmd)}", flush=True)
    subprocess.run(cmd, cwd=cwd, check=True, shell=(os.name == "nt"))


def account_script(email, key):
    accounts = [{"id": "rig-1", "label": email, "email": email, "deviceKey": key, "installId": "rig-install-1"}]
    return (
        "try {"
        f"localStorage.setItem('cc.accounts', {json.dumps(json.dumps(accounts))});"
        "localStorage.setItem('cc.activeAccount', 'rig-1');"
        "} catch (e) {}"
    )


def wait_for(page, locator, what):
    try:
        locator.first.wait_for(timeout=20000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-teams-2309-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: {what} never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


def report_bytes_shown(page):
    # The report renders in the shared viewer's sandboxed frame; wait until its own heading is inside it.
    deadline = time.time() + 45
    seen = []
    while time.time() < deadline:
        seen = []
        for frame in page.frames:
            if frame == page.main_frame:
                continue
            try:
                text = frame.evaluate("document.body ? document.body.innerText : ''")
            except Exception as e:  # a frame being replaced mid-read; read again on the next pass
                seen.append(f"{frame.url[:60]}: {type(e).__name__}")
                continue
            seen.append(f"{frame.url[:60]}: {text[:60]!r}")
            if "Signup page rewrite" in text:
                answers_are_off(frame)
                return
        time.sleep(0.25)
    sys.exit(f"ERROR: the report's own bytes never rendered in the viewer's frame at {page.url}. Frames: {seen}")


def answers_are_off(frame):
    # The Gateway says notes AND answers are off for a team reader (round-3 review R1). Proven on the report's own
    # controls, not inferred from the absence of buttons: the report carries a question as radio buttons; every control
    # in it must be disabled, and clicking one must leave it unchosen.
    controls = frame.evaluate("document.querySelectorAll('input, select, textarea, button').length")
    if controls == 0:
        sys.exit("ERROR: the report frame holds no form controls, so this proof would pass without looking at any")
    live = frame.evaluate("Array.from(document.querySelectorAll('input, select, textarea, button')).filter(e => !e.disabled).length")
    if live != 0:
        sys.exit(f"ERROR: {live} of {controls} control(s) in the report frame are live; with answers off every one must be disabled")
    radio = frame.locator("input[type=radio]").first
    radio.click(force=True)
    if frame.evaluate("Array.from(document.querySelectorAll('input[type=radio]')).some(e => e.checked)"):
        sys.exit("ERROR: clicking a radio button in the report frame chose it; with answers off it must stay unchosen")
    if frame.locator("button").count() != 0:
        sys.exit("ERROR: the report frame holds a button; with notes off nothing adds a Queue button or a notes tray")
    print(f"[proof] answers off: {controls} control(s) in the report, all disabled; a clicked radio stayed unchosen", flush=True)


def shot(page, name):
    page.mouse.move(page.viewport_size["width"] - 5, page.viewport_size["height"] - 5)
    path = OUT / name
    page.screenshot(path=str(path), full_page=False)
    print(f"[shot] {path.relative_to(REPO)}  at={page.url}", flush=True)


def main():
    run(["npx", "vite", "build"], REPO / "apps" / "cockpit")
    run(["dotnet", "build", str(TEST_PROJECT), "-v", "q", "-nologo"], REPO)
    web_root = TEST_BIN / "wwwroot" / "c"
    if web_root.exists():
        shutil.rmtree(web_root)
    shutil.copytree(REPO / "apps" / "cockpit" / "dist", web_root)

    if RIG_DIR.exists():
        shutil.rmtree(RIG_DIR)
    RIG_DIR.mkdir()
    env = dict(os.environ)
    env["CC_TEAMS_2309_PROOF_RIG"] = str(RIG_DIR)
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamReportsProofRig",
         "--logger", "console;verbosity=normal"],
        cwd=REPO, env=env)

    try:
        rig_file = RIG_DIR / "rig.json"
        deadline = time.time() + 300
        while not rig_file.exists():
            if rig.poll() is not None:
                sys.exit(f"ERROR: the rig exited ({rig.returncode}) before it was ready")
            if time.time() > deadline:
                sys.exit("ERROR: the rig did not become ready within 5 minutes")
            time.sleep(0.5)
        r = json.loads(rig_file.read_text())
        base = r["baseUrl"]
        print(f"[rig] ready at {base}", flush=True)

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def context(person, width, height):
                ctx = browser.new_context(viewport={"width": width, "height": height})
                ctx.add_init_script(account_script(person["email"], person["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": person["key"], "url": base}])
                return ctx

            for prefix, (width, height) in (("desktop", (1280, 800)), ("phone-390", (390, 844))):
                # The Collaborator: the list of what was sent to them (S10), then the report open with their comment.
                ctx = context(r["collaborator"], width, height)
                page = ctx.new_page()
                page.goto(f"{base}/reports")
                wait_for(page, page.get_by_test_id("team-report-row"), "the report sent to the Collaborator")
                shot(page, f"{prefix}-1-collaborator-reports-list.png")
                page.get_by_test_id("team-report-row").first.click()
                wait_for(page, page.get_by_test_id("team-report-comment").filter(has_text=COMMENT), "the Collaborator's comment")
                report_bytes_shown(page)
                shot(page, f"{prefix}-2-collaborator-report-open-with-comment.png")
                if prefix == "phone-390":
                    page.get_by_test_id("team-report-comments-note").first.evaluate("e => e.scrollIntoView({block: 'start'})")
                    shot(page, f"{prefix}-2b-collaborator-comment-panel.png")
                ctx.close()

                # The author: their own reports in the team, then the one that was sent, with the comment it received.
                ctx = context(r["author"], width, height)
                page = ctx.new_page()
                page.goto(f"{base}/reports")
                wait_for(page, page.get_by_test_id("team-report-own-row"), "the author's own reports")
                if prefix == "phone-390":
                    # The author is in the WHOLE Cockpit, whose rail does not fold itself away at phone width; a phone
                    # reader folds it with its own button, so the shot is taken that way.
                    page.get_by_test_id("rail-toggle").click()
                shot(page, f"{prefix}-3-author-reports-list.png")
                page.get_by_test_id("team-report-own-row").filter(has_text="Signup page rewrite").click()
                wait_for(page, page.get_by_test_id("team-report-comment-from-person").filter(has_text=COMMENT), "the comment the author received")
                report_bytes_shown(page)
                shot(page, f"{prefix}-4-author-report-with-comment-received.png")
                if prefix == "phone-390":
                    page.get_by_test_id("team-report-comment-from-person").first.evaluate("e => e.scrollIntoView({block: 'start'})")
                    shot(page, f"{prefix}-4b-author-comment-panel.png")
                ctx.close()

            browser.close()
    finally:
        (RIG_DIR / "stop").write_text("stop")
        try:
            rig.wait(timeout=120)
        except subprocess.TimeoutExpired:
            sys.exit("ERROR: the rig did not stop within 2 minutes of the stop file")


if __name__ == "__main__":
    main()
