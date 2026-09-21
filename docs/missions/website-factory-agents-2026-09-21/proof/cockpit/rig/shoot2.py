"""Screenshot QA of the Factory Agents area against the isolated rig Gateway on port 7899, at the pull request head.

Playwright as a library, deliberately: a repeatable scripted run against a throwaway local Gateway, not interactive
work in a signed-in browser profile. A stand-in Director (fakedirector.py) holds the Director stream open for the
whole run, so the roster has real sessions: 134 (the front desk's, Screen 6), 137 (the publisher's, holding the
trigger's lock), and 140 (an ordinary session, no chip).

Usage: shoot2.py <out dir> [on|off]
"""
import json
import os
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import requests
from playwright.sync_api import sync_playwright

sys.path.insert(0, os.path.dirname(__file__))
from fakedirector import FakeDirector, session  # noqa: E402

GW = "http://127.0.0.1:7899"
BASE = GW  # the Cockpit is served at the site root; under /c every route is "Page not found"
OUT = Path(sys.argv[1])
PHASE = sys.argv[2] if len(sys.argv) > 2 else "on"
DB = os.path.join(os.environ["TEMP"], "wbf-qa", "root", "gateway.db")
IDS = os.path.join(os.environ["TEMP"], "wbf-qa", "trigger-ids.json")
OUT.mkdir(parents=True, exist_ok=True)
NOW = datetime.now(timezone.utc)


def iso(dt):
    return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def shot(page, name):
    page.wait_for_timeout(1200)
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
    print("saved", name)


def go(page, path):
    page.goto(f"{BASE}{path}")
    page.wait_for_load_state("networkidle")


def seed_stuck_lock(ids):
    """The publisher's trigger started session 137 seven hours ago and it has not ended. The rig has no real
    Director to start a session, so the start is written into the rig's own trigger row (the two columns a real
    start sets) and its "started" row goes through the record's route; the CHECK that finds the lock is real."""
    started = NOW - timedelta(hours=7)
    c = sqlite3.connect(DB)
    c.execute("UPDATE triggers SET LastSessionId = '137', LastStartedUtc = ? WHERE upper(Id) = upper(?)",
              (started.strftime("%Y-%m-%d %H:%M:%S.%f"), ids["publish"]))
    if c.total_changes != 1:
        raise SystemExit("the publisher's trigger row was not updated")
    c.commit()
    c.close()
    r = requests.post(f"{GW}/gateway/factory/activity", json={
        "factory": "site-care", "factoryAgent": "publisher", "outcome": "started",
        "what": "Started to publish 2 queued pages.", "subject": "Publish queue", "sessionId": "137",
        "actor": "trigger:" + ids["publish"], "occurredUtc": iso(started)})
    r.raise_for_status()
    body = {"checkedAtUtc": iso(NOW - timedelta(seconds=5)), "exitCode": 0, "timedOut": False,
            "output": '{"count": 2}', "errorOutput": None}
    r = requests.post(f"{GW}/directors/qa-rig-director/triggers/{ids['publish']}/checks", json=body)
    r.raise_for_status()
    print("stuck-lock check:", r.text[:200])


def off_phase():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        ctx = browser.new_context(viewport={"width": 1440, "height": 950})
        ctx.add_init_script("try { localStorage.setItem('cc.deviceKey', 'qa-rig'); } catch (e) {}")
        page = ctx.new_page()
        # The rail, on a page that loads with no Director connected. The Sessions page crashes the browser tab when
        # the roster is empty - on origin/main's own Cockpit too, so it predates this change - and is reported.
        go(page, "/fleet-manager")
        shot(page, "18-switch-off-no-rail-item")
        go(page, "/factory-agents")
        shot(page, "19-switch-off-route-shows-nothing")
        browser.close()


def on_phase():
    ids = json.load(open(IDS))
    d = FakeDirector()
    d.start()
    d.push([
        session("134", "website-business front-desk: New business mail", "Idle", NOW - timedelta(hours=3)),
        session("137", "site-care publisher: Publish queue", "Working", NOW - timedelta(hours=7)),
        session("140", "Tidy the docs site", "Working", NOW - timedelta(minutes=40)),
    ])
    seed_stuck_lock(ids)
    try:
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            ctx = browser.new_context(viewport={"width": 1440, "height": 950})
            ctx.add_init_script("try { localStorage.setItem('cc.deviceKey', 'qa-rig'); } catch (e) {}")
            page = ctx.new_page()

            go(page, "/factory-agents")
            shot(page, "01-factories-flow-and-failures")
            go(page, "/factory-agents?tab=agents")
            shot(page, "02-all-factory-agents")
            go(page, "/factory-agents?tab=activity&window=last-24h")
            shot(page, "03-activity-empty-checks-collapsed-and-a-blocked-row")
            go(page, "/factory-agents/website-business/front-desk")
            shot(page, "04-factory-agent-front-desk")
            go(page, "/factory-agents/website-business/reviewer")
            shot(page, "05-factory-agent-all-runs-nothing-to-do")
            go(page, "/factory-agents/website-business/bookkeeper")
            shot(page, "06-factory-agent-broken-check-red")
            go(page, "/factory-agents/site-care/site-keeper")
            shot(page, "07-factory-agent-no-checks-ran-red")

            go(page, "/factory-agents/waiting?factory=website-business")
            shot(page, "08-waiting-for-you-before-handled")
            page.get_by_role("button", name="I have handled it").first.click()
            page.wait_for_load_state("networkidle")
            shot(page, "09-waiting-for-you-after-handled")
            go(page, "/factory-agents?tab=activity&window=last-24h&factory=website-business")
            shot(page, "10-activity-the-correcting-row")

            go(page, "/factory-agents/site-care/seo-writer")
            page.get_by_role("button", name="Pause factory agent").click()
            shot(page, "11-pause-asks-first")
            page.get_by_role("alertdialog").get_by_role("button", name="Pause factory agent").click()
            page.wait_for_load_state("networkidle")
            shot(page, "12-factory-agent-paused")

            go(page, "/factory-agents/site-care/publisher")
            shot(page, "13-stuck-lock-six-hours-red")
            page.get_by_role("button", name="Pause factory agent").click()
            page.get_by_role("alertdialog").get_by_role("button", name="Pause factory agent").click()
            page.wait_for_load_state("networkidle")
            shot(page, "14-stuck-lock-paused")
            page.get_by_role("button", name="Resume factory agent").click()
            page.wait_for_load_state("networkidle")
            shot(page, "15-stuck-lock-released-by-resume")

            go(page, "/factory-agents?tab=reports&window=last-7d")
            page.get_by_role("button", name="Make a report from this").click()
            page.get_by_label("Report name").fill("Night shift, last 7 days")
            page.get_by_label("Report name").press("Enter")
            page.wait_for_load_state("networkidle")
            shot(page, "16-reports-summary-and-saved-report")

            go(page, "/sessions")
            shot(page, "17-sessions-factory-agent-chip")

            go(page, "/factory-agents")
            shot(page, "01b-factories-after-handled-pause-and-release")
            browser.close()
    finally:
        d.stop()


if __name__ == "__main__":
    off_phase() if PHASE == "off" else on_phase()
