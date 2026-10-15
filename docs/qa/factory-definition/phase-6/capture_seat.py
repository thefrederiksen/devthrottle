"""Desktop screenshots of a seat's page on a defined factory and on one without a definition (Factory Definition,
phase 6), from a throwaway Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    uv run --no-project --with playwright --with typer --with click --with rich --with requests python docs/qa/factory-definition/phase-6/capture_seat.py

It needs Playwright for Python and Google Chrome.

Why Playwright and not browser-harness: this is a repeatable script that a reviewer re-runs to get the same pictures,
the first case the browser rules give for Playwright as a library. The browser is a headless one this script starts
and stops.

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, where the Gateway serves it from.
  2. Starts the Gateway on port 7991 with its OWN data folder (a fresh scratch folder), the factory area switched on,
     and CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's Gateway nor the owner's factories.
  3. Registers qa-outreach and qa-undefined with the Gateway's own machine token, and enrolls one browser device
     (a row holding the SHA-256 of a key this script made up): "a person" to this Gateway.
  4. qa-undefined: gives its scout seat a schedule with this tree's own command line, as the person.
  5. qa-outreach: saves the phase 1 example as definition version 1 and publishes it, as the person, which makes the
     boss's schedule from its start; then saves version 2, in which the writer is renamed Author, and leaves it a draft.
  6. Opens the Cockpit in headless Chrome signed in with the person's key, and takes a screenshot of: the defined
     factory's boss seat (no Edit schedule, no Take off this seat, the Gateway's sentence and the way to the Definition
     tab); the publish confirm of an edit of the boss's brief, which names the waiting rename; and the scout seat of
     the factory with no definition, unchanged.
  7. Writes the Gateway answers the report quotes into seat-answers.json, exactly as the Gateway sent them.
  8. Stops the Gateway it started. Nothing is published from the brief editor: the confirm is cancelled.
"""

import hashlib
import json
import os
import secrets
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
EXAMPLE = REPO / "docs" / "qa" / "factory-definition" / "phase-1" / "example"
CLI_DIR = REPO / "tools" / "cc-devthrottle"
PORT = 7991
BASE = f"http://127.0.0.1:{PORT}"
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "qa-outreach"
UNDEFINED = "qa-undefined"
DEVICE_ID = "qa-cockpit-browser"
MACHINE = "RIG_MACHINE"
SCRATCH = Path(tempfile.gettempdir()) / "factory-definition-p6-seat-capture"

ANSWERS = {}


def log(msg):
    print(f"[p6-seat] {msg}", flush=True)


