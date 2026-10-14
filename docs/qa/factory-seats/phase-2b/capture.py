"""Screenshots and real Gateway answers for Factory Seats, phase 2b (the brief panel), from a throwaway local Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    uv run --no-project --with playwright python docs/qa/factory-seats/phase-2b/capture.py

It needs Playwright for Python, Google Chrome and Node (for the development proxy check).

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, which is where the Gateway serves it from.
  2. Starts the Gateway on port 7992 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder) and
     CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's Gateway nor the owner's factories.
  3. Registers one demonstration factory with the Gateway's own machine token, which is how a computer registers one.
  4. ENROLLS ONE BROWSER DEVICE, the way the Gateway stores a device it enrolled itself: a row in the scratch
     Gateway's own device_credentials table holding the SHA-256 of a key this script made up. That key is what "a
     person" is to this Gateway - a device key whose device type is "browser". The machine token is NOT a person: the
     brief's routes refuse a write made with it, and that refusal is one of the answers kept here.
  5. Opens the Cockpit in headless Chrome (Playwright as a library: a repeatable script, not interactive work),
     signed in with THAT DEVICE KEY and nothing else - it is the browser's cookie and the key the app sends as
     "Authorization: Bearer". Every save, publish and bring back in the pictures is the app's own request to the
     brief's own routes under /factory-briefs, made with that key.
  6. Takes the screenshots into this folder at 1280 and at 375 pixels wide. The flow writes versions, so each width
     works on a seat of its own: the Scout at 1280, the Writer at 375. At 375 the Cockpit's menu is collapsed.
  7. Writes every Gateway answer the report quotes into answers.json, exactly as the Gateway sent it.
  8. Starts the Cockpit's development server pointed at the scratch Gateway and reads a brief THROUGH it, to show the
     /factory-briefs line of the development proxy works, then stops it.
  9. Stops the Gateway it started. The scratch data folder is left for inspection and is not in the repository.

WHAT IS NOT A SESSION HERE. The scratch Gateway has no Director and so no session and no session key. The "other
writer" in the stale save, and the twenty waiting drafts, are written with the same person's device key through the
same route a session uses. What a SESSION is refused (publishing, and a twenty-first draft) is proven by the unit
tests, which the report names; it is not in these pictures.
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
PORT = 7992
BASE = f"http://127.0.0.1:{PORT}"
DEV_PORT = 5197
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "brief-demo"
DEVICE_ID = "qa-cockpit-browser"

FIRST = (
    "You are the {name} of Brief Demo.\n\n"
    "Every morning, read the pages we published this week and check each link.\n"
    "Write what you found to the activity record. Ask the boss before you change a page."
)
SECOND = FIRST + "\n\nIn the evening, check the three newest pages a second time."

# Every Gateway answer the report quotes, by name, exactly as sent.
ANSWERS = {}


def log(msg):
    print(f"[brief-capture] {msg}", flush=True)


def request(method, path, key, body=None, base=BASE):
    """One call. Returns (status, parsed body or text); never raises on an HTTP status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key}")
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, text = res.status, res.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        status, text = exc.code, exc.read().decode("utf-8", "replace")
    except OSError:
        return 0, None  # nothing is listening there yet
    try:
        return status, (json.loads(text) if text else None)
    except json.JSONDecodeError:
        return status, text


def call(method, path, key, body=None):
    """A call that must succeed: anything but a 2XX stops the run with the Gateway's answer."""
    status, answer = request(method, path, key, body)
    if not 200 <= status < 300:
        raise SystemExit(f"{method} {path} failed: HTTP {status} {answer}")
    return answer


def keep(name, method, path, status, answer, sent=None, credential=None):
    kept = {"request": f"{method} {path}", "status": status, "body": answer}
    if sent is not None:
        kept["sent"] = sent
    if credential is not None:
        kept["credential"] = credential
    ANSWERS[name] = kept
    log(f"answer kept: {name} -> HTTP {status}")


def expect(status, wanted, what, answer):
    """A refusal is evidence only when it is the refusal that was meant: any other status stops the run."""
    if status != wanted:
        raise SystemExit(f"{what}: expected HTTP {wanted}, the Gateway answered {status} {answer}")


