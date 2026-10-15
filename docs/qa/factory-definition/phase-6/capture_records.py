"""Desktop screenshots and real Gateway answers for the Records tab (Factory Definition, phase 6), from a throwaway Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    uv run --no-project --with playwright python docs/qa/factory-definition/phase-6/capture_records.py

It needs Playwright for Python and Google Chrome.

Why Playwright and not browser-harness: this is a repeatable script that a reviewer re-runs to get the same pictures,
the first case the browser rules give for Playwright as a library. The browser is a headless one this script starts
and stops.

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, where the Gateway serves it from.
  2. Starts the Gateway on port 7990 with its OWN data folder (a fresh scratch folder), the factory area switched on,
     and CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's Gateway nor the owner's factories.
  3. Registers two demonstration factories with the Gateway's own machine token: qa-outreach, which is given records,
     and qa-empty, which never is.
  4. Enrolls one browser device (a row holding the SHA-256 of a key this script made up): "a person" to this Gateway.
  5. Writes qa-outreach's records through the Gateway's own record routes, the way they are written for real: journal
     entries by two seats and by the factory as a whole, a working document in three versions, and attachments
     including an image, some as the person and some as the one-time upload (the machine token, naming its machine).
  6. Opens the Cockpit in headless Chrome signed in with the person's key and walks the Records tab through every state,
     taking a screenshot of each into this folder, and downloads an attachment through the tab.
  7. Writes every Gateway answer the report quotes into records-answers.json, exactly as the Gateway sent it.
  8. Stops the Gateway it started.
"""

import base64
import hashlib
import json
import os
import secrets
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zlib
from datetime import datetime, timezone
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
PORT = 7990
BASE = f"http://127.0.0.1:{PORT}"
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "qa-outreach"
EMPTY = "qa-empty"
DEVICE_ID = "qa-cockpit-browser"
UPLOAD_MACHINE = "RIG_MACHINE"
SCRATCH = Path(tempfile.gettempdir()) / "factory-definition-p6-records-capture"

ANSWERS = {}


def log(msg):
    print(f"[p6-records] {msg}", flush=True)


def request(method, path, key, body=None, headers=None, raw=False):
    """One call. Returns (status, parsed body or text, or bytes when raw); never raises on an HTTP status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header("Authorization", f"Bearer {key}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    for name, value in (headers or {}).items():
        req.add_header(name, value)
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            status, payload = res.status, res.read()
    except urllib.error.HTTPError as exc:
        status, payload = exc.code, exc.read()
    except OSError:
        return 0, None
    if raw:
        return status, payload
    text = payload.decode("utf-8", "replace")
    try:
        return status, (json.loads(text) if text else None)
    except json.JSONDecodeError:
        return status, text


def call(method, path, key, body=None, headers=None):
    status, answer = request(method, path, key, body, headers)
    if not 200 <= status < 300:
        raise SystemExit(f"{method} {path} failed: HTTP {status} {answer}")
    return answer


def keep(name, path, answer):
    ANSWERS[name] = {"request": f"GET {path}", "body": answer}
    log(f"answer kept: {name}")


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
            "DeviceId": DEVICE_ID, "MachineName": "RIG_MACHINE", "DeviceKeyHash": hashlib.sha256(key.encode()).hexdigest(),
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
        "factory": factory, "title": title, "folder": "/home/rig/" + factory, "computer": "RIG_MACHINE", "bossSeat": "boss",
        "seats": [{"id": s, "name": s.capitalize(), "role": s.capitalize(), "briefFile": f"seats/{s}.yaml"} for s in seats],
    })
    log(f"registered {factory}")


def png(width, height, rgb):
    """A small solid-colour PNG, built by hand so the script needs no imaging library."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    rows = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))


# ---------- the records, written through the Gateway's own routes ----------

