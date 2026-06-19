"""
Issue #535 proof harness. Drives the REAL wwwroot/m/ assets (m.js, m.css, index.html)
in headless chromium at a 390x844 mobile viewport, with the gateway endpoints stubbed,
and asserts the post-send navigation behavior for every acceptance criterion.

Outcome (PASS/FAIL per criterion) is printed and screenshots are written to OUT.
"""
import http.server, socketserver, threading, json, os, sys, time, functools

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    "src", "CcDirector.Cockpit", "wwwroot")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "proof535")
os.makedirs(OUT, exist_ok=True)

# Control flags toggled by the test to shape the stubbed gateway responses.
STATE = {"voice_turn_fail": False, "voice_turn_calls": [], "ask_direct_calls": []}

SESSIONS = {
    "sessions": [
        {"sessionId": "sess-A", "name": "Repo Alpha", "repoPath": "C:/repos/alpha",
         "machineName": "dev-machine-1", "statusColor": "red",
         "assessedState": "WaitingForInput", "activityState": "WaitingForInput",
         "lastActivityAt": "2026-06-19T10:00:00Z", "onHold": False, "briefingState": "None"},
        {"sessionId": "sess-B", "name": "Repo Beta", "repoPath": "C:/repos/beta",
         "machineName": "dev-machine-1", "statusColor": "green",
         "assessedState": "Running", "activityState": "Running",
         "lastActivityAt": "2026-06-19T09:00:00Z", "onHold": False, "briefingState": "None"},
    ]
}

class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _file(self, relpath):
        # Strip query string; serve from wwwroot.
        relpath = relpath.split("?", 1)[0].lstrip("/")
        full = os.path.join(ROOT, *relpath.split("/"))
        if not os.path.isfile(full):
            self.send_response(404); self.end_headers(); return
        ctype = ("text/html" if full.endswith(".html") else
                 "application/javascript" if full.endswith(".js") else
                 "text/css" if full.endswith(".css") else "application/octet-stream")
        with open(full, "rb") as f: data = f.read()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        p = self.path
        if p.startswith("/sessions?") or p == "/sessions":
            self._json(SESSIONS); return
        if p.startswith("/wingman/voice/ready"):
            self._json({"sids": []}); return
        if "/wingman/menu" in p:
            self._json({"isMenu": False}); return
        if "/wingman/voice/audio" in p:
            self.send_response(404); self.end_headers(); return
        if "/wingman/voice" in p:
            self._json({"ready": False}); return
        # static assets (/m/index.html, /m/m.js, /m/m.css, /m/sw.js)
        self._file(p)

    def do_POST(self):
        n = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(n) if n else b"{}"
        try: payload = json.loads(raw.decode("utf-8"))
        except Exception: payload = {}
        p = self.path
        if "/wingman/voice-turn" in p:
            STATE["voice_turn_calls"].append(payload)
            if STATE["voice_turn_fail"]:
                self._json({"error": "agent delivery failed (forced)"}, code=500); return
            self._json({"needsChoice": False, "spoken": "Agent turn done.",
                        "reply": "The agent processed your message."}); return
        if "/wingman/transcribe" in p:
            self._json({"transcript": "this is the transcribed recording"}); return
        if "/wingman/ask-direct" in p:
            STATE["ask_direct_calls"].append(payload)
            self._json({"spoken": "Here is the wingman's spoken answer.",
                        "reply": ""}); return
        if "/wingman/tts" in p:
            # 1x1 silent-ish wav bytes; the page only needs a blob it can attempt to load.
            wav = b"RIFF" + b"\x00" * 40
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(wav)))
            self.end_headers(); self.wfile.write(wav); return
        self._json({})

def serve():
    httpd = socketserver.TCPServer(("127.0.0.1", 8753), H)
    httpd.allow_reuse_address = True
    t = threading.Thread(target=httpd.serve_forever, daemon=True); t.start()
    return httpd

