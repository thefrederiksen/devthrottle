"""
Voice-mode operator harness.

Drives the CC Director Voice tab from a desktop Chromium in MOBILE viewport,
injecting a prepared WAV as the microphone (Chromium fake-audio capture), so we
can play the in-car operator end-to-end WITHOUT a phone. The one thing this
cannot exercise is a real cellular upload from the phone; we emulate spotty
network with CDP Network.emulateNetworkConditions instead.

It walks the voice interaction lifecycle, screenshots every stage, records the
network calls (/voice/command, /chat, /tts) with status + timing, and writes a
results.json the report generator consumes.

Usage:
    python voice_harness.py --base http://127.0.0.1:7880 --sid <session-id> \
        --audio-dir <dir> --out <out-dir>
"""
import argparse
import json
import time
import wave
import os
import sys
from datetime import datetime, timezone
from playwright.sync_api import sync_playwright

# Windows consoles default to cp1252; agent replies contain unicode. Never let a
# print() crash the run.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def wav_seconds(path):
    with wave.open(path, "rb") as w:
        return w.getnframes() / float(w.getframerate())


def now_iso():
    return datetime.now(timezone.utc).isoformat()


class Recorder:
    """Collects per-stage results, screenshots, and network events for one run."""

    def __init__(self, out_dir, scenario):
        self.out_dir = out_dir
        self.scenario = scenario
        self.stages = []
        self.network = []
        self.console = []

    def stage(self, key, ok, note, shot=None, extra=None):
        rec = {
            "key": key,
            "ok": bool(ok),
            "note": note,
            "screenshot": shot,
            "at": now_iso(),
        }
        if extra:
            rec.update(extra)
        self.stages.append(rec)
        flag = "PASS" if ok else "FAIL"
        print(f"  [{flag}] {key}: {note}")

    def shot(self, page, name):
        fn = f"{self.scenario}_{name}.png"
        path = os.path.join(self.out_dir, fn)
        page.screenshot(path=path, full_page=False)
        return fn


def attach_listeners(page, rec):
    interesting = ("/voice/command", "/voice/utterance", "/chat", "/tts",
                   "/voice-mode", "/turn-summaries", "/wingman")

    def on_response(resp):
        url = resp.url
        if any(seg in url for seg in interesting):
            entry = {"url": url.split("://", 1)[-1], "status": resp.status,
                     "method": resp.request.method, "at": now_iso()}
            # Pull small JSON bodies (transcript/reply) where useful.
            if "/voice/command" in url or "/chat" in url:
                try:
                    entry["body"] = resp.json()
                except Exception:
                    pass
            rec.network.append(entry)

    def on_request(req):
        if any(seg in req.url for seg in interesting):
            entry = {"url": req.url.split("://", 1)[-1], "status": "(request)",
                     "method": req.method, "at": now_iso()}
            # Capture the POST body for /chat (to see pollOnly/wantProgress) and
            # /tts (to confirm WHICH text was actually sent to be spoken).
            if "/tts" in req.url or "/chat" in req.url:
                try:
                    entry["post"] = req.post_data
                except Exception:
                    pass
            rec.network.append(entry)

    page.on("request", on_request)
    page.on("response", on_response)
    page.on("console", lambda m: rec.console.append({"type": m.type, "text": m.text[:400]}))


def open_voice_tab(page, base, sid, rec):
    page.goto(f"{base}/sessions/{sid}/view", wait_until="domcontentloaded")
    page.wait_for_timeout(1500)
    rec.stage("01_session_view_loaded", True, "Session view loaded in mobile viewport",
              rec.shot(page, "01_loaded"))

    tab = page.query_selector('.tab[data-view="voice"]')
    if not tab:
        rec.stage("02_voice_tab", False, "Voice tab not found in DOM")
        return False
    tab.click()
    page.wait_for_timeout(1200)
    state = (page.text_content("#voiceState") or "").strip()
    btn_disabled = page.get_attribute("#voiceMainBtn", "disabled") is not None
    ok = ("ready" in state.lower()) and not btn_disabled
    rec.stage("02_voice_tab", ok,
              f"Voice tab active. state='{state}' main_btn_disabled={btn_disabled}",
              rec.shot(page, "02_voice_tab"))
    return True


