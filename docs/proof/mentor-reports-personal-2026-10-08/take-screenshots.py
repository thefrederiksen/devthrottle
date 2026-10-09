"""Take the screenshots for docs/proof/mentor-reports-personal-2026-10-08 (owner, 8 Oct 2026: Mentor and Reports in Work,
for everyone).

One foreground command, run from the repository root:

    python docs/proof/mentor-reports-personal-2026-10-08/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway unit-test binaries, starts the proof rig (PersonalMentorReportsProofRig:
a hosted Gateway on 127.0.0.1:7913 with Teams released, one fleet test account with a Mentor page on its own account,
two dev reports its own sessions sent it, and a team it owns), drives a headless browser through the screens, writes a
stop file and waits for the rig to stop.

Playwright is used as a library here, deliberately: the screenshots must be repeatable by a reviewer, and the rig's
account is seeded into a fresh browser context rather than into a signed-in persona browser.
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-mentor-personal-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"
CHOICE_KEY = "devthrottle.currentTeam.rig-1"


def run(cmd, cwd):
    print(f"[run] {' '.join(cmd)}", flush=True)
    subprocess.run(cmd, cwd=cwd, check=True, shell=(os.name == "nt"))


def account_script(email, key, choice):
    accounts = [{"id": "rig-1", "label": email, "email": email, "deviceKey": key, "installId": "rig-install-1"}]
    return (
        "try {"
        f"localStorage.setItem('cc.accounts', {json.dumps(json.dumps(accounts))});"
        "localStorage.setItem('cc.activeAccount', 'rig-1');"
        f"if (!localStorage.getItem('{CHOICE_KEY}')) localStorage.setItem('{CHOICE_KEY}', {json.dumps(choice)});"
        "} catch (e) {}"
    )


def wait_text(page, text):
    try:
        page.get_by_text(text).first.wait_for(timeout=20000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-mentor-personal-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: '{text}' never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


def shot(page, name):
    path = OUT / name
    page.screenshot(path=str(path), full_page=True)
    print(f"[shot] {path.relative_to(REPO)}", flush=True)


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
    env["CC_MENTOR_PERSONAL_PROOF_RIG"] = str(RIG_DIR)
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~PersonalMentorReportsProofRig",
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
        print(f"[rig] ready at {base}, team {r['teamId']}", flush=True)

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def context_for(choice):
                ctx = browser.new_context(viewport={"width": 1280, "height": 860})
                ctx.add_init_script(account_script(who["email"], who["key"], choice))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            # Screen 1: the own account - Mentor and Reports in Work, and the person's own Mentor page.
            ctx = context_for("own-account")
            page = ctx.new_page()
            page.goto(f"{base}/mentor")
            wait_text(page, "only you read this page")
            wait_text(page, "fix the menu, it's too long")
            shot(page, "1-own-account-mentor.png")

            # The own account's Reports: the reports the person's own sessions sent them.
            page.goto(f"{base}/reports")
            wait_text(page, "Invoice export is ready to try")
            wait_text(page, "Release 2.18 checklist passed")
            shot(page, "2-own-account-reports.png")
            ctx.close()

            # Screen 2: a team on screen - Mentor and Reports still in Work, the team block keeps Team, Questions, Requests.
            ctx = context_for(r["teamId"])
            page = ctx.new_page()
            page.goto(f"{base}/mentor")
            wait_text(page, "Mentor - week of")
            wait_text(page, "Questions")
            shot(page, "3-team-on-screen-menu.png")
            ctx.close()

            browser.close()
    finally:
        (RIG_DIR / "stop").write_text("stop")
        try:
            rig.wait(timeout=120)
        except subprocess.TimeoutExpired:
            # The rig did not stop. Stop this script's OWN test run and its test host, and nothing else.
            print("[rig] not stopped after 120s - stopping this script's own test run", flush=True)
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(rig.pid)], check=False)
            rig.wait(timeout=30)
        shutil.rmtree(RIG_DIR, ignore_errors=True)
        # The Cockpit was copied beside the test binaries only for this run; no suite run may find it there.
        shutil.rmtree(web_root, ignore_errors=True)
    print(f"[done] rig exit code {rig.returncode}", flush=True)
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
