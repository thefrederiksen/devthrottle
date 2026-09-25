"""Screenshot proof of the factory Map tab (issue #3383), end to end, in ONE foreground run.

It starts a throwaway Gateway (this build, its own root folder, port 7899, no Tailscale, no authentication, the
factory agents switch on) and the Cockpit's dev server in front of it, as child processes of this script. It
seeds a few record rows through the record's own route, publishes a factory map through PUT /gateway/factory/map
(the body cc-website-factory publish-map --print writes), screenshots the pages with Playwright, and stops both
children before it exits - whatever happens. Nothing is left running.

Playwright as a library, deliberately: a repeatable scripted run against a throwaway local Gateway, not
interactive work in a signed-in browser profile.

Usage: py shoot.py <map body .json> <out dir>
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import requests
from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]
EXE = REPO / "src" / "CcDirector.GatewayApp" / "bin" / "Debug" / "net10.0-windows" / "devthrottle-gateway.exe"
COCKPIT = REPO / "apps" / "cockpit"
GW = "http://127.0.0.1:7899"
UI = "http://127.0.0.1:5199"
BODY = Path(sys.argv[1])
OUT = Path(sys.argv[2])
NOW = datetime.now(timezone.utc)


def wait_for(url, what, seconds=120):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if requests.get(url, timeout=3).status_code < 500:
                print(f"{what} is up")
                return
        except requests.RequestException:
            pass
        time.sleep(1)
    raise SystemExit(f"{what} did not come up at {url} within {seconds}s")


def record(agent, outcome, what, minutes_ago, session=None):
    body = {"factory": "website-business", "factoryAgent": agent, "outcome": outcome, "what": what,
            "actor": f"proof:{agent}", "sessionId": session,
            "occurredUtc": (NOW - timedelta(minutes=minutes_ago)).isoformat().replace("+00:00", "Z")}
    r = requests.post(f"{GW}/gateway/factory/activity", json=body, timeout=10)
    if r.status_code != 201:
        raise SystemExit(f"record refused {agent} {outcome}: {r.status_code} {r.text[:200]}")


def shot(page, name, full=True):
    page.wait_for_timeout(800)
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=full)
    print("saved", name)


def main():
    if not EXE.is_file():
        raise SystemExit(f"build the Gateway host first: {EXE} is missing")
    OUT.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix="factory-map-proof-"))
    (root / "config").mkdir()
    (root / "config" / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(root), CC_GATEWAY_NO_TAILSCALE="1", CC_GATEWAY_NO_AUTH="1")
    children = []
    try:
        gw_log = open(OUT / "gateway-stdout.log", "w", encoding="utf-8")
        children.append(subprocess.Popen([str(EXE), "--port", "7899", "--no-autostart"], env=env,
                                         stdout=gw_log, stderr=subprocess.STDOUT))
        wait_for(f"{GW}/healthz", "the Gateway")

        ui_log = open(OUT / "vite-stdout.log", "w", encoding="utf-8")
        children.append(subprocess.Popen("npx vite --port 5199 --strictPort --host 127.0.0.1", cwd=COCKPIT, shell=True,
                                         env=dict(os.environ, COCKPIT_PROXY_TARGET=GW),
                                         stdout=ui_log, stderr=subprocess.STDOUT))
        wait_for(UI, "the Cockpit dev server")

        # A morning in the record: Scout ran and finished, Sender asked, Mail Watcher ran.
        record("scout", "started", "Run 12 started.", 190, session="s-scout")
        record("scout", "done", "Run 12 succeeded: 2 sites passed.", 120, session="s-scout")
        record("sender", "started", "Run 13 started (after Scout run 12).", 110, session="s-sender")
        record("sender", "escalated", "Run 13 needs you: schedule these 2 emails?", 100, session="s-sender")
        record("mail-watcher", "started", "Run 14 started.", 60, session="s-watch")
        record("mail-watcher", "failed", "Run 14 failed: no dashboard within 45m.", 10, session="s-watch")

        body = json.loads(BODY.read_text(encoding="utf-8"))
        r = requests.put(f"{GW}/gateway/factory/map", json=body, timeout=15)
        print("PUT map:", r.status_code, r.text[:200])
        if r.status_code != 200:
            raise SystemExit("the map was refused")
        broken = dict(body, edges=[dict(body["edges"][0], to="nobody")] + body["edges"][1:])
        r2 = requests.put(f"{GW}/gateway/factory/map", json=broken, timeout=15)
        print("PUT broken map:", r2.status_code, r2.text[:200])
        (OUT / "put-responses.txt").write_text(
            f"PUT real map -> {r.status_code} {r.text}\nPUT map with an arrow to a missing box -> {r2.status_code} {r2.text}\n",
            encoding="utf-8")

        with sync_playwright() as p:
            browser = p.chromium.launch()
            # The rig Gateway runs with no authentication, so any key passes; the Cockpit only needs to hold one to
            # skip its sign-in page. Written as the older single-key form, which the Cockpit adopts as one account.
            enroll = ("if (!localStorage.getItem('cc.accounts')) {"
                      " localStorage.setItem('cc.deviceKey', 'proof-rig-no-auth');"
                      " localStorage.setItem('cc.installId', 'proof-rig'); }")

            def new_page(**kw):
                ctx = browser.new_context(**kw)
                ctx.add_init_script(enroll)
                return ctx.new_page()

            page = new_page(viewport={"width": 1440, "height": 1000})
            page.goto(f"{UI}/factory-agents")
            try:
                page.wait_for_selector("[data-testid=fa-card-website-business]", timeout=30000)
            except Exception:
                shot(page, "00-debug-factories-page")
                print("PAGE TEXT:", page.inner_text("body")[:1500])
                raise
            shot(page, "01-factories-card-with-map-button")

            page.click("[data-testid=fa-map-website-business]")
            page.wait_for_selector("[data-testid=fa-map]")
            shot(page, "02-factory-page-opens-on-the-map")

            page.click("[data-testid=fa-node-sender]")
            shot(page, "03-picked-sender-spec-beside-the-map")

            page.click("[data-testid=fa-node-scout]")
            shot(page, "04-picked-scout")

            page.click("text=/^Agents \\(/")
            shot(page, "05-agents-tab")

            page.goto(f"{UI}/factory-agents/website-business/scout")
            page.wait_for_selector("[data-testid=factory-agent-page]")
            shot(page, "06-agent-page-crumb-links-to-the-factory")

            dark = new_page(viewport={"width": 1440, "height": 1000})
            dark.goto(f"{UI}/factory-agents/website-business")
            dark.wait_for_selector("[data-testid=fa-map]")
            dark.click("[data-testid=fa-node-mail-watcher]")
            shot(dark, "07-picked-mail-watcher")

            phone = new_page(viewport={"width": 390, "height": 844})
            phone.goto(f"{UI}/factory-agents/website-business")
            phone.wait_for_selector("[data-testid=fa-map]")
            shot(phone, "08-phone-width")
            browser.close()
    finally:
        for c in reversed(children):
            subprocess.run(["taskkill", "/PID", str(c.pid), "/T", "/F"], capture_output=True)
        for c in children:
            try:
                c.wait(timeout=20)
            except subprocess.TimeoutExpired:
                print(f"WARNING: child {c.pid} did not stop")
        shutil.rmtree(root, ignore_errors=True)
        print("children stopped, root removed")


if __name__ == "__main__":
    main()
