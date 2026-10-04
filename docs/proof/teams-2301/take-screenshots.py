"""Take the invitation screenshots for docs/proof/teams-2301 (devthrottle_internal#2301).

One foreground command, run from the repository root:

    python docs/proof/teams-2301/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig
(TeamInvitationProofRig: a hosted Gateway on 127.0.0.1:7911 with Teams released, a billed team
and the fleet test accounts, an invitation mailer that records and never sends), drives a
headless browser through screens S2 and S3, writes a stop file and waits for the rig to stop.

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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2301-rig"
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
        debug = Path(tempfile.gettempdir()) / "cc-teams-2301-failed.png"
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
    env["CC_TEAMS_2301_PROOF_RIG"] = str(RIG_DIR)
    # The rig must never reach the website: without the service credential the seat sync fails closed.
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamInvitationProofRig",
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

            def sign_in(ctx, who):
                # What a finished sign-in leaves in a browser: the account in the Cockpit's account store, and the
                # device key mirrored into the cookie a browser navigation carries past the Gateway's sign-in gate.
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])

            def context_for(who):
                ctx = browser.new_context(viewport={"width": 1280, "height": 860})
                if who is not None:
                    sign_in(ctx, who)
                return ctx

            # S2 - the Owner invites someone, and sees what is waiting.
            ctx = context_for(r["owner"])
            page = ctx.new_page()
            page.goto(f"{base}/team/{r['teamId']}/invite")
            wait_text(page, "Waiting invitations")
            wait_text(page, "contractor@example.org")
            shot(page, "s2-invite-owner.png")
            page.get_by_placeholder("name@any-company.com").fill("new.hire@example.org")
            page.get_by_role("button", name="Send invitation").click()
            page.get_by_text("new.hire@example.org").last.wait_for()
            page.wait_for_timeout(500)
            shot(page, "s2-invite-owner-after-sending.png")
            ctx.close()

            # S2 - a Manager is offered only the roles a Manager may invite.
            ctx = context_for(r["manager"])
            page = ctx.new_page()
            page.goto(f"{base}/team/{r['teamId']}/invite")
            wait_text(page, "Waiting invitations")
            shot(page, "s2-invite-manager.png")
            ctx.close()

            # S3, signed in: the invitee opens the link, sees which account joins, and joins.
            ctx = context_for(r["invitee"])
            page = ctx.new_page()
            page.goto(f"{base}/invite/{r['invitee']['token']}")
            page.get_by_role("button", name="Join the team").wait_for()
            shot(page, "s3-signed-in-1-open.png")
            page.get_by_role("button", name="Join the team").click()
            wait_text(page, "You joined the Acme QA team")
            shot(page, "s3-signed-in-2-joined.png")
            # A cancelled invitation's link says so, in plain words.
            page.goto(f"{base}/invite/{r['cancelledToken']}")
            wait_text(page, "This invitation cannot be used")
            shot(page, "s3-cancelled-link.png")
            ctx.close()

            # S3, signed out: the link sends a person with no account to sign in or sign up, and back.
            ctx = context_for(None)
            page = ctx.new_page()
            page.goto(f"{base}/invite/{r['newcomer']['token']}")
            page.wait_for_url("**/signin**")
            page.wait_for_timeout(800)
            next_path = parse_qs(urlparse(page.url).query)["next"][0]
            print(f"[s3] signed out -> {page.url} (next={next_path})", flush=True)
            if next_path != f"/invite/{r['newcomer']['token']}":
                sys.exit(f"ERROR: the sign-in page does not carry the invitation back: next={next_path}")
            shot(page, "s3-signed-out-1-sign-in.png")
            # The sign-up itself happens at devthrottle.com and is not driven here (no account is created on the
            # real identity provider). Its end state is a device key for the new account in this browser, which
            # is what is seeded now; the Cockpit then returns to the `next` address the sign-in page carried.
            sign_in(ctx, r["newcomer"])
            page.goto(f"{base}{next_path}")
            page.get_by_role("button", name="Join the team").wait_for()
            shot(page, "s3-signed-out-2-back-on-the-invitation.png")
            page.get_by_role("button", name="Join the team").click()
            wait_text(page, "You joined the Acme QA team")
            shot(page, "s3-signed-out-3-joined.png")
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
    print(f"[done] rig exit code {rig.returncode}", flush=True)
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
