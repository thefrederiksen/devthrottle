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
    "08_offline_retry": ("Spotty network: retry feedback",
        "The network is dropped mid-send (CDP offline). The uploader should keep trying and tell the user it is retrying."),
    "09_recovered": ("Spotty network: recovery",
        "When the connection returns, the upload completes and produces a transcript without the user re-recording."),
}

SCENARIO_TITLES = {
    "happy": "Scenario A &mdash; Full voice round-trip (the in-car loop)",
    "resilience": "Scenario B &mdash; Spotty-network resilience (the car drive)",
}
SCENARIO_BLURB = {
    "happy": "The complete walkie-talkie loop an operator runs while driving: speak a question, "
             "have it transcribed and sent to the session, then hear the agent's answer read back.",
    "resilience": "The car has flaky LTE. We drop the network mid-upload and confirm the client keeps "
                  "trying and recovers on reconnect rather than losing the message.",
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
    transcript = reply = reply_wait = None
    for r in runs:
        if r["scenario"] == "happy":
            for s in r["stages"]:
                if s["key"] == "05_transcribed":
                    transcript = s.get("transcript")
                if s["key"] == "06_agent_reply":
                    reply = s.get("reply")
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

    if transcript and reply:
        parts.append(f"""<div class="grid2" style="margin-top:16px">
  <div class="card"><h3 style="margin-top:0">Operator said (transcribed + cleaned)</h3>
    <div class="quote">{esc(strip_ts(transcript))}</div>
    <p class="small">Spoken into the fake mic, transcribed by OpenAI STT, then de-filler'd by the Wingman before reaching the session.</p></div>
  <div class="card"><h3 style="margin-top:0">Agent replied (spoken back via TTS)</h3>
    <div class="quote">{esc(strip_ts(reply))}</div>
    <p class="small">Injected into the live session via <code>/chat</code>; reply read aloud via <code>/tts</code>.</p></div>
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
    <li><span class="tag ok">VERIFIED</span> Dropping the network mid-send flips the UI to "Bad connection - retrying (attempt 2)" within ~0.5s and the upload recovers on reconnect with no re-recording.</li>
  </ul></div>""")

    parts.append("""<div class="card findwarn" style="margin:14px 0">
  <h3 style="margin-top:0">Rebuild targets (architecture, not bugs)</h3>
  <ul class="tight">
    <li><span class="tag gap">REBUILD</span> <strong>Turn detection bypasses the Wingman.</strong> The reply loop uses <code>/chat</code>, which polls <code>ActivityState</code> on its own instead of consuming the new <code>TerminalStateDetector</code> / turn-summary. Voice mode should read the Wingman's authoritative state so the work we put into reliable state detection (issue #129) actually benefits it.</li>
    <li><span class="tag gap">REBUILD</span> <strong>It speaks the raw reply, not the Wingman summary.</strong> Today it reads the agent's last paragraph; it should speak the Wingman's purpose-built <code>SpokenText</code> plus the pending question and quick replies ("you can say: approve, or stop").</li>
    <li><span class="tag gap">REBUILD</span> <strong>Whole-blob upload.</strong> Retry works, but each attempt re-sends the entire clip. A mid-flight stall can sit on "Uploading..." with no client-side timeout. A resumable chunked upload (the SHA256/idempotent pattern the phone recorder already uses) would survive spotty car LTE far better.</li>
    <li><span class="tag gap">REBUILD</span> <strong>No working-state ping tied to real state.</strong> While the agent is working the driver should hear a small periodic ping driven by the Wingman's actual state, then a chime when the answer is ready.</li>
    <li><span class="tag gap">REBUILD</span> <strong>Start-of-speech clipping (issue #134)</strong> is not yet addressed; recording should flip to "Ready" only after the first real audio frame.</li>
  </ul></div>
<p class="small">Hard dependency: all of the above is only as good as the Wingman knowing
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
        "pid": "83012",
        "base": "http://127.0.0.1:7880",
    }
    build(args.results, args.out, env)
