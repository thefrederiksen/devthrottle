# LAYOUT CHECK of readiness and the 80% alarm (License Switching phase 6, Track A, piece A3). The Cockpit and the phone
# app run on their own development servers. The Licenses routes are answered with the documented example answers of
# cockpit-api.md, changed in one way only: the machines are machine-a to machine-i, one for each kind of readiness
# sentence, each sentence exactly as LicenseReadinessServiceTests pins it. GET /gateway/licenses/alarms is answered with
# the 80% alarm in the words those tests pin. Every other Gateway call is refused with 503. The script FAILS when a
# readiness sentence is not drawn exactly as given, or is drawn on the wrong row. Made-up names only.
#
# Run from anywhere: python missions/license-switching/phase-6/track-a/readiness/layout_check.py
import copy, io, json, os, re, subprocess, sys, time, urllib.request
from playwright.sync_api import sync_playwright

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")

def parsed(text):
    # A few json blocks of the document are not one value (a request and its answer side by side); they are skipped.
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None


blocks = [parsed(b) for b in re.findall(r"```json\n(.*?)\n```", io.open(DOC, encoding="utf-8").read().replace("\r\n", "\n"), re.S)]
overview = next(b for b in blocks if isinstance(b, dict) and "machines" in b and "accounts" in b and b.get("isEmpty") is False)
forecast = next(b for b in blocks if isinstance(b, dict) and "nextSwitch" in b)
log = next(b for b in blocks if isinstance(b, dict) and "entries" in b)
approvers = next(b for b in blocks if isinstance(b, dict) and "approvers" in b and "isEmpty" in b)

B = "account-b@example.com"
WHY = "holds no sign-in for account-b@example.com"
# (machine, readiness, tone, sentence) - the sentences of LicenseReadinessServiceTests, word for word.
KINDS = [
    ("machine-a", "not-ready", "warning", f"Not ready: {WHY}; DevThrottle is starting a sign-in to {B} now."),
    ("machine-b", "ready", "good", f"Ready for {B}."),
    ("machine-c", "not-ready", "warning", f"Not ready: {WHY}; a sign-in to {B} is running, started at 03:12 UTC."),
    ("machine-d", "not-ready", "warning", f"Not ready: {WHY}; the last sign-in failed at 03:12 UTC "
                                          "(The approval in the browser did not complete); trying again at 03:14 UTC."),
    ("machine-e", "not-ready", "warning", f"Not ready: {WHY}; it was signed in at 03:12 UTC, and its next report should show it."),
    ("machine-f", "not-ready", "warning", f"Not ready: {WHY}, and the Director on machine-f is not connected, so it cannot be signed in yet."),
    ("machine-g", "not-ready", "warning", f"Not ready: {WHY}. No machine is set to approve sign-ins for {B}. "
                                          f"Set an approver for {B} on the Licenses page."),
    ("machine-h", "not-ready", "warning", f"Not ready: holds a sign-in for {B} that expires at 2026-10-11 03:17 UTC, before a switch "
                                          f"could finish; DevThrottle is starting a sign-in to {B} now."),
    ("machine-i", "no-room", "neutral", "No other account has room for it to switch to."),
]

machines = []
template = overview["machines"][0]
for i, (name, readiness, tone, text) in enumerate(KINDS):
    m = copy.deepcopy(template)
    m["machineId"] = f"{i + 1:032x}"
    m["machineName"] = name
    m["label"] = name
    m["readiness"] = readiness
    m["readinessTargetAccountEmail"] = None if readiness == "no-room" else B
    m["readinessText"] = text
    m["readinessTone"] = tone
    m["addSpareText"] = f"Signs {name} in to another account, to be held there as a spare."
    m["canAddSpare"] = True
    machines.append(m)
page_overview = dict(overview, machines=machines)

ALARM_LINE = ("machine-a is not ready to switch off account-a@example.com, which is at 85% of its weekly limit: it holds no "
              "sign-in for account-b@example.com, the account it would switch to next. Nothing to do yet: DevThrottle is "
              "signing it in; if this stays, sign machine-a in to account-b@example.com from the Licenses page.")
ALARM = {"asOfUtc": "2026-10-11T03:12:00Z", "count": 1, "summary": None, "linkText": "Open Licenses", "alarms": [
    {"alarmId": "0123456789abcdef0123456789abcdef", "kind": "not-ready-at-80", "machineName": "machine-a",
     "accountEmail": "account-a@example.com", "text": ALARM_LINE, "openedAtUtc": "2026-10-11T03:12:00Z"}]}