def main():
    from playwright.sync_api import sync_playwright
    httpd = serve(); time.sleep(0.4)
    base = "http://127.0.0.1:8753/m/index.html"
    results = []
    def check(name, ok, detail=""):
        results.append((name, ok, detail))
        print(("PASS" if ok else "FAIL") + " :: " + name + ("  -- " + detail if detail else ""))

    with sync_playwright() as pw:
        # Fake mic so the REAL record path (startRecording -> MediaRecorder -> onRecordingStopped)
        # runs in headless chromium; this exercises the voice-to-agent / voice-to-wingman navigation
        # added in #535 rather than a stub.
        br = pw.chromium.launch(args=[
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",
        ])
        ctx = br.new_context(viewport={"width": 390, "height": 844},
                             service_workers="block",
                             permissions=["microphone"])
        pg = ctx.new_page()

        def open_session():
            pg.goto(base, wait_until="load")
            pg.wait_for_function("document.querySelectorAll('#session-list li').length>0", timeout=8000)
            pg.locator("#session-list li").first.click()
            pg.wait_for_selector("#session-view:not(.hidden)", timeout=4000)
            pg.evaluate("document.getElementById('text-section').open=true")

        def on_list():
            return pg.evaluate("(!document.getElementById('list-view').classList.contains('hidden')) "
                               "&& document.getElementById('session-view').classList.contains('hidden')")
        def on_session():
            return pg.evaluate("(!document.getElementById('session-view').classList.contains('hidden')) "
                               "&& document.getElementById('list-view').classList.contains('hidden')")

        # ---- AC1: text Send to agent returns to the LIST on send ----
        STATE["voice_turn_fail"] = False; STATE["voice_turn_calls"].clear()
        open_session()
        pg.fill("#typed-text", "summarize the last three turns")
        pg.click("#ask-agent-btn")
        pg.wait_for_selector("#list-view:not(.hidden)", timeout=4000)
        # wait until the stubbed voice-turn POST has been recorded
        for _ in range(40):
            if STATE["voice_turn_calls"]: break
            time.sleep(0.1)
        ac1 = on_list() and len(STATE["voice_turn_calls"]) == 1 \
              and STATE["voice_turn_calls"][0].get("text") == "summarize the last three turns"
        check("AC1 text Send to agent -> LIST view, turn sent", ac1,
              "on_list=%s calls=%s" % (on_list(), STATE["voice_turn_calls"]))
        pg.screenshot(path=os.path.join(OUT, "ac1_text_agent_list.png"))

        # ---- AC2: voice-to-agent (Talk to the agent -> Send) returns to the LIST on Send ----
        STATE["voice_turn_fail"] = False; STATE["voice_turn_calls"].clear()
        open_session()
        pg.click("#talk-btn")                       # start recording for the agent
        pg.wait_for_selector("#rec-controls:not(.hidden)", timeout=4000)
        time.sleep(1.2)                             # let MediaRecorder capture a chunk
        pg.click("#send-btn")                       # Send -> onRecordingStopped -> showList()
        pg.wait_for_selector("#list-view:not(.hidden)", timeout=6000)
        # the recording continues in the background through the durable outbox -> voice-turn lands
        for _ in range(80):
            if STATE["voice_turn_calls"]: break
            time.sleep(0.1)
        ac2 = on_list() and len(STATE["voice_turn_calls"]) >= 1 \
              and STATE["voice_turn_calls"][0].get("text") == "this is the transcribed recording"
        check("AC2 voice-to-agent (Send) -> LIST view; upload/transcribe/send continues in background",
              ac2, "on_list=%s calls=%s" % (on_list(), STATE["voice_turn_calls"]))
        pg.screenshot(path=os.path.join(OUT, "ac2_voice_agent_list.png"))

        # ---- AC3: text Ask the wingman STAYS on the session view + shows the answer ----
        STATE["ask_direct_calls"].clear()
        open_session()
        pg.fill("#typed-text", "what changed recently")
        pg.click("#ask-wingman-btn")
        for _ in range(40):
            if STATE["ask_direct_calls"]: break
            time.sleep(0.1)
        # give the DOM a tick to render the answer
        pg.wait_for_function(
            "document.getElementById('hero-summary').textContent.indexOf(\"wingman's spoken answer\")>=0",
            timeout=4000)
        # #hero-summary lives inside the collapsed <details id=text-section>, so read textContent
        # (inner_text would be empty while collapsed - rendering, not population).
        hs3 = pg.text_content("#hero-summary") or ""
        ac3 = on_session() and len(STATE["ask_direct_calls"]) == 1 and "spoken answer" in hs3
        check("AC3 text Ask the wingman -> STAY on session + answer shown", ac3,
              "on_session=%s answer=%r" % (on_session(), hs3[:60]))
        pg.evaluate("document.getElementById('text-section').open=true")
        pg.screenshot(path=os.path.join(OUT, "ac3_text_wingman_stay.png"))

        # ---- AC4: voice-to-wingman (Talk to the wingman -> Send) STAYS on the session view ----
        STATE["ask_direct_calls"].clear()
        open_session()
        pg.click("#talk-wingman-btn")               # start recording for the wingman
        pg.wait_for_selector("#rec-controls:not(.hidden)", timeout=4000)
        time.sleep(1.2)
        pg.click("#send-btn")                        # Send -> stays on session, routes to wingman
        for _ in range(80):
            if STATE["ask_direct_calls"]: break
            time.sleep(0.1)
        pg.wait_for_function(
            "document.getElementById('hero-summary').textContent.indexOf(\"wingman's spoken answer\")>=0",
            timeout=6000)
        hs4 = pg.text_content("#hero-summary") or ""
        ac4 = on_session() and len(STATE["ask_direct_calls"]) >= 1 and "spoken answer" in hs4
        check("AC4 voice-to-wingman (Send) -> STAY on session + answer shown", ac4,
              "on_session=%s calls=%s" % (on_session(), STATE["ask_direct_calls"]))
        pg.evaluate("document.getElementById('text-section').open=true")
        pg.screenshot(path=os.path.join(OUT, "ac4_voice_wingman_stay.png"))

        # ---- AC6 / failure: text agent send forced to fail must NOT be silently lost ----
        # After navigating to the list, the originating row must visibly show it is still sending
        # (blocked[sid] -> "sending..." on the row) while it retries; never disappears silently.
        STATE["voice_turn_fail"] = True; STATE["voice_turn_calls"].clear()
        open_session()
        pg.fill("#typed-text", "please run the build")
        pg.click("#ask-agent-btn")
        pg.wait_for_selector("#list-view:not(.hidden)", timeout=4000)
        # at least one failing attempt recorded + the row shows "sending..."
        for _ in range(60):
            if STATE["voice_turn_calls"]: break
            time.sleep(0.1)
        pg.wait_for_function(
            "Array.from(document.querySelectorAll('#session-list .scard-sub'))"
            ".some(function(e){return e.textContent.indexOf('sending')>=0;})",
            timeout=6000)
        row_sending = pg.evaluate(
            "Array.from(document.querySelectorAll('#session-list .scard-sub'))"
            ".some(function(e){return e.textContent.indexOf('sending')>=0;})")
        attempts_before = len(STATE["voice_turn_calls"])
        # prove it KEEPS retrying (not lost): wait for a second attempt
        time.sleep(2.5)
        attempts_after = len(STATE["voice_turn_calls"])
        ac_fail = on_list() and row_sending and attempts_after > attempts_before
        check("AC-fail forced agent-send failure is surfaced (row 'sending...') and retried, not lost",
              ac_fail, "on_list=%s row_sending=%s attempts %d->%d"
              % (on_list(), row_sending, attempts_before, attempts_after))
        pg.screenshot(path=os.path.join(OUT, "ac_fail_row_sending.png"))

        # now let it succeed and confirm the row clears (recovery, no orphan block)
        STATE["voice_turn_fail"] = False
        pg.wait_for_function(
            "!Array.from(document.querySelectorAll('#session-list .scard-sub'))"
            ".some(function(e){return e.textContent.indexOf('sending')>=0;})",
            timeout=15000)
        check("AC-fail recovery: once delivery succeeds the 'sending...' row clears", True,
              "row cleared")
        pg.screenshot(path=os.path.join(OUT, "ac_fail_recovered.png"))

        # ---- AC7: cache-bust version bumped to v18, no v17 left ----
        html = open(os.path.join(ROOT, "m", "index.html"), encoding="utf-8").read()
        sw = open(os.path.join(ROOT, "m", "sw.js"), encoding="utf-8").read()
        ac7 = ("m.js?v=18" in html and "m.css?v=18" in html and ">v18<" in html.replace(" ", "")
               or "v18" in html) and "wingman-voice-v18" in sw \
              and "v=17" not in html and "v17" not in sw
        ac7 = ("m.js?v=18" in html) and ("m.css?v=18" in html) and ("v18" in html) \
              and ("wingman-voice-v18" in sw) and ("v=18" in sw) \
              and ("v=17" not in html) and ("v=17" not in sw)
        check("AC7 cache-bust bumped to v18 (index.html + sw.js), no v17 remaining", ac7,
              "html_has_v18=%s sw_has_v18=%s" % ("v18" in html, "v18" in sw))

        br.close()

    httpd.shutdown()
    print("\nSUMMARY: %d/%d criteria PASS" % (sum(1 for _,ok,_ in results if ok), len(results)))
    failed = [n for n,ok,_ in results if not ok]
    if failed:
        print("FAILED:", failed); sys.exit(1)
    sys.exit(0)

if __name__ == "__main__":
    main()
