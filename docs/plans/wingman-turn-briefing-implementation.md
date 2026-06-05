# Implementation plan: the wingman-driven Brief (TurnBriefing v2.1 -> working code)

**Status:** PLANNED (2026-06-04)
**Architecture:** docs/architecture/wingman/TURN_BRIEFING.md (v2.1 - contract frozen against
six captured shapes). This plan turns that document into code. The mission and laws there
govern every line here: reduce the meat computer's cognitive load; NO regex/text-parsing in
interpretation paths (D6).

**Scope assumption (stated, overridable):** "a much better Wingman tab" = the whole Brief
experience becomes wingman-driven. The CENTER Brief page renders stored TurnBriefs; the
right-rail WINGMAN tab becomes the turn timeline + brief-feedback; the rail rows show
railLine. Not merely a restyling of the right-rail tab.

---

## Decisions taken (defaults - veto any)

- **DT1. The wingman call is a `claude --print` side-spawn on the STRONG model** (the
  WingmanService pattern: no `--bare`, `--tools ""`, no session persistence). This runs on
  the Claude Max subscription - no per-token API bill, no new keys. Tradeoff: cold-spawn
  latency (~10-40s, absorbed by the yellow state) and it shares Max rate limits with
  interactive sessions; the watch-cancel keeps volume down. Switching to the Anthropic API
  later is a config change, not a redesign.
- **DT2. Turn briefing runs on EVERY session by default** (global kill switch in settings;
  no per-session opt-in ceremony). The old continuous wingman auto-explain loop stays as-is
  and untouched; TurnBriefing is a NEW pipeline beside it, and supersedes it for consumers
  over time.
- **DT3. BRIEFING is NOT a new ActivityState.** The multi-select capture proved asking and
  working coexist, so briefing state is an ORTHOGONAL field (`Session.BriefingState:
  None|Briefing|Briefed|Failed`). The rail color derives: detector-red + brief present ->
  red with railLine; detector-red + briefing in flight -> yellow "briefing..."; fyi urgency
  -> NOT red.
- **DT4. TurnBriefs are stored per session as JSON on disk** (Director-side, survives
  restarts) and served over new REST; the Cockpit never talks to the model.
- **DT5. Verbatim evidence is validated mechanically** (substring check, the one D6-allowed
  mechanical job: validating the model, not interpreting the agent). Failed validation =
  brief marked degraded, never silently shown.

## Phase 1 - Director: lifecycle + store, NO model call yet

Goal: a STUB brief flows end to end, proving the plumbing before any tokens are spent.

1. `Session.BriefingState` + raised event (Core/Sessions) - orthogonal per DT3.
2. `TurnBriefStore` (Core/Wingman): durable JSON per session
   (`state/turnbriefs/{sid}.json`, ring of last N=50 briefs), load on session restore.
