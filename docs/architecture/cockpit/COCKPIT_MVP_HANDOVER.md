# Cockpit MVP - Implementation & QA Handover (loop)

**Status:** ACTIVE
**Date:** 2026-05-31
**For:** an agent implementing the Cockpit. **Work as a loop: MVP first, test it, QA-report it, fix, repeat until the MVP is excellent - then move to the later phases.**

---

## Mission

Implement the whole Cockpit (the single Blazor Server UI that drives every Claude session on the tailnet). But **do the MVP first and get it genuinely working**, proven against a real Director over the tailnet, with an **HTML QA report**. Only after every MVP acceptance check passes do you move on to Phase 2+ (see [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)).

The Director is **final and shipped** - its whole REST surface exists (terminal stream incl. input, prompt, queue+auto-drain, resize, screenshots, git, workspaces, scheduler, relink, wingman/recap/voice). You do **not** change the Director. All your work is in `src/CcDirector.Cockpit/` (+ tests). Read [HANDOVER.md](HANDOVER.md) and [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) first.

---

## The MVP (this is the bar - all must work *well*)

The MVP is "see the fleet and fully drive one session." Five features:

1. **Session rail (fleet-wide).** Left panel lists every session across all Directors (from the Gateway `GET /sessions`), grouped by machine/Director, with the verbatim status dot and a needs-you marker. Clicking a session selects it.
2. **Live terminal.** The selected session's terminal renders coherently (no ghost frames), stays current, **accepts typing** (text, Enter, arrows, Ctrl+C, Esc, slash-command UI), reconnects if dropped, and fills the pane width.
3. **Composer.** Under the terminal: **Speak** (dictation -> fills the box), **Send** (delivers the text), **Queue** (adds to the session's queue), plus **Interrupt** and **Esc**.
4. **Screenshots.** Drag/drop or pick an image -> it reaches the session (Claude can see it).
5. **Queue panel.** Lists queued prompts; enqueue / send-now / remove; the Director **auto-drains** the next item when the session goes idle.

### MVP acceptance criteria (what "working really well" means - the QA checks)

| # | Criterion | Pass condition |
|---|---|---|
| A1 | Rail shows the fleet | Every live session appears within ~2s; status dot color matches the session's real state; an unreachable Director shows inline, doesn't blank the list |
| A2 | Select -> terminal | Clicking a session loads its live terminal; output is coherent (no stacked/ghost frames) |
| A3 | Terminal is current | New output appears live without manual refresh |
| A4 | Terminal input | Typing reaches Claude: a typed `echo`/prompt + Enter runs; **arrows, Ctrl+C, Esc** work in Claude's UI |
| A5 | Send | Composer text + Send is received by the session (round-trips) |
| A6 | Queue + auto-drain | Queued items list correctly; when the session goes idle the next item auto-sends (FIFO); remove works |
| A7 | Speak | Press Speak, talk, text lands in the composer for review (not auto-sent) |
| A8 | Screenshots | A dropped image reaches the session (Claude acknowledges it) |
| A9 | Interrupt / Esc | Both reach the PTY (Ctrl+C stops a run; Esc soft-stops) |
| A10 | Tailnet only | All Director traffic is over the tailnet endpoint (no localhost to Directors); one slow Director doesn't stall the UI |

All ten must be **green against a real Director with a real session** before the MVP is "done."

---

## The loop (run this until the MVP is green, then continue to phases)

1. **Pick** the next acceptance check that isn't passing.
2. **Implement / fix** it in `src/CcDirector.Cockpit/` (+ a unit test where it makes sense).
3. **Build** the Cockpit: `dotnet run --project src/CcDirector.Cockpit` (serves `http://localhost:7470`).
4. **Test it live** in a browser against a real Director + a real session over the tailnet. Drive the UI and **screenshot** the result (use a browser tool). Verify the check's pass condition for real - not just "it compiles."
5. **Record** the result (pass/fail + evidence) in the **HTML QA report** (below).
6. **Repeat.** When all A1-A10 are green, the MVP is done - then start Phase 2 from [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) and keep looping the same way (implement -> live-test -> QA-report) per phase.

Never claim a check passes without having exercised it live and captured evidence.

---

## The QA report (HTML, required)

Maintain a single HTML report at **`docs/features/cockpit-mvp/QA_REPORT.html`**:

- One row per acceptance check (A1-A10): the criterion, **PASS/FAIL**, and **evidence** (a screenshot and/or the relevant log line).
- A header summary: "N/10 passing", date, the Director/session it was tested against.
- Build it from markdown the same way the other reports are built: write `QA_REPORT.md`, then
  `cc-html from-markdown QA_REPORT.md -o QA_REPORT.html --theme boardroom` and
  `python docs/architecture/cockpit/widen.py QA_REPORT.html` (full-width). Embed screenshots as images.
- The report is the source of truth for "is the MVP done." Update it every loop.

---

## How to run + test

- **Run the Cockpit:** `dotnet run --project src/CcDirector.Cockpit` -> `http://localhost:7470`. Gateway URL comes from `appsettings.json` (`Cockpit:GatewayUrl`, default `http://127.0.0.1:7878`).
- **You need a running Gateway + at least one Director with a session.** The Gateway aggregates `GET /sessions`; each session carries its Director's `tailnetEndpoint`, which the Cockpit dials directly for the terminal/writes.
- **Drive + screenshot the UI** with a browser automation tool to fill the QA evidence.
- **Terminal input note:** today input goes xterm `onData` -> `POST /prompt {appendEnter:false}`. The Director's `/stream` is also bidirectional (#3), so an optional perf improvement is to switch input to a direct `ws.send` - do this only if per-keystroke REST feels laggy; it needs no Director change.

---

## Current state (what's already built / verified)

- Cockpit MVP is **largely built**: rail, terminal (typeable), composer (Speak/Send/Queue/Interrupt/Esc), screenshot upload, queue panel.
- **Verified:** terminal renders + Send round-trips against a test Director over the tailnet.
- **Not yet verified live:** the full A1-A10 set (queue auto-drain end-to-end, screenshots reaching Claude, Speak, arrow/Ctrl+C input, multi-Director rail correctness). **That verification is your first job.**

---

## Hard rules

- **Never kill or rebuild the user's running Directors.** They hold live sessions. Test against them read/drive-only; relaunch only what you own.
- **No localhost to Directors** - always the tailnet endpoint from the session DTO. (The Cockpit's own page is loopback; that's fine.)
- **Don't change the Director.** Everything is Cockpit-side. If you think you need a Director change, stop and flag it - the Director is final by design.
- **Don't claim success with errors on screen** - a check is green only with live evidence in the QA report.

---

## Pointers

- Architecture: [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) (+ `cockpit-topology.png`)
- Phases after MVP: [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)
- Current handover/state: [HANDOVER.md](HANDOVER.md)
- Director feature report: [../../features/cockpit-final-build/REPORT.html](../../features/cockpit-final-build/REPORT.html)
