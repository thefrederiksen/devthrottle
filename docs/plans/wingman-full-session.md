# Wingman as a Full-Power, Fresh-Per-Call Claude Code Session

Status: PLAN (not implemented). Awaiting approval.
Date: 2026-05-24

## Problem

The Wingman misclassifies Claude Code's persistent mode footer
`bypass permissions on (shift+tab to cycle)` as a permission prompt. In the
field it transitioned a healthy autonomous session `green -> red` and labeled it
"NEEDS YOU", with the LLM reason: "Footer shows 'bypass permissions on
(shift+tab to cycle)' indicating a permission confirmation the agent cannot pass
autonomously."

That is exactly backwards. The footer is Claude Code's mode indicator (the
`shift+tab` cycle: normal -> auto-accept edits -> plan -> bypass permissions). In
bypass-permissions mode the agent NEVER stops to ask for permission. The
classifier saw the word "permission" and pattern-matched, because the prompt that
drives it has no knowledge of Claude Code's actual terminal chrome.

This is a knowledge gap in the prompt, not a logic bug. Per project rules we do
NOT patch it with a regex/string guard ("if footer contains 'bypass
permissions'..."). We teach the model how the screen looks, in natural language.

## Two design decisions (settled)

1. The Wingman is a full-power Claude Code session (real tools), but it reads its
   partner session through a tool instead of us pasting a fixed terminal slice
   into the prompt. It pulls only as much terminal history as it decides it needs.

2. It runs FRESH every call (`claude --print --no-session-persistence`), not a
   warm process that we `/clear` between calls. Rationale:
   - Isolation is provable with fresh (no session exists to bleed); `/clear` is a
     soft reset whose internals we do not own and cannot guarantee. "No bleed-over
     between sessions" is a hard requirement.
   - Latency is not user-blocking: the cheap byte-gate already applies a
     provisional state instantly on quiet; the Wingman verdict is an async
     refinement that lands a few seconds later.
   - Warm processes cost one idle claude.exe per session across 4+ parallel
     Directors. Fresh consumes nothing between turns.
   - Fresh does NOT mean weak: `--print` still carries tools and
     `--dangerously-skip-permissions`.

## Current state (what already exists, stays as-is)

- `TerminalStateDetector` (src/CcDirector.Core/Wingman): per-session byte-activity
  gate. Bytes flowing => Working (instant). Silent > 5s => quiet gate fires,
  applies provisional `WaitingForInput`, raises turn-ended, then (if `useLlm`)
  calls the classifier. KEEP.
- `WingmanLlmThrottle`: >= 5s per-session floor on LLM calls. KEEP.
- `MapVerdictToActivityState`: verdict string -> ActivityState. KEEP.
- `WingmanService.RunSideClaudeAsync` (WingmanService.cs:1150): already spawns
  `claude --print --model <haiku> --no-session-persistence --tools "" --
  dangerously-skip-permissions --output-format text <prompt>`, strips nested-CC
  env vars, and has a `Stopwatch`. This IS the fresh-per-call lifecycle. We extend
  it, we do not replace it.
- Control API already exposes `GET /sessions/{sid}/buffer?lines=&since=&raw=` and
  `/buffer/html` (ControlEndpoints.cs:361, :413). The read tool sits on these.

## Plan

### Step 1 - Teach the classifier Claude Code's TUI (the actual fix)

In `WingmanService.BuildTerminalStatePrompt` (WingmanService.cs:74), add a
"Claude Code screen reference" section, expressed as natural language:

- Persistent mode footer: `bypass permissions on`, `accept edits on`,
  `plan mode on`, usually with `(shift+tab to cycle)`. This is ALWAYS on screen,
  it is a MODE indicator, it is NEVER a permission prompt. `bypass permissions on`
  specifically means the agent will not stop to ask for permission at all -> this
  alone never implies waiting_for_permission.
- A REAL permission prompt is a bordered box: "Do you want to proceed?" /
  "Do you want to make this edit to <file>?" with numbered options
  ("1. Yes  2. Yes, and don't ask again  3. No, and tell Claude what to do
  differently") and a selector. Parked on that box => waiting_for_permission.
- Empty input box (`>` with the `? for shortcuts` hint, no spinner) =>
  waiting_for_input.
- `esc to interrupt` footer or a spinner / elapsed counter => working.

Add 3-4 few-shot examples (short real tails -> correct verdict), INCLUDING the
exact bypass-footer case as a labeled negative (footer present -> NOT
waiting_for_permission).

Centralize this knowledge as a single shared constant (e.g.
`WingmanService.ClaudeCodeScreenReference`) so the turn-summariser and any future
Wingman prompt reuse the same training. Train once.

This step alone fixes the screenshot, even if Steps 2-3 are deferred.

### Step 2 - Full-power Wingman invoke (fresh per call)

Add a sibling to `RunSideClaudeAsync`, e.g. `RunWingmanSessionAsync`, that keeps
the same fresh `--print --no-session-persistence` lifecycle but:
- enables a scoped allow-list of tools instead of `--tools ""`,
- keeps MCP OFF (lean config = faster cold start),
- keeps `--dangerously-skip-permissions` (already passed),
- keeps the nested-CC env-var stripping and the `Stopwatch`.

Keep model on Haiku for the classify; escalate only if measurement shows Haiku
can't reason over the tool results.

### Step 3 - `read_partner_terminal` tool

Expose a read-only tool, scoped to exactly one partner session, that returns the
partner's terminal tail. Back it with the existing
`GET /sessions/{sid}/buffer?lines=` (ANSI-stripped path) so the Wingman pulls as
much history as it wants, in one or more calls. Read-only: the Wingman can never
write input into the partner session. The partner never sees the Wingman.

Transport options to decide at implementation time (in order of preference):
- a built-in tool the side-call is told about, served in-process from the live
  `CircularTerminalBuffer`; or
- a tiny MCP shim over the existing Control API buffer endpoint.

### Step 4 - Instrument cold-start

Reuse the existing `Stopwatch` to log Wingman invoke duration; emit p50/p95 to the
director log. The latency decision (fresh vs anything else) stays evidence-based,
not assumed.

### Step 5 - Fixtures + opt-in live regression test

- Capture real terminal tails for each state into
  `docs/features/terminal-state-detector/fixtures/`: working, waiting_for_input
  WITH the bypass footer, a genuine permission box, plan mode, cancelled.
  (Prefer capturing from live sessions via the buffer endpoint over reconstructing.)
- Add an opt-in (skippable, requires claude CLI) integration test that runs each
  fixture through `ClassifyTerminalStateAsync` and asserts the expected verdict.
  The bypass-footer fixture is the regression guard for this bug.
- Existing `TerminalStateDetectorTests` (verdict->state mapping) stay.

### Step 6 - End-to-end verification

Launch a slot-4 Director via the `cc-director-launch` scheduled task, open a
session, put it in bypass-permissions mode (shift+tab), let it go quiet, and
confirm the Wingman log shows the correct state and a correct reason. Verify on the
real thing, not a stand-in.

## Isolation guarantees (must hold)

- Working and Wingman are separate processes / separate (or no) sessions.
- One channel only: Wingman -> read_partner_terminal (read-only).
- Wingman never writes input to the Working session.
- Working session has no knowledge of the Wingman.
- Wingman keeps no state across invocations: call K cannot leak into call K+1.

## Out of scope

- Autonomous decision-filtering / the Wingman acting on the user's behalf
  (DEFERRED, needs explicit go-ahead per existing memory).
- Any change to the byte gate, throttle, or verdict mapping.
- Any change to the Working terminal's native Claude Code UX (sacred rule).
