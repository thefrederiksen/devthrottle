# Wingman Terminal-State Detection: Fix and Verification

Date: 2026-05-24
Build under test: cc-director-avalonia3.exe (slot 3), Control API on 127.0.0.1:7883
Classifier: TerminalStateDetector (mode=authoritative, llm=True, quiet=5s)

## Summary

The Wingman was misreading Claude Code's persistent mode footer
`bypass permissions on (shift+tab to cycle)` as a permission prompt and flagging a
healthy autonomous session as "NEEDS YOU" (red). This report covers the root
cause, the fix, the read-only guarantee, and end-to-end verification on a live
Director.

Result: FIXED and verified. A real session showing that exact footer is now
classified green / WaitingForInput, and the regression suite is 5 of 5.

## The bug (before)

In the field, the Wingman log showed:

```
green -> red   "Footer shows 'bypass permissions on (shift+tab to cycle)'
                indicating a permission confirmation the agent cannot pass
                autonomously."   [LLM]
```

That is backwards. `bypass permissions on` is Claude Code's mode indicator (the
shift+tab cycle: normal -> accept edits -> plan -> bypass permissions). In that
mode the agent never stops to ask for permission. The classifier saw the word
"permission" and pattern-matched, because its prompt had no knowledge of Claude
Code's actual on-screen chrome.

## Root cause

`WingmanService.BuildTerminalStatePrompt` described generic TUI concepts but
nothing specific to Claude Code. With no notion of the persistent mode footer, an
uninformed reader cannot tell it apart from a real permission box.

## The fix

A single shared reference, `WingmanService.ClaudeCodeScreenReference`, now teaches
every Wingman prompt how a Claude Code session looks:

- The persistent mode footer (`bypass permissions on` / `accept edits on` /
  `plan mode on`, with `shift+tab to cycle`) is a status line, never a prompt.
  `bypass permissions on` is evidence AGAINST waiting_for_permission.
- A real permission prompt is a bordered box with a numbered choice list
  ("1. Yes  2. Yes, and don't ask again  3. No").
- The empty input box (`>` + `? for shortcuts`, no spinner) is waiting_for_input.
- Spinner / elapsed counter / `esc to interrupt` is working.

`BuildTerminalStatePrompt` now embeds this reference, names the two traps
explicitly (lots-of-text != working; the word "permission" != a prompt), and
carries three few-shot examples including the bypass-footer case as a labeled
negative. No regex or string-match guard was added; the knowledge is expressed to
the model in natural language.

## Read-only guarantee (the Wingman never injects into the terminal)

Audited every terminal-write entry point. The PTY is written only via
`Session.SendInput` / `SendText` / `SendTextAsync` / `SendEnterAsync`. Every caller
is an explicit user-driven path:

- terminal keystrokes (TerminalControl)
- the send box and handover button (MainWindow)
- voice/chat/recovery REST endpoints (ControlEndpoints, ChatService, VoiceMode)
- a caller-supplied PrePrompt at session creation

The state-detection Wingman path -- SessionStatusWingman, TerminalStateDetector,
the WingmanService classifier, ProactiveExplainService -- only ever reads
`buffer.DumpAll()` and writes UI metadata (StatusColor) or a UI-side textbox
mirror (SetPendingPromptText, which populates the cc-director input box, never the
PTY). It contains no call to any Send method. The desktop Wingman is read-only with
respect to the terminal, so it cannot create the feedback loop that injection would.

## Regression suite (fixtures)

Five representative Claude Code tails were run through the real classifier (fresh
`claude --print` Haiku call). Each verdict maps to the expected app state. The
bypass-footer and plan-mode cases are the guards for this bug.

| Fixture | Expected | Verdict | Model's reason |
|---|---|---|---|
| waiting_input_bypass_footer | WaitingForInput | waiting_for_input | empty input box; the bypass-permissions line is the mode footer, not a pending request |
| permission_box | WaitingForPerm | waiting_for_permission | parked on a numbered confirmation box to edit app-settings.json |
| working_spinner | Working | working | spinner, elapsed counter (Brewed for 8s), esc-to-interrupt |
| plan_mode_input | WaitingForInput | waiting_for_input | empty input box; the plan-mode footer is mode status, not a request |
| cancelled | WaitingForInput | waiting_for_input | tests interrupted; agent back at empty input box |