def run_happy_path(page, rec, record_window_s):
    # --- Tap to talk -> recording overlay ---
    page.click("#voiceMainBtn")
    overlay_ok = False
    try:
        page.wait_for_selector("#recordingOverlay.show", timeout=8000)
        overlay_ok = True
    except Exception:
        pass
    page.wait_for_timeout(1200)  # let the elapsed timer tick + audio flow
    state = (page.text_content("#voiceState") or "").strip()
    rec.stage("03_recording", overlay_ok,
              f"Recording overlay shown={overlay_ok}. state='{state}'",
              rec.shot(page, "03_recording"))

    # Hold long enough to capture >=1 full clean pass of the looping fake audio.
    print(f"  ... holding record for {record_window_s:.1f}s (fake mic playing)")
    page.wait_for_timeout(int(record_window_s * 1000))

    # --- Tap to send ---
    if page.query_selector("#recStopBtn"):
        page.click("#recStopBtn")
    else:
        page.click("#voiceMainBtn")
    page.wait_for_timeout(800)
    rec.stage("04_sent", True, "Tapped send; upload/transcribe started",
              rec.shot(page, "04_sent"))

    # --- Wait for transcript (latest-user slot) ---
    # New DOM: the Voice tab shows only the latest exchange in fixed slots, not a
    # growing bubble log. The user's transcript lands in #voiceLatestUser (hidden
    # until populated), the reply in #voiceLatestReply.
    transcript = None
    try:
        page.wait_for_selector("#voiceLatestUser:not([hidden])", timeout=60000)
        transcript = page.text_content("#voiceLatestUser")
        transcript = (transcript or "").strip()
    except Exception:
        pass
    rec.stage("05_transcribed", bool(transcript),
              f"Transcript: {transcript!r}",
              rec.shot(page, "05_transcribed"),
              extra={"transcript": transcript})

    # --- Wait for agent reply (latest-reply slot) ---
    # #voiceLatestReply shows the EAR-FRIENDLY SPOKEN rewrite (chat.summary). The
    # full technical reply (chat.displayText) lives behind "Show full reply" in
    # #voiceLatestFullBody, present only when it differs from the spoken version.
    # Capture both so the report can show the spoken-vs-full distinction, which is
    # the whole point of the rewrite (read concepts aloud, never code).
    spoken = None
    full = None
    t0 = time.time()
    try:
        page.wait_for_selector("#voiceLatestReply:not([hidden])", timeout=150000)
        spoken = (page.text_content("#voiceLatestReply") or "").strip()
        full = (page.text_content("#voiceLatestFullBody") or "").strip()
    except Exception:
        pass
    elapsed = round(time.time() - t0, 1)
    has_separate_full = bool(full) and full != spoken
    rec.stage("06_agent_reply", bool(spoken),
              (f"Spoken reply after {elapsed}s: {spoken[:200]!r}"
               + (f" | full reply available behind tap ({len(full)} chars)" if has_separate_full
                  else " | no separate full reply (spoken == full)"))
              if spoken else f"No agent reply within timeout ({elapsed}s)",
              rec.shot(page, "06_agent_reply"),
              extra={"spoken": spoken, "full": full,
                     "has_separate_full": has_separate_full, "reply_wait_s": elapsed})

    # --- TTS check: speak() fires /tts asynchronously AFTER the reply bubble, so
    # give it a few seconds before judging / closing the page. ---
    page.wait_for_timeout(6000)
    tts_calls = [n for n in rec.network if "/tts" in n["url"]]
    statuses = [c["status"] for c in tts_calls]
    rec.stage("07_tts_spoken", len(tts_calls) > 0,
              f"/tts calls: {len(tts_calls)} (events: {statuses})",
              rec.shot(page, "07_after_speak"))


def chunk_puts(rec):
    """Count chunk PUT requests/responses seen so far."""
    reqs = [n for n in rec.network if "/voice/utterance/" in n["url"] and "/chunk/" in n["url"]]
    ok = [n for n in reqs if n["status"] == 200]
    return len(reqs), len(ok)


