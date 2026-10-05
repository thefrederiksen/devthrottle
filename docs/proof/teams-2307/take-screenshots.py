"""Take the Questions screenshots for docs/proof/teams-2307 (devthrottle_internal#2307).

One foreground command, run from the repository root:

    python docs/proof/teams-2307/take-screenshots.py

It builds the Cockpit, puts it beside the Gateway unit-test binaries, starts the proof rig (TeamQuestionsProofRig: a
hosted Gateway on 127.0.0.1:7914 with Teams released, and one team "DevThrottle" in which tech@mindzie.com, a Developer,
wrote "Pricing" with the question "Should the trial be 14 days or 30?" and sent it to two Collaborators,
docs@mindzie.com and dev@mindzie.com). Then, in a headless browser:

  - at desktop width, docs@ opens Questions, sees the question waiting, chooses "30 days", writes a comment and sends;
  - at 390 px phone width, dev@ does the same with their own comment;
  - after each, tech@ (the person who asked) opens their report and reads the comment they received, with the
    Gateway's line saying which question and which choice it was about.

It writes a stop file and waits for the rig to stop. Playwright is used as a library, deliberately (the same choice as
docs/proof/teams-2309): the screenshots must be repeatable by a reviewer, and the rig's accounts are seeded into fresh
browser contexts rather than into a signed-in persona browser.
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
RIG_DIR = Path(tempfile.gettempdir()) / "cc-teams-2307-rig"
TEST_PROJECT = REPO / "src" / "CcDirector.Gateway.UnitTests"
TEST_BIN = TEST_PROJECT / "bin" / "Debug" / "net10.0"
COMMENTS = {
    "desktop": "30 days. Most trials I watched never reached a first session inside two weeks.",
    "phone-390": "30, but say on the page that the card is not charged until day 30.",
}
CHOICE = "30 days"
WORDS_SENTENCE = "Your words go to "


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
        debug = Path(tempfile.gettempdir()) / "cc-teams-2307-failed.png"
        page.screenshot(path=str(debug), full_page=True)
        body = page.evaluate("document.body.innerText")
        sys.exit(f"ERROR: {what} never appeared at {page.url}. Screenshot: {debug}\nPage text:\n{body[:1500]}")


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
    env["CC_TEAMS_2307_PROOF_RIG"] = str(RIG_DIR)
    env.pop("NOTIFY_OWNER_SERVICE_TOKEN", None)
    rig = subprocess.Popen(
        ["dotnet", "test", str(TEST_PROJECT), "--no-build", "--filter", "FullyQualifiedName~TeamQuestionsProofRig",
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

            for prefix, (width, height), who in (("desktop", (1280, 800), "desktopCollaborator"),
                                                 ("phone-390", (390, 844), "phoneCollaborator")):
                comment = COMMENTS[prefix]

                # The Collaborator: the question waiting on them (S8), with the recommended option already picked.
                ctx = context(r[who], width, height)
                page = ctx.new_page()
                page.goto(f"{base}/questions")
                card = page.get_by_test_id("team-questions-waiting").get_by_test_id("team-question")
                wait_for(page, card, "the question waiting on the Collaborator")
                wait_for(page, page.get_by_text(WORDS_SENTENCE), "the sentence saying where the words go")
                shot(page, f"{prefix}-1-question-waiting.png")

                card.first.get_by_test_id("team-question-option").filter(has_text=CHOICE).click()
                card.first.get_by_test_id("team-question-comment").fill(comment)
                shot(page, f"{prefix}-2-question-filled-in.png")
                card.first.get_by_test_id("team-question-send").click()
                answered = page.get_by_test_id("team-questions-answered").get_by_test_id("team-question")
                wait_for(page, answered.filter(has_text=comment), "the answered question with the words as sent")
                answered.first.evaluate("e => e.scrollIntoView({block: 'start'})")
                shot(page, f"{prefix}-3-question-answered.png")
                ctx.close()

                # The person who asked: their own report, and the comment it received, said to be about that question.
                ctx = context(r["author"], width, height)
                page = ctx.new_page()
                page.goto(f"{base}/reports")
                wait_for(page, page.get_by_test_id("team-report-own-row"), "the author's own reports")
                if prefix == "phone-390":
                    # The author is in the WHOLE Cockpit, whose rail does not fold itself away at phone width; a phone
                    # reader folds it with its own button, so the shot is taken that way (as in teams-2309).
                    page.get_by_test_id("rail-toggle").click()
                page.get_by_test_id("team-report-own-row").filter(has_text="Pricing").click()
                received = page.get_by_test_id("team-report-comment-from-person").filter(has_text=comment)
                wait_for(page, received, "the comment the author received")
                wait_for(page, received.get_by_test_id("team-report-comment-about"), "the line saying which question it is about")
                received.first.evaluate("e => e.scrollIntoView({block: 'start'})")
                shot(page, f"{prefix}-4-author-received-the-comment.png")
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