Result: 5 of 5 PASS. Fixtures and the captured `results.json` live under
`docs/features/terminal-state-detector/fixtures/`. The test
(`TerminalStateClassifierLiveTests`) is opt-in via `WINGMAN_LIVE_TESTS=1` so normal
CI is unaffected.

## End-to-end on the live Director

A real ClaudeCode session was created on the slot-3 Director (the build carrying
this fix). It settled at the empty prompt showing the bug footer:

```
Claude Code v2.1.150 ... Opus 4.7 (1M context)
D:\ReposFred\cc-director
> Try "edit <filepath> to ..."
bypass permissions on (shift+tab to cycle)
```

The Wingman classified it:

- statusColor = green
- activityState = WaitingForInput
- reason = "empty input box with hint; bypass-permissions line is the mode footer,
  not a permission prompt"

Wingman log (most recent first), mirroring the field screenshot's panel:

```
green -> green  llm=true  "empty input box with hint; bypass-permissions line is
                           the mode footer, not a permission prompt"
blue  -> green  llm=false "ready, awaiting next prompt"
green -> blue   llm=false "working"
```

The same screen that previously produced a false red now produces a correct green,
and the LLM verdict explicitly recognizes the footer.

## Phase 2: full-power, fresh-per-call Wingman session (built and verified)

The classifier was promoted from a single Haiku call with a pasted 4000-char tail
to a FULL-POWER, fresh-per-call Claude Code session that reads the terminal on its
own and decides how far back to look.

How it works (`WingmanService.ClassifyTerminalStateViaSessionAsync` +
`RunWingmanSessionAsync`):
- On the quiet-gate trigger, the WHOLE terminal history (ANSI stripped, capped at
  200 KB) is written to a temp snapshot file.
- A fresh `claude --print` session is spawned with read-only tools
  (`Read Grep Glob`), MCP disabled (`--strict-mcp-config` + empty `--mcp-config`
  for a lean, fast cold start), bounded `--max-turns 6`, and
  `--no-session-persistence` (no state carried between calls).
- The session Reads as much of the snapshot as it needs, then returns the same
  JSON verdict. The temp file is deleted afterward.

Read-only by construction: the only tools allowed (Read/Grep/Glob) cannot write,
and a separate `claude` process has no handle to the partner's in-memory PTY, so it
cannot inject into -- or resize -- the terminal it is judging. This holds the
"Wingman never writes the terminal" invariant.

Wiring: ON by default; set `CC_DIRECTOR_TERMSTATE_FULLSESSION=0` to fall back to
the lighter tail-paste judge. Everything else (the byte gate, the >=5s throttle,
the verdict->state mapping) is unchanged.

### Phase 2 regression (both judges, same fixtures)

Running the suite with `WINGMAN_LIVE_FULLSESSION=1` exercises both judges over the
five fixtures: 10 of 10 PASS (5/5 tail-paste, 5/5 full-session). The full-session
reasons are visibly tool-grounded -- they quote the snapshot and cite line numbers,
e.g. for the bypass-footer case: "Session shows completed work ('Done. I updated
the loader...') with prompt box at bottom (> with '? for shortcuts' hint) and no
spinner or active indicator."

### Phase 2 on the live Director

Slot-3 Director launched with the flag on; the log confirms
`TerminalStateDetector Start (mode=authoritative, llm=True, judge=full-session)`.
A real session in bypass-permissions mode was classified by the full-power judge:

```
wingman session done in 12792ms (tools=Read Grep Glob, maxTurns=6)
LLM verdict=waiting_for_input
  "Input box visible with hint text ... and no active spinner or elapsed-time
   counter - agent finished and awaiting next instruction."   judge=full-session
```

The session spun up, used its read tools, finished in ~13 s, and produced the
correct green / WaitingForInput verdict -- not a false permission red. Latency is
not user-blocking: the byte gate already set the provisional state instantly; this
verdict is the async refinement.

## Status

Done and verified:
- Phase 1: Claude Code screen reference + few-shot (the fix), read-only audit,
  fixtures + opt-in live test (5/5), live-Director verification.
- Phase 2: full-power fresh-per-call Wingman session with read-only tool access,
  both-judge regression (10/10), live-Director verification.

Plan of record: `docs/plans/wingman-full-session.md`.
