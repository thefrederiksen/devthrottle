"""Take the Team page screenshots for docs/proof/teams-2303 (devthrottle_internal#2303).

One foreground command, run from the repository root:

    python docs/proof/teams-2303/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig
(TeamPageProofRig: a hosted Gateway on 127.0.0.1:7912 with Teams released, a billed team with
one fleet test account in each role and a waiting invitation, an invitation mailer that records
and never sends, and no seat-sync credential so the website is never called), drives a headless
browser through screen S1 as each role, writes a stop file, waits for the rig to stop, and checks
the role change the Owner made is what the server holds.

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
from urllib.parse import parse_qs, urlparse

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2303-rig"
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
    try:
        page.get_by_text(text).first.wait_for(timeout=15000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-teams-2303-failed.png"
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
                # What a finished sign-in leaves in a browser: the account in the Cockpit's account store, and the
                # device key mirrored into the cookie a browser navigation carries past the Gateway's sign-in gate.
                ctx = browser.new_context(viewport={"width": 1280, "height": 860})
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            team_page = f"{base}/team/{r['teamId']}/members"

            # S1 as a Manager: the same list, roles as plain text, Remove only on Developers and Collaborators.
            ctx = context_for(r["manager"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme QA")
            wait_text(page, "new.hire@example.org")
            shot(page, "s1-manager.png")
            ctx.close()

            # S1 as a Developer: the list, nothing to click.
            ctx = context_for(r["developer"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme QA")
            shot(page, "s1-developer.png")
            ctx.close()

            # A Collaborator has no Team page: the Gateway's sentence.
            ctx = context_for(r["collaborator"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "may not open the Team page")
            shot(page, "s1-collaborator-no-team-page.png")
            ctx.close()

            # S1 as the Owner: everything; then a role change and the remove confirmation.
            ctx = context_for(r["owner"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme QA")
            wait_text(page, "new.hire@example.org")
            shot(page, "s1-owner.png")
            page.get_by_label("Role of dev@mindzie.com").select_option("Collaborator")
            wait_text(page, "dev@mindzie.com is now a Collaborator.")
            wait_text(page, "2 paid seats, 3 Collaborators (free), 1 invitation waiting")
            shot(page, "s1-owner-after-role-change.png")
            row = page.locator("tr", has_text="james@client.example")
            row.get_by_role("button", name="Remove").click()
            page.get_by_role("alertdialog").wait_for()
            shot(page, "s1-owner-remove-confirmation.png")
            page.get_by_role("button", name="Keep them").click()
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
        after_file = RIG_DIR / "after.json"
        after = json.loads(after_file.read_text()) if after_file.exists() else None
        shutil.rmtree(RIG_DIR, ignore_errors=True)
        # The Cockpit was copied beside the test binaries only for this run; no suite run may find it there.
        shutil.rmtree(web_root, ignore_errors=True)
    print(f"[done] rig exit code {rig.returncode}", flush=True)
    print(f"[server] members after the run: {after}", flush=True)
    dev = [m for m in (after or []) if m["Email"] == "dev@mindzie.com"]
    if not dev or dev[0]["Role"] != "Collaborator":
        sys.exit(f"ERROR: the server does not hold the role change the page made: {after}")
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
