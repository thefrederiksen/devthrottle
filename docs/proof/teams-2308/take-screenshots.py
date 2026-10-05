"""Take the Requests screenshots for docs/proof/teams-2308 (devthrottle_internal#2308, screen S9).

One foreground command, run from the repository root:

    python docs/proof/teams-2301/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig
(TeamRequestProofRig: a hosted Gateway on 127.0.0.1:7912 with Teams released, the team "Acme QA"
of the fleet's test accounts - Owner, Manager, Developer, Collaborator - and four requests in every
state), drives a headless browser through both sides of S9 at desktop width and at 390 pixels,
writes a stop file and waits for the rig to stop.

Playwright is used as a library here, deliberately: the screenshots must be repeatable by a
reviewer, and the rig's accounts are seeded into a fresh browser context rather than into a
signed-in persona browser.
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2308-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"


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
    # Waits on the page's own visible text, so a sentence split across elements or wrapped is still found.
    try:
        page.wait_for_function("t => document.body.innerText.includes(t)", arg=text, timeout=15000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-teams-2308-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: '{text}' never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


def phone_menu_collapsed(page, width):
    # The whole Cockpit keeps its menu open at any width until the person collapses it (a Collaborator's pages-only
    # app is a bar instead). At phone width the shots collapse it the way a person would, with its own button.
    if width < 600:
        page.get_by_role("button", name="Collapse the menu").click()
        page.get_by_role("button", name="Expand the menu").wait_for()


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
    env["CC_TEAMS_2308_PROOF_RIG"] = str(RIG_DIR)
    # The rig must never reach the website: without the service credential the seat sync fails closed.
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamRequestProofRig",
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
        team = r["teamId"]
        print(f"[rig] ready at {base}, team {team}", flush=True)

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def page_for(who, width):
                # What a finished sign-in leaves in a browser: the account in the Cockpit's account store, and the
                # device key mirrored into the cookie a browser navigation carries past the Gateway's sign-in gate.
                ctx = browser.new_context(viewport={"width": width, "height": 860 if width > 600 else 844})
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx, ctx.new_page()

            for width, tag in ((1280, "desktop"), (390, "phone-390")):
                # The Collaborator's page: the write box, and their requests newest first with what happened to each.
                ctx, page = page_for(r["collaborator"], width)
                page.goto(f"{base}/requests")
                wait_text(page, "Send the weekly summary on Monday mornings")
                wait_text(page, "Not this quarter: the viewer is being replaced in November")
                shot(page, f"{tag}-1-collaborator-requests.png")
                ctx.close()

                # The Owner's list: every request, with the buttons the Gateway's verdicts allow.
                ctx, page = page_for(r["owner"], width)
                page.goto(f"{base}/team/{team}/requests")
                wait_text(page, "docs@mindzie.com")
                page.get_by_role("button", name="Accept").first.wait_for()
                phone_menu_collapsed(page, width)
                shot(page, f"{tag}-2-owner-list.png")
                ctx.close()

                # A Developer at the same address reads the Gateway's refusal, and nothing else.
                ctx, page = page_for(r["developer"], width)
                page.goto(f"{base}/team/{team}/requests")
                wait_text(page, "a Developer may not read the team's Requests list")
                phone_menu_collapsed(page, width)
                shot(page, f"{tag}-3-developer-refused.png")
                ctx.close()

            # The Manager marks the waiting request Not doing this: the reason is asked for first, and the button
            # stays off until one is written. Then the Collaborator sees the change, with who and why.
            ctx, page = page_for(r["manager"], 1280)
            page.goto(f"{base}/team/{team}/requests")
            card = page.get_by_role("article", name="Send the weekly summary on Monday mornings instead of Friday afternoons")
            card.get_by_role("button", name="Not doing this").click()
            card.get_by_role("button", name="Mark Not doing this").wait_for()
            if card.get_by_role("button", name="Mark Not doing this").is_enabled():
                sys.exit("ERROR: Mark Not doing this was enabled with no reason written")
            shot(page, "desktop-4-manager-reason-required.png")
            card.get_by_label("Why is this not being done?").fill("The summary goes out on Friday so it covers the whole week.")
            card.get_by_role("button", name="Mark Not doing this").click()
            card.get_by_text("Not doing this - You").wait_for()
            shot(page, "desktop-5-manager-after-not-doing-this.png")
            ctx.close()

            for width, tag in ((1280, "desktop"), (390, "phone-390")):
                ctx, page = page_for(r["collaborator"], width)
                page.goto(f"{base}/requests")
                wait_text(page, "The summary goes out on Friday so it covers the whole week.")
                shot(page, f"{tag}-6-collaborator-sees-the-change.png")
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
