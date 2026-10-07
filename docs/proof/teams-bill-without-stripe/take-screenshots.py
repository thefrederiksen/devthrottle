"""Take the Billing section screenshots for docs/proof/teams-bill-without-stripe (Teams v1, the team bill without Stripe).

One foreground command, run from the repository root:

    python docs/proof/teams-bill-without-stripe/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig (TeamBillProofRig: a hosted
Gateway on 127.0.0.1:7914 with Teams released, a team with one example.org account in each role and NO bill yet, an
invitation mailer that records and never sends), drives a headless browser through the Team page's Billing section as
the Owner - start the plan through the checkout, then cancel it - and as a Manager and a Developer, writes a stop file,
waits for the rig to stop, and checks the server holds what the page did: an active bill with auto-renew off and one
history line that charged nothing.

Playwright is used as a library here, deliberately (the same reason as docs/proof/teams-2303): the screenshots must be
repeatable by a reviewer, and the rig's accounts are seeded into a fresh, throwaway browser context rather than into a
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-bill-rig"
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
        debug = Path(tempfile.gettempdir()) / "cc-teams-bill-failed.png"
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
    env["CC_TEAMS_BILL_PROOF_RIG"] = str(RIG_DIR)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamBillProofRig",
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

            team_page = f"{base}/team/{r['teamId']}/members"

            # The Owner, before the plan: the Billing section says Not started and offers Start the team plan.
            ctx = context_for(r["owner"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme")
            wait_text(page, "The team plan has not started.")
            shot(page, "owner-1-not-started.png")

            # The checkout: seats, price, total, period - and no charge.
            page.get_by_role("button", name="Start the team plan").click()
            page.get_by_role("alertdialog").wait_for()
            wait_text(page, "No charge - you will not be charged")
            shot(page, "owner-2-checkout.png")
            page.get_by_role("alertdialog").get_by_role("button", name="Start the plan").click()
            wait_text(page, "The team plan has started.")
            wait_text(page, "Charged: US$0.00")
            shot(page, "owner-3-active.png")

            # Cancel: the Gateway's warning, then the plan is Ending - active to the end of the period.
            page.get_by_role("button", name="Cancel plan").click()
            page.get_by_role("alertdialog").wait_for()
            wait_text(page, "then ends.")
            shot(page, "owner-4-cancel-confirmation.png")
            page.get_by_role("alertdialog").get_by_role("button", name="Cancel plan").click()
            wait_text(page, "Ending")
            wait_text(page, "Auto-renew is off - switch it on to keep the team plan.")
            if page.get_by_role("button", name="Renew now").count() != 0:
                sys.exit("ERROR: an ending plan offered Renew now - only an ended plan may")
            shot(page, "owner-5-ending.png")
            ctx.close()

            # A Manager: the same bill, read-only - no switch, no buttons, and the Gateway's note.
            ctx = context_for(r["manager"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme")
            wait_text(page, "Only the team's Owner can change the billing.")
            shot(page, "manager-read-only.png")
            ctx.close()

            # A Developer: the Team page, and no Billing section.
            ctx = context_for(r["developer"])
            page = ctx.new_page()
            page.goto(team_page)
            wait_text(page, "Team Acme")
            if page.get_by_role("region", name="Billing").count() != 0:
                sys.exit("ERROR: a Developer was shown the Billing section")
            shot(page, "developer-no-billing.png")
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
    print(f"[server] bill after the run: {after}", flush=True)
    if after is None or after["status"] != "active" or after["autoRenew"] is not False or after["seats"] != 4:
        sys.exit(f"ERROR: the server does not hold the bill the page made: {after}")
    history = after["history"]
    if len(history) != 1 or history[0]["ChargedCents"] != 0 or history[0]["AmountCents"] != 4 * 4900:
        sys.exit(f"ERROR: the history is not one line of US$196.00 charged US$0.00: {history}")
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
