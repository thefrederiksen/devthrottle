"""Screenshots and real Gateway answers for the Your inbox screens (Factory Inbox mission, step C), from a throwaway local
Gateway, driven with browser-harness.

Run from the repository root, in the foreground, after building the Gateway (Debug), the Cockpit and the phone app:

    ~/.dotnet/dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    (cd apps/cockpit && npm run build) && (cd apps/mobile && npm run build)
    python3 docs/qa/factory-inbox/your-inbox-screens/capture.py

It needs Google Chrome and browser-harness. What it does, and nothing else:
  1. Copies the Cockpit and phone builds into the Gateway's wwwroot/c and wwwroot/mobile, where the Gateway serves them.
  2. Starts the Gateway on port 7993 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it touches neither the owner's Gateway nor any tailnet setting. Its config.json switches
     the factory area on, and its devices file holds two owner devices - a phone and a browser.
  3. Registers two factories, Booking and Old Shop, archives Old Shop, and leaves one typed message in Booking from the
     phone through the real route.
  4. Writes the Your inbox messages, and the history rows of the sessions that sent them, straight into the scratch
     database, as the Your inbox store and the session recorder write them. A message reaches Your inbox from a session's
     own key (cc-devthrottle inbox send) or from a Boss's decision made with its session key, and only a Director mints a
     session key, which a scratch Gateway does not have. Every word on screen is still the Gateway's own fold of those
     rows, and EVERY OWNER ACTION - list, filter, read, archive, reply, the choices after a session ended, and each
     refusal - goes through the Gateway's real routes from the browser.
  5. Starts its own headless Chrome (a scratch profile, its own debugging port) and drives it with browser-harness
     under its own name, so it never touches the owner's browser; takes the screenshots into this folder at phone width
     (390) and Cockpit width (1366).
  6. Writes every Gateway answer the report quotes into answers.json, exactly as the Gateway sent it.
  7. Stops the browser and the Gateway it started. The scratch folders are left for inspection, not in the repository.
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
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
PORT = 7993
CHROME_PORT = 9347
BASE = f"http://127.0.0.1:{PORT}"
DOTNET = str(Path.home() / ".dotnet" / "dotnet")
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
MOBILE_DIST = REPO / "apps" / "mobile" / "dist"
PHONE_KEY = "dk_rig_phone_" + "3" * 32
BROWSER_KEY = "dk_rig_browser_" + "4" * 32

# The sessions that wrote to Your inbox.
BOSS = "b0550000-0000-4000-8000-0000000000b1"          # Booking's Boss, ended (its decision is the message)
OLD_BOSS = "b0550000-0000-4000-8000-0000000000b2"      # Old Shop's Boss, ended; Old Shop is archived
DEVELOPER = "d3500000-0000-4000-8000-000000000412"     # running: no ending on its history row
WRITER = "e4d00000-0000-4000-8000-000000000250"        # ended, in no factory
SEAT = "5ea70000-0000-4000-8000-000000000302"          # ended, a seat of Booking
DIRECTOR = "rig-director-1"

ANSWERS = {}
NOW = datetime.now(timezone.utc).replace(microsecond=0, tzinfo=None)


def log(msg):
    print(f"[your-inbox-capture] {msg}", flush=True)


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


def keep(name, method, path, key, body=None):
    status, answer = request(method, path, key, body)
    ANSWERS[name] = {"request": f"{method} {path}", "status": status, "body": answer}
    log(f"answer kept: {name} -> HTTP {status}")
    return status, answer


def wait_for_gateway(proc):
    for _ in range(180):
        if proc.poll() is not None:
            raise SystemExit(f"the Gateway exited with code {proc.returncode} before it answered")
        try:
            urllib.request.urlopen(BASE + "/", timeout=2)
            return
        except urllib.error.HTTPError:
            return
        except OSError:
            time.sleep(1)
    raise SystemExit(f"the Gateway did not answer on port {PORT} within three minutes")


def find_token(root):
    for path in Path(root).rglob("gateway-token.txt"):
        token = path.read_text(encoding="utf-8").strip()
        if token:
            return token
    raise SystemExit(f"no gateway-token.txt under {root}: the Gateway did not write its token")


def wait_until_ready(token):
    for _ in range(180):
        status, _ = request("GET", "/cron/jobs", token)
        if 200 <= status < 300:
            return
        if status not in (0, 503):
            raise SystemExit(f"GET /cron/jobs answered HTTP {status} while waiting for readiness")
        time.sleep(1)
    raise SystemExit("the Gateway was not ready within three minutes")


def register(token, factory, title):
    call("PUT", "/gateway/factory/registry", token, {
        "factory": factory, "title": title, "folder": "/home/rig/" + factory, "computer": "RIG_MACHINE",
        "bossSeat": "boss", "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"}],
    })


def database(root):
    for path in Path(root).rglob("*.db"):
        try:
            with sqlite3.connect(path) as db:
                if db.execute("select 1 from sqlite_master where name='your_inbox_messages'").fetchone():
                    return path
        except sqlite3.DatabaseError:
            continue
    raise SystemExit(f"no Gateway database with the Your inbox tables under {root}")


def stamp(minutes_ago):
    return (NOW - timedelta(minutes=minutes_ago)).strftime("%Y-%m-%d %H:%M:%S")


class Rows:
    """Rows written as the Your inbox store and the session recorder write them (see the module docstring, step 4)."""

    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.row_factory = sqlite3.Row
        row = self.db.execute("select tenant_id, Id from factory_inbox_messages limit 1").fetchone()
        if row is None:
            raise SystemExit("no factory inbox message to read the account from: the phone's send did not land")
        self.tenant = row["tenant_id"]
        self.upper = row["Id"] == row["Id"].upper()

    def _guid(self):
        g = str(uuid.uuid4())
        return g.upper() if self.upper else g

    def _insert(self, table, values):
        """Insert with every NOT NULL column the row does not name set to its type's empty value."""
        for col in self.db.execute(f"pragma table_info({table})").fetchall():
            name, kind, notnull, default = col["name"], (col["type"] or "").upper(), col["notnull"], col["dflt_value"]
            if name in values or not notnull or default is not None:
                continue
            values[name] = 0 if any(k in kind for k in ("INT", "REAL", "NUM")) else ""
        cols = ", ".join(values)
        marks = ", ".join("?" for _ in values)
        self.db.execute(f"insert into {table} ({cols}) values ({marks})", list(values.values()))

    def session(self, sid, number, name, ended, factory=None, seat=None, repo="/home/rig/repos/website"):
        self._insert("session_history", {
            "tenant_id": self.tenant, "SessionId": sid, "SessionNumber": number, "SessionName": name,
            "DirectorId": DIRECTOR, "RepoPath": repo, "RepoName": Path(repo).name, "AgentKind": "ClaudeCode",
            "Factory": factory, "FactorySeat": seat, "StartedAtUtc": stamp(600), "LastSeenUtc": stamp(30),
            "EndingKind": "exited" if ended else None, "EndingLabel": "Exited" if ended else None,
            "EndedAtUtc": stamp(30) if ended else None, "SummaryIsPartial": 0, "SummaryAttempts": 0,
        })

    def message(self, subject, body, minutes_ago, from_kind, session=None, number=None, name=None, factory=None, seat=None,
                state="unread"):
        mid = self._guid()
        self._insert("your_inbox_messages", {
            "Id": mid, "tenant_id": self.tenant, "Subject": subject, "Body": body, "FromKind": from_kind,
            "SessionId": session, "SessionNumber": number, "SessionName": name, "Factory": factory, "Seat": seat,
            "State": state, "SentAtUtc": stamp(minutes_ago),
            "ReadAtUtc": stamp(minutes_ago - 5) if state != "unread" else None,
            "ArchivedAtUtc": stamp(minutes_ago - 5) if state == "archived" else None,
        })
        return mid

    def commit(self):
        self.db.commit()


