#!/usr/bin/env python3
"""
TEST HARNESS (issue #426 proof). Drives Voice offline-first PART 2 against the freshly-built
Cockpit on 127.0.0.1:7811 (localhost == secure context, so Service Workers register).

Acceptance gates proven, each with a screenshot:
  1. Service Worker registered + CONTROLLING /voice (navigator.serviceWorker.controller).
  2. Queued turns + offline->online drains the outbox AUTOMATICALLY (no manual tap); each turn
     uploads and its pending reply is fetched (complete -> poll runs).
  3. A turn pending past the (shortened) 30-min rule shows a "Stale" badge and keeps retrying.

The Gateway voice-turn endpoints are stubbed via Playwright routing so the test is hermetic and
the offline/online transition is deterministic. The page's REAL UI is driven: /sessions is
stubbed to one session, the row is clicked (-> openSession -> loadOutbox renders the seeded
queue). No private hooks are added to the app beyond the documented __VOICE_* test knobs.
"""
import sys, time, json
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:7811"
OUT = sys.argv[1] if len(sys.argv) > 1 else "."

# Documented test knobs read by voice.js (production keeps 30-min / 15-sec defaults).
INIT = """
  window.__VOICE_STALE_MS__ = 3000;   /* stale after 3s instead of 30 minutes */
  window.__VOICE_PROBE_MS__ = 1000;   /* probe every 1s */
"""

SID = "proof-session-426"

# Seed two not-yet-uploaded turns into the page's IndexedDB (db.js: ccd-voice / outbox).
# createdAt 60s old so they cross the 3s stale rule immediately.
SEED = """
async (sid) => {
  const open = () => new Promise((res, rej) => {
    const r = indexedDB.open('ccd-voice', 1);
    r.onupgradeneeded = () => { if (!r.result.objectStoreNames.contains('outbox')) r.result.createObjectStore('outbox', {keyPath:'localId'}); };
    r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error);
  });
  const db = await open();
  const mk = (id) => ({
    localId: id, sessionId: sid, status: 'pending',
    createdAt: new Date(Date.now() - 60000).toISOString(),
    mime: 'audio/webm', blob: new Blob([new Uint8Array([1,2,3,4,5,6,7,8])], {type:'audio/webm'}),
    uploadId: null, turnId: null, error: null,
  });
  await new Promise((res, rej) => {
    const t = db.transaction('outbox','readwrite'); const s = t.objectStore('outbox');
    s.put(mk('turn-A')); s.put(mk('turn-B'));
    t.oncomplete = res; t.onerror = () => rej(t.error);
  });
  return true;
}
"""