def seed(key, token):
    """Write qa-outreach's records: as the person (the browser key), and as the one-time upload (the machine token)."""
    root = f"/factory-records/{FACTORY}"
    upload = {"X-DevThrottle-Upload-Machine": UPLOAD_MACHINE}

    def attach(seat, name, content_type, data, as_upload=False):
        body = {"seat": seat, "name": name, "contentType": content_type, "contentBase64": base64.b64encode(data).decode()}
        if as_upload:
            body["importKey"] = f"upload:{name}"
        answer = call("POST", root + "/attachments", token if as_upload else key, body, upload if as_upload else None)
        return answer["attachment"]["id"]

    picture = attach("writer", "shots/home-page.png", "image/png", png(64, 40, (40, 120, 200)))
    ledger_csv = attach("boss", "exports/leads-2026-10-13.csv", "text/csv", b"name,company\nAda,Analytical\nGrace,Navy\n",
                        as_upload=True)
    attach(None, "notes/kickoff.txt", "text/plain", b"Two posts a week, every week.\n")

    def journal(seat, date, text, ids=None, as_upload=False):
        body = {"seat": seat, "date": date, "text": text, "attachmentIds": ids or []}
        if as_upload:
            body["importKey"] = f"upload:journal:{seat}:{date}"
        call("POST", root + "/journal", token if as_upload else key, body, upload if as_upload else None)

    journal(None, "2026-10-12", "The factory started. The goal is two posts a week, every week.")
    journal("boss", "2026-10-13", "Picked the two topics for this week and handed them to the writer.\nLeads list exported.",
            [ledger_csv], as_upload=True)
    journal("boss", "2026-10-14", "Checked the writer's draft. One fix asked for: link last week's post.")
    journal("writer", "2026-10-13", "Drafted the first post from the template.", as_upload=True)
    journal("writer", "2026-10-14", "Published the first post. Screenshot of the home page attached.", [picture])

    def write(name, text, expected, as_upload=False):
        body = {"name": name, "text": text, "expectedVersion": expected}
        if as_upload:
            body["importKey"] = f"upload:doc:{name}:{expected}"
        call("PUT", root + "/documents", token if as_upload else key, body, upload if as_upload else None)

    write("outreach/ledger.json", json.dumps({"posts": []}, indent=2), 0, as_upload=True)
    write("outreach/ledger.json", json.dumps({"posts": [{"title": "Why we ship small", "date": "2026-10-14"}]}, indent=2), 1)
    write("outreach/ledger.json", json.dumps({"posts": [{"title": "Why we ship small", "date": "2026-10-14"},
                                                        {"title": "One checkout per task", "date": "2026-10-16"}]}, indent=2), 2)
    write("focus.md", "This week: two posts, both linked from the home page.\n", 0)
    log("records written")
    return picture


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


def open_tab(page, factory):
    page.goto(f"{BASE}/factories/{factory}/records", wait_until="networkidle")
    wait(page, "[data-testid=fa-rec-journals]", "the Records tab")