def seed(token, root):
    register(token, "booking", "Booking")
    register(token, "old-shop", "Old Shop")
    # A typed message left in Booking from the phone, through the real route: the Boss's decline below is about it.
    status, answer = request("POST", "/gateway/factory/booking/inbox", PHONE_KEY,
                             {"text": "Move Friday's shoot to Monday.", "note": "none"})
    if status != 201:
        raise SystemExit(f"leaving a message in booking failed: HTTP {status} {answer}")
    call("POST", "/gateway/factories/old-shop/archive", BROWSER_KEY, {"schedules": []})

    rows = Rows(database(root))
    rows.session(BOSS, 301, "Booking - Boss", ended=True, factory="booking", seat="boss", repo="/home/rig/booking")
    rows.session(OLD_BOSS, 288, "Old Shop - Boss", ended=True, factory="old-shop", seat="boss", repo="/home/rig/old-shop")
    rows.session(DEVELOPER, 412, "Release - Developer", ended=False, repo="/home/rig/repos/devthrottle")
    rows.session(WRITER, 250, "Website - Writer", ended=True, repo="/home/rig/repos/website")
    rows.session(SEAT, 302, "Booking - Developer", ended=True, factory="booking", seat="developer", repo="/home/rig/booking")

    ids = {}
    # Oldest first, so the list (newest first) reads top-down in this order reversed.
    ids["archived"] = rows.message(
        "Weekly cost report sent", "The weekly cost report for the fleet is in your reports.", 3000, "session",
        session=DEVELOPER, number=412, name="Release - Developer", state="archived")
    ids["read"] = rows.message(
        "Backups checked", "Every Director's backup ran last night; nothing failed.", 1500, "session",
        session=WRITER, number=250, name="Website - Writer", state="read")
    ids["old-shop"] = rows.message(
        "Pushed back: reopen the shop for the holidays",
        "Pushed back. The Boss's reason:\nThe shop's supplier contract ended in October.\n\n"
        "Your message (sent " + stamp(240)[:16] + " UTC):\nReopen the shop for the holidays.",
        200, "factory", session=OLD_BOSS, factory="old-shop", seat="boss")
    ids["seat"] = rows.message(
        "Pricing page copy needs your call",
        "Two versions of the pricing page headline are in the folder. Which one should go live?", 90, "session",
        session=SEAT, number=302, name="Booking - Developer", factory="booking", seat="developer")
    ids["ended"] = rows.message(
        "Contact form fixed - one question",
        "The contact form sends again. Should the confirmation email come from you or from the team address?", 60,
        "session", session=WRITER, number=250, name="Website - Writer")
    ids["running"] = rows.message(
        "v2.25 notes ready for your read",
        "The release notes for v2.25 are drafted. Read them when you can; I will wait for your word before tagging.", 30,
        "session", session=DEVELOPER, number=412, name="Release - Developer")
    ids["declined"] = rows.message(
        "Declined: Move Friday's shoot to Monday.",
        "Declined. The Boss's reason:\nThe client's contract fixes Friday; moving it breaks the contract.\n\n"
        "Your message (sent " + stamp(20)[:16] + " UTC):\nMove Friday's shoot to Monday.",
        10, "factory", session=BOSS, factory="booking", seat="boss")
    rows.commit()
    log(f"seeded: two factories (one archived), five sessions, {len(ids)} Your inbox messages")
    return ids