def wait_for_gateway(proc):
    for _ in range(120):
        if proc.poll() is not None:
            raise SystemExit(f"the Gateway exited with code {proc.returncode} before it answered")
        try:
            urllib.request.urlopen(BASE + "/", timeout=2)
            return
        except urllib.error.HTTPError:
            return  # it answered, even if with a refusal
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
    # The Gateway listens before its database is open and answers 503 "starting" until then.
    for _ in range(120):
        status, _ = request("GET", "/cron/jobs", token)
        if 200 <= status < 300:
            return
        if status not in (0, 503):
            raise SystemExit(f"GET /cron/jobs answered HTTP {status} while waiting for readiness")
        time.sleep(1)
    raise SystemExit("the Gateway was not ready within two minutes")


def enroll_browser(root, key):
    """Write the one device row this run uses, the way the Gateway stores a device it enrolled (a SHA-256 of the key)."""
    found = list(Path(root).rglob("gateway.db"))
    if len(found) != 1:
        raise SystemExit(f"expected one gateway.db under the scratch root, found {found}")
    db = sqlite3.connect(found[0])
    try:
        cols = {row[1] for row in db.execute("PRAGMA table_info(device_credentials)")}
        if not cols:
            raise SystemExit("the scratch gateway.db has no device_credentials table")
        tenant_col = "tenant_id" if "tenant_id" in cols else "TenantId"
        row = {
            "DeviceId": DEVICE_ID,
            "MachineName": "RIG_MACHINE",
            "DeviceKeyHash": hashlib.sha256(key.encode()).hexdigest(),
            "KeyPrefix": key[:8],
            "KeyLast4": key[-4:],
            "IssuedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
            "Status": "active",
            "Platform": "linux",
            "DeviceType": "browser",
            tenant_col: "local",
        }
        missing = [k for k in row if k not in cols]
        if missing:
            raise SystemExit(f"device_credentials has no column(s) {missing}; its columns are {sorted(cols)}")
        db.execute(f"INSERT INTO device_credentials ({', '.join(row)}) VALUES ({', '.join('?' for _ in row)})", list(row.values()))
        db.commit()
    finally:
        db.close()
    log(f"enrolled the browser device '{DEVICE_ID}' in {found[0]}")


def seed(token):
    """One factory with a boss, a Scout and a Writer. No seat has a brief on the Gateway."""
    call("PUT", "/gateway/factory/registry", token, {
        "factory": FACTORY,
        "title": "Brief Demo",
        "folder": "/home/rig/" + FACTORY,
        "computer": "RIG_MACHINE",
        "bossSeat": "boss",
        "goalText": "Publish one useful page a week and keep every page we publish correct.",
        "goalFile": "GOAL.md",
        "goalApprovedOn": "2026-10-04",
        "seats": [
            {"id": "boss", "name": "Boss", "role": "Boss", "briefFile": "seats/boss.yaml"},
            {"id": "scout", "name": "Scout", "role": "Scout", "briefFile": "seats/scout.yaml"},
            {"id": "writer", "name": "Writer", "role": "Writer", "briefFile": "seats/writer.yaml"},
        ],
    })
    log(f"registered {FACTORY}")


def shot(page, name, suffix):
    page.screenshot(path=str(OUT / f"{name}-{suffix}.png"), full_page=True)
    log(f"{name}-{suffix}.png")


def wait(page, selector, what, state="visible"):
    try:
        page.wait_for_selector(selector, timeout=30000, state=state)
    except Exception:
        debug = Path(tempfile.gettempdir()) / "brief-rig-debug.png"
        page.screenshot(path=str(debug), full_page=True)
        raise SystemExit(f"{what} did not appear ({selector}); the page is in {debug}")


def confirm(page, what):
    """Press the confirm's own button - the primary one - whatever the Gateway called it."""
    wait(page, "[role=alertdialog]", what)
    page.click("[role=alertdialog] .ui-btn-primary")


def brief_of(key, seat):
    status, answer = request("GET", f"/gateway/factories/{FACTORY}/seats/{seat}", key)
    if status != 200:
        raise SystemExit(f"the seat page of {seat} answered HTTP {status} {answer}")
    return answer["brief"]