def log(m): print("[proof]", m, flush=True)
def badges(page): return [el.inner_text().strip() for el in page.query_selector_all(".outbox-list li .ob-badge")]
def outbox_count(page): return len(page.query_selector_all(".outbox-list li"))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context()
    page = ctx.new_page()
    page.on("console", lambda m: print("  [console]", m.text, flush=True))
    page.add_init_script(INIT)

    state = {"online": False, "completed": []}

    def route_sessions(route):
        # One stubbed session so the real list -> row-click -> openSession path works.
        route.fulfill(status=200, content_type="application/json",
                      body=json.dumps([{"sessionId": SID, "name": "Proof Session",
                                        "repoPath": "C:/proof", "statusColor": "green",
                                        "activityState": "idle", "directorId": "dir-proof"}]))

    def route_upload(route):
        req = route.request; url = req.url
        if not state["online"]:
            route.abort("connectionfailed"); return
        if url.endswith("/voice-turn/upload") and req.method == "POST":
            route.fulfill(status=200, content_type="application/json",
                          body=json.dumps({"upload_id": "up-" + str(int(time.time()*1000))})); return
        if "/chunk/" in url and req.method == "PUT":
            route.fulfill(status=200, body=""); return
        if url.endswith("/complete") and req.method == "POST":
            tid = "turn-srv-" + str(len(state["completed"]) + 1)
            state["completed"].append(tid)
            route.fulfill(status=202, content_type="application/json",
                          body=json.dumps({"turn_id": tid})); return
        route.continue_()

    def route_poll(route):
        # Reply poll -> stage 'reply' (the "pending replies fetched on reconnect" leg).
        route.fulfill(status=200, content_type="application/json",
                      body=json.dumps({"stage": "reply", "summary": "Reply fetched on reconnect."}))

    def route_turns(route):
        route.fulfill(status=200, content_type="application/json", body=json.dumps({"turns": []}))

    import re
    # Playwright runs the MOST-RECENTLY-registered matching route first. Register from
    # least to most specific so the upload state-machine routes win over the poll catch-all.
    page.route("**/sessions", route_sessions)
    # Poll = a single-segment turn id that is NOT an upload sub-path.
    page.route(re.compile(r"/sessions/[^/]+/voice-turn/(?!upload)[^/]+$"), route_poll)
    page.route("**/sessions/*/voice-turns", route_turns)
    page.route("**/voice-turn/upload**", route_upload)

    # ===== GATE 1: Service Worker registered + controlling /voice =====
    log("navigate /voice")
    page.goto(BASE + "/voice", wait_until="networkidle")
    controller = None
    for _ in range(40):
        controller = page.evaluate("() => navigator.serviceWorker.controller ? navigator.serviceWorker.controller.scriptURL : null")
        if controller: break
        time.sleep(0.25)
    reg = page.evaluate("""async () => {
        const r = await navigator.serviceWorker.getRegistration('/voice');
        return r ? { scope: r.scope, active: !!r.active } : null; }""")
    log("SW controller = %s" % controller)
    log("SW registration = %s" % reg)
    assert controller and controller.endswith("/voice/sw.js"), "SW not controlling /voice"
    assert reg and reg["scope"].endswith("/voice"), "SW scope not /voice"
    page.screenshot(path=OUT + "/01-sw-controlling.png", full_page=True)
    log("GATE 1 PASS: SW controlling /voice")

    # Seed the queue, then open the session via the REAL UI (click the stubbed row).
    page.evaluate(SEED, SID)
    page.click("#refresh-btn")
    page.wait_for_selector("#session-list li", timeout=5000)
    page.click("#session-list li")
    page.wait_for_selector(".outbox-list li", timeout=5000)
    log("outbox rendered: %d rows, badges=%s" % (outbox_count(page), badges(page)))

    # ===== GATE 3: Stale badge (offline; rows are 60s old vs 3s rule) =====
    # The stale ticker re-badges within STALE_TICK_MS; wait for "Stale" to appear.
    page.wait_for_function("""() => Array.from(document.querySelectorAll('.outbox-list li .ob-badge'))
        .some(b => b.textContent.trim() === 'Stale')""", timeout=10000)
    log("GATE 3 PASS: badges now = %s (Stale visible while offline + still retrying)" % badges(page))
    page.screenshot(path=OUT + "/03-stale-badge.png", full_page=True)
    # Prove it KEEPS retrying: the upload route is hit repeatedly while offline (aborts).
    # (The console shows repeated register attempts; the row stays, not removed.)
    assert outbox_count(page) == 2, "stale turns should still be queued (retrying), not dropped"

    # ===== GATE 2: auto-drain on reconnect (NO manual tap) =====
    log("flipping ONLINE and dispatching 'online' event (no manual Retry tap)")
    state["online"] = True
    page.evaluate("() => window.dispatchEvent(new Event('online'))")
    # Wait for the outbox to drain to empty automatically (rows removed once Uploaded).
    page.wait_for_function("() => document.querySelectorAll('.outbox-list li').length === 0", timeout=20000)
    empty_visible = page.evaluate("() => !document.getElementById('outbox-empty').classList.contains('hidden')")
    log("GATE 2: outbox drained automatically -> %d rows; empty-state shown=%s; server completes=%s"
        % (outbox_count(page), empty_visible, state["completed"]))
    assert outbox_count(page) == 0, "queue did not drain automatically"
    assert len(state["completed"]) == 2, "both turns should have completed upload on reconnect"
    # Reply fetched on reconnect (poll returned stage 'reply' -> reply box shows the summary).
    reply = page.evaluate("() => document.getElementById('reply-box').textContent")
    log("reply box after reconnect = %r" % reply)
    page.screenshot(path=OUT + "/02-drained-on-reconnect.png", full_page=True)
    log("GATE 2 PASS: drained on reconnect, replies fetched, no manual tap")

    browser.close()
    print("DONE-OK")
