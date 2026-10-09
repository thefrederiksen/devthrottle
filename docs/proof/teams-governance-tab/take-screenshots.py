"""Take the Governance tab screenshots for docs/proof/teams-governance-tab (Teams v1, the team's Governance tab).

One foreground command, run from the repository root:

    python docs/proof/teams-governance-tab/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway test binaries, starts the proof rig (TeamGovernanceProofRig: a hosted
Gateway on 127.0.0.1:7915 with Teams released, a team with one example.org account in each role and no rule changed yet),
adds two skills to the team's own library through the Gateway's team routes, then drives a headless browser through the
Governance tab: as the Owner it switches rules, makes a skill Required and sets a limit - every change through the tab
itself - and as a Developer it reads the same rules read-only. It writes a stop file, waits for the rig to stop, and
checks the server holds what the tab did.

Playwright is used as a library here, deliberately (the same reason as docs/proof/teams-bill-without-stripe): the
screenshots must be repeatable by a reviewer, and the rig's accounts are seeded into a fresh, throwaway browser context
rather than into a signed-in persona browser.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-governance-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"


def run(cmd, cwd):
    print(f"[run] {' '.join(cmd)}", flush=True)
    subprocess.run(cmd, cwd=cwd, check=True, shell=(os.name == "nt"))


def account_script(email, key, team_id):
    accounts = [{"id": "rig-1", "label": email, "email": email, "deviceKey": key, "installId": "rig-install-1"}]
    return (
        "try {"
        f"localStorage.setItem('cc.accounts', {json.dumps(json.dumps(accounts))});"
        "localStorage.setItem('cc.activeAccount', 'rig-1');"
        f"localStorage.setItem('devthrottle.currentTeam.rig-1', {json.dumps(team_id)});"
        "} catch (e) {}"
    )


def api(base, key, method, path, body=None):
    req = urllib.request.Request(f"{base}/{path}", method=method,
                                 data=None if body is None else json.dumps(body).encode("utf-8"),
                                 headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    with urllib.request.urlopen(req) as resp:
        return resp.status


def add_skill(base, key, team, skill_id, name, summary):
    api(base, key, "POST", f"teams/{team}/skills", {
        "id": skill_id, "name": name, "summary": summary, "triggers": [name.lower()],
        "bodyMarkdown": f"# {name}\n\n{summary}",
    })
    api(base, key, "POST", f"teams/{team}/skills/{skill_id}/publish")


def wait_text(page, text):
    try:
        page.get_by_text(text).first.wait_for(timeout=15000)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "cc-teams-governance-failed.png"
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
    env["CC_TEAMS_GOVERNANCE_PROOF_RIG"] = str(RIG_DIR)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamGovernanceProofRig",
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

        # The team's own library, added the way a Manager adds it.
        add_skill(base, r["manager"]["key"], team, "review-before-merge", "Review before merge",
                  "A second session reviews every change before it merges.")
        add_skill(base, r["manager"]["key"], team, "one-worktree-per-task", "One worktree per task",
                  "Every task gets its own worktree off main.")

        with sync_playwright() as p:
            browser = p.chromium.launch()

            def context_for(who):
                ctx = browser.new_context(viewport={"width": 1280, "height": 1100})
                ctx.add_init_script(account_script(who["email"], who["key"], team))
                ctx.add_cookies([{"name": "cc-gateway-token", "value": who["key"], "url": base}])
                return ctx

            tab = f"{base}/settings?tab=governance"

            # The Owner sets the rules through the tab itself.
            ctx = context_for(r["owner"])
            page = ctx.new_page()
            page.goto(tab)
            wait_text(page, "the rules every member's sessions work under")
            for name in ["An agent reviews every pull request before a person merges it", "Nobody merges their own agent's work", "Any other agent"]:
                before = len(page.locator(".gov-changes li").all())
                page.get_by_role("switch", name=name).click()
                page.wait_for_function(f"document.querySelectorAll('.gov-changes li').length > {before}")
            for skill, level in [("Skill:review-before-merge", "Required"), ("Skill:one-worktree-per-task", "Suggested")]:
                page.get_by_label("Skill or workflow to add").select_option(skill)
                page.get_by_label("Level to add it at").select_option(level)
                page.get_by_role("region", name="Required skills and workflows").get_by_role("button", name="Add").click()
                wait_text(page, f"\" {level.lower()}")
            page.get_by_label("Agent hours per member per week").fill("45")
            page.get_by_label("Agent hours per member per week").locator("xpath=..").get_by_role("button", name="Save").click()
            wait_text(page, "to 45 h")
            shot(page, "owner-governance.png")
            ctx.close()

            # A Developer: the same rules, read-only, with the Gateway's sentence.
            ctx = context_for(r["developer"])
            page = ctx.new_page()
            page.goto(tab)
            wait_text(page, "Only the team's Owner and Managers can change these rules.")
            if page.get_by_role("switch").first.is_enabled():
                sys.exit("ERROR: a Developer was given a switch that works")
            if page.get_by_label("Skill or workflow to add").count() != 0:
                sys.exit("ERROR: a Developer was offered Add")
            shot(page, "developer-read-only.png")
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
    print(f"[server] governance after the run: {after}", flush=True)
    if after is None or not after["noSelfMerge"] or not after["agentReviewsPullRequests"] or after["allowOtherAgents"] \
            or after["agentHoursPerWeek"] != 45 or after["changes"] != 6:
        sys.exit(f"ERROR: the server does not hold the rules the tab made: {after}")
    if sorted((i["Name"], i["Level"]) for i in after["items"]) != [("One worktree per task", "Suggested"), ("Review before merge", "Required")]:
        sys.exit(f"ERROR: the server does not hold the items the tab named: {after['items']}")
    if rig.returncode != 0:
        sys.exit(f"ERROR: the rig reported failure ({rig.returncode})")


if __name__ == "__main__":
    main()
