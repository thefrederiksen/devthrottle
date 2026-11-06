"""Screenshots and real Gateway answers for the Factory Inbox screens (Factory Inbox mission, phase 4), from a throwaway
local Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug), the Cockpit and the phone app:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build) && (cd apps/mobile && npm run build)
    uv run --no-project --with playwright python docs/qa/factory-inbox/phase-4-factory-inbox-screens/capture.py

It needs Google Chrome. What it does, and nothing else:
  1. Copies the Cockpit and phone builds into the Gateway's wwwroot/c and wwwroot/mobile, where the Gateway serves them.
  2. Starts the Gateway on port 7992 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it touches neither the owner's Gateway nor the owner's factories. Its config.json
     switches the factory area on, and its devices file holds two owner devices - a phone and a browser - so a message
     is left from a real owner device, exactly as the routes require (a machine token is refused the send).
  3. Registers five demonstration factories and archives one, and leaves typed messages in them from the phone.
  4. Writes the Secretary's and the Boss's steps for some of those messages straight into the scratch database:
     received, handed over, and the Boss's decision. Those steps need a Director with real Secretary and Boss sessions,
     which a scratch Gateway does not have (phase 6 proves them live, on the deployed Gateway). Every word and tone on
     screen is still the Gateway's own fold of those rows - nothing on screen is written by this script.
  5. Opens the phone app and the Cockpit in headless Chrome (Playwright as a library: a repeatable script, not
     interactive work), with a fake microphone, and takes the screenshots into this folder.
  6. Writes every Gateway answer the report quotes into answers.json, exactly as the Gateway sent it.
  7. Stops the Gateway it started. The scratch data folder is left for inspection and is not in the repository.

ONE KIND OF SCREENSHOT IS NOT FROM THE GATEWAY'S OWN DATA, and its file name says so: *-no-director-NOT-GATEWAY-DATA.png.
"Waiting - no Director is running on the Boss's computer" is written by the wake (phase 2), which is on its own branch
and not on main yet. For that picture the browser is handed this Gateway's real thread with one message's state label
replaced by the wake's exact sentence (FactoryInboxWords.WaitingLabel on origin/factory-inbox/wake), to show that the
thread prints it as it is handed.
"""

import json
import os
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
PORT = 7992
BASE = f"http://127.0.0.1:{PORT}"
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
MOBILE_DIST = REPO / "apps" / "mobile" / "dist"
PHONE_KEY = "dk_rig_phone_" + "1" * 32
BROWSER_KEY = "dk_rig_browser_" + "2" * 32
BOSS = "b0550000-0000-4000-8000-000000000001"
SECRETARY = "5ec00000-0000-4000-8000-000000000002"
WRITER = "a1b2c3d4-0000-4000-8000-000000000003"
NO_DIRECTOR = ("Waiting - no Director is running on the Boss's computer, RIG_MACHINE; "
               "it goes on when one connects there")

ANSWERS = {}


def log(msg):
    print(f"[inbox-capture] {msg}", flush=True)


def request(method, path, token, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {token}")
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, text = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, text = exc.code, exc.read().decode("utf-8", "replace")
    try:
        return status, (json.loads(text) if text else None)
    except json.JSONDecodeError:
        return status, text


def call(method, path, token, body=None):
    status, answer = request(method, path, token, body)
    if not 200 <= status < 300:
        raise SystemExit(f"{method} {path} failed: HTTP {status} {answer}")
    return answer


def keep(name, method, path, status, answer):
    ANSWERS[name] = {"request": f"{method} {path}", "status": status, "body": answer}
    log(f"answer kept: {name} -> HTTP {status}")


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


def register(token, factory, title):
    call("PUT", "/gateway/factory/registry", token, {
        "factory": factory, "title": title, "folder": "/home/rig/" + factory, "computer": "RIG_MACHINE",
        "bossSeat": "boss", "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"}],
    })


def leave(factory, text, note="none"):
    status, answer = request("POST", f"/gateway/factory/{factory}/inbox", PHONE_KEY, {"text": text, "note": note})
    if status != 201:
        raise SystemExit(f"leaving a message in {factory} failed: HTTP {status} {answer}")
    return answer["message"]["id"]


def database(root):
    for path in Path(root).rglob("*.db"):
        try:
            with sqlite3.connect(path) as db:
                if db.execute("select 1 from sqlite_master where name='factory_inbox_messages'").fetchone():
                    return path
        except sqlite3.DatabaseError:
            continue
    raise SystemExit(f"no Gateway database with the factory inbox tables under {root}")