def run_resilience(page, rec, record_window_s, cdp):
    """Drop the network DURING recording so chunks genuinely queue, then restore
    and confirm the held chunks resume and the utterance still transcribes. This
    is the real test of the chunked design: what already uploaded stays uploaded."""
    page.click("#voiceMainBtn")
    try:
        page.wait_for_selector("#recordingOverlay.show", timeout=8000)
    except Exception:
        pass

    # Record a few seconds with the network UP so some chunks upload normally.
    page.wait_for_timeout(4500)
    before_total, before_ok = chunk_puts(rec)

    # Now drop the network WHILE still recording. New chunks keep being produced
    # (held locally) and the uploader should retry rather than lose them.
    cdp.send("Network.emulateNetworkConditions", {
        "offline": True, "latency": 0, "downloadThroughput": 0, "uploadThroughput": 0})
    print(f"  ... {before_ok} chunks up before drop; network OFFLINE mid-recording")
    # Keep recording through the outage so chunks pile up in the local queue.
    page.wait_for_timeout(int(record_window_s * 1000))
    state_off = (page.text_content("#voiceState") or "").strip()
    # Tap send while still offline: it must hold, not fail.
    if page.query_selector("#recStopBtn"):
        page.click("#recStopBtn")
    else:
        page.click("#voiceMainBtn")
    page.wait_for_timeout(2500)
    holding = "retry" in state_off.lower() or "holding" in state_off.lower() \
        or "retry" in (page.text_content("#voiceState") or "").lower() \
        or "holding" in (page.text_content("#voiceState") or "").lower()
    rec.stage("08_offline_holds", holding,
              f"Dropped network mid-recording after {before_ok} chunks; UI held the audio "
              f"(state: '{(page.text_content('#voiceState') or '').strip()}') instead of failing.",
              rec.shot(page, "08_offline_holds"),
              extra={"chunks_up_before_drop": before_ok})

    # Restore the network; held chunks should drain and the utterance complete.
    cdp.send("Network.emulateNetworkConditions", {
        "offline": False, "latency": 50, "downloadThroughput": 5_000_000, "uploadThroughput": 1_000_000})
    print("  ... network ONLINE; held chunks should resume")
    recovered = False
    try:
        page.wait_for_selector("#voiceLatestUser:not([hidden])", timeout=90000)
        recovered = True
    except Exception:
        pass
    page.wait_for_timeout(1500)
    after_total, after_ok = chunk_puts(rec)
    rec.stage("09_resumed", recovered,
              f"After reconnect the held chunks resumed: {after_ok} chunks uploaded total "
              f"(was {before_ok} before the drop) and the utterance transcribed without re-recording."
              if recovered else "Did NOT recover within timeout after reconnect",
              rec.shot(page, "09_resumed"),
              extra={"chunks_up_after_recover": after_ok, "chunks_up_before_drop": before_ok})


