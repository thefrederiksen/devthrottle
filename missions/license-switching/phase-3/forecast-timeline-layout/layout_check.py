# LAYOUT CHECK OF THE FORECAST TIMELINE ON THE LICENSES PAGE (License Switching, 20 October 2026).
#
#   python missions\license-switching\phase-3\forecast-timeline-layout\layout_check.py   (after npm ci at the root)
#
# The Cockpit dev server, with the Licenses routes answered from the Gateway's own example answers in
# missions/license-switching/cockpit-api.md: the forecast "with a gap" of section 3 and the overview of that same
# picture, and the overview and forecast of section 2 (no gap). At phone width the Cockpit's rail is collapsed, as a
# reader on a phone has it.
# It takes the page at desktop width and at phone width, and fails when the PAGE scrolls sideways at either width (the
# chart may scroll inside its own box; the page may not). Everything is stopped at the end.
import io, json, os, re, subprocess, sys, time, urllib.request
from playwright.sync_api import sync_playwright

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))
doc = io.open(os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md"), encoding="utf-8").read().replace("\r\n", "\n")
blocks = re.findall(r"```json\n(.*?)\n```", doc, re.S)
overview, forecast, gap, gap_overview, log = (json.loads(blocks[i]) for i in (0, 2, 3, 4, 5))
if "outlook" not in gap or not gap["outlook"]["gaps"]:
    sys.exit("the fourth example of the document is not the forecast with a gap")
approvers = {"approvers": [], "machines": []}
state = {"forecast": gap, "overview": gap_overview}

PORT = 5198
vite = subprocess.Popen(f"npx vite --port {PORT} --strictPort", cwd=os.path.join(ROOT, "apps", "cockpit"), shell=True,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
failures = []
try:
    for _ in range(90):
        try:
            urllib.request.urlopen(f"http://localhost:{PORT}/", timeout=1)
            break
        except Exception:
            time.sleep(1)
    else:
        sys.exit("the dev server did not start")

    def handle(route):
        path = route.request.url.split(f"localhost:{PORT}")[1]
        if path.startswith("/gateway/licenses/forecast"):
            return route.fulfill(json=state["forecast"])
        if path.startswith("/gateway/licenses/log"):
            return route.fulfill(json=log)
        if path.startswith("/gateway/licenses/alarms"):
            return route.fulfill(json={"count": 0, "summary": None, "linkText": None, "alarms": []})
        if path.startswith("/gateway/licenses/approvers"):
            return route.fulfill(json=approvers)
        if path.startswith("/gateway/licenses"):
            return route.fulfill(json=state["overview"])
        if route.request.resource_type in ("fetch", "xhr"):
            return route.fulfill(status=503, json={"error": "not part of this layout check"})
        return route.continue_()

    with sync_playwright() as p:
        browser = p.chromium.launch()
        for name, width, height, overview_answer, forecast_answer in (
            ("desktop-with-gap", 1440, 1000, gap_overview, gap),
            ("phone-with-gap", 390, 844, gap_overview, gap),
            ("desktop-no-gap", 1440, 1000, overview, forecast),
        ):
            state["overview"], state["forecast"] = overview_answer, forecast_answer
            page = browser.new_page(viewport={"width": width, "height": height}, color_scheme="dark")
            page.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key'); localStorage.setItem('cockpit.railCollapsed', '" + ("true" if width < 800 else "false") + "')")
            page.route("**/*", handle)
            page.goto(f"http://localhost:{PORT}/licenses")
            page.wait_for_selector("[data-testid=lic-outlook]")
            page.wait_for_timeout(700)
            sideways = page.evaluate("document.scrollingElement.scrollWidth - window.innerWidth")
            chart = page.evaluate("(() => { const b = document.querySelector('.lic-tl-scroll'); return [b.scrollWidth, b.clientWidth]; })()")
            print(f"{name}: page scrolls sideways by {sideways}px; chart box {chart[1]}px wide holding {chart[0]}px")
            if sideways > 0:
                failures.append(f"{name}: the page scrolls sideways by {sideways}px")
            page.screenshot(path=os.path.join(OUT, name + ".png"), full_page=True)
            page.locator("[data-testid=lic-outlook]").screenshot(path=os.path.join(OUT, name + "-chart.png"))
            page.close()
        browser.close()
finally:
    subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

if failures:
    sys.exit("FAIL: " + "; ".join(failures))
print("PASS: no sideways page scroll at desktop or phone width")