def request(method, path, key, body=None):
    """One call. Returns (status, parsed body or text); never raises on an HTTP status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, payload = res.status, res.read()
    except urllib.error.HTTPError as exc:
        status, payload = exc.code, exc.read()
    except OSError:
        return 0, None
    text = payload.decode("utf-8", "replace")
    try:
        return status, (json.loads(text) if text else None)
    except json.JSONDecodeError:
        return status, text


def call(method, path, key, body=None):
    status, answer = request(method, path, key, body)
    if not 200 <= status < 300:
        raise SystemExit(f"{method} {path} failed: HTTP {status} {answer}")
    return answer


def must(cond, what):
    if not cond:
        raise SystemExit(what)


def wait_for_gateway(proc):
    for _ in range(120):
        if proc.poll() is not None:
            raise SystemExit(f"the Gateway exited with code {proc.returncode} before it answered")
        try:
            urllib.request.urlopen(BASE + "/", timeout=2)
            return
        except urllib.error.HTTPError:
            return
        except OSError:
            time.sleep(1)
    raise SystemExit(f"the Gateway did not answer on port {PORT} within two minutes")


def find_token(root):
    for path in Path(root).rglob("gateway-token.txt"):
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    raise SystemExit(f"no gateway-token.txt under {root}: the Gateway did not write its token")


def wait_until_ready(token):
    for _ in range(120):
        status, _ = request("GET", "/cron/jobs", token)
        if 200 <= status < 300:
            return
        if status not in (0, 503):
            raise SystemExit(f"GET /cron/jobs answered HTTP {status} while waiting for readiness")
        time.sleep(1)
    raise SystemExit("the Gateway was not ready within two minutes")


def enroll_browser(root, key):
    found = list(Path(root).rglob("gateway.db"))
    if len(found) != 1:
        raise SystemExit(f"expected one gateway.db under the scratch root, found {found}")
    db = sqlite3.connect(found[0])
    try:
        cols = {row[1] for row in db.execute("PRAGMA table_info(device_credentials)")}
        tenant_col = "tenant_id" if "tenant_id" in cols else "TenantId"
        row = {
            "DeviceId": DEVICE_ID, "MachineName": MACHINE, "DeviceKeyHash": hashlib.sha256(key.encode()).hexdigest(),
            "KeyPrefix": key[:8], "KeyLast4": key[-4:], "IssuedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
            "Status": "active", "Platform": "windows", "DeviceType": "browser", tenant_col: "local",
        }
        missing = [k for k in row if k not in cols]
        if missing:
            raise SystemExit(f"device_credentials has no column(s) {missing}; its columns are {sorted(cols)}")
        db.execute(f"INSERT INTO device_credentials ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
        db.commit()
    finally:
        db.close()
    log(f"enrolled the browser device '{DEVICE_ID}'")


def register(token, factory, title, seats):
    call("PUT", "/gateway/factory/registry", token, {
        "factory": factory, "title": title, "folder": "/home/rig/" + factory, "computer": MACHINE, "bossSeat": "boss",
        "seats": [{"id": s, "name": s.capitalize(), "role": s.capitalize(), "briefFile": f"seats/{s}.yaml"} for s in seats],
    })
    log(f"registered {factory}")


def cli(key, args):
    """This tree's cc-devthrottle, as the holder of `key`; returns its JSON answer."""
    env = dict(os.environ, CC_GATEWAY_URL=BASE, CC_GATEWAY_SESSION_KEY=key, NO_COLOR="1", COLUMNS="200")
    env.pop("CC_SESSION_ID", None)
    launch = ("import sys; sys.path[:0] = [sys.argv[1], sys.argv[2]]; sys.argv = ['cc-devthrottle'] + sys.argv[3:]; "
              "from src.cli import tool_main; tool_main()")
    r = subprocess.run([sys.executable, "-c", launch, str(CLI_DIR), str(CLI_DIR.parent), *args, "--json"],
                       cwd=str(CLI_DIR), env=env, capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise SystemExit(f"cc-devthrottle {' '.join(args)} exited {r.returncode}:\n{r.stdout}{r.stderr}")
    return json.loads(r.stdout)


def example_files():
    return {p.relative_to(EXAMPLE).as_posix(): p.read_text(encoding="utf-8") for p in sorted(EXAMPLE.rglob("*")) if p.is_file()}


def seed(key):
    """qa-undefined's scout gets a schedule; qa-outreach is defined, published, and has a rename waiting as a draft."""
    job = cli(key, ["schedule", "create", "--name", "QA Undefined scout at noon", "--machine", MACHINE,
                    "--repo", "/home/rig/qa-undefined", "--cron", "0 12 * * *", "--tz", "Europe/Copenhagen",
                    "--seed", "Scout round.", "--factory", UNDEFINED, "--seat", "scout"])
    log(f"qa-undefined scout schedule {job['id']}")

    files = example_files()
    call("PUT", f"/factory-definitions/{FACTORY}/draft", key, {"expectedVersion": 0, "note": "the first definition", "content": files})
    call("POST", f"/factory-definitions/{FACTORY}/publish", key, {"version": 1, "replaces": None})
    renamed = dict(files)
    must("name: Writer" in renamed["seats/writer.yaml"], "the example's writer is not named Writer")
    renamed["seats/writer.yaml"] = renamed["seats/writer.yaml"].replace("name: Writer", "name: Author")
    call("PUT", f"/factory-definitions/{FACTORY}/draft", key, {"expectedVersion": 1, "note": "the writer is Author now", "content": renamed})
    log("qa-outreach: version 1 published, version 2 (the writer renamed Author) saved as a draft")


# ---------- the browser ----------

def shot(page, name):
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
    log(f"{name}.png")


def wait(page, selector, what, state="visible"):
    try:
        page.wait_for_selector(selector, timeout=30000, state=state)
    except Exception:
        debug = SCRATCH / "debug.png"
        page.screenshot(path=str(debug), full_page=True)
        raise SystemExit(f"{what} did not appear ({selector}); the page is in {debug}")


def flow(page, key):
    # 1. The defined factory's boss seat: no Edit schedule, no Take off this seat; the sentence and the link.
    boss = f"/gateway/factories/{FACTORY}/seats/boss"
    page.goto(f"{BASE}/factories/{FACTORY}/seats/boss", wait_until="networkidle")
    wait(page, "[data-testid=fa-seat-defined]", "the defined factory's sentence on the seat page")
    starts = call("GET", boss, key)["starts"]
    must(starts["schedules"] and all(s["editLabel"] is None and s["unlink"] is None for s in starts["schedules"]),
         f"a schedule of the defined factory still offers an edit or a take-off: {starts['schedules']}")
    must(page.locator("text=Edit schedule").count() == 0 and page.locator("text=Take off this seat").count() == 0,
         "the page still draws Edit schedule or Take off this seat")
    ANSWERS["30-defined-seat-starts"] = {"request": f"GET {boss}", "starts": starts}
    shot(page, "30-defined-seat-no-edit-no-take-off")

    # 1b. The sentence's link opens the factory's Definition tab.
    page.click("[data-testid=fa-seat-defined] a")
    page.wait_for_url(f"**/factories/{FACTORY}/definition")
    wait(page, ".fa-tab.active:has-text('Definition')", "the Definition tab, selected")
    shot(page, "37-seat-link-opens-the-definition-tab")
    page.goto(f"{BASE}/factories/{FACTORY}/seats/boss", wait_until="networkidle")
    wait(page, "[data-testid=fa-seat-defined]", "the seat page again")

    # 2. The publish confirm of an edit of the boss's brief names the rename waiting in the definition.
    page.click("[data-testid=fa-seat-brief-edit]")
    wait(page, "[data-testid=fa-brief-text]", "the brief editor")
    page.fill("[data-testid=fa-brief-text]", "Run the factory, and check the writer's posts before they go out.\n")
    page.click("[data-testid=fa-brief-publish]")
    wait(page, "[role=alertdialog] [data-testid=fa-confirm-lines]", "the brief publish confirm")
    lines = page.inner_text("[role=alertdialog] [data-testid=fa-confirm-lines]")
    must("Seat Author (writer) changed: name Writer -> Author." in lines, f"the confirm does not name the rename: {lines}")
    editor = call("GET", boss + "/brief/editor", key)
    ANSWERS["31-brief-publish-confirm"] = {"request": f"GET {boss}/brief/editor", "confirmLines": editor["publish"]["confirmLines"]}
    shot(page, "31-brief-publish-confirm-names-the-rename")
    page.keyboard.press("Escape")
    wait(page, "[role=alertdialog]", "the confirm closing", state="detached")
    must(call("GET", f"/factory-definitions/{FACTORY}", key)["publishedVersion"] == 1, "something was published from the confirm")

    # 3. The same seat page on a factory with no definition: unchanged, Edit schedule and Take off this seat offered.
    scout = f"/gateway/factories/{UNDEFINED}/seats/scout"
    page.goto(f"{BASE}/factories/{UNDEFINED}/seats/scout", wait_until="networkidle")
    wait(page, "text=Take off this seat", "Take off this seat on the factory with no definition")
    undefined = call("GET", scout, key)["starts"]
    must(undefined["definedText"] is None and undefined["schedules"][0]["editLabel"] == "Edit schedule",
         f"the factory with no definition changed: {undefined}")
    must(page.locator("[data-testid=fa-seat-defined]").count() == 0, "the factory with no definition shows the definition sentence")
    ANSWERS["32-undefined-seat-starts"] = {"request": f"GET {scout}", "starts": undefined}
    shot(page, "32-no-definition-seat-unchanged")

    # 4. The defined factory's Seats tab, one crumb from the seat page: no Edit schedule there either, and the sentence.
    seats_route = f"/gateway/factories/{FACTORY}/seats"
    page.goto(f"{BASE}/factories/{FACTORY}/seats", wait_until="networkidle")
    wait(page, "[data-testid=fa-seats-defined]", "the defined factory's sentence on the Seats tab")
    seats = call("GET", seats_route, key)
    schedules = [s for row in seats["rows"] for s in row["schedules"]]
    must(schedules and all(s["editLabel"] is None for s in schedules), f"a schedule on the Seats tab still offers an edit: {schedules}")
    must(page.locator("text=Edit schedule").count() == 0, "the Seats tab still draws Edit schedule")
    ANSWERS["38-defined-seats-tab"] = {"request": f"GET {seats_route}", "definedText": seats["definedText"],
                                       "definitionHref": seats["definitionHref"], "schedules": schedules}
    shot(page, "38-defined-seats-tab-no-edit")

    # 5. Bring back on the boss's brief history. A boss brief edit is saved over the waiting rename (version 3) and
    #    published, so version 1 is below the published one and offers Bring back - which restores ALL of version 1.
    call("PUT", f"/factory-briefs/{FACTORY}/boss/draft", key,
         {"text": "Run the factory, and check the writer's posts before they go out.\n", "expectedVersion": 2})
    call("POST", f"/factory-definitions/{FACTORY}/publish", key, {"version": 3, "replaces": 1})
    page.goto(f"{BASE}/factories/{FACTORY}/seats/boss", wait_until="networkidle")
    page.click("[data-testid=fa-seat-brief-history]")
    wait(page, "[data-testid=fa-brief-back-1]", "Bring back on version 1 of the boss's brief history")
    page.click("[data-testid=fa-brief-back-1]")
    wait(page, "[role=alertdialog] [data-testid=fa-confirm-lines]", "the Bring back confirm")
    lines = page.inner_text("[role=alertdialog] [data-testid=fa-confirm-lines]")
    must("It also brings back the rest of definition version 1" in lines and "name Author -> Writer" in lines,
         f"the Bring back confirm does not name the rename it undoes: {lines}")
    history = call("GET", boss + "/brief/history", key)
    row = next(r for r in history["rows"] if r["version"] == 1)
    ANSWERS["39-bring-back-confirm"] = {"request": f"GET {boss}/brief/history", "confirmLines": row["bringBack"]["confirmLines"]}
    shot(page, "39-bring-back-confirm-names-what-it-undoes")
    page.keyboard.press("Escape")
    wait(page, "[role=alertdialog]", "the confirm closing", state="detached")
    must(call("GET", f"/factory-definitions/{FACTORY}", key)["publishedVersion"] == 3, "something was published from the Bring back confirm")


def capture(key):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        try:
            context = browser.new_context(viewport={"width": 1280, "height": 1100})
            context.add_cookies([{"name": "cc-gateway-token", "value": key, "url": BASE}])
            page = context.new_page()
            page.goto(BASE + "/", wait_until="domcontentloaded")
            page.evaluate(
                """(key) => {
                    localStorage.setItem('cc.accounts', JSON.stringify([
                      { id: 'rig', label: 'Rig', email: null, deviceKey: key, installId: 'rig' }]));
                    localStorage.setItem('cc.activeAccount', 'rig');
                    localStorage.setItem('cc.deviceKey', key);
                }""",
                key,
            )
            flow(page, key)
            context.close()
        finally:
            browser.close()


def main():
    if not (GATEWAY_BIN / "CcDirector.Gateway.dll").exists():
        raise SystemExit(f"build the Gateway first: {GATEWAY_BIN} has no CcDirector.Gateway.dll")
    if not (COCKPIT_DIST / "index.html").exists():
        raise SystemExit(f"build the Cockpit first: {COCKPIT_DIST} has no index.html (cd apps/cockpit && npm run build)")
    target = GATEWAY_BIN / "wwwroot" / "c"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(COCKPIT_DIST, target)
    if SCRATCH.exists():
        shutil.rmtree(SCRATCH)
    SCRATCH.mkdir(parents=True)
    root = SCRATCH / "root"
    (root / "config").mkdir(parents=True)
    (root / "config" / "config.json").write_text(
        json.dumps({"factoryAgents": {"enabled": True}, "time_zone": "America/Toronto"}), encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=str(root), CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1")
    env.pop("CC_GATEWAY_NO_AUTH", None)
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    gateway_log = open(SCRATCH / "gateway.out.txt", "w", encoding="utf-8")
    proc = subprocess.Popen(["dotnet", str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env, stdout=gateway_log, stderr=subprocess.STDOUT)
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        wait_until_ready(token)
        key = "qa-browser-" + secrets.token_hex(16)
        enroll_browser(root, key)
        register(token, FACTORY, "QA Outreach", ["boss", "writer"])
        register(token, UNDEFINED, "QA Undefined", ["boss", "scout"])
        seed(key)
        capture(key)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        gateway_log.close()
        log("Gateway stopped")

    (OUT / "seat-answers.json").write_text(json.dumps(ANSWERS, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    log("seat-answers.json written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
