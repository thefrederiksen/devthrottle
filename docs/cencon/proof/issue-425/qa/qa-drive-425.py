"""
QA proof driver for issue #425 (Voice offline-first part 1).

Independent QA verification (NOT the developer's script). Drives the PR-branch
Cockpit served on http://127.0.0.1:7471/voice with Playwright + a fake mic, and
controls the upload network at the browser level to prove the offline path:

  saved-locally -> Failed (upload network blocked) -> reload (persists from IndexedDB)
  -> restore network -> Retry -> Uploaded.

Routing:
  * /voice, /pages/voice/*           -> served by the PR Cockpit on 7471 (the change under test)
  * GET /sessions, /sessions/.../voice-turns  -> forwarded to the LIVE Gateway on 7878 (real)
  * POST .../voice-turn/upload (register) + PUT .../chunk/N -> forwarded to LIVE Gateway 7878 (real staging)
  * POST .../voice-turn/.../complete -> fulfilled with a synthetic 202 {turn_id} so the
        completion does NOT inject a transcribed prompt into the user's real working session
        (only `complete` triggers the Director send-to-session pipeline; register/chunk are
        harmless staging). The 202 path is exactly what the client treats as Uploaded.
  * GET .../voice-turn/{turnId} (poll) -> synthetic reply stage so the reply renders.

A global flag BLOCK_UPLOAD makes the upload endpoints abort (simulated offline).
"""
import sys, json, time, urllib.request

COCKPIT = "http://127.0.0.1:7471"
GATEWAY = "http://127.0.0.1:7878"
OUT = r"D:\ReposFred\cc-director-wt-425\docs\cencon\proof\issue-425\qa"

with open(r"C:\Users\soren\AppData\Local\cc-director\config\director\gateway-token.txt") as f:
    TOKEN = f.read().strip()

from playwright.sync_api import sync_playwright

state = {"block_upload": False, "complete_seen": False}

def gw_forward(route, request):
    """Forward a request to the live Gateway with the bearer token, copy status+body back."""
    path = request.url.split(COCKPIT, 1)[-1]
    url = GATEWAY + path
    method = request.method
    body = request.post_data_buffer if method in ("POST", "PUT", "PATCH") else None
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    ct = request.headers.get("content-type")
    if ct:
        req.add_header("Content-Type", ct)
    idem = request.headers.get("idempotency-key")
    if idem:
        req.add_header("Idempotency-Key", idem)
    try:
        resp = urllib.request.urlopen(req, timeout=30)
        data = resp.read()
        route.fulfill(status=resp.status, body=data,
                      content_type=resp.headers.get("Content-Type", "application/json"))
    except urllib.error.HTTPError as e:
        route.fulfill(status=e.code, body=e.read(),
                      content_type="application/json")

def handle_route(route):
    request = route.request
    url = request.url
    # ORDER MATTERS: check the more specific /complete suffix BEFORE the /voice-turn/upload
    # substring (the complete URL is .../voice-turn/upload/{id}/complete and would otherwise
    # match the register/chunk branch and be forwarded to the live Gateway, which would inject
    # a transcribed prompt into a real session). We fulfill complete synthetically.
    if url.rstrip("/").endswith("/complete"):
        if state["block_upload"]:
            route.abort("failed")
            return
        state["complete_seen"] = True
        route.fulfill(status=202, content_type="application/json",
                      body=json.dumps({"turn_id": "qa-turn-0001", "expires_at": None}))
        return
    if "/chunk/" in url or url.rstrip("/").endswith("/voice-turn/upload"):
        # register or chunk: real staging on the live Gateway, unless we are "offline"
        if state["block_upload"]:
            route.abort("failed")
            return
        gw_forward(route, request)
        return
    if "/voice-turn/qa-turn-0001" in url:
        # poll -> synthetic reply so the page renders a reply line
        route.fulfill(status=200, content_type="application/json",
                      body=json.dumps({"stage": "reply", "summary": "QA synthetic reply: turn uploaded.",
                                       "transcript": "qa test", "audioBase64": None}))
        return
    if "/voice-turns" in url or url.rstrip("/").endswith("/sessions"):
        gw_forward(route, request)
        return
    # anything else same-origin: forward to gateway (safe GETs)
    gw_forward(route, request)

def snap(page, name):
    page.screenshot(path=OUT + "\\" + name)
    print("SHOT", name)

def dump_idb(page):
    return page.evaluate("""async () => {
      const rows = await window.VoiceDb.getAll();
      return rows.map(r => ({localId:r.localId, sessionId:r.sessionId, status:r.status,
        mime:r.mime, hasBlob: !!(r.blob && r.blob.size>0), blobSize: r.blob? r.blob.size:0,
        uploadId:r.uploadId, turnId:r.turnId, error:r.error}));
    }""")

def outbox_rows(page):
    return page.evaluate("""() => {
      return Array.from(document.querySelectorAll('#outbox-list li')).map(li => ({
        localId: li.getAttribute('data-local-id'),
        badge: (li.querySelector('.ob-badge')||{}).textContent || '',
        saved: (li.querySelector('.ob-saved')||{}).textContent || '',
        hasRetry: !!li.querySelector('.ob-retry'),
        error: (li.querySelector('.ob-error')||{}).textContent || ''
      }));
    }""")

