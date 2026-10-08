"""Take the rename, delete and leave screenshots for docs/proof/team-manage-2026-10-08 (Teams v1, rename, delete and
leave a team).

One foreground command, run from the repository root:

    python docs/proof/team-manage-2026-10-08/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries and starts the Team page proof rig (TeamPageProofRig: a
hosted Gateway on 127.0.0.1:7912 with Teams released, a billed team "Acme QA" with one fleet test account in each role,
an invitation mailer that records and never sends, no seat-sync credential, and a scratch database deleted afterwards).
Then, in a headless browser:

  1. the Owner's Members tab: "The team" card, Delete disabled with the Gateway's sentence while others remain;
  2. the Developer's Members tab: "Leave this team", its confirmation, and leaving for real - they land on their own
     account's Settings;
  3. the Owner renames the team, removes everyone else, and opens the delete confirmation with the name typed.

Last, it checks the server: the Owner is the only member left, and the team carries its new name.

Playwright is used as a library, deliberately: the screenshots must be repeatable by a reviewer, and the rig's accounts
are seeded into a fresh browser context - never the owner's browser profile or Director.
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-team-manage-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"
NEW_NAME = "Acme QA Labs"


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


def fail(page, message):
    debug = Path(tempfile.gettempdir()) / "cc-team-manage-failed.png"
    page.screenshot(path=str(debug), full_page=True)
    body = page.evaluate("document.body.innerText")
    sys.exit(f"ERROR: {message} at {page.url}. Screenshot: {debug}\nPage text:\n{body[:2000]}")


def wait_text(page, text):
    try:
        page.get_by_text(text).first.wait_for(timeout=15000)
    except Exception:
        fail(page, f"'{text}' never appeared")


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
    env["CC_TEAMS_2303_PROOF_RIG"] = str(RIG_DIR)
    # The rig must never reach the website: without the service credential the seat sync fails closed.
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamPageProofRig",
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
        print(f"[rig] ready at {base}, team {r['teamId']}", flush=True)

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def context_for(who):
                ctx = browser.new_context(viewport={"width": 1280, "height": 900})
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            # The old Team page address leads to the Members tab of Settings with the team on screen.
            members = f"{base}/team/{r['teamId']}/members"

            # 1. The Owner, with others in the team.
            owner = context_for(r["owner"])
            page = owner.new_page()
            page.goto(members)
            wait_text(page, "The team")
            wait_text(page, "other people are still in the team")
            card = page.get_by_role("region", name="The team")
            if not card.get_by_role("button", name="Delete this team").is_disabled():
                fail(page, "Delete is not disabled while others remain")
            card.scroll_into_view_if_needed()
            shot(page, "1-owner-the-team-card.png")

            # 2. The Developer: Leave, its confirmation, and leaving.
            dev = context_for(r["developer"])
            dpage = dev.new_page()
            dpage.goto(members)
            wait_text(dpage, "Leave this team")
            dcard = dpage.get_by_role("region", name="The team")
            if dcard.get_by_role("button", name="Rename").count() or dcard.get_by_role("button", name="Delete this team").count():
                fail(dpage, "a Developer is offered Rename or Delete")
            dcard.scroll_into_view_if_needed()
            shot(dpage, "2-developer-members-tab-with-leave.png")
            dcard.get_by_role("button", name="Leave this team").click()
            dialog = dpage.get_by_role("alertdialog")
            dialog.wait_for()
            wait_text(dpage, "Your paid seat comes off the team's bill")
            shot(dpage, "3-developer-leave-confirmation.png")
            dialog.get_by_role("button", name="Leave this team").click()
            try:
                dpage.wait_for_url("**/settings?tab=account", timeout=15000)
            except Exception:
                fail(dpage, "leaving did not land on the own account's Settings")
            dev.close()

            # 3. The Owner renames the team, removes everyone else, then opens the delete confirmation.
            page.reload()
            wait_text(page, "The team")
            card = page.get_by_role("region", name="The team")
            card.get_by_role("button", name="Rename").click()
            page.get_by_label("Team name").fill(NEW_NAME)
            page.get_by_role("button", name="Save name").click()
            wait_text(page, f"The team is now called {NEW_NAME}.")
            # The menu's team block shows the new name at once.
            try:
                page.get_by_test_id("nav-team-heading").get_by_text(NEW_NAME, exact=True).wait_for(timeout=15000)
            except Exception:
                fail(page, "the menu's team block did not show the new name")

            for email in ["tech@mindzie.com", "docs@mindzie.com", "james@client.example"]:
                row = page.locator("tr", has_text=email)
                row.get_by_role("button", name="Remove").click()
                confirm = page.get_by_role("alertdialog")
                confirm.wait_for()
                confirm.get_by_role("button", name="Remove").click()
                wait_text(page, "has been removed from the team")
                confirm.wait_for(state="detached")

            card = page.get_by_role("region", name="The team")
            delete = card.get_by_role("button", name="Delete this team")
            try:
                page.wait_for_function("() => !document.querySelector('[data-testid=team-delete-blocked]')", timeout=15000)
            except Exception:
                fail(page, "Delete stayed blocked once the Owner was alone")
            delete.click()
            dialog = page.get_by_role("alertdialog")
            dialog.wait_for()
            confirm_button = dialog.get_by_role("button", name="Delete this team")
            if not confirm_button.is_disabled():
                fail(page, "the delete confirmation can be confirmed before the name is typed")
            dialog.get_by_label("To confirm, type the team's name").fill(NEW_NAME)
            if confirm_button.is_disabled():
                fail(page, "the delete confirmation stays disabled with the name typed")
            shot(page, "4-owner-delete-confirmation.png")
            dialog.get_by_role("button", name="Keep the team").click()
            owner.close()

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
        after_file = RIG_DIR / "after.json"
        after = json.loads(after_file.read_text()) if after_file.exists() else None
        shutil.rmtree(RIG_DIR, ignore_errors=True)
        # The Cockpit was copied beside the test binaries only for this run; no suite run may find it there.
        shutil.rmtree(web_root, ignore_errors=True)
    print(f"[done] rig exit code {rig.returncode}", flush=True)
    print(f"[server] members after the run: {after}", flush=True)
    if after is None or [m["Email"] for m in after] != ["qa@mindzie.com"]:
        sys.exit(f"ERROR: the server does not hold the Owner alone after the Developer left and the rest were removed: {after}")
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
