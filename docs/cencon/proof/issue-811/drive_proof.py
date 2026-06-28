"""
Drive the Chat-mode proof for issue #811 against the mock Gateway, at a phone-sized viewport, with
Chrome fake media for the dictation Speak control. Captures one screenshot per acceptance criterion
into docs/cencon/proof/issue-811/ and copies the wire-log for the request-body / bearer captures.
"""
import json
import subprocess
import sys
import time
import wave
import struct
import math
import urllib.request
import urllib.error
from pathlib import Path
from playwright.sync_api import sync_playwright

HERE = Path(__file__).parent
WT = Path(r"D:\ReposFred\devthrottle-wt-811h")
DIST = WT / "mobile" / "dist"
OUT = WT / "docs" / "cencon" / "proof" / "issue-811"
OUT.mkdir(parents=True, exist_ok=True)
SID = "11111111-1111-1111-1111-111111111111"
PY = sys.executable


def make_wav(path):
    # 1.5s 440Hz mono 16k sine - the fake-audio source for getUserMedia.
    rate = 16000
    with wave.open(str(path), "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        for i in range(int(rate * 1.5)):
            w.writeframes(struct.pack("<h", int(0.3 * 32767 * math.sin(2 * math.pi * 440 * i / rate))))


def wait_port(port, timeout=20):
    # Any HTTP response (even 401 in auth-on mode) means the server is up and listening.
    for _ in range(timeout * 5):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/sessions", timeout=1).read()
            return True
        except urllib.error.HTTPError:
            return True
        except Exception:
            time.sleep(0.2)
    return False


def start_server(port, auth, log_path):
    p = subprocess.Popen([PY, str(HERE / "mock_gateway.py"), str(DIST), str(port), auth, str(log_path)])
    if not wait_port(port):
        raise RuntimeError(f"server on {port} did not start")
    return p


def shot(page, name):
    page.screenshot(path=str(OUT / name))
    print("captured", name)


def run_auth_on(pw, wav):
    port = 7866
    log = OUT / "requests-auth-on.jsonl"
    srv = start_server(port, "on", log)
    browser = pw.chromium.launch(args=[
        "--use-fake-ui-for-media-stream",
        "--use-fake-device-for-media-stream",
        f"--use-file-for-fake-audio-capture={wav}",
    ])
    ctx = browser.new_context(viewport={"width": 390, "height": 844}, device_scale_factor=2)
    page = ctx.new_page()
    base = f"http://127.0.0.1:{port}/m"
    try:
        # Roster
        page.goto(base + "/", wait_until="domcontentloaded")
        page.wait_for_timeout(800)
        shot(page, "01-roster.png")

        # Chat default - tool calls / results / thinking HIDDEN even though the session HAS them (AC2)
        page.goto(f"{base}/session/{SID}/chat", wait_until="domcontentloaded")
        page.wait_for_timeout(1200)
        shot(page, "02-chat-default-hidden.png")

        # AC3: toggle each category on
        page.get_by_role("checkbox", name="Tool calls").check()
        page.wait_for_timeout(500)
        shot(page, "03-toggle-toolcalls.png")
        page.get_by_role("checkbox", name="Results").check()
        page.wait_for_timeout(500)
        shot(page, "04-toggle-results.png")
        page.get_by_role("checkbox", name="Thinking").check()
        page.wait_for_timeout(500)
        shot(page, "05-toggle-thinking-all-on.png")

        # AC4: back to default and capture the formatted assistant response (markdown, HTML inert,
        # ANSI stripped, machinery dropped, links with copy buttons)
        page.get_by_role("checkbox", name="Tool calls").uncheck()
        page.get_by_role("checkbox", name="Results").uncheck()
        page.get_by_role("checkbox", name="Thinking").uncheck()
        page.wait_for_timeout(500)
        shot(page, "06-rendering-parity.png")

        # AC6: type + Send -> the new user turn appears
        page.locator(".term-input").fill("Looks good, please ship it.")
        page.get_by_role("button", name="Send", exact=True).click()
        page.wait_for_timeout(800)
        shot(page, "07-send-new-turn.png")

        # AC7: control keys (bodies captured in the wire log)
        for key in ["Enter", "Esc", "Stop", "Up", "Down", "Left", "Right"]:
            page.get_by_role("button", name=key, exact=True).click()
            page.wait_for_timeout(150)
        shot(page, "08-control-keys.png")

        # AC8: Speak dictation dialog (the SAME shared dialog)
        page.get_by_role("button", name="Speak", exact=True).click()
        page.wait_for_timeout(1200)
        shot(page, "09-dictation-recording.png")
        # Pause -> transcribe the segment -> PAUSED with transcript
        page.locator(".dictate-pause").click()
        page.wait_for_timeout(2500)
        shot(page, "10-dictation-paused.png")
        # Insert -> drops transcript into the box
        page.locator(".dictate-insert").click()
        page.wait_for_timeout(600)
        shot(page, "11-dictation-inserted.png")

        # AC5: live update without yanking a scrolled-up reader. Scroll to top, append a turn
        # out-of-band (a new turn completing), confirm the scroll position does not jump.
        page.locator(".chat-scroll").evaluate("el => el.scrollTop = 0")
        page.wait_for_timeout(300)
        top_before = page.locator(".chat-scroll").evaluate("el => el.scrollTop")
        urllib.request.urlopen(urllib.request.Request(
            f"http://127.0.0.1:{port}/sessions/{SID}/prompt",
            data=json.dumps({"text": "A NEW ASSISTANT-DRIVEN TURN", "appendEnter": True}).encode(),
            headers={"Content-Type": "application/json", "Authorization": "Bearer test-gateway-token-811"},
            method="POST")).read()
        page.wait_for_timeout(3500)  # > one 2.5s poll
        top_after = page.locator(".chat-scroll").evaluate("el => el.scrollTop")
        Path(OUT / "ac5-scroll.txt").write_text(
            f"scrollTop before append (scrolled up) = {top_before}\n"
            f"scrollTop after a new turn arrived    = {top_after}\n"
            f"no-yank PASS = {abs(top_before - top_after) < 5}\n", encoding="utf-8")
        shot(page, "12-liveupdate-scrolled-up-not-yanked.png")
        # Now scroll to bottom to show the new turn did land and is followed when at bottom.
        page.locator(".chat-scroll").evaluate("el => el.scrollTop = el.scrollHeight")
        page.wait_for_timeout(400)
        shot(page, "13-liveupdate-bottom-shows-new-turn.png")

        # AC1 + AC9: switch to the Terminal raw mirror of the SAME session via the view toggle.
        page.get_by_role("tab", name="Terminal").click()
        page.wait_for_timeout(1500)
        shot(page, "14-terminal-raw-mirror.png")
        # From Terminal, reveal controls and Send (drives the SAME session)
        page.get_by_role("button", name="Keys", exact=True).click()
        page.wait_for_timeout(300)
        page.locator(".term-input").fill("sent from the Terminal view")
        page.get_by_role("button", name="Send", exact=True).click()
        page.wait_for_timeout(600)
        # Back to Chat - the Terminal-sent turn is now in the SAME session's history
        page.get_by_role("tab", name="Chat").click()
        page.wait_for_timeout(2000)
        page.locator(".chat-scroll").evaluate("el => el.scrollTop = el.scrollHeight")
        page.wait_for_timeout(400)
        shot(page, "15-crossview-terminal-send-in-chat.png")
    finally:
        ctx.close()
        browser.close()
        srv.terminate()
        try:
            srv.wait(timeout=5)
        except Exception:
            srv.kill()


def run_auth_off(pw):
    port = 7867
    log = OUT / "requests-auth-off.jsonl"
    srv = start_server(port, "off", log)
    browser = pw.chromium.launch()
    ctx = browser.new_context(viewport={"width": 390, "height": 844}, device_scale_factor=2)
    page = ctx.new_page()
    base = f"http://127.0.0.1:{port}/m"
    try:
        page.goto(f"{base}/session/{SID}/chat", wait_until="domcontentloaded")
        page.wait_for_timeout(1500)
        shot(page, "16-chat-auth-off.png")
    finally:
        ctx.close()
        browser.close()
        srv.terminate()
        try:
            srv.wait(timeout=5)
        except Exception:
            srv.kill()


def main():
    wav = HERE / "fake_audio.wav"
    make_wav(wav)
    with sync_playwright() as pw:
        run_auth_on(pw, wav)
        run_auth_off(pw)
    print("DONE")


if __name__ == "__main__":
    main()
