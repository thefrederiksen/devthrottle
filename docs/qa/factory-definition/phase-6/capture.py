"""Desktop screenshots and real Gateway answers for Factory Definition, phase 6 (the Cockpit), from a throwaway Gateway.

Run from the repository root, in the foreground, after building the Gateway (Debug) and the Cockpit:

    dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
    (cd apps/cockpit && npm run build)
    uv run --no-project --with playwright python docs/qa/factory-definition/phase-6/capture.py

It needs Playwright for Python, Google Chrome and Node (for the development proxy check).

Why Playwright and not browser-harness: this is a repeatable script that a reviewer re-runs to get the same pictures,
the first case the browser rules give for Playwright as a library. Nothing here is interactive work in a signed-in
browser of the owner's; the browser is a headless one this script starts and stops.

What it does, and nothing else:
  1. Copies the Cockpit build into the Gateway's wwwroot/c, where the Gateway serves it from.
  2. Starts the Gateway on port 7989 with its OWN data folder (CC_DIRECTOR_ROOT, a fresh scratch folder), the factory
     area switched on in that folder's config.json, and CC_GATEWAY_NO_TAILSCALE=1, so it can touch neither the owner's
     Gateway nor the owner's factories.
  3. Registers two demonstration factories with the Gateway's own machine token, which is how a computer registers
     one: qa-outreach (which is given a definition here) and qa-undefined (which never is).
  4. Enrolls one browser device: a row in the scratch Gateway's device_credentials table holding the SHA-256 of a key
     this script made up. That key is what "a person" is to this Gateway.
  5. Opens the Cockpit in headless Chrome signed in with THAT key, and walks the Definition tab through every state the
     mission asks for, taking a screenshot of each into this folder. Every publish, restore, import and export in the
     pictures is the app's own request, made with that key.
  6. Writes every Gateway answer the report quotes into answers.json, exactly as the Gateway sent it, and the two
     exported zips' hashes.
  7. Starts the Cockpit's development server pointed at the scratch Gateway and reads the definition THROUGH it, to
     show its /factory-definitions line works, then stops it.
  8. Stops the Gateway it started.
"""

import hashlib
import io
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
import zipfile
from datetime import datetime, timezone
from pathlib import Path

from playwright.sync_api import sync_playwright

REPO = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
EXAMPLE = REPO / "docs" / "qa" / "factory-definition" / "phase-1" / "example"
PORT = 7989
BASE = f"http://127.0.0.1:{PORT}"
DEV_PORT = 5196
GATEWAY_BIN = REPO / "src" / "CcDirector.Gateway" / "bin" / "Debug" / "net10.0"
COCKPIT_DIST = REPO / "apps" / "cockpit" / "dist"
FACTORY = "qa-outreach"
UNDEFINED = "qa-undefined"
DEVICE_ID = "qa-cockpit-browser"
SCRATCH = Path(tempfile.gettempdir()) / "factory-definition-p6-capture"

ANSWERS = {}


def log(msg):
    print(f"[p6-capture] {msg}", flush=True)


def request(method, path, key, body=None, base=BASE, raw=False):
    """One call. Returns (status, parsed body or text, or bytes when raw); never raises on an HTTP status."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method)
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
    if raw:
        return status, payload
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
        "goalText": "Two posts a week, every week.", "goalFile": "GOAL.md", "goalApprovedOn": "2026-10-14",
        "seats": [{"id": s, "name": s.capitalize(), "role": s.capitalize(), "briefFile": f"seats/{s}.yaml"} for s in seats],
    })
    log(f"registered {factory}")


# ---------- the folders the flow imports, each written as a zip the way a person would make one ----------

def example_files():
    return {p.relative_to(EXAMPLE).as_posix(): p.read_text(encoding="utf-8") for p in sorted(EXAMPLE.rglob("*")) if p.is_file()}


def renamed(files):
    """Version 2: the writer renamed Author, its brief changed, and the boss's start moved an hour."""
    changed = dict(files)
    changed["seats/writer.yaml"] = changed["seats/writer.yaml"].replace("name: Writer", "name: Author").replace(
        "Write one post from the template.", "Write one post from the template, and link the last one.")
    changed["seats/boss.yaml"] = changed["seats/boss.yaml"].replace('"0 8 * * 1-5"', '"0 9 * * 1-5"')
    return changed