def run_progress(page, rec):
    """Drive a LONG agent turn (>2 min) and prove the periodic spoken progress
    note fires: the client requests a note about every two minutes, the note is
    shown on screen AND sent to /tts to be read aloud, and the real answer still
    arrives afterwards. Uses the typed composer (deterministic prompt) rather than
    the mic. Slow by nature - one progress note lands around the two-minute mark."""
    # A genuinely slow but completely benign request. Phrasing matters on two
    # counts: (1) imperative "run exactly this, do not explain" trips the agent's
    # prompt-injection guard and it refuses; (2) without "foreground / wait", the
    # agent backgrounds the command and replies in seconds. We need it to BLOCK in
    # the foreground for ~3 min so the turn stays Working past the two-minute mark.
    PROMPT = ("Please use the Bash tool to run a slow benchmark for me, and wait for it to "
              "finish before you reply. Run it in the foreground (not in the background) and "
              "set the command timeout to 300 seconds. The command: a loop that prints a step "
              "number then sleeps 5 seconds, repeated 35 times, which takes a bit under three "
              "minutes. Reply only once it has completed.")
    page.fill("#prompt", PROMPT)
    page.click("#sendBtn")
    page.wait_for_timeout(1500)
    rec.stage("10_long_turn_sent", True,
              "Sent a ~3 minute command via the composer to force a long turn.",
              rec.shot(page, "10_long_turn_sent"))

    # Watch for the first progress note: it shows up in #voiceLatestSys and a
    # /chat response carries a non-empty progressNote. The client fires its first
    # progress request ~120s in, so allow up to ~2.5 min.
    note_text = None
    deadline = time.time() + 175
    while time.time() < deadline:
        page.wait_for_timeout(3000)
        sys_hidden = page.get_attribute("#voiceLatestSys", "hidden") is not None
        sys_txt = (page.text_content("#voiceLatestSys") or "").strip()
        if sys_txt and not sys_hidden:
            note_text = sys_txt
            break
        # Stop early if the answer already rendered (turn finished before a note).
        if page.get_attribute("#voiceLatestReply", "hidden") is None:
            break

    progress_resps = [n for n in rec.network
                      if "/chat" in n["url"] and isinstance(n.get("body"), dict)
                      and (n["body"].get("progressNote") or "").strip()]
    server_note = progress_resps[0]["body"]["progressNote"].strip() if progress_resps else None
    shown = note_text or server_note
    rec.stage("11_progress_note", bool(shown),
              f"Progress note generated and shown during the long turn: {shown!r}"
              if shown else "No progress note fired within ~2.5 min",
              rec.shot(page, "11_progress_note"),
              extra={"progress_note": shown})

    # Confirm the note was actually SPOKEN: a /tts request whose text matches it.
    tts_posts = [n.get("post", "") or "" for n in rec.network if "/tts" in n["url"] and n.get("post")]
    spoken_match = bool(shown) and any(shown[:30] in t for t in tts_posts)
    rec.stage("12_progress_spoken", spoken_match,
              "Progress note was sent to /tts to be read aloud."
              if spoken_match else "Progress note was not found in any /tts request body.",
              rec.shot(page, "12_progress_after"))

    # The real answer must still arrive and render after the progress note(s).
    reply = None
    try:
        page.wait_for_selector("#voiceLatestReply:not([hidden])", timeout=120000)
        reply = (page.text_content("#voiceLatestReply") or "").strip()
    except Exception:
        pass
    rec.stage("13_answer_after_progress", bool(reply),
              f"Final answer rendered after the progress note(s): {reply[:160]!r}"
              if reply else "Final answer did not render after the progress note(s)",
              rec.shot(page, "13_answer_after_progress"),
              extra={"reply": reply})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True)
    ap.add_argument("--sid", required=True)
    ap.add_argument("--audio-dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--scenarios", default="happy,resilience,progress",
                    help="comma-separated subset to run (happy,resilience,progress)")
    args = ap.parse_args()
    want = {s.strip() for s in args.scenarios.split(",") if s.strip()}

    os.makedirs(args.out, exist_ok=True)
    happy_wav = os.path.join(args.audio_dir, "utterance_what_changed.wav")
    resil_wav = os.path.join(args.audio_dir, "utterance_status.wav")

    # iPhone-ish portrait mobile viewport.
    mobile = {"viewport": {"width": 390, "height": 844}, "device_scale_factor": 3,
              "is_mobile": True, "has_touch": True,
              "user_agent": ("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) "
                             "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 "
                             "Mobile/15Eic Safari/604.1")}

    results = {"started": now_iso(), "base": args.base, "sid": args.sid, "runs": []}

    def launch(p, wav):
        return p.chromium.launch(headless=True, args=[
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
            f"--use-file-for-fake-audio-capture={wav}",
            "--autoplay-policy=no-user-gesture-required",
        ])

    with sync_playwright() as p:
        # ---- Run 1: happy path ----
        if "happy" in want:
            win = wav_seconds(happy_wav) * 2 + 1   # 2 loop periods guarantees one clean pass
            print(f"[happy] fake mic = {os.path.basename(happy_wav)} ({wav_seconds(happy_wav):.1f}s), "
                  f"record window {win:.1f}s")
            rec = Recorder(args.out, "happy")
            b = launch(p, happy_wav)
            ctx = b.new_context(**mobile, ignore_https_errors=True)
            ctx.grant_permissions(["microphone"], origin=args.base)
            page = ctx.new_page()
            attach_listeners(page, rec)
            try:
                if open_voice_tab(page, args.base, args.sid, rec):
                    run_happy_path(page, rec, win)
            except Exception as e:
                rec.stage("ERROR", False, f"happy path crashed: {e}")
            results["runs"].append({"scenario": "happy", "stages": rec.stages,
                                    "network": rec.network, "console": rec.console})
            ctx.close(); b.close()

        # ---- Run 2: spotty-network resilience ----
        if "resilience" in want:
            win2 = wav_seconds(resil_wav) * 2 + 1
            print(f"[resilience] fake mic = {os.path.basename(resil_wav)}, record window {win2:.1f}s")
            rec2 = Recorder(args.out, "resilience")
            b = launch(p, resil_wav)
            ctx = b.new_context(**mobile, ignore_https_errors=True)
            ctx.grant_permissions(["microphone"], origin=args.base)
            page = ctx.new_page()
            attach_listeners(page, rec2)
            cdp = ctx.new_cdp_session(page)
            cdp.send("Network.enable")
            try:
                if open_voice_tab(page, args.base, args.sid, rec2):
                    run_resilience(page, rec2, win2, cdp)
            except Exception as e:
                rec2.stage("ERROR", False, f"resilience path crashed: {e}")
            results["runs"].append({"scenario": "resilience", "stages": rec2.stages,
                                    "network": rec2.network, "console": rec2.console})
            ctx.close(); b.close()

        # ---- Run 3: long turn + periodic spoken progress note ----
        if "progress" in want:
            print("[progress] long turn (>2 min) to exercise the periodic spoken progress note")
            rec3 = Recorder(args.out, "progress")
            b = launch(p, happy_wav)   # mic unused here; we type the prompt
            ctx = b.new_context(**mobile, ignore_https_errors=True)
            ctx.grant_permissions(["microphone"], origin=args.base)
            page = ctx.new_page()
            attach_listeners(page, rec3)
            try:
                if open_voice_tab(page, args.base, args.sid, rec3):
                    run_progress(page, rec3)
            except Exception as e:
                rec3.stage("ERROR", False, f"progress path crashed: {e}")
            results["runs"].append({"scenario": "progress", "stages": rec3.stages,
                                    "network": rec3.network, "console": rec3.console})
            ctx.close(); b.close()

    results["finished"] = now_iso()
    with open(os.path.join(args.out, "results.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print(f"\nDONE. results.json + screenshots in {args.out}")


if __name__ == "__main__":
    main()
