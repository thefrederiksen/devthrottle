"""Screenshots and real Gateway answers for the small defects the Factory Inbox end-to-end proof found (defects 5, 6
and 8 of docs/qa/factory-inbox/phase-6-end-to-end/README.md on the end-to-end-proof branch), from a throwaway local
Gateway, driven with browser-harness. Built on docs/qa/factory-inbox/your-inbox-screens/capture.py.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the phone app:

    ~/.dotnet/dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
    (cd apps/mobile && npm run build)
    python3 docs/qa/factory-inbox/proof-findings-polish/capture.py

What it does, and nothing else:
  1. Copies the phone build into the Gateway's wwwroot/mobile, where the Gateway serves it.
  2. Starts the Gateway on port 7994 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it touches neither the owner's Gateway nor any tailnet setting.
  3. Registers "Factory Inbox test" on the computer fi-inbox-test, where no Director runs, and "Old shop", which it
     archives. Leaves one typed message in Factory Inbox test from the phone through the real route, and waits for the
     Gateway's own wake to find no Director there (the 60-second gather window, then the look).
  4. Writes one Your inbox message with the long subject of the proof's step 4 straight into the scratch database, as
     the Your inbox store writes it (only a Director mints the session key that sends one; see the step C rig).
  5. Drives its own headless Chrome (scratch profile, its own debugging port) at phone width (390) and takes the
     pictures into this folder; writes the Gateway answers quoted in the README into answers.json, exactly as sent.
  6. Stops the browser and the Gateway it started.
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
PORT = 7994
CHROME_PORT = 9348
BASE = f"http://127.0.0.1:{PORT}"
DOTNET = str(Path.home() / ".dotnet" / "dotnet")
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
MOBILE_DIST = REPO / "apps" / "mobile" / "dist"
PHONE_KEY = "dk_rig_phone_" + "5" * 32
BROWSER_KEY = "dk_rig_browser_" + "6" * 32
BOSS = "b0550000-0000-4000-8000-0000000000c1"
DIRECTOR = "rig-director-1"
LONG_SUBJECT = ("Declined: Step 4 test: please email every customer on our list a 50 percent discount offer today, and pay "
                "for a newsletter tool with the company card to send it.")

ANSWERS = {}
NOW = datetime.now(timezone.utc).replace(microsecond=0, tzinfo=None)


def log(msg):
    print(f"[proof-findings-polish] {msg}", flush=True)


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
            "DirectorId": DIRECTOR, "RepoPath": repo, "RepoName": Path(repo).name, "AgentKind": "Codex",
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


def register(token, factory, title, computer):
    call("PUT", "/gateway/factory/registry", token, {
        "factory": factory, "title": title, "folder": "/home/rig/" + factory, "computer": computer,
        "bossSeat": "boss", "seats": [{"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"}],
    })


def seed(token, root):
    register(token, "factory-inbox-test", "Factory Inbox test", "fi-inbox-test")
    register(token, "old-shop", "Old shop", "fi-inbox-test")
    call("POST", "/gateway/factories/old-shop/archive", BROWSER_KEY, {"schedules": []})
    status, answer = request("POST", "/gateway/factory/factory-inbox-test/inbox", PHONE_KEY,
                             {"text": "Step 1 test: please have a short note written that lists three plain-English names "
                                      "we could give the notes folder of this test factory.", "note": "none"})
    if status != 201:
        raise SystemExit(f"leaving a message failed: HTTP {status} {answer}")

    rows = Rows(database(root))
    rows.session(BOSS, 170, "Factory Inbox test - Boss", ended=True, factory="factory-inbox-test", seat="boss",
                 repo="/home/rig/factory-inbox-test")
    declined = rows.message(
        LONG_SUBJECT,
        "Declined. The Boss's reason:\nOutside this factory's work: no external messages, no spending.\n\n"
        "Your message (sent " + stamp(20)[:16] + " UTC):\n" + LONG_SUBJECT[len("Declined: "):],
        10, "factory", session=BOSS, factory="factory-inbox-test", seat="boss")
    rows.commit()
    log("seeded: two factories (one archived), one waiting message, one Your inbox message with a long subject")
    return {"declined": declined}


def wait_for_the_reason():
    """The Gateway's wake finds no Director on fi-inbox-test after its gather window; until then the row has no reason."""
    for _ in range(240):
        _, answer = request("GET", "/gateway/factory/inbox", BROWSER_KEY)
        rows = {f["factory"]: f["row"] for f in (answer or {}).get("factories", [])}
        reason = rows.get("factory-inbox-test", {}).get("reasonLine")
        if reason:
            log(f"the row's reason: {reason}")
            return
        time.sleep(1)
    raise SystemExit("the wake did not record why the message waits within four minutes")


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