NONE = {"asOfUtc": "2026-10-11T03:12:00Z", "count": 0, "summary": None, "linkText": None, "alarms": []}
state = {"alarms": NONE}


def handle(route):
    path = "/" + route.request.url.split("/", 3)[3]
    if path.startswith("/gateway/licenses/alarms"):
        return route.fulfill(json=state["alarms"])
    if path.startswith("/gateway/licenses/forecast"):
        return route.fulfill(json=forecast)
    if path.startswith("/gateway/licenses/log"):
        return route.fulfill(json=log)
    if path.startswith("/gateway/licenses/approvers"):
        return route.fulfill(json=approvers)
    if path.split("?")[0] == "/gateway/licenses":
        return route.fulfill(json=page_overview)
    if route.request.resource_type in ("fetch", "xhr") and not path.startswith("/@") and not path.startswith("/src") \
            and not path.startswith("/node_modules") and not path.startswith("/mobile/src") and not path.startswith("/mobile/@"):
        return route.fulfill(status=503, json={"error": "not part of this layout check"})
    return route.continue_()


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


cockpit = serve("cockpit", 5195)
mobile = serve("mobile", 5194)
try:
    with sync_playwright() as p:
        browser = p.chromium.launch()

        def shot(page, name, full=False, element=None):
            page.wait_for_timeout(800)
            target = os.path.join(OUT, name + ".png")
            (element.screenshot(path=target) if element is not None else page.screenshot(path=target, full_page=full))
            print("shot", name)

        desk = browser.new_page(viewport={"width": 1440, "height": 900})
        desk.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        desk.route("**/*", handle)

        # Every kind of readiness sentence, each on its own machine's row, exactly as given.
        state["alarms"] = NONE
        desk.goto("http://localhost:5195/licenses")
        desk.wait_for_selector("[data-testid=lic-machine-readiness]")
        rows = desk.locator("[data-testid=lic-schedule-row]")
        if rows.count() != len(KINDS):
            sys.exit(f"FAIL: the schedule has {rows.count()} rows, expected {len(KINDS)}")
        for i, (name, readiness, tone, text) in enumerate(KINDS):
            row = rows.nth(i)
            if name not in row.inner_text():
                sys.exit(f"FAIL: row {i} is not {name}")
            line = row.locator("[data-testid=lic-machine-readiness]")
            drawn = line.inner_text().strip()
            if drawn != text:
                sys.exit(f"FAIL: {name} shows {drawn!r}, expected {text!r}")
            if f"lic-tone-{tone}" not in (line.get_attribute("class") or ""):
                sys.exit(f"FAIL: {name} is not drawn in the {tone} tone")
        if desk.query_selector("[data-testid=license-alarm-banner]") is not None:
            sys.exit("FAIL: a banner was drawn with no alarm open")
        schedule = desk.locator("[data-testid=lic-schedule]")
        schedule.scroll_into_view_if_needed()
        shot(desk, "licenses-schedule-every-readiness-kind", element=schedule)
        shot(desk, "licenses-page-with-readiness", full=True)

        # The 80% alarm: the red banner on the Licenses page and on another page.
        state["alarms"] = ALARM
        desk.goto("http://localhost:5195/licenses")
        desk.wait_for_selector("[data-testid=license-alarm-banner]")
        if ALARM_LINE not in desk.locator("[data-testid=license-alarm-banner]").inner_text():
            sys.exit("FAIL: the banner does not show the 80% alarm's line as given")
        shot(desk, "licenses-80-alarm-banner")
        desk.goto("http://localhost:5195/history")
        desk.wait_for_selector("[data-testid=license-alarm-banner]")
        shot(desk, "history-80-alarm-banner")

        phone = browser.new_page(viewport={"width": 390, "height": 844}, device_scale_factor=2, is_mobile=True, has_touch=True)
        phone.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        phone.route("**/*", handle)
        phone.goto("http://localhost:5194/mobile/")
        phone.wait_for_selector("[data-testid=license-alarm-banner]")
        shot(phone, "phone-home-80-alarm-banner")
        browser.close()
finally:
    stop(cockpit)
    stop(mobile)
print("layout check passed")