3. `TurnPackageBuilder` (Core/Wingman): assembles the frozen turn package -
   transcript delta since the last brief (WidgetBuilder), the turn's hook prompt,
   rolling intent (last brief's intent), screen-grid snapshot (Session._htmlParser),
   ReplyPending + composer-unsubmitted detection (the boot gotcha from the captures).
4. `TurnBriefOrchestrator` (Core/Wingman): subscribes to the detector's turn-end,
   debounces the JSONL-flush race (transcript settled OR grid-only package), applies the
   ~10s watch-cancel (user input cancels the in-flight brief), writes a STUB TurnBrief.
5. REST (ControlApi): `GET /sessions/{sid}/turnbriefs` (list) + `/turnbriefs/latest`;
   DTOs in Gateway.Contracts mirroring the frozen v2.1 contract (selectionMode, submit,
   urgency, answerVia, options[].send/.note, evidence, confidence, railLine).
6. `SessionDto` gains `BriefingState` + `RailLine` so the rail and FIFO get it for free.
7. Tests: state transitions (incl. ask-while-working), store round-trip + restart
   survival, watch-cancel, package assembly fixtures for all six captured shapes.

Exit: slot-5 session runs a turn -> yellow flash -> stub brief retrievable, rail shows
the stub railLine. Zero LLM cost.

## Phase 2 - the wingman call

Goal: replace the stub with the strong model, held to the captures as the quality bar.

1. `TurnBriefGenerator` (Core/Wingman): side-claude spawn per DT1, structured JSON out
   (schema instruction in-prompt; response parsed as JSON, NEVER prose-mined - D6).
   Stdout-or-stderr error reporting (the #168 lesson).
2. The prompt: built from the prior-art `NeedsUserShort` wisdom + the SIX capture files
   as the spec (each names its correct output). Proportionality rule (small turn ->
   short brief). Honest-ambiguity rule (confidence: ambiguous, never invented certainty).
3. Validation layer: evidence substring check (DT5); options sanity (>=2 or zero, no
   invented choices; multi-select requires submit); urgency/railLine present when
   needsYou is.
4. Failure ladder: generator error -> brief stored with `degraded: true` + the Brief v1
   mini-condenser output as the labeled fallback tier; never a blank.
5. Eval harness (the captures become executable): fixture tests feed each capture's turn
   package through prompt assembly; a live opt-in test (WINGMAN_LIVE_TESTS=1 pattern)
   runs the real model against the six shapes and asserts the validation layer passes.

Exit: the six shapes re-staged on slot-5 each produce a correct, validated TurnBrief.
Measure: latency per brief, briefs/day x cost feel on Max limits (doc open question).

## Phase 3 - Cockpit consumption (and the regex parser dies)

1. `BriefPane` renders the TurnBrief: intent ("YOU'RE DOING"), did, needsYou statement
   with urgency styling (blocking=red / review=red / fyi=quiet), evidence behind
   "Claude's words", options as buttons.
2. **Answering**: single-select -> one tap sends `options[].send` via the raw-input path
   (keys) or prompt (reply). Multi-select -> buttons TOGGLE locally, a Submit button
   sends the toggles + `submit` sequence in order. Auto-advance stays.
3. **DELETE `ComputeQuickOptions`** and the choice-gate - D6 made flesh.
4. Rail + triage rows: railLine replaces the state enum on red rows; yellow chip while
   BriefingState=Briefing ("wingman reading the turn...").
5. Right-rail WINGMAN tab rebuilt: the TURN TIMELINE (stored briefs, newest first - the
   session's story) + "this brief is wrong" (D7) -> POST stores {brief, package, note}
   as a labeled example under state/brief-feedback/.
6. Degrade ladder per architecture: TurnBrief -> Brief v1 condenser -> raw summary ->
   terminal; each tier LABELED on screen.

Exit: live E2E on slot-5 across all six shapes driven from the Cockpit UI, including
answering a multi-select via toggle+submit without touching the terminal.

## Phase 4 - fleet + feedback (separate go-ahead)

- Phone FIFO cards + voice read TurnBriefStore (Android-first track owns the port).
- Feedback corpus review loop (collect ambiguous/wrong cases -> prompt iteration).
- Cost/latency report after a week of real fleet usage; revisit DT1 (API vs Max) with data.
- Old-Director sunset: as Directors relaunch onto new builds, the v1 condenser tier ages out.

## Risks / honest unknowns

- **Latency window**: if a turn ends and the user is ALREADY staring at the session, even a
  20s yellow is friction. Watch-cancel covers typing; staring-without-typing does not
  cancel. Mitigation: the Brief shows the live screen tail during yellow, so waiting is
  never blind.
- **Max rate limits**: brief volume is fleet-wide turn rate. Phase 2 measures before
  Phase 3 turns it on everywhere; the kill switch (DT2) is the brake.
- **Schema drift in claude --print JSON**: the validation layer rejects and degrades
  rather than rendering garbage - the page never lies, it downgrades visibly.
- **Multi-viewer writes**: two Cockpit viewers answering the same questionnaire - the
  Director's existing single-PTY serialization handles ordering; last answer wins, same
  as two people at one keyboard.

## Document history

| Date | Author | Change |
|---|---|---|
| 2026-06-04 | claude (cc-director session) | Initial implementation plan from TURN_BRIEFING v2.1. |
