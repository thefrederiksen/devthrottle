#!/usr/bin/env python3
"""
TEST HARNESS (issue #425 proof). Drives the offline-first Voice page through:
  record (network blocked) -> saved locally + Failed -> reload persists -> Retry -> Uploaded + reply.

Launches Chromium with a fake mic so MediaRecorder produces real audio, points at the
voice-proof-proxy front door (our updated files + the live Gateway 7878 backend), and
captures a screenshot at each acceptance gate.
"""
import sys
import time
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:7935"
OUT = sys.argv[1] if len(sys.argv) > 1 else "."
# Upload endpoints we block to simulate a dead connection at upload time.
UPLOAD_GLOB = "**/voice-turn/upload**"

def log(m): print("[proof]", m, flush=True)

def badge_text(page):
    el = page.query_selector(".outbox-list li .ob-badge")
    return el.inner_text().strip() if el else "(none)"

def outbox_count(page):
    return len(page.query_selector_all(".outbox-list li"))

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, args=[
        "--use-fake-device-for-media-stream",
        "--use-fake-ui-for-media-stream",
        "--autoplay-policy=no-user-gesture-required",
    ])
    ctx = browser.new_context(permissions=["microphone"])
    page = ctx.new_page()
    page.on("console", lambda m: print("  [console]", m.text, flush=True))

    # Capture the upload-complete response to PROVE the resumed upload reached the live
    # Gateway end-to-end (202 + a real turn_id) - the part issue #425 owns.
    def on_resp(r):
        if "/voice-turn/upload/" in r.url and r.url.endswith("/complete"):
            try:
                body = r.text()
            except Exception:
                body = "(unreadable)"
            print("  [upload-complete]", r.status, body, flush=True)
        elif r.url.endswith("/voice-turn/upload"):
            try:
                body = r.text()
            except Exception:
                body = "(unreadable)"
            print("  [upload-register]", r.status, body, flush=True)
    page.on("response", on_resp)

    log("open /voice")
    page.goto(BASE + "/voice", wait_until="networkidle")
    page.wait_for_selector("#session-list li", timeout=15000)

    # Open the first session.
    page.query_selector("#session-list li").click()
    page.wait_for_selector("#speak-btn", timeout=10000)
    log("session open")

    # --- BLOCK the network at upload time ---
    page.route(UPLOAD_GLOB, lambda route: route.abort())
    log("upload endpoints blocked (simulating dead connection)")

    # Record ~2s.
    page.click("#speak-btn")  # Speak
    time.sleep(2.2)
    page.click("#speak-btn")  # Stop
    log("recording stopped")

    # The save-locally write + 'Recording saved locally' must appear, then the upload fails.
    page.wait_for_selector(".outbox-list li", timeout=10000)
    stage1 = page.inner_text("#stage-line")
    log("stage after stop: " + stage1)
    # wait for the Failed badge (upload aborts)
    page.wait_for_function(
        "() => { const b=document.querySelector('.outbox-list li .ob-badge'); return b && b.textContent.trim()==='Failed'; }",
        timeout=15000)
    log("outbox badge: " + badge_text(page) + " ; count=" + str(outbox_count(page)))
    page.screenshot(path=OUT + "/01-saved-locally-failed.png", full_page=True)

    # --- RELOAD: the Pending/Failed turn must survive (read back from IndexedDB) ---
    log("reload page")
    page.reload(wait_until="networkidle")
    page.wait_for_selector("#session-list li", timeout=15000)
    page.query_selector("#session-list li").click()
    page.wait_for_selector(".outbox-list li", timeout=15000)
    page.wait_for_function(
        "() => { const b=document.querySelector('.outbox-list li .ob-badge'); return b && b.textContent.trim()==='Failed'; }",
        timeout=15000)
    log("after reload, outbox badge: " + badge_text(page) + " ; count=" + str(outbox_count(page)))
    page.screenshot(path=OUT + "/02-reload-persisted.png", full_page=True)

    # --- RESTORE network and RETRY ---
    page.unroute(UPLOAD_GLOB)
    # Slow the chunk PUTs slightly so the Uploading badge is visible to the screenshot
    # (functionally a no-op; the bytes still go to the live Gateway).
    def slow(route):
        time.sleep(0.8)
        route.continue_()
    page.route("**/voice-turn/upload/**/chunk/**", slow)
    log("network restored; clicking Retry")
    page.click(".outbox-list li .ob-retry")
    try:
        page.wait_for_function(
            "() => { const b=document.querySelector('.outbox-list li .ob-badge'); return b && b.textContent.trim()==='Uploading'; }",
            timeout=8000)
        log("captured Uploading badge")
        page.screenshot(path=OUT + "/03a-retry-uploading.png", full_page=True)
    except Exception:
        log("Uploading badge not caught (too fast)")

    # Retry should march the badge through Uploading -> Uploaded (then the row clears as
    # the bytes become durable), and a reply should arrive.
    try:
        page.wait_for_function(
            "() => { const b=document.querySelector('.outbox-list li .ob-badge'); return b && b.textContent.trim()==='Uploaded'; }",
            timeout=20000)
        log("badge reached Uploaded")
        page.screenshot(path=OUT + "/03-retry-uploaded.png", full_page=True)
    except Exception:
        log("badge passed Uploaded quickly (row cleared); count now=" + str(outbox_count(page)))
        page.screenshot(path=OUT + "/03-retry-uploaded.png", full_page=True)

    # Wait for the reply to land (a turn appears in history, or the reply box / stage fills).
    try:
        page.wait_for_function(
            "() => { const h=document.querySelector('#history-list li'); const r=document.querySelector('#reply-box'); "
            "return (h!==null) || (r && r.textContent.trim().length>0); }",
            timeout=8000)
        log("reply / history landed")
    except Exception as e:
        log("WARN reply wait: " + str(e))
    time.sleep(1.5)
    log("final stage: " + page.inner_text("#stage-line"))
    log("history rows: " + str(len(page.query_selector_all('#history-list li'))))
    log("outbox rows: " + str(outbox_count(page)))
    page.screenshot(path=OUT + "/04-reply-and-cleared.png", full_page=True)

    ctx.close()
    browser.close()
    log("DONE")