def with_a_check(files):
    """Version 3: the writer is started by a check. It saves; a publish of it is refused until checks can run."""
    changed = dict(files)
    changed["checks/new_mail.py"] = 'print(\'{"count": 0}\')\n'
    changed["seats/writer.yaml"] = changed["seats/writer.yaml"].replace(
        "grants: [publish-posts]",
        'starts:\n  - every: 10m\n    check: checks/new_mail.py\n    prompt: "New mail: {count}."\ngrants: [publish-posts]')
    return changed


def zip_of(files, folder):
    """A zip as the operating system makes one of a folder: every file under the folder's own name."""
    path = SCRATCH / f"{folder}.zip"
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in sorted(files.items()):
            z.writestr(f"{folder}/{name}", text.encode("utf-8"))
    return path


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


def text(page, selector):
    return page.inner_text(selector).strip()


def must(cond, what):
    if not cond:
        raise SystemExit(what)


def open_tab(page, factory):
    page.goto(f"{BASE}/factories/{factory}/definition", wait_until="networkidle")
    wait(page, "[data-testid=fa-def-state]", "the Definition tab")


def confirm(page, what):
    wait(page, "[role=alertdialog]", what)
    page.click("[role=alertdialog] .ui-btn-primary")


def download(page, click_selector, name):
    with page.expect_download() as info:
        page.click(click_selector)
    path = SCRATCH / name
    info.value.save_as(str(path))
    return path


def zip_listing(path):
    with zipfile.ZipFile(path) as z:
        return sorted(z.namelist())


def flow(page, key):
    tab = f"/gateway/factories/{FACTORY}/definition"

    # 1. A factory with no definition: what that means, and what an export of it leaves out.
    open_tab(page, UNDEFINED)
    shot(page, "01-no-definition")
    keep("01-tab-no-definition", "GET", f"/gateway/factories/{UNDEFINED}/definition", 200,
         call("GET", f"/gateway/factories/{UNDEFINED}/definition", key))
    exported = download(page, "[data-testid=fa-def-export]", "qa-undefined-from-today.zip")
    ANSWERS["01-export-from-today"] = {"file": exported.name, "files": zip_listing(exported)}
    page.goto(f"{BASE}/factories/{UNDEFINED}", wait_until="networkidle")
    wait(page, "[data-testid=fa-overview]", "the undefined factory's Overview")
    shot(page, "02-no-definition-overview-unchanged")

    # 2. Import a folder as a zip: the first draft, and what publishing it changes (everything, a first publish).
    open_tab(page, FACTORY)
    page.set_input_files("[data-testid=fa-def-import-file]", str(zip_of(example_files(), FACTORY)))
    wait(page, "[data-testid=fa-def-outcome]", "what the Gateway said about the import")
    wait(page, "[data-testid=fa-def-what-changes]", "what changes in the first draft")
    shot(page, "03-imported-first-draft")
    keep("03-tab-first-draft", "GET", tab, 200, call("GET", tab, key))

    # 3. Publish it: the confirm, then what the Gateway applied.
    page.click("[data-testid=fa-def-publish]")
    wait(page, "[role=alertdialog]", "the publish confirm")
    shot(page, "04-publish-confirm-first")
    confirm(page, "the publish confirm")
    wait(page, "[data-testid=fa-def-outcome] li", "what the publish applied")
    shot(page, "05-published-version-1")
    keep("05-tab-published", "GET", tab, 200, call("GET", tab, key))

    # 4. A second draft: the writer renamed, its brief changed, the boss's start moved. What changes, line by line.
    page.set_input_files("[data-testid=fa-def-import-file]", str(zip_of(renamed(example_files()), FACTORY)))
    wait(page, "[data-testid=fa-def-card-draft]", "the second draft")
    shot(page, "06-draft-with-what-changes")
    keep("06-tab-second-draft", "GET", tab, 200, call("GET", tab, key))
    page.click("[data-testid=fa-def-publish]")
    shot(page, "07-publish-confirm-second")
    confirm(page, "the second publish confirm")
    wait(page, "[data-testid=fa-def-card-draft]", "the draft card going away", state="detached")
    shot(page, "08-published-version-2")

    # 5. A draft the publish refuses: the Gateway's sentence and the check's finding, by file and line, on the tab.
    page.set_input_files("[data-testid=fa-def-import-file]", str(zip_of(with_a_check(renamed(example_files())), FACTORY)))
    wait(page, "[data-testid=fa-def-card-draft]", "the third draft")
    page.click("[data-testid=fa-def-publish]")
    confirm(page, "the third publish confirm")
    wait(page, "[data-testid=fa-def-refused]", "the refused publish")
    shot(page, "09-publish-refused-with-findings")
    must("seats/writer.yaml" in text(page, "[data-testid=fa-def-refused]"), "the refusal does not name the file")
    status, answer = request("POST", f"/factory-definitions/{FACTORY}/publish", key, {"version": 3, "replaces": 2})
    must(status == 422, f"a publish of version 3 answered {status}, not 422: {answer}")
    keep("09-publish-refused", "POST", f"/factory-definitions/{FACTORY}/publish", status, answer)

    # 6. History, one version open, and a restore of version 1.
    page.click("[data-testid=fa-def-history]")
    wait(page, "[data-testid=fa-def-history-table]", "the history")
    shot(page, "10-history")
    keep("10-history", "GET", tab + "/history", 200, call("GET", tab + "/history", key))
    page.click("[data-testid=fa-def-row-1] >> text=Open")
    wait(page, "[data-testid=fa-def-version-view]", "version 1")
    page.click("[data-testid=fa-def-version-view] details >> nth=0")
    shot(page, "11-version-1-open")
    page.click("[data-testid=fa-def-version-view] >> text=Close")
    wait(page, "[data-testid=fa-def-row-restore-1]", "Restore on version 1")
    page.click("[data-testid=fa-def-row-restore-1]")
    wait(page, "[role=alertdialog]", "the restore confirm")
    shot(page, "12-restore-confirm")
    confirm(page, "the restore confirm")
    wait(page, "[data-testid=fa-def-outcome]", "what the restore did")
    shot(page, "13-restored-version-1-as-version-4")
    keep("13-tab-after-restore", "GET", tab, 200, call("GET", tab, key))

    # 7. Export: the published version, twice, byte for byte the same.
    first = download(page, "[data-testid=fa-def-export]", "export-1.zip")
    second = download(page, "[data-testid=fa-def-export]", "export-2.zip")
    a, b = first.read_bytes(), second.read_bytes()
    must(a == b, "two exports of one version are not the same bytes")
    shot(page, "14-exported")
    ANSWERS["14-export-twice"] = {
        "files": zip_listing(first),
        "sha256_first": hashlib.sha256(a).hexdigest(),
        "sha256_second": hashlib.sha256(b).hexdigest(),
        "same_bytes": True,
    }