class Steps:
    """The Secretary's and the Boss's steps, written as the store writes them (see the module docstring, step 4)."""

    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.row_factory = sqlite3.Row

    def _row(self, message_id):
        row = self.db.execute("select * from factory_inbox_messages where lower(Id)=lower(?)", (message_id,)).fetchone()
        if row is None:
            raise SystemExit(f"message {message_id} is not in the database")
        return row

    @staticmethod
    def _at(row, minutes):
        sent = datetime.fromisoformat(str(row["SentAtUtc"]).replace(" ", "T")[:26])
        return (sent + timedelta(minutes=minutes)).strftime("%Y-%m-%d %H:%M:%S")

    @staticmethod
    def _id(like):
        new = str(uuid.uuid4())
        return new.upper() if like == like.upper() else new

    def bundle(self, factory, message_ids, received_after=2, handed_after=3):
        first = self._row(message_ids[0])
        bundle_id = self._id(first["Id"])
        self.db.execute(
            "insert into factory_inbox_bundles (Id, tenant_id, Factory, HandedBySessionId, HandedAtUtc) values (?,?,?,?,?)",
            (bundle_id, first["tenant_id"], factory, SECRETARY, self._at(first, handed_after)))
        for mid in message_ids:
            row = self._row(mid)
            self.db.execute(
                "update factory_inbox_messages set State='with-boss', ReceivedAtUtc=?, ReceivedBySessionId=?, BundleId=? "
                "where Id=?", (self._at(row, received_after), SECRETARY, bundle_id, row["Id"]))
        self.db.commit()
        return bundle_id

    def decide(self, message_id, kind, after, why=None, session=None, close_bundle=False):
        row = self._row(message_id)
        todo_id = None
        if kind == "todo":
            todo_id = self._id(row["Id"])
            self.db.execute(
                "insert into factory_todos (Id, tenant_id, Factory, Text, FromMessageId, AddedBySessionId, AddedAtUtc) "
                "values (?,?,?,?,?,?,?)", (todo_id, row["tenant_id"], row["Factory"], row["Text"], row["Id"], BOSS, self._at(row, after)))
        self.db.execute(
            "insert into factory_inbox_decisions (Id, tenant_id, MessageId, Factory, Kind, Why, DelegatedSessionId, TodoId, "
            "DecidedBySessionId, DecidedAtUtc) values (?,?,?,?,?,?,?,?,?,?)",
            (self._id(row["Id"]), row["tenant_id"], row["Id"], row["Factory"], kind, why, session, todo_id, BOSS, self._at(row, after)))
        self.db.execute("update factory_inbox_messages set State='decided' where Id=?", (row["Id"],))
        if close_bundle:
            self.db.execute("update factory_inbox_bundles set ClosedAtUtc=?, ReadBySessionId=?, ReadAtUtc=? where Id=?",
                            (self._at(row, after), BOSS, self._at(row, after - 1), row["BundleId"]))
        self.db.commit()


def seed(token, root):
    for factory, title in (("managed-marketing", "Managed Marketing"), ("website-factory", "Website Factory"),
                           ("booking", "Booking"), ("private", "Private"), ("newsletter", "Newsletter"),
                           ("old-shop", "Old Shop")):
        register(token, factory, title)
    call("POST", "/gateway/factories/old-shop/archive", BROWSER_KEY, {"schedules": []})

    m1 = leave("managed-marketing", "The LinkedIn draft this morning is too salesy.", "bad")
    m2 = leave("managed-marketing", "Add a weekly video round-up.")
    m3 = leave("managed-marketing", "Love the new thumbnails.", "good")
    w1 = leave("website-factory", "The contact form on the pricing page sends nothing.", "bad")
    b1 = leave("booking", "Move Friday's shoot to Monday.")
    leave("newsletter", "Draft next week's issue around the three new case studies.")

    steps = Steps(database(root))
    steps.bundle("managed-marketing", [m1, m2])
    steps.decide(m1, "delegated", 5, session=WRITER)
    steps.decide(m2, "todo", 6, close_bundle=True)
    steps.bundle("managed-marketing", [m3])
    steps.bundle("website-factory", [w1])
    steps.bundle("booking", [b1])
    steps.decide(b1, "declined", 7, why="The client's contract fixes Friday; moving it breaks the contract.", close_bundle=True)
    log("seeded: six factories (one archived), six messages, two bundles decided")


def shot(page, name):
    page.screenshot(path=str(OUT / f"{name}.png"), full_page=True)
    log(f"{name}.png")


def wait(page, selector, what, timeout=30000):
    try:
        page.wait_for_selector(selector, timeout=timeout)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "inbox-rig-debug.png"
        page.screenshot(path=str(debug), full_page=True)
        raise SystemExit(f"{what} did not appear ({selector}); the page is in {debug}")


