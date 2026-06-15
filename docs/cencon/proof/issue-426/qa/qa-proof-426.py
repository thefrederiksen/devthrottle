#!/usr/bin/env python3
"""
INDEPENDENT QA proof for issue #426 (Voice offline-first part 2). Written by the QA Agent
from scratch - does NOT reuse the developer's harness. Drives the REAL /voice page served by
a QA-built Cockpit on 127.0.0.1:7912 (localhost = secure context, so Service Workers register).

The 4 acceptance criteria, each verified independently with its own screenshot:
  G1. A Service Worker is registered and CONTROLLING /voice (navigator.serviceWorker.controller).
  G2. With queued turns, offline->online drains the queue AUTOMATICALLY (no manual tap) and the
      pending reply is fetched (the upload's complete->poll runs to stage 'reply').
  G3. On a browser WITHOUT Background Sync, the online event + periodic probe STILL drain.
      QA isolates this path: SyncManager is removed before any script runs, so reg.sync is
      undefined and Background Sync cannot fire - only the online-event/probe fallback remains.
  G4. A turn pending past the (shortened) 30-minute rule shows "Stale" and KEEPS retrying.

Gateway voice-turn endpoints are stubbed via Playwright routing so offline/online is deterministic
and the test is hermetic. Only the documented __VOICE_* test knobs are used.
"""
import sys, time, json, re
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:7912"
OUT = sys.argv[1] if len(sys.argv) > 1 else "."
SID = "qa-session-426"

# Documented test knobs (production keeps 30-min / 15-sec defaults).
KNOBS = """
  window.__VOICE_STALE_MS__ = 3000;   /* Stale after 3s instead of 30 minutes */
  window.__VOICE_PROBE_MS__ = 1000;   /* probe every 1s */
"""

# G3 isolation: remove Background Sync so ONLY the online-event + periodic-probe path can drain.
# voice.js gates Background Sync on `reg.sync` (the SyncManager on a ServiceWorkerRegistration);
# delete that accessor from the prototype so reg.sync is undefined for every registration.
KILL_BG_SYNC = """
  try { delete window.SyncManager; } catch (e) {}
  try {
    if (window.ServiceWorkerRegistration &&
        ServiceWorkerRegistration.prototype &&
        'sync' in ServiceWorkerRegistration.prototype) {
      delete ServiceWorkerRegistration.prototype.sync;
    }
  } catch (e) {}
  try {
    Object.defineProperty(ServiceWorkerRegistration.prototype, 'sync',
      { get: function () { return undefined; }, configurable: true });
  } catch (e) {}
"""