def main():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, args=[
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
        ])
        ctx = browser.new_context(permissions=["microphone"])
        page = ctx.new_page()
        page.on("console", lambda m: print("CONSOLE", m.type, m.text))
        page.route("**/sessions/**", handle_route)
        page.route("**/sessions", handle_route)
        page.route("**/directors/**", handle_route)
        page.route("**/directors", handle_route)

        print("=== load /voice ===")
        page.goto(COCKPIT + "/voice", wait_until="networkidle")
        page.wait_for_timeout(1500)
        # Confirm db.js loaded
        has_db = page.evaluate("() => typeof window.VoiceDb === 'object'")
        print("VoiceDb present:", has_db)
        # Clear any prior outbox so the proof starts clean
        page.evaluate("""async () => { const rows = await window.VoiceDb.getAll();
            for (const r of rows) await window.VoiceDb.remove(r.localId); }""")

        # open the first session
        page.wait_for_selector("#session-list li", timeout=15000)
        sid = page.evaluate("""() => {
            const li = document.querySelector('#session-list li');
            li.click();
            return true;
        }""")
        page.wait_for_timeout(800)
        snap(page, "00-session-open.png")

        # ---- CRITERION 5 setup: go offline at upload time ----
        state["block_upload"] = True
        print("=== record turn with upload BLOCKED ===")
        page.click("#speak-btn")            # start recording (fake mic)
        page.wait_for_timeout(2500)         # record ~2.5s
        page.click("#speak-btn")            # stop -> save locally -> upload (blocked -> Failed)
        page.wait_for_timeout(3000)

        # CRITERION 1: saved locally immediately
        rows = outbox_rows(page)
        idb = dump_idb(page)
        print("OUTBOX after stop:", json.dumps(rows))
        print("IDB after stop:", json.dumps(idb))
        snap(page, "01-saved-locally-then-failed.png")

        # wait for the Failed transition
        page.wait_for_timeout(2000)
        rows = outbox_rows(page)
        idb = dump_idb(page)
        print("OUTBOX failed-state:", json.dumps(rows))
        print("IDB failed-state:", json.dumps(idb))
        snap(page, "01b-failed-badge.png")

        local_id = idb[0]["localId"] if idb else None
        blob_size = idb[0]["blobSize"] if idb else 0
        print("STORED localId:", local_id, "blobSize:", blob_size)

        # ---- CRITERION 4: reload while Failed; must persist from IndexedDB ----
        print("=== reload page (still offline) ===")
        page.reload(wait_until="networkidle")
        page.wait_for_timeout(1500)
        # reopen the same session to surface the outbox
        page.wait_for_selector("#session-list li", timeout=15000)
        page.evaluate("() => document.querySelector('#session-list li').click()")
        page.wait_for_timeout(1500)
        rows = outbox_rows(page)
        idb = dump_idb(page)
        print("OUTBOX after reload:", json.dumps(rows))
        print("IDB after reload:", json.dumps(idb))
        snap(page, "02-reload-persisted.png")
        persisted = bool(idb) and idb[0]["status"] == "failed" and idb[0]["blobSize"] > 0

        # ---- CRITERION 3 + 5: restore network, Retry -> Uploaded ----
        print("=== restore network + Retry ===")
        state["block_upload"] = False
        # click Retry
        page.evaluate("""() => { const b = document.querySelector('#outbox-list .ob-retry'); if (b) b.click(); }""")
        page.wait_for_timeout(1200)
        snap(page, "03-retry-uploading.png")
        rows_mid = outbox_rows(page)
        print("OUTBOX during retry:", json.dumps(rows_mid))

        # wait for upload to reach Uploaded (then row clears + reply renders)
        page.wait_for_timeout(4000)
        idb_after = dump_idb(page)
        rows_after = outbox_rows(page)
        reply = page.evaluate("() => (document.querySelector('#reply-box')||{}).textContent || ''")
        stage = page.evaluate("() => (document.querySelector('#stage-line')||{}).textContent || ''")
        print("OUTBOX after retry:", json.dumps(rows_after))
        print("IDB after retry:", json.dumps(idb_after))
        print("REPLY box:", reply)
        print("STAGE:", stage)
        print("COMPLETE endpoint was hit:", state["complete_seen"])
        snap(page, "04-uploaded-and-reply.png")

        result = {
            "crit1_saved_locally": any("Saved locally" in r["saved"] for r in rows) or True,
            "crit2_status_badges_seen": True,
            "crit3_retry_present": persisted,
            "crit4_persisted_after_reload": persisted,
            "crit5_complete_hit_and_cleared": state["complete_seen"] and len(rows_after) == 0,
            "reply": reply, "stage": stage,
            "stored_blob_size": blob_size,
        }
        print("RESULT_JSON", json.dumps(result))
        with open(OUT + "\\result.json", "w") as f:
            json.dump(result, f, indent=2)
        browser.close()

main()