def sign_in(context, key, path):
    context.add_cookies([{"name": "cc-gateway-token", "value": key, "url": BASE}])
    page = context.new_page()
    page.goto(BASE + path, wait_until="domcontentloaded")
    page.evaluate(
        """(key) => {
            localStorage.setItem('cc.accounts', JSON.stringify([
              { id: 'rig', label: 'Rig', email: null, deviceKey: key, installId: 'rig' }]));
            localStorage.setItem('cc.activeAccount', 'rig');
            localStorage.setItem('cc.deviceKey', key);
        }""",
        key,
    )
    return page


def no_director(route):
    """The thread of 'newsletter' with its one sent message's state replaced by the wake's sentence (NOT GATEWAY DATA)."""
    response = route.fetch()
    body = response.json()
    for m in body.get("messages", []):
        if m["state"] == "sent":
            m["stateLabel"] = NO_DIRECTOR
    route.fulfill(response=response, json=body)


def phone(browser):
    context = browser.new_context(viewport={"width": 390, "height": 844}, device_scale_factor=2, permissions=["microphone"])
    page = sign_in(context, PHONE_KEY, "/mobile/")

    page.goto(BASE + "/mobile/inbox", wait_until="networkidle")
    wait(page, "[data-testid=fi-row]", "the factory inbox rows")
    shot(page, "phone-01-factory-inbox-list")

    # The menu, on the Home screen, offers the Inbox (the factory area switch is on for this account).
    page.goto(BASE + "/mobile/", wait_until="networkidle")
    page.click("button[aria-label='Open menu']")
    wait(page, "nav.drawer a[href='/mobile/inbox']", "the Inbox menu entry")
    shot(page, "phone-02-menu-inbox-entry")
    page.click("nav.drawer a[href='/mobile/inbox']")
    wait(page, "[data-testid=fi-row]", "the factory inbox rows, opened from the menu")

    page.click("[data-testid=fi-row] >> text=Managed Marketing")
    wait(page, "[data-testid=fi-message]", "the Managed Marketing thread")
    shot(page, "phone-03-thread-with-trails")

    page.click("button.fi-summary-button")
    wait(page, ".fi-summary-line", "the summary sentence")
    shot(page, "phone-04-thread-summary")
    page.click("button.fi-summary-button")

    page.fill(".fi-composer-text", "Please make the round-up five minutes at most.")
    page.click("button.fi-note[data-note='bad']")
    page.click("button.fi-send")
    wait(page, "text=Your message was left.", "the typed message being left")
    page.wait_for_selector("text=Please make the round-up five minutes at most.")
    shot(page, "phone-05-typed-message-left")

    # Spoken: the same voice box as a session, with a fake microphone. This scratch Gateway has no transcription key, so
    # the Gateway answers "could not be transcribed just now" and the recording is kept on the device to be sent again -
    # the durable path's held state, live.
    page.click("button.fi-mic")
    wait(page, ".dictate-overlay", "the voice box")
    page.wait_for_timeout(2500)
    shot(page, "phone-06-voice-box-recording")
    page.click(".dictate-actions button:has-text('Send')")
    wait(page, "text=Your recording is saved on this device and will be sent again.", "the held recording", timeout=60000)
    shot(page, "phone-07-recording-held-no-transcription")

    # A refused send: an archived factory, in the Gateway's words.
    page.goto(BASE + "/mobile/inbox/old-shop", wait_until="networkidle")
    wait(page, "[data-testid=fi-composer]", "the archived factory's composer")
    page.fill(".fi-composer-text", "Is anyone still here?")
    page.click("button.fi-send")
    wait(page, ".fi-composer-error", "the archived factory's refusal")
    shot(page, "phone-08-refused-archived-factory")

    page.route("**/gateway/factory/newsletter/inbox", no_director)
    page.goto(BASE + "/mobile/inbox/newsletter", wait_until="networkidle")
    wait(page, f"text={NO_DIRECTOR}", "the no-Director sentence")
    shot(page, "phone-09-no-director-NOT-GATEWAY-DATA")
    context.close()