def flow(page, key, token, seat, name, suffix):
    """The whole flow and its failure cases on one seat, in one browser, as one person."""
    route = f"/factory-briefs/{FACTORY}/{seat}"
    view = f"/gateway/factories/{FACTORY}/seats/{seat}"
    address = f"{BASE}/factories/{FACTORY}/seats/{seat}"
    first, second = FIRST.format(name=name), SECOND.format(name=name)
    person = "the enrolled browser device key, as Authorization: Bearer"

    # 1. A seat with no brief on the Gateway: the file, and "Write the brief here".
    page.goto(address, wait_until="networkidle")
    wait(page, "[data-testid=fa-seat-brief-path]", "the brief file's path")
    shot(page, "01-no-brief-on-the-gateway", suffix)
    keep(f"01-seat-page-brief-no-brief-{suffix}", "GET", view, 200, brief_of(key, seat), credential=person)
    status, answer = request("GET", route, key)
    expect(status, 404, "the published brief of a seat with none", answer)
    keep(f"01-published-brief-of-a-seat-with-none-{suffix}", "GET", route, status, answer, credential=person)

    # WHO MAY WRITE. The Gateway's own machine token is not a person and not a session: refused, nothing written.
    status, answer = request("PUT", f"{route}/draft", token, {"text": first, "expectedVersion": 0})
    expect(status, 403, "a draft saved with the machine token", answer)
    keep(f"02-machine-token-save-refused-{suffix}", "PUT", f"{route}/draft", status, answer,
         sent={"text": "(the first brief)", "expectedVersion": 0}, credential="the Gateway's own machine token, as Authorization: Bearer")
    status, answer = request("POST", f"{route}/publish", token, {"version": 1})
    expect(status, 403, "a publish made with the machine token", answer)
    keep(f"02-machine-token-publish-refused-{suffix}", "POST", f"{route}/publish", status, answer,
         sent={"version": 1}, credential="the Gateway's own machine token, as Authorization: Bearer")

    # 2. Write the first brief and save it as a draft.
    page.click("[data-testid=fa-seat-brief-edit]")
    wait(page, "[data-testid=fa-brief-text]", "the editor")
    page.fill("[data-testid=fa-brief-text]", first)
    shot(page, "02-writing-the-first-brief", suffix)
    page.click("[data-testid=fa-brief-save]")
    wait(page, "[data-testid=fa-notice]", "what the Gateway said about the saved draft")
    wait(page, "[data-testid=fa-seat-brief-draft]", "the seat saying it has drafts and nothing published")
    shot(page, "03-a-draft-and-nothing-published", suffix)
    keep(f"03-seat-page-brief-drafts-only-{suffix}", "GET", view, 200, brief_of(key, seat), credential=person)
    status, answer = request("GET", route, key)
    expect(status, 404, "the published brief of a seat with only a draft", answer)
    keep(f"03-published-brief-while-only-a-draft-{suffix}", "GET", route, status, answer, credential=person)

    # 3. Open the newest draft and publish it. The confirm, then the published brief.
    page.click("[data-testid=fa-seat-brief-edit]")
    wait(page, "[data-testid=fa-brief-start]", "the editor saying it starts from the newest draft")
    page.click("[data-testid=fa-brief-publish]")
    wait(page, "[role=alertdialog]", "the publish confirm")
    shot(page, "04-publish-confirm", suffix)
    confirm(page, "the publish confirm")
    wait(page, "[data-testid=fa-seat-brief-version]", "the published brief's version line")
    shot(page, "05-published-version-1", suffix)
    status, answer = request("GET", route, key)
    expect(status, 200, "the published brief after the first publish", answer)
    keep(f"05-published-brief-version-1-{suffix}", "GET", route, status, answer, credential=person)
    if answer["version"] != 1 or answer["state"] != "published" or answer["text"] != first:
        raise SystemExit(f"the first publish did not publish what was typed: {answer}")

    # 4. A second version: edit the text and publish. The app saves it as version 2 and publishes that version.
    page.click("[data-testid=fa-seat-brief-edit]")
    wait(page, "[data-testid=fa-brief-text]", "the editor")
    page.fill("[data-testid=fa-brief-text]", second)
    page.click("[data-testid=fa-brief-publish]")
    confirm(page, "the second publish confirm")
    wait(page, "[data-testid=fa-brief-editor]", "the editor closing after the second publish", state="detached")
    page.wait_for_function("document.querySelector('[data-testid=fa-seat-brief-version]')?.textContent.startsWith('Version 2')", timeout=30000)
    shot(page, "06-second-version-published", suffix)
    status, answer = request("GET", route, key)
    keep(f"06-published-brief-version-2-{suffix}", "GET", route, status, answer, credential=person)
    if answer["version"] != 2 or answer["text"] != second:
        raise SystemExit(f"the second publish did not publish version 2: {answer}")

    # 5. History: both versions, Open on the first, then bring the first back.
    page.click("[data-testid=fa-seat-brief-history]")
    wait(page, "[data-testid=fa-brief-version-1]", "version 1 in the history")
    page.click("[data-testid=fa-brief-open-1]")
    wait(page, "[data-testid=fa-brief-opened]", "version 1's text")
    shot(page, "07-history-with-version-1-open", suffix)
    page.click("[data-testid=fa-brief-back-1]")
    wait(page, "[role=alertdialog]", "the bring back confirm")
    shot(page, "08-bring-back-confirm", suffix)
    confirm(page, "the bring back confirm")
    wait(page, "[data-testid=fa-brief-version-3]", "version 3 in the history after the bring back")
    shot(page, "09-first-version-brought-back", suffix)
    status, answer = request("GET", route, key)
    keep(f"09-published-brief-after-bring-back-{suffix}", "GET", route, status, answer, credential=person)
    if answer["version"] != 3 or answer["text"] != first:
        raise SystemExit(f"the bring back did not publish a copy of version 1 as version 3: {answer}")
    status, answer = request("GET", f"{route}/history", key)
    keep(f"09-history-after-bring-back-{suffix}", "GET", f"{route}/history", status, answer, credential=person)
    page.click("[data-testid=fa-brief-history-close]")
    wait(page, "[data-testid=fa-seat-brief-text]", "the brief after the history closed")
    shot(page, "10-the-brief-after-bring-back", suffix)

    # FAILURE: a publish the Gateway refuses. Nothing was changed, so Publish asks to publish version 3 again.
    page.click("[data-testid=fa-seat-brief-edit]")
    wait(page, "[data-testid=fa-brief-text]", "the editor")
    page.click("[data-testid=fa-brief-publish]")
    confirm(page, "the publish confirm")
    wait(page, ".ui-confirm-error", "the refused publish in the confirm")
    shot(page, "11-refused-publish-already-published", suffix)
    status, answer = request("POST", f"{route}/publish", key, {"version": 3})
    expect(status, 409, "publishing the version that is already published", answer)
    keep(f"11-publish-refused-already-published-{suffix}", "POST", f"{route}/publish", status, answer, sent={"version": 3}, credential=person)
    status, answer = request("POST", f"{route}/publish", key, {"version": 1})
    expect(status, 409, "publishing a version that was replaced", answer)
    keep(f"11-publish-refused-replaced-version-{suffix}", "POST", f"{route}/publish", status, answer, sent={"version": 1}, credential=person)
    page.click("[role=alertdialog] button:has-text('Cancel')")

    # FAILURE: a stale save. The editor is open on version 3; another writer saves version 4 meanwhile.
    mine = first + "\n\nThe owner adds: never publish on a Friday."
    theirs = first + "\n\nAdded from another window: check the sitemap too."
    page.fill("[data-testid=fa-brief-text]", mine)
    other = call("PUT", f"{route}/draft", key, {"text": theirs, "expectedVersion": 3})
    log(f"another writer saved version {other['version']} while the editor was open")
    page.click("[data-testid=fa-brief-save]")
    wait(page, "[data-testid=fa-brief-conflict]", "the refused save with the newer version")
    wait(page, "[data-testid=fa-brief-theirs]", "the newer version's text beside the owner's")
    if page.input_value("[data-testid=fa-brief-text]") != mine:
        raise SystemExit("the owner's text was not kept in the box after the stale save")
    shot(page, "12-stale-save-newer-version-beside-yours", suffix)
    status, answer = request("PUT", f"{route}/draft", key, {"text": mine, "expectedVersion": 3})
    expect(status, 409, "a save stating a version that is no longer the newest", answer)
    keep(f"12-stale-save-refused-{suffix}", "PUT", f"{route}/draft", status, answer,
         sent={"text": "(the owner's text)", "expectedVersion": 3}, credential=person)
    status, standing = request("GET", f"{route}/draft", key)
    if standing["version"] != 4 or standing["text"] != theirs:
        raise SystemExit(f"the stale save changed what is stored: {standing}")
    # Then the owner says: save my text after the newer version.
    page.click("[data-testid=fa-brief-save-over]")
    wait(page, "[data-testid=fa-brief-editor]", "the editor closing after the save", state="detached")
    wait(page, "[data-testid=fa-seat-brief-draft]", "the page saying a newer draft waits")
    shot(page, "13-saved-after-the-newer-version", suffix)
    status, standing = request("GET", f"{route}/draft", key)
    keep(f"13-newest-version-after-saving-over-{suffix}", "GET", f"{route}/draft", status, standing, credential=person)
    if standing["version"] != 5 or standing["text"] != mine:
        raise SystemExit(f"saving after the newer version did not add version 5: {standing}")

    # TWENTY DRAFTS WAITING, and a history longer than one page. Versions 4 and 5 wait already; 18 more make 20.
    for version in range(6, 24):
        call("PUT", f"{route}/draft", key, {"text": f"{first}\n\nDraft number {version}.", "expectedVersion": version - 1})
    page.goto(address, wait_until="networkidle")
    wait(page, "[data-testid=fa-seat-brief-waiting]", "the twenty waiting drafts on the seat page")
    shot(page, "14-twenty-drafts-waiting", suffix)
    keep(f"14-seat-page-brief-twenty-waiting-{suffix}", "GET", view, 200, brief_of(key, seat), credential=person)
    page.click("[data-testid=fa-seat-brief-history]")
    wait(page, "[data-testid=fa-brief-version-23]", "the newest page of the history")
    wait(page, "[data-testid=fa-brief-older]", "Older versions")
    shot(page, "15-history-newest-page-twenty-waiting", suffix)
    status, answer = request("GET", f"{view}/brief/history", key)
    keep(f"15-history-view-newest-page-{suffix}", "GET", f"{view}/brief/history", status,
         {k: answer[k] for k in ("summaryText", "pageText", "waitingText", "nextBefore", "olderLabel", "newestLabel")}, credential=person)
    page.click("[data-testid=fa-brief-older]")
    wait(page, "[data-testid=fa-brief-version-1]", "the oldest page of the history")
    shot(page, "16-history-older-page", suffix)
    status, answer = request("GET", f"{view}/brief/history?before=4", key)
    keep(f"16-history-view-older-page-{suffix}", "GET", f"{view}/brief/history?before=4", status,
         {k: answer[k] for k in ("summaryText", "pageText", "waitingText", "nextBefore", "olderLabel", "newestLabel")}, credential=person)
    status, answer = request("GET", f"{view}/brief/history?before=0", key)
    expect(status, 400, "a page of the history starting below version 0", answer)
    keep(f"16-history-view-bad-starting-version-{suffix}", "GET", f"{view}/brief/history?before=0", status, answer, credential=person)

    # A person's publish makes room: publish the newest draft from the history, and nothing waits.
    page.click("[data-testid=fa-brief-newest]")
    wait(page, "[data-testid=fa-brief-publish-23]", "Publish on the newest draft")
    page.click("[data-testid=fa-brief-publish-23]")
    confirm(page, "the publish confirm in the history")
    wait(page, "[data-testid=fa-brief-waiting]", "the waiting sentence going away", state="detached")
    page.wait_for_function("!document.querySelector('[data-testid=fa-brief-publish-23]')", timeout=30000)
    shot(page, "17-published-from-the-history-nothing-waits", suffix)
    keep(f"17-seat-page-brief-after-making-room-{suffix}", "GET", view, 200, brief_of(key, seat), credential=person)


