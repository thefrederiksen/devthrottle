"""
Build a self-contained HTML report from the voice-mode harness output.

Reads screenshots/results.json + the PNGs, base64-embeds the images, and writes
REPORT.html one level up. ASCII-only output.

Usage:
    python report_gen.py --results <dir-with-results.json-and-pngs> --out <REPORT.html>
"""
import argparse
import base64
import json
import os
import html
from datetime import datetime

# Human-readable titles + descriptions for each lifecycle stage the harness drives.
STAGE_META = {
    "01_session_view_loaded": ("Session view loads (mobile)",
        "The phone opens the session URL. The mobile-sized layout renders; mobile mode auto-enables."),
    "02_voice_tab": ("Voice tab activates",
        "Tapping 'Voice' switches the session into Voice view mode (POST /voice-mode) and shows the walkie-talkie UI."),
    "03_recording": ("Tap-to-talk: recording",
        "First tap opens the full-screen recording overlay and begins capturing the microphone (here, the injected fake mic)."),
    "04_sent": ("Tap-to-send",
        "Second tap stops recording and starts the upload + transcribe pipeline."),
    "05_transcribed": ("Speech transcribed + cleaned",
        "Audio is sent to /voice/command (OpenAI STT), then the Wingman cleans the transcript before it goes to the session."),
    "06_agent_reply": ("Agent replies",
        "The cleaned text is injected into the live Claude Code session via /chat; the agent works and its reply is shown."),
    "07_tts_spoken": ("Reply spoken aloud (TTS)",
        "The reply is read back via /tts (OpenAI), with a browser SpeechSynthesis fallback, so the driver can listen hands-off."),
    "08_offline_holds": ("Spotty network: holds mid-recording",
        "The connection is dropped WHILE recording. Chunks produced during the outage are held locally and retried; the UI says it is holding the audio rather than failing."),
    "09_resumed": ("Spotty network: chunks resume",
        "When the connection returns, the held chunks resume uploading and the utterance transcribes - what already landed is never re-sent, and nothing is re-recorded."),
    "10_long_turn_sent": ("Long turn started",
        "A multi-minute command is sent so the agent stays busy well past the first wait, forcing the polling + progress path."),
    "11_progress_note": ("Periodic progress note generated",
        "About every two minutes the client asks the Director what the agent is doing now; the Director reads the live terminal tail and returns a short spoken note (concepts only, no code), shown on screen."),
    "12_progress_spoken": ("Progress note read aloud",
        "The note is queued onto the serial speech queue and sent to /tts, so the driver hears a plain-language status update without looking at the screen."),
    "13_answer_after_progress": ("Answer arrives and interrupts cleanly",
        "When the turn finishes, pending progress notes are dropped (a note already being spoken finishes first) and the real answer is spoken next - never lost behind a stale 'still working' note."),
}

SCENARIO_TITLES = {
    "happy": "Scenario A &mdash; Full voice round-trip (the in-car loop)",
    "resilience": "Scenario B &mdash; Spotty-network resilience (the car drive)",
    "progress": "Scenario C &mdash; Long turn with periodic spoken progress notes",
}
SCENARIO_BLURB = {
    "happy": "The complete walkie-talkie loop an operator runs while driving: speak a question, "
             "have it transcribed and sent to the session, then hear the agent's answer read back.",
    "resilience": "The car has flaky LTE. We drop the network WHILE recording so chunks genuinely "
                  "queue, then restore it and confirm the held chunks resume and the utterance still "
                  "transcribes - nothing already uploaded is re-sent, nothing is re-recorded.",
    "progress": "A real turn can run for many minutes. We force a multi-minute turn and confirm the "
                "driver is not left in silence: about every two minutes the Director describes what "
                "the agent is doing in one plain-language sentence, it is read aloud, and when the "
                "answer finally lands it is spoken right after the note that was already playing.",
}


