# THE QA RUN OF THE LICENSES PAGE AGAINST A REAL LOCAL GATEWAY (License Switching, phase 3).
#
# One command, everything in the foreground, everything stopped at the end:
#   1. starts qa-gateway (the product's own GatewayHost, hosted mode, throwaway storage, seeded license data);
#   2. reads the three Licenses routes over HTTP with the seeded browser key and saves each answer as it came;
#   3. starts the Cockpit's own development server, forwarding Gateway routes to that Gateway;
#   4. opens the page in a browser enrolled with the seeded key - no request is intercepted or stubbed - and
#      takes the screenshots;
#   5. stops the development server and the Gateway.
#
# Run from anywhere:  python missions\license-switching\phase-3\qa_run.py      (after `npm ci` at the repository root)
import json, os, subprocess, sys, time, urllib.request, urllib.error
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(HERE, "qa")
os.makedirs(OUT, exist_ok=True)
VITE_PORT = 5198


def get(port, path, key):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", headers={"Authorization": "Bearer " + key, "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as err:
        return err.code, json.loads(err.read().decode("utf-8") or "{}")


def save(name, value):
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
        json.dump(value, f, indent=2)
        f.write("\n")


gateway = subprocess.Popen(
    ["dotnet", "run", "--project", os.path.join(HERE, "qa-gateway"), "-c", "Debug"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
vite = None
failures = []
try:
    line = ""
    while not line.startswith("{"):
        line = gateway.stdout.readline()
        if line == "":
            sys.exit("the QA Gateway ended before it printed its port")
    seeded = json.loads(line)
    port, owner, empty = seeded["port"], seeded["ownerKey"], seeded["emptyKey"]
    print(f"Gateway on port {port}")

    # ---- the answers, as the real Gateway gave them ----
    for name, path in (("overview", "/gateway/licenses"), ("forecast", "/gateway/licenses/forecast"), ("log", "/gateway/licenses/log")):
        status, body = get(port, path, owner)
        print(f"GET {path} as the owner -> {status}")
        if status != 200:
            failures.append(f"{path} answered {status}")
        save(f"answer-{name}.json", body)
    status, body = get(port, "/gateway/licenses", empty)
    print(f"GET /gateway/licenses as the account that never reported -> {status}, isEmpty={body.get('isEmpty')}")
    save("answer-overview-empty.json", body)
    req = urllib.request.Request(f"http://127.0.0.1:{port}/gateway/licenses/switch-now", method="POST", data=b'{"accountEmail":"account-a@example.com","targetAccountEmail":null}',
                                 headers={"Authorization": "Bearer " + owner, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            switch_status = resp.status
    except urllib.error.HTTPError as err:
        switch_status = err.code
    print(f"POST /gateway/licenses/switch-now as the owner -> {switch_status} (the route arrives in the follow-up)")

    # ---- the page ----
    env = dict(os.environ, COCKPIT_PROXY_TARGET=f"http://127.0.0.1:{port}")
    vite = subprocess.Popen(f"npx vite --port {VITE_PORT} --strictPort", cwd=os.path.join(ROOT, "apps", "cockpit"), shell=True,
                            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(90):
        try:
            urllib.request.urlopen(f"http://localhost:{VITE_PORT}/", timeout=1)
            break
        except Exception:
            time.sleep(1)
    else:
        sys.exit("the Cockpit development server did not start")

    with sync_playwright() as p:
        browser = p.chromium.launch()

        def open_as(key):
            context = browser.new_context(viewport={"width": 1440, "height": 1000})
            context.add_init_script(f"localStorage.setItem('cc.deviceKey', {json.dumps(key)})")
            page = context.new_page()
            seen = []
            page.on("response", lambda r: seen.append((r.status, r.url.split(str(VITE_PORT))[1])) if "/gateway/licenses" in r.url else None)
            page.goto(f"http://localhost:{VITE_PORT}/licenses")
            return page, seen

        def shot(page, name, full=True):
            page.wait_for_timeout(700)
            page.screenshot(path=os.path.join(OUT, name + ".png"), full_page=full)
            print("screenshot", name)

        page, seen = open_as(owner)
        page.wait_for_selector("[data-testid=lic-account]")
        page.wait_for_selector("[data-testid=lic-log-row]")
        shot(page, "accounts-with-data")
        cards = page.locator("[data-testid=lic-account]").count()
        rows = page.locator("[data-testid=lic-log-row]").count()
        failed = page.locator("[data-testid=lic-log-row][data-state=failed]").count()
        hatched = page.locator(".lic-bar-unknown").count()
        print(f"on the page: {cards} account cards, {rows} log rows, {failed} failed, {hatched} hatched bars")
        if failed < 1:
            failures.append("no failed switch is in the log on the page")
        if hatched < 1:
            failures.append("no window is drawn as not read")
        page.locator("[data-testid=lic-log-row][data-state=failed]").first.scroll_into_view_if_needed()
        shot(page, "log-with-failed-switch", full=False)
        page.locator(".lic-bar-unknown").first.scroll_into_view_if_needed()
        page.locator("[data-testid=lic-account]", has=page.locator(".lic-bar-unknown")).first.screenshot(path=os.path.join(OUT, "not-read.png"))
        print("screenshot not-read")
        if page.get_by_role("button", name="Switch now").count() > 0:
            page.get_by_role("button", name="Switch now").first.click()
            page.wait_for_selector("[role=alertdialog]")
            shot(page, "switch-now-confirmation", full=False)
            page.get_by_role("alertdialog").get_by_role("button", name="Switch now").click()
            page.wait_for_selector(".ui-confirm-error")
            shot(page, "switch-now-no-route-yet", full=False)
            print("Switch now, confirmed, against this Gateway:", page.locator(".ui-confirm-error").inner_text())
        else:
            failures.append("the page offered no Switch now control")
        print("requests the page made:", sorted(set(seen)))

        page, seen = open_as(empty)
        page.wait_for_selector(".ui-empty")
        shot(page, "empty", full=False)
        print("empty state says:", page.locator(".ui-empty").inner_text())
        browser.close()
finally:
    if vite is not None:
        subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        gateway.stdin.write("\n")
        gateway.stdin.flush()
        gateway.wait(timeout=60)
    except Exception:
        gateway.kill()

if failures:
    sys.exit("QA RUN FAILED: " + "; ".join(failures))
print("QA RUN DONE")