def capture(key, token):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        try:
            # Tall enough that a whole page is in one picture: the Cockpit scrolls inside its own frame, so a
            # "full page" screenshot is only ever as tall as the window.
            for width, height, suffix, seat, name in ((1280, 1900, "desktop", "scout", "Scout"), (375, 3600, "phone", "writer", "Writer")):
                context = browser.new_context(viewport={"width": width, "height": height})
                # The browser holds ONE credential: the enrolled device key. It is the cookie that lets a page load
                # through and the key the app sends on every call.
                context.add_cookies([{"name": "cc-gateway-token", "value": key, "url": BASE}])
                page = context.new_page()
                page.goto(BASE + "/", wait_until="domcontentloaded")
                page.evaluate(
                    """(key) => {
                        localStorage.setItem('cc.accounts', JSON.stringify([
                          { id: 'rig', label: 'Rig', email: null, deviceKey: key, installId: 'rig' }]));
                        localStorage.setItem('cc.activeAccount', 'rig');
                        localStorage.setItem('cc.deviceKey', key);
                        // At phone width the menu is collapsed, as a reader on a narrow window keeps it, so the
                        // picture shows the page rather than the menu.
                        if (window.innerWidth < 600) localStorage.setItem('cockpit.railCollapsed', 'true');
                    }""",
                    key,
                )
                # What the app really sent for each write, as the browser saw it: the proof of how it was authenticated.
                writes = []

                def note(req, writes=writes):
                    if "/factory-briefs/" in req.url and req.method in ("PUT", "POST"):
                        sent = req.headers.get("authorization", "")
                        writes.append({
                            "request": f"{req.method} {req.url.replace(BASE, '')}",
                            "authorization": "Bearer <the enrolled browser device key>" if sent == f"Bearer {key}" else "NOT the enrolled device key",
                        })

                page.on("request", note)
                flow(page, key, token, seat, name, suffix)
                if not writes or any(w["authorization"].startswith("NOT") for w in writes):
                    raise SystemExit(f"a write from the app was not made with the enrolled device key: {writes}")
                ANSWERS[f"00-writes-the-app-made-{suffix}"] = writes
                log(f"the app made {len(writes)} writes at {suffix}, every one with the enrolled device key")
                context.close()
        finally:
            browser.close()