# ------------------------------------------------------------------------------------------------------------- browser

def start_chrome(profile):
    proc = subprocess.Popen([
        "google-chrome", "--headless=new", f"--remote-debugging-port={CHROME_PORT}", f"--user-data-dir={profile}",
        "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--use-fake-ui-for-media-stream",
        "--use-fake-device-for-media-stream", "about:blank",
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        # Chrome puts its single-instance socket under TMPDIR, and a long folder name there makes the socket path too long
        # to open; its profile is still the scratch folder named above.
        env={k: v for k, v in os.environ.items() if k != "TMPDIR"})
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{CHROME_PORT}/json/version", timeout=2)
            return proc
        except OSError:
            time.sleep(0.5)
    proc.kill()
    raise SystemExit("the scratch Chrome did not open its debugging port")


HARNESS_PRELUDE = r'''
import base64, json, time
BASE = %(base)r
OUT = %(out)r

VIEW = {}

def size(width, height, mobile):
    VIEW.update(width=width, height=height, mobile=mobile)
    cdp("Emulation.setDeviceMetricsOverride", width=width, height=height, deviceScaleFactor=2 if mobile else 1, mobile=mobile)

def go(path):
    cdp("Page.navigate", url=BASE + path)
    wait_for_load()

def until(expr, what, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        if js(expr):
            return
        time.sleep(0.25)
    shot("debug-" + what.replace(" ", "-"))
    raise SystemExit("did not appear: " + what)

def click(selector, text=None):
    ok = js("""(() => { const els = [...document.querySelectorAll(%%s)];
        const el = els.find(e => %%s === null || e.textContent.includes(%%s));
        if (!el) return false; el.scrollIntoView({block: 'center'}); el.click(); return true; })()""" %% (
        json.dumps(selector), json.dumps(text), json.dumps(text)))
    if not ok:
        raise SystemExit("nothing to click: " + selector + " " + str(text))
    time.sleep(0.3)

def fill(selector, value):
    js("""(() => { const el = document.querySelector(%%s);
        const set = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set;
        set.call(el, %%s); el.dispatchEvent(new Event('input', {bubbles: true})); })()""" %% (json.dumps(selector), json.dumps(value)))
    time.sleep(0.2)

def shot(name):
    # The whole page: the viewport is stretched to the page's height for the picture and put back after.
    h = int(js("Math.max(document.documentElement.scrollHeight, document.body.scrollHeight)"))
    w, mobile = VIEW["width"], VIEW["mobile"]
    full = max(h, VIEW["height"])
    cdp("Emulation.setDeviceMetricsOverride", width=w, height=full, deviceScaleFactor=2 if mobile else 1, mobile=mobile)
    # Chrome paints only the tab in front; a later harness run may be driving another one.
    cdp("Page.bringToFront")
    time.sleep(0.4)
    data = cdp("Page.captureScreenshot", _response_timeout=60, format="png")["data"]
    open(OUT + "/" + name + ".png", "wb").write(base64.b64decode(data))
    size(w, VIEW["height"], mobile)
    print("shot", name, flush=True)

def sign_in(key):
    go("/mobile/")
    cdp("Network.enable")
    cdp("Network.setCookie", name="cc-gateway-token", value=key, url=BASE)
    js("""(() => {
        localStorage.clear();
        localStorage.setItem('cc.accounts', JSON.stringify([
          { id: 'rig', label: 'Rig', email: null, deviceKey: %%s, installId: 'rig' }]));
        localStorage.setItem('cc.activeAccount', 'rig');
        localStorage.setItem('cc.deviceKey', %%s);
    })()""" %% (json.dumps(key), json.dumps(key)))

def has(selector, text=None):
    return """(() => [...document.querySelectorAll(%%s)].some(e => %%s === null || e.textContent.includes(%%s)))()""" %% (
        json.dumps(selector), json.dumps(text), json.dumps(text))
'''

PHONE = r'''
ensure_real_tab()
cdp("Page.bringToFront")
size(390, 844, True)
sign_in(%(phone_key)r)
IDS = %(ids)r

# The menu: Inbox with the Gateway's unread count beside it.
go("/mobile/")
until(has("button[aria-label='Open menu']"), "the menu button")
click("button[aria-label='Open menu']")
until(has("[data-testid=inbox-count]"), "the menu count")
shot("phone-01-menu-inbox-unread-count")

# The list: Your inbox, with the filters and the unread rows in bold.
go("/mobile/inbox?tab=yours")
until(has("[data-testid=yi-row]"), "the Your inbox rows")
time.sleep(0.5)
shot("phone-02-your-inbox-list-unread")

# Read a factory's message: opening marks it read.
go("/mobile/inbox?tab=yours&message=" + IDS["declined"])
until(has("[data-testid=yi-body]"), "the declined message")
until(has("[data-testid=yi-state]", "Read"), "the message marked read")
shot("phone-03-read-a-factory-message")

# Reply to a factory: into Booking's Factory inbox.
click(".yi-action-reply")
fill(".yi-reply textarea", "Then keep Friday, and tell the client we confirmed.")
shot("phone-04a-reply-to-a-factory-written")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-reply-status]"), "the reply's destination")
shot("phone-04b-reply-to-a-factory-sent")

# Reply to a running session: into its own inbox.
go("/mobile/inbox?tab=yours&message=" + IDS["running"])
until(has("[data-testid=yi-body]"), "the running session's message")
click(".yi-action-reply")
fill(".yi-reply textarea", "Read them - tag it.")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-reply-status]"), "the reply to the running session")
shot("phone-05-reply-to-a-running-session")

# Reply to an ended session that was a factory's seat: exactly the Gateway's two choices.
go("/mobile/inbox?tab=yours&message=" + IDS["seat"])
until(has("[data-testid=yi-body]"), "the ended seat's message")
click(".yi-action-reply")
fill(".yi-reply textarea", "Use the shorter headline.")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-offers]"), "the two choices")
shot("phone-06a-reply-to-an-ended-session-two-choices")
click("[data-testid=yi-offer]", "Leave this reply in the Factory inbox")
until(has("[data-testid=yi-reply-status]"), "the reply left in the factory")
shot("phone-06b-ended-session-reply-left-in-its-factory")

# Archive.
go("/mobile/inbox?tab=yours&message=" + IDS["ended"])
until(has("[data-testid=yi-body]"), "the ended session's message")
click("[data-testid=yi-action]", "Archive")
until(has("[data-testid=yi-state]", "Archived"), "the message archived")
shot("phone-07a-archived")
go("/mobile/inbox?tab=yours")
until(has("[data-testid=yi-filter]"), "the filters")
click("[data-testid=yi-filter]", "Archived")
until(has("[data-testid=yi-row]", "Contact form fixed"), "the archived view")
shot("phone-07b-archived-view")

# Refused: a reply to a message from an archived factory, in the Gateway's words.
go("/mobile/inbox?tab=yours&message=" + IDS["old-shop"])
until(has("[data-testid=yi-body]"), "the Old Shop message")
click(".yi-action-reply")
fill(".yi-reply textarea", "Then close it for good.")
click(".yi-reply .fi-send")
until(has(".yi-reply .fi-composer-error"), "the refusal")
shot("phone-08-refused-archived-factory")

# Read state: the list with every message read.
go("/mobile/inbox?tab=yours")
until(has("[data-testid=yi-row]"), "the list after reading")
time.sleep(0.5)
shot("phone-09-list-after-reading")
'''

PHONE_EMPTY = r'''
ensure_real_tab()
cdp("Page.bringToFront")
size(390, 844, True)
sign_in(%(phone_key)r)
go("/mobile/inbox?tab=yours")
until(has("[data-testid=yi-empty]"), "the empty sentence")
time.sleep(0.5)
shot("phone-10-empty")
'''

COCKPIT = r'''
ensure_real_tab()
cdp("Page.bringToFront")
size(1366, 900, False)
sign_in(%(browser_key)r)
IDS = %(ids)r

go("/inbox?tab=yours")
until(has("[data-testid=yi-row]"), "the Cockpit list")
until(has(".nav-badge"), "the rail count")
time.sleep(0.5)
shot("cockpit-01-list-unread-and-rail-count")

click("[data-testid=yi-filter]", "Unread")
until(has("[data-testid=yi-filter][aria-pressed=true]", "Unread"), "the Unread filter")
time.sleep(0.5)
shot("cockpit-02-unread-filter")

go("/inbox?tab=yours&message=" + IDS["declined"])
until(has("[data-testid=inbox-reading-pane] [data-testid=yi-body]"), "the reading pane")
until(has("[data-testid=yi-state]", "Read"), "the message marked read")
time.sleep(0.5)
shot("cockpit-03-read-in-the-reading-pane")

click(".yi-action-reply")
fill(".yi-reply textarea", "Agreed - keep Friday.")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-reply-status]"), "the reply's destination")
shot("cockpit-04-reply-to-a-factory")

go("/inbox?tab=yours&message=" + IDS["running"])
until(has("[data-testid=yi-body]"), "the running session's message")
click(".yi-action-reply")
fill(".yi-reply textarea", "One more read from me tonight.")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-reply-status]"), "the reply to the running session")
shot("cockpit-05-reply-to-a-running-session")

go("/inbox?tab=yours&message=" + IDS["seat"])
until(has("[data-testid=yi-body]"), "the ended seat's message")
click(".yi-action-reply")
fill(".yi-reply textarea", "And keep the price in the headline.")
click(".yi-reply .fi-send")
until(has("[data-testid=yi-offers]"), "the two choices")
shot("cockpit-06-reply-to-an-ended-session-two-choices")

go("/inbox?tab=yours&message=" + IDS["running"])
until(has("[data-testid=yi-body]"), "the running session's message")
click("[data-testid=yi-action]", "Archive")
until(has("[data-testid=yi-state]", "Archived"), "the message archived")
time.sleep(0.5)
shot("cockpit-07-archived")

go("/inbox?tab=yours&message=" + IDS["old-shop"])
until(has("[data-testid=yi-body]"), "the Old Shop message")
click(".yi-action-reply")
fill(".yi-reply textarea", "Close it for good.")
click(".yi-reply .fi-send")
until(has(".yi-reply .fi-composer-error"), "the refusal")
shot("cockpit-08-refused-archived-factory")
'''

COCKPIT_EMPTY = r'''
ensure_real_tab()
cdp("Page.bringToFront")
size(1366, 900, False)
sign_in(%(browser_key)r)
go("/inbox?tab=yours")
until(has("[data-testid=yi-empty]"), "the empty sentence")
time.sleep(0.5)
shot("cockpit-09-empty")
'''


def harness(script, values):
    env = dict(os.environ, BU_NAME="your-inbox-qa", BU_CDP_URL=f"http://127.0.0.1:{CHROME_PORT}", BH_RECORD="0")
    text = (HARNESS_PRELUDE % {"base": BASE, "out": str(OUT)}) + (script % values)
    result = subprocess.run(["browser-harness"], input=text, text=True, env=env, capture_output=True, timeout=600)
    print(result.stdout, end="", flush=True)
    if result.returncode != 0:
        print(result.stderr, flush=True)
        raise SystemExit(f"browser-harness failed with exit code {result.returncode}")


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

    root = tempfile.mkdtemp(prefix="your-inbox-rig-")
    profile = tempfile.mkdtemp(prefix="your-inbox-chrome-")
    config = Path(root) / "config"
    (config / "director").mkdir(parents=True, exist_ok=True)
    (config / "config.json").write_text(json.dumps({"factoryAgents": {"enabled": True}}), encoding="utf-8")
    issued = "2026-11-06T08:00:00Z"
    (config / "director" / "devices.json").write_text(json.dumps([
        {"deviceId": "rig-phone", "machineName": "Rig phone", "deviceKey": PHONE_KEY, "issuedAtUtc": issued,
         "status": "active", "platform": "android", "deviceType": "phone"},
        {"deviceId": "rig-browser", "machineName": "Rig browser", "deviceKey": BROWSER_KEY, "issuedAtUtc": issued,
         "status": "active", "platform": "linux", "deviceType": "browser"},
    ]), encoding="utf-8")
    env = dict(os.environ, CC_DIRECTOR_ROOT=root, CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1",
               LOCALAPPDATA=root)
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    gateway_log = open(Path(root) / "gateway-console.log", "w", encoding="utf-8")
    proc = subprocess.Popen([DOTNET, str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env, stdout=gateway_log, stderr=subprocess.STDOUT)
    chrome = None
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        wait_until_ready(token)
        ids = seed(token, root)

        keep("list-inbox", "GET", "/gateway/inbox", BROWSER_KEY)
        keep("count", "GET", "/gateway/inbox/count", PHONE_KEY)
        keep("read-the-machine-token-refused", "GET", "/gateway/inbox", token)

        chrome = start_chrome(profile)
        values = {"phone_key": PHONE_KEY, "browser_key": BROWSER_KEY, "ids": ids}
        harness(PHONE, values)
        # The phone pass read every unread message. For the Cockpit's pictures four are marked unread again through the
        # real route - the owner's own "Mark unread" - so the rail's count and the Unread filter have something to show.
        for name in ("declined", "running", "seat", "old-shop"):
            call("POST", f"/gateway/inbox/{ids[name]}/state", BROWSER_KEY, {"state": "unread"})
        harness(COCKPIT, values)

        keep("list-after-the-run", "GET", "/gateway/inbox", BROWSER_KEY)
        keep("list-archived-after-the-run", "GET", "/gateway/inbox?view=archived", BROWSER_KEY)
        keep("declined-message-with-its-replies", "GET", f"/gateway/inbox/{ids['declined']}", BROWSER_KEY)
        keep("ended-seat-message-with-its-replies", "GET", f"/gateway/inbox/{ids['seat']}", BROWSER_KEY)
        keep("ended-session-reply-offers", "POST", f"/gateway/inbox/{ids['seat']}/reply", BROWSER_KEY, {"text": "A quoted answer."})
        keep("archived-factory-reply-refused", "POST", f"/gateway/inbox/{ids['old-shop']}/reply", BROWSER_KEY, {"text": "A quoted answer."})
        keep("booking-factory-inbox-after-the-replies", "GET", "/gateway/factory/booking/inbox", BROWSER_KEY)

        # THE EMPTY STATE: every message archived, through the real route, then the inbox view.
        for name, mid in ids.items():
            request("POST", f"/gateway/inbox/{mid}/state", BROWSER_KEY, {"state": "archived"})
        keep("list-empty", "GET", "/gateway/inbox", BROWSER_KEY)
        keep("count-empty", "GET", "/gateway/inbox/count", BROWSER_KEY)
        harness(PHONE_EMPTY, values)
        harness(COCKPIT_EMPTY, values)
    finally:
        if chrome is not None:
            chrome.terminate()
            try:
                chrome.wait(timeout=15)
            except subprocess.TimeoutExpired:
                chrome.kill()
            log("scratch Chrome stopped")
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        gateway_log.close()
        log(f"Gateway stopped; its console log is {Path(root) / 'gateway-console.log'}")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