# Defect 6: the Factory inbox list row says why the message waits, in the Gateway's words.
go("/mobile/inbox")
until(has("[data-testid=fi-row] .fi-row-reason"), "the row's reason")
time.sleep(0.5)
shot("phone-01-factory-inbox-row-says-why-it-waits")

# Defect 5: a long Your inbox subject in the app bar; the bell stays inside the right edge.
go("/mobile/inbox?tab=yours&message=" + IDS["declined"])
until(has("[data-testid=yi-body]"), "the declined message")
time.sleep(0.5)
where = json.loads(js("""(() => { const b = document.querySelector('.app-bar .alerts-bell').getBoundingClientRect();
    const bar = document.querySelector('.app-bar').getBoundingClientRect();
    return JSON.stringify({bellLeft: b.left, bellRight: b.right, barRight: bar.right, viewport: window.innerWidth}); })()"""))
print("BELL", where, flush=True)
if where["bellRight"] > where["barRight"] or where["bellRight"] > where["viewport"]:
    shot("debug-bell-off-the-edge")
    raise SystemExit("the bell is off the right edge of the app bar: " + json.dumps(where))
shot("phone-02-long-subject-bell-in-place")
'''


def harness(script, values):
    env = dict(os.environ, BU_NAME="proof-findings-polish-qa", BU_CDP_URL=f"http://127.0.0.1:{CHROME_PORT}", BH_RECORD="0")
    text = (HARNESS_PRELUDE % {"base": BASE, "out": str(OUT)}) + (script % values)
    result = subprocess.run(["browser-harness"], input=text, text=True, env=env, capture_output=True, timeout=600)
    print(result.stdout, end="", flush=True)
    if result.returncode != 0:
        print(result.stderr, flush=True)
        raise SystemExit(f"browser-harness failed with exit code {result.returncode}")


def main():
    if not (GATEWAY_BIN / "CcDirector.Gateway.dll").exists():
        raise SystemExit(f"build the Gateway first: {GATEWAY_BIN} has no CcDirector.Gateway.dll")
    if not (MOBILE_DIST / "index.html").exists():
        raise SystemExit(f"build the phone app first: {MOBILE_DIST} has no index.html")
    target = GATEWAY_BIN / "wwwroot" / "mobile"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(MOBILE_DIST, target)
    log(f"phone app copied to {target}")

    root = tempfile.mkdtemp(prefix="fi-polish-rig-")
    profile = tempfile.mkdtemp(prefix="fi-polish-chrome-")
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
        wait_for_the_reason()

        keep("factory-inbox-list", "GET", "/gateway/factory/inbox", BROWSER_KEY)
        keep("archived-factory-send-refused", "POST", "/gateway/factory/old-shop/inbox", PHONE_KEY,
             {"text": "Reopen the shop.", "note": "none"})

        chrome = start_chrome(profile)
        harness(PHONE, {"phone_key": PHONE_KEY, "ids": ids})
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
        said = [p for p in (Path(root) / "logs").rglob("*.log")
                if "CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve auto-provisioning disabled" in p.read_text(encoding="utf-8", errors="replace")]
        if not said:
            raise SystemExit("the Gateway's log does not say Tailscale Serve was disabled: do not trust this run")
        log(f"the Gateway's log says Tailscale Serve was disabled: {said[0]}")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
