# INTERIM LAYOUT CHECK ONLY - not the phase's proof. The Cockpit dev server with the Licenses routes answered
# from the Gateway's own example answers (cockpit-api.md on the Gateway branch). The real proof is run against
# a real local Gateway once the routes are on main.
import subprocess, re, json, time, os, sys, copy, urllib.request
from playwright.sync_api import sync_playwright
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))
doc = subprocess.check_output(["git", "show", "HEAD:missions/license-switching/cockpit-api.md"], cwd=ROOT).decode("utf-8")
overview, forecast, log, empty, _ = [json.loads(b) for b in re.findall(r"```json\n(.*?)```", doc, re.S)]
state = {"overview": overview}
PORT = 5199
vite = subprocess.Popen(f"npx vite --port {PORT} --strictPort", cwd=os.path.join(ROOT, "apps", "cockpit"), shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://localhost:{PORT}/", timeout=1); break
        except Exception: time.sleep(1)
    else: sys.exit("the dev server did not start")
    def handle(route):
        url = route.request.url
        path = url.split(f"localhost:{PORT}")[1]
        if path.startswith("/gateway/licenses/forecast"): return route.fulfill(json=forecast)
        if path.startswith("/gateway/licenses/log"): return route.fulfill(json=log)
        if path.startswith("/gateway/licenses/switch-now"): return route.fulfill(status=409, json={"code": "target-full", "error": "account-b@example.com is at its limit until its 5-hour window resets."})
        if path.startswith("/gateway/licenses"): return route.fulfill(json=state["overview"])
        if route.request.resource_type in ("fetch", "xhr"): return route.fulfill(status=503, json={"error": "not part of this layout check"})
        return route.continue_()
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        page.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        page.route("**/*", handle)
        def shot(name, full=True):
            page.wait_for_timeout(600)
            page.screenshot(path=os.path.join(OUT, name + ".png"), full_page=full)
            print("shot", name)
        page.goto(f"http://localhost:{PORT}/licenses"); page.wait_for_selector("[data-testid=lic-account]")
        shot("accounts-with-data")
        page.locator("[data-testid=lic-log]").scroll_into_view_if_needed(); shot("log-with-failed-switch", full=False)
        page.get_by_role("button", name="Switch now").first.click(); page.wait_for_selector("[role=alertdialog]")
        shot("switch-now-confirmation", full=False)
        page.get_by_role("alertdialog").get_by_role("button", name="Switch now").click(); page.wait_for_selector(".ui-confirm-error")
        shot("switch-now-refused", full=False)
        v = copy.deepcopy(overview); a = v["accounts"][2]
        a["fiveHour"] = {"utilization": None, "utilizationText": "Not known", "level": "not-known", "resetsAtUtc": None, "resetsText": None}
        a["isStale"] = True; a["status"] = "stale"; a["statusText"] = "Stale"; a["statusTone"] = "warning"; a["statusDetail"] = "Its numbers are too old to trust."
        v["accounts"][1]["renewsText"] = "Renews 17 Oct"
        v["machines"][1]["spares"][0]["renewalFailedText"] = "The last renewal of this sign-in failed 3 hours ago."
        v["machines"][1]["spares"][0]["renewalFailedTone"] = "bad"
        state["overview"] = v
        page.goto(f"http://localhost:{PORT}/licenses"); page.wait_for_selector("[data-testid=lic-account]"); shot("not-read-and-renewal")
        state["overview"] = empty
        page.goto(f"http://localhost:{PORT}/licenses"); page.wait_for_selector(".ui-empty"); shot("empty", full=False)
        browser.close()
finally:
    subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
