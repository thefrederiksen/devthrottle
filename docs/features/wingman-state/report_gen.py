"""
Build a self-contained HTML report for the Wingman / session-state hardening work
(GitHub issues #136 + #137). Base64-embeds the screenshots so the file is portable.
ASCII-only output.

Usage:
    python report_gen.py --screens screenshots --out REPORT.html
"""
import argparse
import base64
import html
import os
from datetime import datetime


def img(screens_dir, fn):
    path = os.path.join(screens_dir, fn)
    if not os.path.exists(path):
        return '<div class="noshot">(missing: %s)</div>' % html.escape(fn)
    with open(path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    return '<img class="shot" src="data:image/png;base64,%s" alt="%s">' % (b64, html.escape(fn))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--screens", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    P = []
    P.append("""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CC Director - Wingman / Session-State Hardening Report</title>
<style>
  :root { --ink:#1a1d23; --muted:#5b6573; --line:#e2e6ec; --bg:#f7f8fa; --card:#fff;
          --pass:#1f7a4d; --passbg:#e7f5ee; --fail:#9c2b2b; --failbg:#fbe9e9;
          --accent:#2d5fb0; --amber:#8a5a00; --amberbg:#fbf2dd; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--ink);
         font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; line-height:1.55; }
  .wrap { max-width:1040px; margin:0 auto; padding:40px 24px 80px; }
  header h1 { font-family:Georgia,'Times New Roman',serif; font-size:30px; margin:0 0 6px; }
  header .sub { color:var(--muted); font-size:15px; }
  h2 { font-family:Georgia,serif; font-size:22px; margin:42px 0 8px; padding-top:14px; border-top:2px solid var(--line); }
  h3 { font-size:16px; margin:24px 0 4px; }
  p { margin:8px 0; }
  .lead { font-size:16px; color:#333; }
  .grid2 { display:grid; grid-template-columns:1fr 1fr; gap:16px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:18px 20px; }
  .kpi { display:flex; gap:26px; flex-wrap:wrap; align-items:baseline; }
  .kpi .big { font-size:38px; font-weight:700; font-family:Georgia,serif; }
  .pill { display:inline-block; font-size:12px; font-weight:700; letter-spacing:.03em; padding:3px 9px; border-radius:999px; }
  .pass { color:var(--pass); background:var(--passbg); }
  .amber { color:var(--amber); background:var(--amberbg); }
  table { border-collapse:collapse; width:100%; font-size:14px; margin:10px 0; }
  td, th { border-bottom:1px solid var(--line); padding:9px 10px; text-align:left; vertical-align:top; }
  th { color:var(--muted); font-weight:600; font-size:12px; text-transform:uppercase; letter-spacing:.04em; }
  .shot { width:100%; border:1px solid var(--line); border-radius:10px; box-shadow:0 2px 10px rgba(0,0,0,.08); background:#111; }
  .noshot { color:var(--muted); font-style:italic; padding:40px 0; text-align:center; border:1px dashed var(--line); border-radius:10px; }
  .cap { font-size:13px; color:var(--muted); margin-top:6px; }
  code { font-family:ui-monospace,Consolas,monospace; background:#eef1f5; padding:1px 5px; border-radius:4px; font-size:13px; }
  .note { font-family:ui-monospace,Consolas,monospace; font-size:12.5px; color:#222; background:#f1f3f6;
          border:1px solid var(--line); border-radius:6px; padding:10px 12px; white-space:pre-wrap; }
  ul.tight { margin:6px 0; padding-left:20px; } ul.tight li { margin:5px 0; }
  .tag { font-size:11px; font-weight:700; padding:2px 7px; border-radius:5px; }
  .tag.done { color:var(--pass); background:var(--passbg); } .tag.todo { color:var(--amber); background:var(--amberbg); }
  footer { margin-top:50px; color:var(--muted); font-size:12.5px; }
</style></head><body><div class="wrap">""")

    P.append("""<header>
  <h1>CC Director - Wingman / Session-State Hardening</h1>
  <div class="sub">What I implemented to stop the status badge flip-flopping, and to make session-state detection trustworthy &nbsp;|&nbsp; %s</div>
</header>
<p class="lead">The colored session dot and the "NEEDS YOU" banner were flip-flopping: a session
sitting idle at its prompt would flash red ("NEEDS YOU") while the summary underneath said
"Nothing pending." This report explains the root causes, the fixes I shipped, and shows the
same idle scenario now resolving to a stable green "READY".</p>""" % datetime.now().strftime("%Y-%m-%d"))

    P.append("""<div class="card" style="margin-top:16px"><div class="kpi">
  <div><div class="big">168</div><div class="cap">Wingman unit tests passing</div></div>
  <div><div class="big">0</div><div class="cap">red events in the fixed idle run</div></div>
  <div><div class="big">7</div><div class="cap">distinct fixes implemented</div></div>
  <div style="align-self:center"><span class="pill pass">CORE FIXED + PUSHED</span></div>
</div></div>""")

    # The bug
    P.append("<h2>The bug</h2>")
    P.append("""<p>Two layers drive what you see:</p>
<ul class="tight">
  <li><strong>ActivityState</strong> (Working / WaitingForInput / WaitingForPerm / Idle) - the "is it working?" truth, owned by the terminal-state detector.</li>
  <li><strong>StatusColor</strong> (the green/blue/yellow/red dot + banner) - derived from ActivityState plus buffer scans and the turn-summary slow path.</li>
</ul>
<p>The badge contradicted itself because color writers fought each other (last write won) and a
classifier misread the screen. The original report:</p>""")
    P.append('<div class="card">%s<div class="cap">BEFORE: an idle session shows a red "NEEDS YOU" / "ACTION NEEDED" banner whose detail is just the persistent mode footer "bypass permissions on (shift+tab to cycle)", while the SUMMARY correctly says "Nothing pending." The badge also flipped red/green repeatedly.</div></div>' % img(args.screens, "before_bug.png"))

    P.append("""<h3>Root causes</h3>
<ul class="tight">
  <li>The turn-summary classifier was never taught that the persistent "bypass permissions on (shift+tab to cycle)" footer is a MODE line, not a permission request, so it returned "needs user" and painted red.</li>
  <li>A turn summary was allowed to repaint an idle session red even with no real on-screen gate.</li>
  <li>Color writes were blind last-writer-wins, so a cosmetic byte burst or a re-evaluated mapping could clobber a correct verdict.</li>
  <li>The detector's quiet gate reset on EVERY byte, so a continuously repainting status line / hook starved the LLM judge and pinned a stale "Working".</li>
</ul>""")

    # The fixes
    P.append("<h2>What I implemented</h2>")
    P.append("""<table>
  <tr><th>Fix</th><th>What it does</th><th>Status</th></tr>
  <tr><td><strong>A. Footer teaching</strong></td><td>The turn-summary classifier now gets the same "the mode footer is not a prompt" rule the state classifier already had; conversational offers ("say the word") are not a gate.</td><td><span class="tag done">pushed b1d37c9</span></td></tr>
  <tr><td><strong>B. Idle corroboration gate</strong></td><td>A turn summary cannot flip an idle session red unless the buffer shows real evidence (a known question marker, a [y/n]/numbered box, or the interrupted footer).</td><td><span class="tag done">pushed b1d37c9</span></td></tr>
  <tr><td><strong>C. Source precedence</strong></td><td>Replaced last-writer-wins with source confidence (Inferred &lt; ActivityState &lt; PositiveEvidence) plus an activity "generation"; a positive-evidence verdict is sticky within a generation, released on a real state change.</td><td><span class="tag done">pushed 72f84ed</span></td></tr>
  <tr><td><strong>1+2. Trigger-starvation fix</strong></td><td>The detector now reads the RESOLVED on-screen grid (not raw bytes) and resets its quiet countdown only on real activity (the "esc to interrupt" working footer on screen, or upper-screen content actually changing). A cosmetic status-line/spinner repaint no longer starves the LLM judge.</td><td><span class="tag todo">implemented, uncommitted</span></td></tr>
  <tr><td><strong>4. Generation threading</strong></td><td>The turn summary now carries the generation it was computed for; if the session moved on during the ~10s LLM call, the stale verdict is dropped.</td><td><span class="tag todo">implemented, uncommitted</span></td></tr>
  <tr><td><strong>5. Interrupted surfaced</strong></td><td>The "cancelled" verdict and the "What should Claude do instead?" footer now surface as red positive-evidence ("interrupted - waiting for redirection") instead of a silent green.</td><td><span class="tag todo">implemented, uncommitted</span></td></tr>
  <tr><td><strong>6. Permission = positive evidence</strong></td><td>The WaitingForPerm red is tagged PositiveEvidence so a byte burst or re-evaluated mapping cannot repaint over an authoritative permission gate.</td><td><span class="tag todo">implemented, uncommitted</span></td></tr>
</table>
<p class="cap">A, B, C are committed and pushed to main (issue #136). Items 1+2, 4, 5, 6 are from the
consolidated backlog (issue #137), implemented and unit-tested this session, not yet committed.</p>""")

    # Verification
    P.append("<h2>Verification</h2>")
    P.append("""<p>All 168 Wingman unit tests pass, including new tests for: positive-evidence stickiness and
release, the idle-corroboration gate, the stale-generation drop, the permission source mapping,
the interrupted footer marker, and the on-screen-grid snapshot the trigger fix relies on.</p>
<div class="note">Passed!  - Failed: 0, Passed: 168, Skipped: 0, Total: 168 - CcDirector.Core.Tests.dll (net10.0)</div>""")

    P.append("""<h3>Live: the same idle scenario, now stable green</h3>
<p>On a fresh slot-5 build I reran the exact trigger: a turn that ends with a conversational
offer ("I will do it whenever you give the word") while the bypass-permissions mode footer is on
screen. The session settles to green "READY" and the wingman correctly describes the mode footer
as a persistent status, not a prompt.</p>""")
    P.append('<div class="card">%s<div class="cap">AFTER: green "READY" - "Welcome screen with no active spinner, elapsed counter, or permission prompt; mode footer shows persistent status; ready for user command input". No red, no contradiction. The Wingman Log at the bottom shows only green-to-green transitions.</div></div>' % img(args.screens, "session_idle_green.png"))

    P.append("""<p>The session's full wingman color-event history for the run contained <strong>zero red events</strong>;
transitions were only green (idle/ready) and blue (working):</p>
<div class="note">blue -> blue   [working - active indicators on screen]
green -> blue  [working]
blue -> green  [ready, awaiting next prompt]
green -> blue  [working]
blue -> green  [ready, awaiting next prompt]
green -> blue  [streaming output]
RED events in history: 0</div>
<p class="cap">Before the fix, this same idle-with-offer scenario produced a red "NEEDS YOU" that
flip-flopped against the green fast path. It no longer does.</p>""")

    # Honest remainder
    P.append("<h2>Honest status and what remains</h2>")
    P.append("""<ul class="tight">
  <li><span class="tag done">DONE</span> The reported flip-flop is fixed and the core (A/B/C) is on main. The remaining state-detection hardening (items 1+2, 4, 5, 6) is implemented and unit-tested, awaiting commit.</li>
  <li><span class="tag todo">WATCH</span> The trigger fix keys "still working" off the ASCII "esc to interrupt" footer plus upper-screen content change, read from the resolved grid. A genuinely-running global hook (e.g. a "stop hook") legitimately shows that footer, so the detector correctly reports Working while it runs - that is not starvation. Worth confirming on a long real session that the gate releases to idle once the hook finishes.</li>
  <li><span class="tag todo">OPEN (#137)</span> Item 3: the terminal-state classifier and the turn-summary classifier are still two separate LLM calls that could disagree; consolidating to one authoritative verdict both consume is the larger follow-up. Item 7: turn the #131 misclassification reports into regression fixtures.</li>
</ul>
<p class="cap">Tracking: #136 (flip-flop, A/B/C shipped) and #137 (consolidated state-detection backlog).</p>""")

    P.append("""<footer>Generated %s. Self-contained: screenshots are embedded. Slot-5 test Director, isolated sandbox session.</footer>
</div></body></html>""" % datetime.now().strftime("%Y-%m-%d %H:%M"))

    with open(args.out, "w", encoding="utf-8") as f:
        f.write("".join(P))
    print("wrote %s (%d KB)" % (args.out, os.path.getsize(args.out) // 1024))


if __name__ == "__main__":
    main()