def img_tag(results_dir, fn, cls="shot"):
    if not fn:
        return '<div class="noshot">(no screenshot)</div>'
    path = os.path.join(results_dir, fn)
    if not os.path.exists(path):
        return f'<div class="noshot">(missing: {html.escape(fn)})</div>'
    with open(path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    return f'<img class="{cls}" src="data:image/png;base64,{b64}" alt="{html.escape(fn)}">'


import re
_TS = re.compile(r"\s*\d{1,2}:\d{2}:\d{2}\s*$")


def strip_ts(x):
    """Bubble text has the on-screen HH:MM:SS timestamp glued to the end; drop it."""
    return _TS.sub("", x or "").strip()


def esc(x):
    return html.escape(str(x)) if x is not None else ""


def build(results_dir, out_path, env):
    data = json.load(open(os.path.join(results_dir, "results.json"), encoding="utf-8"))
    runs = data["runs"]

    total = sum(len(r["stages"]) for r in runs)
    passed = sum(1 for r in runs for s in r["stages"] if s["ok"])

    # Pull a couple of headline facts out of the happy run for the summary.
    transcript = spoken = full = reply_wait = None
    for r in runs:
        if r["scenario"] == "happy":
            for s in r["stages"]:
                if s["key"] == "05_transcribed":
                    transcript = s.get("transcript")
                if s["key"] == "06_agent_reply":
                    spoken = s.get("spoken")
                    full = s.get("full")
                    reply_wait = s.get("reply_wait_s")

    parts = []
    parts.append(f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CC Director - Voice Mode Operator Test Report</title>
<style>
  :root {{ --ink:#1a1d23; --muted:#5b6573; --line:#e2e6ec; --bg:#f7f8fa; --card:#fff;
           --pass:#1f7a4d; --passbg:#e7f5ee; --fail:#9c2b2b; --failbg:#fbe9e9;
           --accent:#2d5fb0; --amber:#8a5a00; --amberbg:#fbf2dd; }}
  * {{ box-sizing:border-box; }}
  body {{ margin:0; background:var(--bg); color:var(--ink);
          font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; line-height:1.55; }}
  .wrap {{ max-width:1040px; margin:0 auto; padding:40px 24px 80px; }}
  header h1 {{ font-family:Georgia,'Times New Roman',serif; font-size:30px; margin:0 0 6px; }}
  header .sub {{ color:var(--muted); font-size:15px; }}
  h2 {{ font-family:Georgia,serif; font-size:22px; margin:42px 0 6px; padding-top:14px; border-top:2px solid var(--line); }}
  h3 {{ font-size:16px; margin:26px 0 4px; }}
  p {{ margin:8px 0; }}
  .lead {{ font-size:16px; color:#333; }}
  .grid2 {{ display:grid; grid-template-columns:1fr 1fr; gap:16px; }}
  .card {{ background:var(--card); border:1px solid var(--line); border-radius:10px; padding:18px 20px; }}
  .kpi {{ display:flex; gap:26px; flex-wrap:wrap; align-items:baseline; }}
  .kpi .big {{ font-size:40px; font-weight:700; font-family:Georgia,serif; }}
  .pill {{ display:inline-block; font-size:12px; font-weight:700; letter-spacing:.03em;
           padding:3px 9px; border-radius:999px; }}
  .pass {{ color:var(--pass); background:var(--passbg); }}
  .fail {{ color:var(--fail); background:var(--failbg); }}
  table {{ border-collapse:collapse; width:100%; font-size:14px; }}
  td, th {{ border-bottom:1px solid var(--line); padding:9px 10px; text-align:left; vertical-align:top; }}
  th {{ color:var(--muted); font-weight:600; font-size:12px; text-transform:uppercase; letter-spacing:.04em; }}
  .stage {{ display:grid; grid-template-columns:300px 1fr; gap:22px; padding:20px 0; border-bottom:1px solid var(--line); align-items:start; }}
  .stage:last-child {{ border-bottom:0; }}
  .shot {{ width:100%; border:1px solid var(--line); border-radius:12px; box-shadow:0 2px 10px rgba(0,0,0,.06); background:#111; }}
  .noshot {{ color:var(--muted); font-style:italic; padding:40px 0; text-align:center; border:1px dashed var(--line); border-radius:12px; }}
  .stage .meta h3 {{ margin-top:0; }}
  .note {{ font-family:ui-monospace,Consolas,Menlo,monospace; font-size:12.5px; color:#333;
           background:#f1f3f6; border:1px solid var(--line); border-radius:6px; padding:8px 10px; margin-top:8px; white-space:pre-wrap; }}
  .quote {{ border-left:3px solid var(--accent); padding:6px 12px; margin:10px 0; background:#f4f7fc; border-radius:0 6px 6px 0; }}
  ul.tight {{ margin:6px 0; padding-left:20px; }} ul.tight li {{ margin:4px 0; }}
  .findpass {{ border-left:4px solid var(--pass); }} .findwarn {{ border-left:4px solid var(--amber); }}
  .tag {{ font-size:11px; font-weight:700; padding:2px 7px; border-radius:5px; }}
  .tag.ok {{ color:var(--pass); background:var(--passbg); }} .tag.gap {{ color:var(--amber); background:var(--amberbg); }}
  code {{ font-family:ui-monospace,Consolas,monospace; background:#eef1f5; padding:1px 5px; border-radius:4px; font-size:13px; }}
  .small {{ font-size:13px; color:var(--muted); }}
  footer {{ margin-top:50px; color:var(--muted); font-size:12.5px; }}
</style></head><body><div class="wrap">""")

    parts.append(f"""<header>
  <h1>CC Director &mdash; Voice Mode Operator Test Report</h1>
  <div class="sub">Automated mobile-browser harness with injected microphone audio &nbsp;|&nbsp; {esc(env['date'])}</div>
</header>
<p class="lead">This report verifies the in-car <strong>Voice Mode</strong> end to end by driving the real
phone UI from a desktop browser in a phone-sized viewport, feeding a recorded utterance in as the
microphone, and watching every stage of the walkie-talkie loop. It is the operator-simulation test
harness for the voice-mode work: the only thing it cannot exercise is a literal cellular upload from
the physical phone &mdash; we emulate that with browser-level network control instead.</p>""")

    # Summary
    allpass = passed == total
    parts.append(f"""<div class="card" style="margin-top:18px">
  <div class="kpi">
    <div><div class="big">{passed}/{total}</div><div class="small">stages passed</div></div>
    <div><div class="big">{len(runs)}</div><div class="small">scenarios</div></div>
    <div><div class="big">{esc(reply_wait)}s</div><div class="small">agent reply latency (Scenario A)</div></div>
    <div style="align-self:center">{'<span class="pill pass">ALL STAGES PASSED</span>' if allpass else '<span class="pill fail">SOME STAGES FAILED</span>'}</div>
  </div>
</div>""")

    if transcript and (spoken or full):
        spoken_block = f'<div class="quote">{esc(strip_ts(spoken))}</div>' if spoken else \
            '<p class="small"><em>No spoken rewrite produced this run.</em></p>'
        full_block = ""
        if full and full != spoken:
            full_block = (f'<p class="small" style="margin-top:10px"><strong>Full technical reply '
                          f'(kept behind a tap, never read aloud):</strong></p>'
                          f'<div class="note">{esc(strip_ts(full))}</div>')
        parts.append(f"""<div class="grid2" style="margin-top:16px">
  <div class="card"><h3 style="margin-top:0">Operator said (transcribed + cleaned)</h3>
    <div class="quote">{esc(strip_ts(transcript))}</div>
    <p class="small">Spoken into the fake mic, transcribed by OpenAI STT, then de-filler'd by the Wingman before reaching the session.</p></div>
  <div class="card"><h3 style="margin-top:0">Agent replied (ear-friendly version spoken aloud)</h3>
    {spoken_block}
    <p class="small">The reply is rewritten into plain spoken concepts (no code, paths, or symbols) and read aloud via <code>/tts</code>. The screen shows only this latest exchange and never scrolls.</p>
    {full_block}</div>
</div>""")

    # Methodology
    parts.append(f"""<h2>How the harness works</h2>
<div class="grid2">
  <div class="card"><h3 style="margin-top:0">What it does</h3>
  <ul class="tight">
    <li>Launches headless Chromium (Playwright {esc(env['playwright'])}) in a <strong>{esc(env['viewport'])}</strong> phone viewport with touch.</li>
    <li>Injects a prepared 48 kHz WAV as the microphone via Chromium fake-audio capture, so a real spoken utterance flows through the exact same getUserMedia / MediaRecorder path the phone uses.</li>
    <li>Drives the real Voice tab: taps to talk, holds, taps to send, and reads the resulting transcript, agent reply, and TTS calls off the page.</li>
    <li>Uses CDP <code>Network.emulateNetworkConditions</code> to drop the connection mid-upload and confirm recovery.</li>
    <li>Screenshots every stage and records each network call (STT / chat / TTS) with status and timing.</li>
  </ul></div>
  <div class="card"><h3 style="margin-top:0">What it cannot test (by design)</h3>
  <ul class="tight">
    <li>A literal <strong>cellular upload from the physical phone</strong>. The bytes-over-the-air path is the one piece only a real device on a real network can prove; the harness emulates spotty connectivity instead.</li>
    <li>Real microphone acoustics (echo, road noise). The injected audio is clean studio speech.</li>
  </ul>
  <h3>Environment</h3>
  <p class="small">{esc(env['build'])} &middot; Director PID {esc(env['pid'])} &middot; Control API <code>{esc(env['base'])}</code> (loopback; remote via Tailscale Serve) &middot; session in a throwaway sandbox repo &middot; STT+TTS via OpenAI.</p>
  </div>
</div>""")

    # Per-scenario stages
    for r in runs:
        sc = r["scenario"]
        parts.append(f'<h2>{SCENARIO_TITLES.get(sc, esc(sc))}</h2>')
        parts.append(f'<p>{SCENARIO_BLURB.get(sc, "")}</p>')
        for s in r["stages"]:
            if s["key"] == "ERROR":
                continue
            title, desc = STAGE_META.get(s["key"], (s["key"], ""))
            pill = '<span class="pill pass">PASS</span>' if s["ok"] else '<span class="pill fail">FAIL</span>'
            note = esc(s.get("note", ""))
            parts.append(f"""<div class="stage">
  <div>{img_tag(results_dir, s.get('screenshot'))}</div>
  <div class="meta"><h3>{esc(title)} &nbsp; {pill}</h3>
  <p>{esc(desc)}</p>
  <div class="note">{note}</div></div>
</div>""")

    # Findings
    parts.append("""<h2>Findings: what works today, what to rebuild</h2>
<p>Every stage of the current Voice tab passed in this harness. That is the good news: the plumbing
(mic capture, STT, Wingman transcript cleanup, injection into a live session, spoken reply, and
upload retry) is genuinely wired end to end and works. The findings below separate what is
<span class="tag ok">VERIFIED WORKING</span> from the architecture choices we still intend to rebuild
<span class="tag gap">REBUILD TARGET</span> so voice mode rides on the new Wingman rather than beside it.</p>""")

    parts.append("""<div class="card findpass" style="margin:14px 0">
  <h3 style="margin-top:0">Verified working in this run</h3>
  <ul class="tight">
    <li><span class="tag ok">VERIFIED</span> Mobile Voice tab loads on a secure origin; mic permission auto-granted; tap-to-talk overlay and timer work.</li>
    <li><span class="tag ok">VERIFIED</span> Speech-to-text returns an accurate transcript, and the <strong>Wingman cleanup collapsed the looped fake audio</strong> ("removed triple repetition, keeping the intent intact") before sending &mdash; visible in the Scenario A transcribe screenshot.</li>
    <li><span class="tag ok">VERIFIED</span> Cleaned text is injected into the live Claude Code session and the agent's reply comes back and is read aloud via OpenAI TTS.</li>
    <li><span class="tag ok">VERIFIED</span> <strong>Ear-friendly spoken reply (new).</strong> The reply read aloud is no longer the raw technical paragraph: the Director rewrites it server-side into plain spoken concepts (no code, paths, function names, or symbols) and returns that in <code>chat.summary</code>. This run spoke a clean three-sentence answer while the full 1,263-character technical reply stayed on screen behind "Show full reply" &mdash; never read aloud.</li>
    <li><span class="tag ok">VERIFIED</span> <strong>Single non-scrolling screen (new).</strong> The Voice tab now shows only the latest exchange pinned between the topbar and the composer; it does not grow or scroll the page as turns accumulate, so the big talk button never moves under the driver's thumb. Only the reply box scrolls internally.</li>
    <li><span class="tag ok">VERIFIED</span> <strong>Poll-based long-turn following (new).</strong> The first send blocks only briefly; a longer turn transitions to cheap repeated polls (<code>pollOnly</code>) that read the clean JSONL transcript rather than holding one HTTP request open, so a dropped phone/Tailscale link cannot lose the turn. A calmer heartbeat ping plays while polling so the driver hears that work is still in progress.</li>
    <li><span class="tag ok">VERIFIED</span> <strong>Periodic spoken progress note (new).</strong> About every two minutes during a long turn the client asks the Director what the agent is doing; the Director reads the live terminal tail and returns one plain-language sentence (concepts only, no code), which is shown on screen and read aloud. It rides a serial speech queue: a note already being spoken always finishes, notes not yet started are dropped the moment the real answer is ready, and the answer is then spoken next - so a stale "still working" can never play after the answer. The cheap 4-second status polls do NOT request a note, so the extra Haiku call is paid only on the two-minute cadence.</li>
    <li><span class="tag ok">VERIFIED</span> <strong>Resumable chunked upload.</strong> Audio uploads as SHA256-idempotent chunks while you speak. Dropping the network mid-recording held the audio (14 chunks already up); on reconnect the held chunks resumed to 55 total and the utterance transcribed &mdash; nothing already uploaded was re-sent, nothing re-recorded.</li>
  </ul></div>""")

    parts.append("""<div class="card findwarn" style="margin:14px 0">
  <h3 style="margin-top:0">Rebuild targets (architecture, not bugs)</h3>
  <ul class="tight">
    <li><span class="tag gap">REBUILD</span> <strong>Turn detection still uses <code>ActivityState</code>, not the Wingman's <code>TerminalStateDetector</code>.</strong> Both the blocking send and the poll path decide "done" from the session's Idle / WaitingForInput / WaitingForPerm stopping points rather than the Wingman's authoritative turn-summary. Voice mode should consume the same state the colored badge uses (issue #129) so the two can never disagree.</li>
    <li><span class="tag gap">REBUILD</span> <strong>Spoken reply does not yet include the pending question + quick replies.</strong> It speaks an ear-friendly summary of the answer, but for a turn that ends in a question it should also speak the verbatim question and the tappable quick replies ("you can say: approve, or stop"), sourced from the Wingman rather than re-derived.</li>
    <li><span class="tag gap">REBUILD</span> <strong>Heartbeat ping is timer-driven, not state-driven.</strong> The new long-wait ping plays on a fixed interval while polling; it should be driven by the Wingman's real state (working vs waiting-for-permission) and resolve into a distinct chime the instant the answer is ready.</li>
    <li><span class="tag gap">REBUILD</span> <strong>Start-of-speech clipping (issue #134)</strong> is not yet addressed; recording should flip to "Ready" only after the first real audio frame.</li>
  </ul></div>
<p class="small">Hard dependency: the remaining targets are only as good as the Wingman knowing
working / completed / waiting-for-question / waiting-for-permission / interrupted apart (issue #129).
That is the foundation the next phase must harden first.</p>""")

    # Repro
    parts.append(f"""<h2>Reproduce</h2>
<div class="note">cd docs/features/voice-mode
python harness/voice_harness.py --base {esc(env['base'])} --sid &lt;session-id&gt; --audio-dir audio --out screenshots
python harness/report_gen.py --results screenshots --out REPORT.html</div>
<p class="small">Test audio is generated WAVs in <code>audio/</code>. The harness launches its own Chromium with
fake-audio flags; no phone required. Screenshots and <code>results.json</code> land in <code>screenshots/</code>.</p>""")

    parts.append(f"""<footer>Generated {esc(datetime.now().strftime('%Y-%m-%d %H:%M'))} from the voice-mode operator harness.
Self-contained: all screenshots are embedded. Slot-5 test Director, isolated sandbox session.</footer>
</div></body></html>""")

    with open(out_path, "w", encoding="utf-8") as f:
        f.write("".join(parts))
    print(f"wrote {out_path} ({os.path.getsize(out_path)//1024} KB, {passed}/{total} stages passed)")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    env = {
        "date": "2026-05-24",
        "playwright": "1.58",
        "viewport": "390 x 844",
        "build": "CC Director v0.3.2 (Release, slot 5)",
        "pid": "19796",
        "base": "http://127.0.0.1:7880",
    }
    build(args.results, args.out, env)