# Seed two not-yet-uploaded turns, created 60s ago (so they cross the 3s Stale rule at once).
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
    mime: 'audio/webm', blob: new Blob([new Uint8Array([9,8,7,6,5,4,3,2,1])], {type:'audio/webm'}),
    uploadId: null, turnId: null, error: null,
  });
  await new Promise((res, rej) => {
    const t = db.transaction('outbox','readwrite'); const s = t.objectStore('outbox');
    s.put(mk('qa-turn-1')); s.put(mk('qa-turn-2'));
    t.oncomplete = res; t.onerror = () => rej(t.error);
  });
  return true;
}
"""

def log(m): print("[qa-proof]", m, flush=True)
def badges(page): return [el.inner_text().strip() for el in page.query_selector_all("#outbox-list li .ob-badge")]
def rows(page): return len(page.query_selector_all("#outbox-list li"))

fails = []

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context()
    page = ctx.new_page()
    page.on("console", lambda m: print("  [console]", m.text, flush=True))
    page.add_init_script(KILL_BG_SYNC)   # G3: remove Background Sync BEFORE voice.js runs
    page.add_init_script(KNOBS)

    state = {"online": False, "completed": [], "upload_attempts": 0}

    def route_sessions(route):
        route.fulfill(status=200, content_type="application/json",
                      body=json.dumps([{"sessionId": SID, "name": "QA Session 426",
                                        "repoPath": "C:/qa", "statusColor": "green",
                                        "activityState": "idle", "directorId": "dir-qa"}]))

    def route_upload(route):
        req = route.request; url = req.url
        state["upload_attempts"] += 1
        if not state["online"]:
            route.abort("connectionfailed"); return
        if url.endswith("/voice-turn/upload") and req.method == "POST":
            route.fulfill(status=200, content_type="application/json",
                          body=json.dumps({"upload_id": "up-" + str(int(time.time()*1000)) + "-" + str(state["upload_attempts"])})); return
        if "/chunk/" in url and req.method == "PUT":
            route.fulfill(status=200, body=""); return
        if url.endswith("/complete") and req.method == "POST":
            tid = "turn-srv-" + str(len(state["completed"]) + 1)
            state["completed"].append(tid)
            route.fulfill(status=202, content_type="application/json",
                          body=json.dumps({"turn_id": tid})); return
        route.continue_()

    def route_poll(route):
        route.fulfill(status=200, content_type="application/json",
                      body=json.dumps({"stage": "reply", "summary": "QA: reply fetched on reconnect."}))

    def route_turns(route):
        route.fulfill(status=200, content_type="application/json", body=json.dumps({"turns": []}))

    # Most-recently-registered route wins; register least->most specific.
    page.route("**/sessions", route_sessions)
    page.route(re.compile(r"/sessions/[^/]+/voice-turn/(?!upload)[^/]+$"), route_poll)
    page.route("**/sessions/*/voice-turns", route_turns)
    page.route("**/voice-turn/upload**", route_upload)

    # ===== G1: Service Worker registered + controlling /voice =====
    log("G1: navigate /voice")
    page.goto(BASE + "/voice", wait_until="networkidle")
    controller = None
    for _ in range(40):
        controller = page.evaluate("() => navigator.serviceWorker.controller ? navigator.serviceWorker.controller.scriptURL : null")
        if controller: break
        time.sleep(0.25)
    reg = page.evaluate("""async () => {
        const r = await navigator.serviceWorker.getRegistration('/voice');
        return r ? { scope: r.scope, active: !!r.active, hasSync: !!(r && r.sync) } : null; }""")
    log("G1: controller=%s  registration=%s" % (controller, reg))
    g1 = bool(controller and controller.endswith("/voice/sw.js") and reg and reg["scope"].endswith("/voice"))
    bg_sync_removed = bool(reg and reg["hasSync"] is False)
    if not g1: fails.append("G1: SW not registered/controlling /voice (controller=%s reg=%s)" % (controller, reg))
    if not bg_sync_removed: fails.append("G3-setup: reg.sync still present - Background Sync not isolated for the fallback proof")
    page.screenshot(path=OUT + "/g1-sw-controlling.png", full_page=True)
    log("G1 %s (Background-Sync-removed=%s)" % ("PASS" if g1 else "FAIL", bg_sync_removed))

    # Seed queue + open the session through the REAL UI (refresh -> click stubbed row).
    page.evaluate(SEED, SID)
    page.click("#refresh-btn")
    page.wait_for_selector("#session-list li", timeout=5000)
    page.click("#session-list li")
    page.wait_for_selector("#outbox-list li", timeout=5000)
    log("outbox rendered: %d rows, badges=%s" % (rows(page), badges(page)))

    # ===== G4: Stale badge while offline; rows are 60s old vs 3s rule =====
    try:
        page.wait_for_function("""() => Array.from(document.querySelectorAll('#outbox-list li .ob-badge'))
            .some(b => b.textContent.trim() === 'Stale')""", timeout=10000)
        g4_badge = True
    except Exception:
        g4_badge = False
    attempts_before = state["upload_attempts"]
    page.wait_for_timeout(3500)   # pump the event loop so the offline probe keeps retrying
    attempts_after = state["upload_attempts"]
    still_retrying = attempts_after > attempts_before
    still_queued = rows(page) == 2
    log("G4: stale-badge=%s  rows-still-queued=%s  upload-attempts %d->%d (retrying=%s)"
        % (g4_badge, still_queued, attempts_before, attempts_after, still_retrying))
    g4 = g4_badge and still_queued and still_retrying
    if not g4: fails.append("G4: stale=%s queued=%s retrying=%s" % (g4_badge, still_queued, still_retrying))
    page.screenshot(path=OUT + "/g4-stale-badge.png", full_page=True)
    log("G4 %s" % ("PASS" if g4 else "FAIL"))

    # ===== G2 + G3: auto-drain on reconnect via the FALLBACK path (no Background Sync) =====
    log("G2/G3: flip ONLINE + dispatch 'online' (no manual tap); Background Sync is removed")
    state["online"] = True
    page.evaluate("() => window.dispatchEvent(new Event('online'))")
    try:
        page.wait_for_function("() => document.querySelectorAll('#outbox-list li').length === 0", timeout=20000)
        drained = True
    except Exception:
        drained = False
    empty_visible = page.evaluate("() => !document.getElementById('outbox-empty').classList.contains('hidden')")
    reply = page.evaluate("() => document.getElementById('reply-box').textContent")
    log("G2/G3: rows=%d drained=%s empty-shown=%s completes=%s reply=%r"
        % (rows(page), drained, empty_visible, state["completed"], reply))
    g2 = drained and rows(page) == 0 and len(state["completed"]) == 2 and "reply fetched" in (reply or "").lower()
    g3 = g2  # drain happened with SyncManager removed => fallback path proven
    if not g2: fails.append("G2: drained=%s rows=%d completes=%s reply=%r" % (drained, rows(page), state["completed"], reply))
    if not g3: fails.append("G3: fallback drain failed (drained=%s)" % drained)
    page.screenshot(path=OUT + "/g2-g3-drained-fallback.png", full_page=True)
    log("G2 %s / G3 %s" % ("PASS" if g2 else "FAIL", "PASS" if g3 else "FAIL"))

    browser.close()

print("")
if fails:
    print("QA RESULT: FAIL")
    for f in fails: print("  -", f)
    sys.exit(1)
else:
    print("QA RESULT: PASS - all 4 acceptance criteria verified")
    sys.exit(0)
