"""Take the Collaborator's-app screenshots for docs/proof/teams-2306 (devthrottle_internal#2306).

One foreground command, run from the repository root:

    python docs/proof/teams-2306/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway unit-test binaries, starts the proof rig
(TeamCollaboratorAppProofRig: a hosted Gateway on 127.0.0.1:7912 with Teams released, and one account that is a
Collaborator in "DevThrottle" and a Developer in "Paul's project"), drives a headless browser at desktop width and at
390 px phone width, writes a stop file and waits for the rig to stop.

Playwright is used as a library here, deliberately (the same choice as docs/proof/teams-2301): the screenshots must be
repeatable by a reviewer, and the rig's account is seeded into a fresh browser context rather than into a signed-in
persona browser. Each context is a fresh browser, so it opens on the Gateway's start verdict: here, the chooser.
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2306-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"
NOT_AVAILABLE = "This page is not available to Collaborators."


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


def wait_text(page, text):
    try:
        page.get_by_text(text).first.wait_for(timeout=15000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-teams-2306-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: '{text}' never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


def rail_labels(page):
    return page.eval_on_selector_all(".nav-list:not(.nav-list-foot) .nav-link-label", "els => els.map(e => e.textContent)")


def shot(page, name):
    # The pointer is parked off the rail, so a row is lit only because it is the page on screen, never by hover.
    page.mouse.move(page.viewport_size["width"] - 5, page.viewport_size["height"] - 5)
    path = OUT / name
    page.screenshot(path=str(path), full_page=False)
    print(f"[shot] {path.relative_to(REPO)}  rail={rail_labels(page)}  at={page.url}", flush=True)


def choose_team(page, label):
    page.locator(".team-switcher-select").select_option(label=label)


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
    env["CC_TEAMS_2306_PROOF_RIG"] = str(RIG_DIR)
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamCollaboratorAppProofRig",
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
        who = r["person"]
        print(f"[rig] ready at {base}", flush=True)

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def context(width, height):
                ctx = browser.new_context(viewport={"width": width, "height": height})
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            for prefix, (width, height) in (("desktop", (1280, 800)), ("phone-390", (390, 844))):
                ctx = context(width, height)
                page = ctx.new_page()

                # A fresh browser: this account has two teams and no Director on its own account, so the Gateway says
                # "choose" and the chooser (S11) is what opens at the bare address.
                page.goto(f"{base}/")
                wait_text(page, "Choose a team")
                shot(page, f"{prefix}-0-fresh-browser-chooser.png")
                page.get_by_role("button", name="Open DevThrottle").click()
                wait_text(page, "No questions waiting on you.")
                page.locator(".team-switcher-select").wait_for(timeout=15000)

                # The team where they are a Collaborator: Questions, three pages and nothing else, and at the foot who is
                # signed in, their role and Sign out.
                labels = rail_labels(page)
                if labels != ["Questions", "Requests", "Reports"]:
                    sys.exit(f"ERROR: the Collaborator's rail is {labels}")
                shot(page, f"{prefix}-1-questions.png")

                page.get_by_role("link", name="Requests").click()
                wait_text(page, "No requests from you yet.")
                shot(page, f"{prefix}-2-requests.png")

                page.get_by_role("link", name="Reports").click()
                wait_text(page, "No reports sent to you yet.")
                shot(page, f"{prefix}-3-reports.png")

                # Any other address, typed: the one plain page.
                page.goto(f"{base}/sessions")
                wait_text(page, NOT_AVAILABLE)
                shot(page, f"{prefix}-4-typed-sessions-not-available.png")
                page.goto(f"{base}/skills")
                wait_text(page, NOT_AVAILABLE)
                if prefix == "desktop":
                    shot(page, f"{prefix}-5-typed-skills-not-available.png")

                # The same account, switched to the team where it is a Developer: the whole app.
                choose_team(page, "Paul's project - Developer")
                page.locator(".nav-list-foot").wait_for(timeout=15000)
                labels = rail_labels(page)
                if labels[:3] != ["Fleet Manager", "Sessions", "Fleet Map"]:
                    sys.exit(f"ERROR: the Developer's rail is {labels}")
                shot(page, f"{prefix}-6-developer-team-full-app.png")
                if prefix == "desktop":
                    choose_team(page, "Your own account")
                    page.locator(".nav-list-foot").wait_for(timeout=15000)
                    shot(page, f"{prefix}-7-own-account-full-app.png")
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
