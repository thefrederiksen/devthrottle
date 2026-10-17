# THE LAYOUT CHECK OF THE APPROVER FORM (License Switching, phase 6, track C, developer C3).
#
#   python missions\license-switching\phase-6\track-c\form-layout\form_layout_check.py      (after npm ci at the root)
#
# The Cockpit dev server, with the Licenses routes answered from the Gateway's own example answers in
# missions/license-switching/cockpit-api.md (the working tree's copy), and the browser profiles of each Director
# answered by this script. It photographs the approver form in each of its states: empty, opened from a card, filled,
# each missing choice after Save, a Director that is not connected, profiles loading, a Director with none, a saved
# approver and a refused one. Every name is made up: the machines and accounts of the examples are renamed to
# machine-a, machine-b and alice, bob and carol at example.com before the page sees them.
#
# It is a layout check, not the behaviour's proof: the behaviour is pinned by LicensesViewSignIns.test.tsx.
import copy, json, os, re, subprocess, sys, time, urllib.request
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", "..", "..", ".."))
DOC = os.path.join(ROOT, "missions", "license-switching", "cockpit-api.md")
PORT = 5198

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
APPROVER_SET = made_up(next(b for b in blocks if "browserProfile" in b and "canApproveNow" in b))
REFUSAL = {"code": "invalid_browser_profile", "error": "No browser profile named work-old is on machine-a."}

# machine-b has a second user, reporting from its own Director, so the user has to be chosen there.
second = copy.deepcopy(next(m for m in OVERVIEW["machines"] if m["machineName"] == "machine-b"))
second.update({"machineId": "guest0000000000000000000000000000", "osUser": "guest", "label": "machine-b (guest)",
               "directorId": "dir-windows-guest"})
OVERVIEW["machines"].append(second)
for machine in OVERVIEW["machines"]:
    machine["canListBrowserProfiles"] = True
    machine["browserProfilesText"] = f"The browser profiles of {machine['label']}'s Director."
OFFLINE = copy.deepcopy(OVERVIEW)
for machine in OFFLINE["machines"]:
    if machine["machineName"] == "machine-a":
        machine["canListBrowserProfiles"] = False
        machine["browserProfilesText"] = "machine-a's Director is not connected, so its browser profiles cannot be listed."

PROFILES = {
    "dir-mac": [{"id": "work", "name": "work"}, {"id": "personal", "name": "Personal"}],
    "dir-windows": [{"id": "work", "name": "work"}],
    "dir-windows-guest": [{"id": "guest", "name": "Guest"}],
}
state = {"overview": OVERVIEW, "profiles": "listed", "put": "saved"}

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
        found = re.fullmatch(r"/directors/([^/]+)/browsers", path)
        if found:
            if state["profiles"] == "loading":
                return None  # never answered: the form stays on its loading sentence
            listed = [] if state["profiles"] == "none" else PROFILES.get(found.group(1), [])
            return route.fulfill(json={"browsers": listed, "harnessInstalled": True})
        if path == "/gateway/licenses/approvers" and request.method == "PUT":
            if state["put"] == "refused":
                return route.fulfill(status=400, json=REFUSAL)
            return route.fulfill(json=APPROVER_SET)
        if path == "/gateway/licenses/approvers":
            return route.fulfill(json=APPROVERS)
        if path.startswith("/gateway/licenses/forecast"):
            return route.fulfill(json=FORECAST)
        if path.startswith("/gateway/licenses/log"):
            return route.fulfill(json=LOG)
        if path == "/gateway/licenses":
            return route.fulfill(json=state["overview"])
        if request.resource_type in ("fetch", "xhr"):
            return route.fulfill(status=503, json={"error": "not part of this layout check"})
        return route.continue_()

    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 1000})
        page.add_init_script("localStorage.setItem('cc.deviceKey','layout-check-key')")
        page.route("**/*", handle)
        form = page.locator("[data-testid=lic-approver-editor]")

        def shot(name, element=True):
            page.wait_for_timeout(500)
            path = os.path.join(HERE, name + ".png")
            if element:
                # The form with the approver list above it, so the form is seen where it sits on the page.
                page.locator("[data-testid=lic-approver-editor]").scroll_into_view_if_needed()
                box = form.bounding_box()
                view = page.viewport_size
                top = max(box["y"] - 220, 0)
                bottom = min(box["y"] + box["height"] + 40, view["height"])
                page.screenshot(path=path, clip={"x": 0, "y": top, "width": view["width"], "height": bottom - top})
            else:
                page.screenshot(path=path)
            print("shot", name)

        def fresh(overview=OVERVIEW, profiles="listed", put="saved"):
            state.update({"overview": overview, "profiles": profiles, "put": put})
            page.goto(f"http://localhost:{PORT}/licenses")
            page.wait_for_selector("[data-testid=lic-approver]")

        def open_new():
            page.get_by_role("button", name="Set an approver").click()
            form.wait_for()

        def pick(label, value):
            form.get_by_label(label).select_option(value)

        def save():
            form.get_by_role("button", name="Save").click()

        fresh(); open_new(); shot("form-empty")

        fresh()
        card = page.get_by_role("region", name="carol@example.com")
        card.scroll_into_view_if_needed(); page.wait_for_timeout(300)
        card.screenshot(path=os.path.join(HERE, "card-set-the-approver.png")); print("shot card-set-the-approver")
        card.get_by_role("button", name="Set the approver").click(); form.wait_for(); shot("form-opened-from-card")

        fresh(); open_new()
        pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        page.wait_for_selector("[data-testid=lic-approver-editor] option[value=work]", state="attached")
        pick("Browser profile on that machine", "work"); shot("form-filled")

        fresh(); open_new(); save(); shot("missing-everything")

        fresh(); open_new(); pick("Approving machine", "machine-a")
        page.wait_for_selector("[data-testid=lic-approver-editor] option[value=work]", state="attached")
        pick("Browser profile on that machine", "work"); save(); shot("missing-account")

        fresh(); open_new(); pick("Claude account", "carol@example.com"); save(); shot("missing-machine")

        fresh(); open_new(); pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-b"); save()
        shot("missing-user")

        fresh(); open_new(); pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        page.wait_for_selector("[data-testid=lic-approver-editor] option[value=work]", state="attached")
        save(); shot("missing-profile")

        fresh(OFFLINE); open_new(); pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        shot("director-not-connected")

        fresh(profiles="loading"); open_new(); pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        shot("profiles-loading")

        fresh(profiles="none"); open_new(); pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        shot("no-profiles")

        fresh(put="refused"); open_new()
        pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        page.wait_for_selector("[data-testid=lic-approver-editor] option[value=work]", state="attached")
        pick("Browser profile on that machine", "work"); save(); page.wait_for_selector("[data-testid=lic-approver-error]")
        shot("refused-by-the-gateway")

        fresh(); open_new()
        pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-a")
        page.wait_for_selector("[data-testid=lic-approver-editor] option[value=work]", state="attached")
        pick("Browser profile on that machine", "work"); save(); page.wait_for_selector("[data-testid=lic-approver-answer]")
        page.locator("[data-testid=lic-approver-answer]").scroll_into_view_if_needed(); shot("saved", element=False)

        page.set_viewport_size({"width": 900, "height": 1000})
        fresh(); open_new()
        pick("Claude account", "carol@example.com"); pick("Approving machine", "machine-b"); save()
        shot("narrow-window-missing-user")
        browser.close()
finally:
    subprocess.run(f"taskkill /PID {vite.pid} /T /F", shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