def capture(key):
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome", headless=True)
        try:
            context = browser.new_context(viewport={"width": 1280, "height": 1900}, accept_downloads=True)
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
            writes = []

            def note(req):
                if ("/factory-definitions/" in req.url or "/definition/import" in req.url) and req.method == "POST":
                    sent = req.headers.get("authorization", "")
                    writes.append({"request": f"{req.method} {req.url.replace(BASE, '')}",
                                   "authorization": "Bearer <the enrolled browser device key>" if sent == f"Bearer {key}" else "NOT the enrolled device key"})

            page.on("request", note)
            flow(page, key)
            must(writes and not any(w["authorization"].startswith("NOT") for w in writes),
                 f"a write from the app was not made with the enrolled device key: {writes}")
            ANSWERS["00-writes-the-app-made"] = writes
            context.close()
        finally:
            browser.close()


def development_proxy(key):
    dev = f"http://127.0.0.1:{DEV_PORT}"
    path = f"/factory-definitions/{FACTORY}"
    env = dict(os.environ, COCKPIT_PROXY_TARGET=BASE)
    proc = subprocess.Popen(["npx", "vite", "--port", str(DEV_PORT), "--strictPort", "--host", "127.0.0.1"],
                            cwd=str(REPO / "apps" / "cockpit"), env=env, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT,
                            shell=os.name == "nt")
    try:
        for _ in range(90):
            status, through = request("GET", path, key, base=dev)
            if status == 200 and isinstance(through, dict):
                break
            time.sleep(1)
        else:
            raise SystemExit(f"the development server did not pass {path} to the Gateway within 90 seconds: HTTP {status} {through}")
        _, direct = request("GET", path, key)
        must(through == direct, "the answer through the development proxy is not the Gateway's own answer")
        ANSWERS["15-development-proxy"] = {"request": f"GET {dev}{path}", "status": status,
                                           "body": {k: through[k] for k in ("factory", "version", "state", "publishedVersion")},
                                           "same_as_the_gateway_answered_directly": True}
        log("the development proxy passed /factory-definitions to the Gateway")
    finally:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True) if os.name == "nt" else proc.terminate()
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
        capture(key)
        development_proxy(key)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        gateway_log.close()
        log("Gateway stopped")

    (OUT / "answers.json").write_text(json.dumps(ANSWERS, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    log("answers.json written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