def cockpit(browser):
    context = browser.new_context(viewport={"width": 1366, "height": 900})
    page = sign_in(context, BROWSER_KEY, "/")

    page.goto(BASE + "/inbox", wait_until="networkidle")
    wait(page, "[data-testid=fi-row]", "the Cockpit inbox rows")
    shot(page, "cockpit-01-inbox-list-and-empty-pane")

    page.click("[data-testid=fi-row] >> text=Managed Marketing")
    wait(page, "[data-testid=inbox-reading-pane] [data-testid=fi-message]", "the reading pane")
    shot(page, "cockpit-02-reading-pane-thread")

    page.goto(BASE + "/factories/booking", wait_until="networkidle")
    wait(page, "[data-testid=fa-leave-message]", "Leave a message on the factory page")
    page.click("[data-testid=fa-leave-message]")
    page.fill("[data-testid=fa-leave-message-panel] .fi-composer-text", "Tell the client Monday works for us too.")
    page.click("[data-testid=fa-leave-message-panel] button.fi-send")
    wait(page, "text=Open this factory's inbox", "the message left from the factory page")
    shot(page, "cockpit-03-factory-page-leave-a-message")

    page.goto(BASE + "/factories/old-shop", wait_until="networkidle")
    wait(page, "[data-testid=fa-leave-message]", "Leave a message on the archived factory's page")
    page.click("[data-testid=fa-leave-message]")
    page.fill("[data-testid=fa-leave-message-panel] .fi-composer-text", "Restart the shop please.")
    page.click("[data-testid=fa-leave-message-panel] button.fi-send")
    wait(page, "[data-testid=fa-leave-message-panel] .fi-composer-error", "the archived factory's refusal")
    shot(page, "cockpit-04-refused-archived-factory")

    page.goto(BASE + "/inbox/booking", wait_until="networkidle")
    wait(page, "[data-testid=inbox-reading-pane] [data-testid=fi-message]", "the Booking thread")
    shot(page, "cockpit-05-declined-and-new-message")

    page.route("**/gateway/factory/newsletter/inbox", no_director)
    page.goto(BASE + "/inbox/newsletter", wait_until="networkidle")
    wait(page, f"text={NO_DIRECTOR}", "the no-Director sentence")
    shot(page, "cockpit-06-no-director-NOT-GATEWAY-DATA")
    context.close()


def main():
    if not (GATEWAY_BIN / "CcDirector.Gateway.dll").exists():
        raise SystemExit(f"build the Gateway first: {GATEWAY_BIN} has no CcDirector.Gateway.dll")
    for dist, name in ((COCKPIT_DIST, "c"), (MOBILE_DIST, "mobile")):
        if not (dist / "index.html").exists():
            raise SystemExit(f"build {dist.parent.name} first: {dist} has no index.html")
        target = GATEWAY_BIN / "wwwroot" / name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(dist, target)
        log(f"{dist.parent.name} copied to {target}")

    root = tempfile.mkdtemp(prefix="inbox-rig-")
    config = Path(root) / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    (config / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    # Two owner devices: the Gateway imports this file once, on its first start, into its own database.
    issued = "2026-11-05T08:00:00Z"
    (config / "director" / "devices.json").write_text(json.dumps([
        {"deviceId": "rig-phone", "machineName": "Rig phone", "deviceKey": PHONE_KEY, "issuedAtUtc": issued,
         "status": "active", "platform": "android", "deviceType": "phone"},
        {"deviceId": "rig-browser", "machineName": "Rig browser", "deviceKey": BROWSER_KEY, "issuedAtUtc": issued,
         "status": "active", "platform": "windows", "deviceType": "browser"},
    ]), encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=root, CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1")
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    proc = subprocess.Popen(["dotnet", str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env)
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        wait_until_ready(token)
        seed(token, root)

        for name, method, path, key in (
            ("factory-inbox-list", "GET", "/gateway/factory/inbox", BROWSER_KEY),
            ("managed-marketing-thread", "GET", "/gateway/factory/managed-marketing/inbox", BROWSER_KEY),
            ("managed-marketing-summary", "GET", "/gateway/factory/managed-marketing/inbox/summary", BROWSER_KEY),
            ("booking-thread", "GET", "/gateway/factory/booking/inbox", BROWSER_KEY),
        ):
            status, answer = request(method, path, key)
            keep(name, method, path, status, answer)
        for name, factory, key in (("send-to-archived-factory", "old-shop", PHONE_KEY),
                                   ("send-with-the-machine-token", "booking", token)):
            path = f"/gateway/factory/{factory}/inbox"
            status, answer = request("POST", path, key, {"text": "A test that must be refused."})
            keep(name, "POST", path, status, answer)

        with sync_playwright() as p:
            browser = p.chromium.launch(channel="chrome", headless=True, args=[
                "--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"])
            try:
                phone(browser)
                cockpit(browser)
            finally:
                browser.close()

        status, answer = request("GET", "/gateway/factory/managed-marketing/inbox", BROWSER_KEY)
        keep("managed-marketing-thread-after-the-run", "GET", "/gateway/factory/managed-marketing/inbox", status, answer)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log("Gateway stopped")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
