# LAYOUT CHECK of the license alarm banner (License Switching phase 6, Track A, piece A1). The Cockpit and the phone app
# run on their own development servers; GET /gateway/licenses/alarms is answered with the Gateway's own example answers
# (the words pinned by LicenseAlarmFoldTests), and every other Gateway call is refused with 503, so what is checked is
# only where the banner sits and how it looks on real pages. Made-up names only.
#
# Run from anywhere: python missions/license-switching/phase-6/track-a/alarm/layout_check.py
import os, subprocess, sys, time, urllib.request
from playwright.sync_api import sync_playwright

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))

ONE_LINE = "machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account."
NONE = {"asOfUtc": "2026-10-17T08:00:00Z", "count": 0, "summary": None, "linkText": None, "alarms": []}
ONE = {"asOfUtc": "2026-10-17T08:00:00Z", "count": 1, "summary": None, "linkText": "Open Licenses", "alarms": [
    {"alarmId": "0123456789abcdef0123456789abcdef", "kind": "not-ready-at-80", "machineName": "machine-a",
     "accountEmail": "alice@example.com", "text": ONE_LINE, "openedAtUtc": "2026-10-17T07:58:00Z"}]}
SEVERAL = {"asOfUtc": "2026-10-17T08:00:00Z", "count": 3, "summary": "3 license problems need you.", "linkText": "Open Licenses", "alarms": [
    ONE["alarms"][0],
    {"alarmId": "fedcba9876543210fedcba9876543210", "kind": "stopped-reporting", "machineName": "machine-b",
     "accountEmail": None, "text": "machine-b has not reported for 20 minutes. Check that the Director on machine-b is running.",
     "openedAtUtc": "2026-10-17T07:59:00Z"},
    {"alarmId": "00112233445566778899aabbccddeeff", "kind": "approver-cannot-approve", "machineName": None,
     "accountEmail": "bob@example.com", "text": "The approver of bob@example.com is not signed in to claude.ai. Sign the approving browser profile in again.",
     "openedAtUtc": "2026-10-17T07:59:30Z"}]}


def serve(app, port):
    proc = subprocess.Popen(f"npx vite --port {port} --strictPort", cwd=os.path.join(ROOT, "apps", app), shell=True,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    base = f"http://localhost:{port}/" + ("mobile/" if app == "mobile" else "")
    for _ in range(90):
        try:
            urllib.request.urlopen(base, timeout=1)
            return proc
        except Exception:
            time.sleep(1)
    sys.exit(f"the {app} development server did not start")


def stop(proc):
    # Only the development server this script started.
    subprocess.run(f"taskkill /PID {proc.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


state = {"alarms": ONE}


def handle(route):
    path = "/" + route.request.url.split("/", 3)[3]
    if path.startswith("/gateway/licenses/alarms"):
        return route.fulfill(json=state["alarms"])
    if route.request.resource_type in ("fetch", "xhr") and not path.startswith("/@") and not path.startswith("/src") \
            and not path.startswith("/node_modules") and not path.startswith("/mobile/src") and not path.startswith("/mobile/@"):
        return route.fulfill(status=503, json={"error": "not part of this layout check"})
    return route.continue_()


cockpit = serve("cockpit", 5197)
mobile = serve("mobile", 5196)
try:
    with sync_playwright() as p:
        browser = p.chromium.launch()

        def shot(page, name):
            page.wait_for_timeout(800)
            page.screenshot(path=os.path.join(OUT, name + ".png"), full_page=False)
            print("shot", name)

        desk = browser.new_page(viewport={"width": 1440, "height": 900})
        desk.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        desk.route("**/*", handle)
        state["alarms"] = ONE
        desk.goto("http://localhost:5197/history"); desk.wait_for_selector("[data-testid=license-alarm-banner]")
        shot(desk, "cockpit-history-one-alarm")
        state["alarms"] = SEVERAL
        desk.goto("http://localhost:5197/directors"); desk.wait_for_selector("[data-testid=license-alarm-summary]")
        shot(desk, "cockpit-directors-several-alarms")
        state["alarms"] = NONE
        desk.goto("http://localhost:5197/history"); desk.wait_for_timeout(1500)
        if desk.query_selector("[data-testid=license-alarm-banner]") is not None:
            sys.exit("FAIL: a banner was drawn with no alarm open")
        shot(desk, "cockpit-history-no-alarm")

        phone = browser.new_page(viewport={"width": 390, "height": 844}, device_scale_factor=2, is_mobile=True, has_touch=True)
        phone.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        phone.route("**/*", handle)
        state["alarms"] = ONE
        phone.goto("http://localhost:5196/mobile/"); phone.wait_for_selector("[data-testid=license-alarm-banner]")
        shot(phone, "phone-home-one-alarm")
        state["alarms"] = SEVERAL
        phone.goto("http://localhost:5196/mobile/settings"); phone.wait_for_selector("[data-testid=license-alarm-summary]")
        shot(phone, "phone-settings-several-alarms")
        browser.close()
finally:
    stop(cockpit)
    stop(mobile)
