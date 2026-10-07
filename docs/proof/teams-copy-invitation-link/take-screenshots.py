"""Take the screenshots for docs/proof/teams-copy-invitation-link (Teams v1, the Owner can copy an invitation link).

One foreground command, run from the repository root:

    python docs/proof/teams-copy-invitation-link/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig (TeamInviteLinkProofRig: a hosted
Gateway on 127.0.0.1:7915 with Teams released and its public address set to that same address, a team with a running
plan and its Owner, a second example.org account with no team, and an invitation mailer that reports the email as not
sent), drives a headless browser as the Owner through Invite someone -> Send invitation, shoots the link panel, presses
Copy invitation link and reads the clipboard back, then opens THAT link as the second account and joins. It writes a stop
file, waits for the rig to stop, and checks the server now lists the newcomer as a Developer.

Playwright is used as a library here, deliberately (the same reason as docs/proof/teams-2303): the screenshots must be
repeatable by a reviewer, and the rig's accounts are seeded into fresh, throwaway browser contexts rather than into a
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-link-rig"
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
        debug = Path(tempfile.gettempdir()) / "cc-teams-link-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: '{text}' never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


def shot(page, name):
    path = OUT / name
    page.screenshot(path=str(path), full_page=True)
    print(f"[shot] {path.relative_to(REPO)}", flush=True)


NEWCOMER_EMAIL = "newcomer@example.org"


def r_newcomer_email():
    return NEWCOMER_EMAIL


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
    env["CC_TEAMS_LINK_PROOF_RIG"] = str(RIG_DIR)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamInviteLinkProofRig",
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
                ctx = browser.new_context(viewport={"width": 1280, "height": 1000})
                ctx.add_init_script(account_script(who["email"], who["key"]))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            invite_page = f"{base}/team/{r['teamId']}/invite"

            # The Owner sends an invitation; the email cannot be sent, and the link is shown once with Copy.
            ctx = context_for(r["owner"])
            ctx.grant_permissions(["clipboard-read", "clipboard-write"], origin=base)
            page = ctx.new_page()
            page.goto(invite_page)
            wait_text(page, "Invite someone to Acme")
            page.get_by_label("Email").fill(r["newcomer"]["email"])
            page.get_by_role("button", name="Send invitation").click()
            wait_text(page, "Copy invitation link")
            link = page.get_by_label("The invitation link").input_value()
            if not link.startswith(f"{base}/invite/"):
                sys.exit(f"ERROR: the link shown is not this Gateway's accept page: {link}")
            page.get_by_role("button", name="Copy invitation link").click()
            wait_text(page, "Copied. Paste it into a message to them.")
            copied = page.evaluate("navigator.clipboard.readText()")
            if copied != link:
                sys.exit("ERROR: the clipboard does not hold the link shown")
            shot(page, "owner-link-shown-and-copied.png")
            ctx.close()

            # The newcomer opens the copied link and joins the team.
            ctx = context_for(r["newcomer"])
            page = ctx.new_page()
            page.goto(copied)
            wait_text(page, "Join the team")
            shot(page, "newcomer-opens-the-copied-link.png")
            page.get_by_role("button", name="Join the team").click()
            page.wait_for_timeout(1500)
            shot(page, "newcomer-joined.png")
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
    if after is None or {"Email": r_newcomer_email(), "Role": "Developer"} not in after:
        sys.exit(f"ERROR: the newcomer did not join through the copied link: {after}")
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