def development_proxy(key):
    """The Cockpit's development server, pointed at the scratch Gateway: a brief read THROUGH its proxy."""
    dev = f"http://127.0.0.1:{DEV_PORT}"
    path = f"/factory-briefs/{FACTORY}/scout"
    env = dict(os.environ, COCKPIT_PROXY_TARGET=BASE)
    proc = subprocess.Popen(["npx", "vite", "--port", str(DEV_PORT), "--strictPort", "--host", "127.0.0.1"],
                            cwd=str(REPO / "apps" / "cockpit"), env=env, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    try:
        for _ in range(90):
            if proc.poll() is not None:
                raise SystemExit(f"the development server exited with code {proc.returncode}")
            status, through = request("GET", path, key, base=dev)
            if status == 200 and isinstance(through, dict):
                break
            time.sleep(1)
        else:
            raise SystemExit(f"the development server did not pass {path} to the Gateway within 90 seconds: HTTP {status} {through}")
        _, direct = request("GET", path, key)
        if through != direct:
            raise SystemExit("the answer through the development proxy is not the Gateway's own answer")
        ANSWERS["18-development-proxy"] = {
            "request": f"GET {dev}{path}",
            "status": status,
            "body": {k: through[k] for k in ("factory", "seat", "version", "state", "publishedVersion")},
            "same_as_the_gateway_answered_directly": True,
        }
        log("the development proxy passed /factory-briefs to the Gateway")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log("development server stopped")


def main():
    if not (GATEWAY_BIN / "CcDirector.Gateway.dll").exists():
        raise SystemExit(f"build the Gateway first: {GATEWAY_BIN} has no CcDirector.Gateway.dll")
    if not (COCKPIT_DIST / "index.html").exists():
        raise SystemExit(f"build the Cockpit first: {COCKPIT_DIST} has no index.html (cd apps/cockpit && npm run build)")
    target = GATEWAY_BIN / "wwwroot" / "c"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(COCKPIT_DIST, target)
    log(f"Cockpit copied to {target}")

    root = tempfile.mkdtemp(prefix="brief-rig-")
    # The scratch Gateway's own config.json: the factory area switched on (it is off by default) and the account's
    # display time zone pinned, so the times read the same on any machine.
    (Path(root) / "config").mkdir(parents=True, exist_ok=True)
    (Path(root) / "config" / "config.json").write_text(
        json.dumps({"factoryAgents": {"enabled": True}, "time_zone": "America/Toronto"}), encoding="utf-8")
    # The file watcher polls: a machine running many sessions has no file-watch slots left, and the Gateway would
    # stop at start-up reading its own settings file.
    env = dict(os.environ, CC_DIRECTOR_ROOT=root, CC_GATEWAY_NO_TAILSCALE="1", DOTNET_USE_POLLING_FILE_WATCHER="1")
    env.pop("CC_GATEWAY_NO_AUTH", None)
    log(f"starting the Gateway on port {PORT}, data folder {root}")
    proc = subprocess.Popen(["dotnet", str(GATEWAY_BIN / "CcDirector.Gateway.dll"), "--port", str(PORT)],
                            cwd=str(GATEWAY_BIN), env=env)
    try:
        wait_for_gateway(proc)
        token = find_token(root)
        log("Gateway answered; its machine token read from its own data folder")
        wait_until_ready(token)
        key = "qa-browser-" + secrets.token_hex(16)
        enroll_browser(root, key)
        seed(token)
        capture(key, token)
        development_proxy(key)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log("Gateway stopped")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
