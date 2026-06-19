"""Drive the proof page and capture screenshots + state for issue #533.

Loads the real /m/ page (served by serve_proof.py with the gateway stubbed), at a mobile viewport,
and exercises both entry paths:
  A. Tap the play triangle on a voice-ready row -> session view shown AND audio auto-playing.
  B. Tap the row body on a voice-ready row     -> session view shown AND NOT auto-playing.
Prints Expected vs Actual for each acceptance criterion and saves screenshots.
"""
import time
import sys
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:8533/m/index.html"
OUT = "D:/ReposFred/devthrottle-wt-533/docs/cencon/proof/issue-533"

results = []


def check(name, expected, actual):
    ok = (expected == actual)
    results.append((name, expected, actual, ok))
    print(("PASS" if ok else "FAIL") + " | " + name + " | expected=" + repr(expected) + " actual=" + repr(actual))


with sync_playwright() as p:
    browser = p.chromium.launch(args=["--autoplay-policy=no-user-gesture-required"])
    ctx = browser.new_context(viewport={"width": 390, "height": 844}, device_scale_factor=2)
    page = ctx.new_page()

    # ---- State 1: list with a voice-ready row ----
    page.goto(BASE)
    page.wait_for_selector(".scard-play", timeout=8000)
    time.sleep(0.4)
    ready_row = page.eval_on_selector_all(".scard-play", "els => els.length")
    list_visible = page.evaluate("!document.getElementById('list-view').classList.contains('hidden')")
    check("AC-list: voice-ready row shows a play triangle", True, ready_row >= 1)
    check("State1: list view visible", True, list_visible)
    page.screenshot(path=OUT + "/01-list.png")

    # ---- State 2: TAP THE TRIANGLE -> open session + auto-play ----
    page.click(".scard-play")
    # Wait for the session view and for auto-play to enter the speaking state.
    page.wait_for_function("!document.getElementById('session-view').classList.contains('hidden')", timeout=8000)
    # waitPlayable + play() may take a moment to buffer; poll for speaking up to a few seconds.
    speaking = False
    for _ in range(60):
        speaking = page.evaluate("document.getElementById('play-btn').classList.contains('speaking')")
        if speaking:
            break
        time.sleep(0.1)
    in_session = page.evaluate("window.__proof.inSessionView()")
    paused = page.evaluate("document.getElementById('tts-audio').paused")
    pstate = page.evaluate("window.__proof.playState()")
    check("AC1: triangle navigates to session view (session shown, list hidden)", True, in_session)
    check("AC2: ready voice auto-plays - Play button in 'speaking' state", True, speaking)
    check("AC2: audio element is actually playing (not paused)", False, paused)
    print("   triangle-path playState =", pstate, " heroStatus =", page.evaluate("window.__proof.heroStatus()"))
    page.screenshot(path=OUT + "/02-triangle-session-playing.png")

    # ---- State 3: back to list, TAP THE ROW BODY -> open session, NO auto-play ----
    page.click("#back-btn")
    page.wait_for_selector(".scard-play", timeout=8000)
    time.sleep(0.3)
    # Click the row body (the name area), not the triangle.
    page.click(".scard-main")
    page.wait_for_function("!document.getElementById('session-view').classList.contains('hidden')", timeout=8000)
    # Give the same buffering window the triangle path got, to prove it stays idle (does NOT auto-play).
    time.sleep(3.5)
    in_session2 = page.evaluate("window.__proof.inSessionView()")
    speaking2 = page.evaluate("document.getElementById('play-btn').classList.contains('speaking')")
    ready2 = page.evaluate("document.getElementById('play-btn').classList.contains('ready')")
    paused2 = page.evaluate("document.getElementById('tts-audio').paused")
    pstate2 = page.evaluate("window.__proof.playState()")
    check("AC3: row-body tap opens the session view", True, in_session2)
    check("AC3: row-body tap does NOT auto-play (Play button not speaking)", False, speaking2)
    check("AC3: row-body tap leaves audio paused (idle)", True, paused2)
    check("AC3: Play button settles to 'ready' (buffered, manual play available)", True, ready2)
    print("   row-body-path playState =", pstate2, " heroStatus =", page.evaluate("window.__proof.heroStatus()"))
    page.screenshot(path=OUT + "/03-rowtap-session-idle.png")

    ctx.close()
    browser.close()

print("\n==== SUMMARY ====")
allok = all(r[3] for r in results)
for name, exp, act, ok in results:
    print(("PASS" if ok else "FAIL") + " " + name)
print("ALL_PASS=" + str(allok))
sys.exit(0 if allok else 2)
