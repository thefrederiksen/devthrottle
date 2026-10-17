# THE LAYOUT CHECK OF THE APPROVER ROWS' HEALTH (License Switching, phase 6, track C, developer C3, pull request 2).
#
#   python missions\license-switching\phase-6\track-c\health-layout\health_layout_check.py      (after npm ci at the root)
#
# The Cockpit dev server, with the Licenses routes answered from the Gateway's own example answers in
# missions/license-switching/cockpit-api.md, and the approvers route answered with four rows: a healthy registered one,
# one that needs a fresh sign-in, one that has not been checked on a machine that never reported, and one whose
# Director is not connected, with a display name. The health sentences of the rows that are not in the document are
# the Gateway's own wording, as its tests pin it (LicenseSignInEndpointsTests). Every name is made up: machines and
# accounts are renamed to machine-a, machine-c and alice, bob, carol and dave at example.com.
#
# A layout check, not the behaviour's proof: that is LicensesViewSignIns.test.tsx.
import copy, json, os, re, subprocess, sys, time, urllib.request
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
PORT = 5197

RENAME = [
    ("the-windows-machine", "machine-b"),
    ("the-mac", "machine-a"),
    ("the-laptop", "machine-c"),
    ("account-a@example.com", "alice@example.com"),
    ("account-b@example.com", "bob@example.com"),
    ("account-c@example.com", "carol@example.com"),
]


def made_up(value):
    text = json.dumps(value)
    for old, new in RENAME:
        text = text.replace(old, new)
    return json.loads(text)


def example(text):
    # A few examples are a run of fields shown without the braces around them.
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return json.loads("{" + text + "}")


with open(DOC, encoding="utf-8") as f:
    blocks = [example(b) for b in re.findall(r"```json\n(.*?)\n```", f.read().replace("\r\n", "\n"), re.S)]
OVERVIEW = made_up(blocks[0])
FORECAST = made_up(blocks[2])
LOG = made_up(blocks[3])
APPROVERS = made_up(next(b for b in blocks if isinstance(b.get("approvers"), list) and not b["isEmpty"]))
REMOVED = made_up(next(b for b in blocks if "removed" in b and "accountEmail" in b))

healthy, never_checked = APPROVERS["approvers"]
fresh = copy.deepcopy(healthy)
fresh.update({
    "accountEmail": "carol@example.com", "canApproveNow": False, "status": "cannot-approve", "statusText": "Cannot approve",
    "statusTone": "warning", "source": "owner", "sourceText": "Set by you.", "reachText": None, "state": "needs-fresh-sign-in",
    "healthText": 'The approver for carol@example.com (browser profile "work" on machine-a) needs a fresh claude.ai sign-in on the approval page.',
    "whatToDo": "Open that browser on machine-a and sign in to claude.ai once.",
})
fresh["text"] = f"{fresh['healthText']} {fresh['whatToDo']}"
offline = copy.deepcopy(healthy)
offline.update({
    "accountEmail": "dave@example.com", "browserProfile": "work-chrome", "browserProfileName": "Work Chrome",
    "canApproveNow": False, "status": "not-connected", "statusText": "Not connected", "statusTone": "warning",
    "reachText": "The Director on machine-a is not connected, so no sign-in can be approved there now.",
    "healthText": 'The approver for dave@example.com (browser profile "Work Chrome" on machine-a) can approve sign-ins; it was last checked 2026-10-11 00:12 UTC.',
})
offline["text"] = f"Sign-ins for dave@example.com are approved on machine-a, in the browser profile Work Chrome. {offline['reachText']}"
APPROVERS["approvers"] = [healthy, never_checked, fresh, offline]

vite = subprocess.Popen(f"npx vite --port {PORT} --strictPort", cwd=os.path.join(ROOT, "apps", "cockpit"), shell=True,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://localhost:{PORT}/", timeout=1)
            break
        except Exception:
            time.sleep(1)
    else:
        sys.exit("the dev server did not start")

    def handle(route):
        request = route.request
        path = request.url.split(f"localhost:{PORT}")[1].split("?")[0]
        if path == "/gateway/licenses/approvers" and request.method == "DELETE":
            return route.fulfill(json=REMOVED)
        if path == "/gateway/licenses/approvers":
            return route.fulfill(json=APPROVERS)
        if path.startswith("/gateway/licenses/forecast"):
            return route.fulfill(json=FORECAST)
        if path.startswith("/gateway/licenses/log"):
            return route.fulfill(json=LOG)
        if path == "/gateway/licenses":
            return route.fulfill(json=OVERVIEW)
        if request.resource_type in ("fetch", "xhr"):
            return route.fulfill(status=503, json={"error": "not part of this layout check"})
        return route.continue_()

    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        page.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        page.route("**/*", handle)
        page.goto(f"http://localhost:{PORT}/licenses")
        page.wait_for_selector("[data-testid=lic-approver]")
        page.wait_for_timeout(500)
        rows = page.locator("[data-testid=lic-approver]")
        names = ["row-healthy-registered", "row-not-checked-never-reported", "row-needs-fresh-sign-in", "row-not-connected-with-display-name"]
        for i, name in enumerate(names):
            rows.nth(i).scroll_into_view_if_needed()
            rows.nth(i).screenshot(path=os.path.join(HERE, name + ".png"))
            print("shot", name)
        table = page.locator("[data-testid=lic-approvers]")
        table.scroll_into_view_if_needed()
        table.screenshot(path=os.path.join(HERE, "approvers-table.png"))
        print("shot approvers-table")
        rows.nth(0).get_by_role("button", name="Remove").click()
        page.wait_for_selector("[role=alertdialog]")
        page.wait_for_timeout(400)
        page.screenshot(path=os.path.join(HERE, "registered-row-remove-confirmation.png"))
        print("shot registered-row-remove-confirmation")
        page.set_viewport_size({"width": 900, "height": 1000})
        page.keyboard.press("Escape")
        page.wait_for_timeout(400)
        table.scroll_into_view_if_needed()
        table.screenshot(path=os.path.join(HERE, "approvers-table-narrow.png"))
        print("shot approvers-table-narrow")
        browser.close()
finally:
    subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