def flow(page, key, picture):
    tab = f"/gateway/factories/{FACTORY}/records"

    # 1. A factory with nothing recorded: each section says so.
    open_tab(page, EMPTY)
    shot(page, "20-records-empty")
    keep("20-records-empty", f"/gateway/factories/{EMPTY}/records", call("GET", f"/gateway/factories/{EMPTY}/records", key))

    # 2. The tab with every kind of record.
    open_tab(page, FACTORY)
    shot(page, "21-records")
    keep("21-records", tab, call("GET", tab, key))

    # 3. One seat's journal, with an entry that names an attachment, and the factory's own journal.
    page.click("[data-testid=fa-rec-journal-writer] >> text=Read")
    wait(page, "[data-testid=fa-rec-journal]", "the writer's journal")
    shot(page, "22-journal-of-a-seat")
    keep("22-journal-of-a-seat", tab + "/journal?seat=writer", call("GET", tab + "/journal?seat=writer", key))
    page.click("[data-testid=fa-rec-journal] >> text=Back to the records")
    page.click("[data-testid=fa-rec-journal-boss] >> text=Read")
    wait(page, "[data-testid=fa-rec-journal] >> text=Leads list exported.", "the boss's journal")
    shot(page, "23-journal-written-by-the-upload")
    page.click("[data-testid=fa-rec-journal] >> text=Back to the records")
    page.click("[data-testid=fa-rec-journal-factory] >> text=Read")
    wait(page, "[data-testid=fa-rec-journal] >> text=The factory started.", "the factory's own journal")
    shot(page, "24-journal-of-the-factory")
    page.click("[data-testid=fa-rec-journal] >> text=Back to the records")

    # 4. A working document at its newest version, every version, and an earlier one opened.
    page.click("[data-testid='fa-rec-document-outreach/ledger.json'] >> text=Open")
    wait(page, "[data-testid=fa-rec-document]", "the newest version of the ledger")
    shot(page, "25-document-newest-version")
    page.click("[data-testid=fa-rec-document] >> text=History")
    wait(page, "[data-testid=fa-rec-history]", "the ledger's versions")
    shot(page, "26-document-history")
    keep("26-document-history", tab + "/documents/history?name=outreach/ledger.json",
         call("GET", tab + "/documents/history?name=outreach%2Fledger.json", key))
    page.click("[data-testid=fa-rec-version-1] >> text=Open")
    wait(page, "[data-testid=fa-rec-document] >> text=An earlier version", "version 1 of the ledger")
    shot(page, "27-document-earlier-version")
    page.click("[data-testid=fa-rec-document] >> text=Back to the records")

    # 5. Download the image through the tab: the same bytes the writer kept.
    wait(page, "[data-testid='fa-rec-attachment-shots/home-page.png']", "the image row")
    with page.expect_download() as info:
        page.click("[data-testid='fa-rec-attachment-shots/home-page.png'] >> text=Download")
    saved = SCRATCH / "downloaded.png"
    info.value.save_as(str(saved))
    sent = png(64, 40, (40, 120, 200))
    must(saved.read_bytes() == sent, "the downloaded image is not the bytes that were kept")
    ANSWERS["28-attachment-downloaded"] = {"file": info.value.suggested_filename, "id": picture,
                                           "sha256": hashlib.sha256(sent).hexdigest(), "same_bytes_as_kept": True}
    shot(page, "28-attachment-downloaded")

    # 6. Paging: the writer's journal grows past one page (24 entries written by the upload at one sitting, so many
    #    share a day), and the factory's attachments past one page of 100.
    root = f"/factory-records/{FACTORY}"
    for i in range(24):
        call("POST", root + "/journal", key, {"seat": "writer", "date": f"2026-09-{10 + i % 12:02d}",
                                              "text": f"Older work, note {i + 1}.", "attachmentIds": []})
    for i in range(100):
        call("POST", root + "/attachments", key, {"seat": "writer", "name": f"shots/run-{i:03d}.png", "contentType": "image/png",
                                                  "contentBase64": base64.b64encode(png(4, 4, (i, 80, 160))).decode()})
    log("26 journal entries for the writer and 103 attachments in all")

    open_tab(page, FACTORY)
    page.click("[data-testid=fa-rec-journal-writer] >> text=Read")
    wait(page, "[data-testid=fa-rec-journal] >> text=Older entries", "the writer's journal with an Older step")
    must("Entries 1 to 20 are shown" in page.inner_text("[data-testid=fa-rec-journal]"), "the first journal page does not say which entries it shows")
    shot(page, "33-journal-first-page")
    first = call("GET", tab + "/journal?seat=writer", key)
    page.click("[data-testid=fa-rec-journal] >> text=Older entries")
    wait(page, "[data-testid=fa-rec-journal] >> text=Newest entries", "the older page of the writer's journal")
    must("Entries 21 to 26 are shown" in page.inner_text("[data-testid=fa-rec-journal]"), "the older journal page does not say which entries it shows")
    shot(page, "34-journal-older-entries")
    older = call("GET", tab + f"/journal?seat=writer&before={first['nextBefore']}", key)
    seen = [e["id"] for e in first["entries"]] + [e["id"] for e in older["entries"]]
    must(len(seen) == len(set(seen)) == 26 and older["nextBefore"] is None, f"the two journal pages do not hold the 26 entries once each: {len(seen)}")
    ANSWERS["34-journal-pages"] = {"first": first["summaryText"], "older": older["summaryText"], "entries_once_each": 26}
    page.click("[data-testid=fa-rec-journal] >> text=Back to the records")

    wait(page, "[data-testid=fa-rec-attachments] >> text=Older files", "Older files on the tab")
    shot(page, "35-attachments-first-page")
    page.click("[data-testid=fa-rec-attachments] >> text=Older files")
    wait(page, "[data-testid=fa-rec-attachments] >> text=Newest files", "the older page of the attachments")
    shot(page, "36-attachments-older-files")
    tab_view = call("GET", tab, key)
    rest = call("GET", tab + f"/attachments?before={tab_view['attachmentsNextBefore']}", key)
    ids = [a["id"] for a in tab_view["attachmentRows"]] + [a["id"] for a in rest["rows"]]
    must(len(ids) == len(set(ids)) == 103 and rest["nextBefore"] is None, f"the two attachment pages do not hold the 103 files once each: {len(ids)}")
    ANSWERS["36-attachment-pages"] = {"first": tab_view["attachments"]["summaryText"], "older": rest["summaryText"], "files_once_each": 103}


def capture(key, picture):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        try:
            context = browser.new_context(viewport={"width": 1280, "height": 1100}, accept_downloads=True)
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
            flow(page, key, picture)
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
        register(token, EMPTY, "QA Empty", ["boss"])
        picture = seed(key, token)
        capture(key, picture)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        gateway_log.close()
        log("Gateway stopped")

    (OUT / "records-answers.json").write_text(json.dumps(ANSWERS, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    log("records-answers.json written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
