# The Wingman

This is the single source of truth for the Wingman: what it is, the invariants it must
hold, and how it is actually built today (as-shipped). If you change Wingman behavior, read
this first and keep it accurate.

---

## 1. What the Wingman is

The Wingman is the user's second set of eyes on every Claude Code / AI session card in CC
Director. It rides alongside each session and answers, for the user, "what is this session
doing, does it need me, and what did it actually say?" - without making the user read the
raw terminal. It is a cross-cutting component used in many places and growing.

The bar for every Wingman feature: **does it actually help, or does it send the user back
to the raw tab?** A lossy or wrong answer the user can't trust is worse than nothing.

---

## 2. Hard invariants (enforced)

These hold for every Wingman path. The audit gate
(`CcDirector.Core.Tests/Wingman/WingmanCharterAuditTests.cs`) fails the build if any file
under `src/CcDirector.Core/Wingman/` violates the mechanical ones.

1. **Strong model only - NEVER a cheap model.** The Wingman's LLM features run on
   `WingmanService.Model` (currently `opus`). There is no Haiku/cheap tier; a weak model
   cannot read a screen faithfully or answer without summarizing. `DefaultModel`/`StrongModel`
   are back-compat aliases of `Model`. (Audited: no cheap-model literal in Wingman source.)
   Note: this applies to the **content** features in sections 4-5. Turn-**state detection**
   (section 3) uses no model at all.
2. **The Wingman's LLM calls are read-only.** No Wingman LLM call may write to a session.
   Any full-power Wingman session gets a read-only tool allow-list (`Read Grep Glob`) only,
   and the side-calls are tool-less (`--tools ""`). (Audited: no non-read-only `allowedTools`.)
   Actuation does NOT change this: when the Wingman acts on a terminal it does so via the
   structured-intent path (section 7), where the **Director** writes - the model only ever
   returns a proposed action and is never handed a write tool.
7. **One write chokepoint.** All Wingman actuation goes through `WingmanActionExecutor`; it is
   the only file under `src/CcDirector.Core/Wingman/` allowed to call a Session write method,
   and every actuation it performs is logged. (Audited.)
8. **Actuation is request-driven - the Wingman never acts on its own.** Every action originates
   from an explicit `POST /sessions/{sid}/wingman/act`. There is no turn-completion hook, timer,
   or background loop that invokes actuation; the Wingman does not wake up and "figure out what
   to do" after a turn. (Audited: nothing under `src/CcDirector.Core/Wingman/` calls
   `WingmanActionExecutor.Execute`.)
3. **Faithful, not summarizing, when content is asked for.** Status outputs (badge, terse
   briefing) may be short, but when the user asks to *read* content ("read me the article")
   the Wingman reproduces it verbatim, complete, no length cap.
4. **Stateless side-calls.** Each Wingman LLM call is a fresh `claude --print`
   (`--no-session-persistence`, MCP off). No hidden conversation memory between calls.
5. **Fail closed - never fabricate.** On any failure or ambiguity, return an explicit
   `unknown`/error result. Never invent a state, file, decision, or content.
6. **All Wingman code lives under `src/CcDirector.Core/Wingman/`** so the audit covers it.

---

## 3. Turn-state detection (the badge): one timer, blue or red

This is how the colored status badge is decided. It is **one mechanical rule with no LLM,
no regex, and no screen parsing** - deliberately the simplest thing that can possibly work.

### The detector: `TerminalStateDetector`
A per-session watcher on the terminal byte stream with exactly two rules:

1. **A byte out of the ConPTY means the agent is producing output -> `Working`.** We set
   Working the instant a byte arrives and re-arm an idle countdown on every byte. We do not
   inspect what the byte is.
2. **Complete silence for `QuietThreshold` (10s) -> `WaitingForInput`.** When the stream has
   produced nothing for the threshold, the session "needs you". That is the entire decision.

The detector treats a long silence as "needs you" regardless of *why* output stopped - the
agent may have finished cleanly, be blocked on a question, or just be thinking slowly. It
does not try to tell those apart. The only derived signal it relies on is "time since the
last byte", which the session's `CircularTerminalBuffer.LastWriteAtUtc` already tracks.

**One exception: Director-induced repaints are not agent output.** When the Director issues a
PTY resize - on attaching/switching to a session, force-refresh, or a layout change
(`TerminalControl.ResizeSession`) - Claude Code repaints its whole screen and emits a burst of
bytes. Those bytes are *our* doing, not the agent working, so without a guard the detector
would flip an idle (red) session to blue the instant you switch to it. Before each resize the
control calls `Session.SuppressActivityFor` (a ~1.5s window); the detector early-returns on any
byte while `now < Session.SuppressActivityUntilUtc`, counting neither Working nor an idle re-arm.
The window is far under the 10s quiet threshold, so a genuine work-start landing inside it is at
most delayed until the next byte after the window. The detector stays content-blind - it never
inspects the bytes, only whether the Director just caused them.

### The colour: `SessionStatusWingman` (the single writer)
`SessionStatusWingman` is the sole writer of `Session.StatusColor`. It is a direct,
mechanical map from `ActivityState` to a colour - there is **no other colour algorithm
anywhere**:

| ActivityState                         | Colour            | Reason       |
|---------------------------------------|-------------------|--------------|
| `Working`, `Starting`                 | blue              | "working"    |
| `WaitingForInput`, `WaitingForPerm`, `Idle` | red         | "needs you"  |
| `Exited`                              | gray (`unknown`)  | "exited"     |

Because the detector only ever emits `Working` and `WaitingForInput`, in practice the badge
is just **blue (working)** or **red (needs you)**.

### Toggle
- `CC_DIRECTOR_TERMINAL_STATE=0` - use the Claude-Code hook path to drive `ActivityState`
  instead of the terminal timer (off by default; see section 7 on why hooks are not used).
  The colour mapping above is unchanged either way.

---

## 4. Ask the Wingman (faithful, full-access answers)

A voice/REST channel separate from talking to the agent: the user asks the Wingman a
question and it answers faithfully from the session, reading content verbatim.

- **`WingmanService.AnswerViaSessionAsync`** - a read-only full-power session (`Read Grep
  Glob`, strong model) handed the whole terminal + repo. It reads as much as it needs and
  reproduces content verbatim; **no length cap**. Used for free-text questions.
- **Endpoint** `POST /sessions/{sid}/wingman/ask`: a free-text `Question` -> the faithful
  answer path; `Mode=explain` -> a terse "what's happening" briefing
  (`AskAboutSessionAsync`, explain mode).
- **"Hey wingman" routing**: `CleanVoiceTranscriptAsync` (run on each dictated utterance)
  cleans the transcript AND returns a `Target` (`agent` | `wingman`), detecting the wake
  phrase by LLM intent (not regex) and stripping it. The phone routes on that target;
  there is also an explicit "Ask Wingman" button.

---

## 5. Other Wingman responsibilities (all strong-model side-calls)

These are content features and have nothing to do with the badge colour (section 3 owns
that). They each read this session's own terminal transcript.

- **Per-turn summary** - `SummarizeTurnAsync` (Agent View headline + structured turn). It is
  cached and persisted for the Agent view, voice, and goals. It does **not** vote on the
  badge colour.
- **Explain briefing** - `AskAboutSessionAsync(explain)` (terse "what's happening").
- **Rules / memory enforcement** - `CheckRulesAsync` (CLAUDE.md violations).
- **Goal tracking** - `AssessGoalAsync` (on-track / drifting / complete).
- **Git awareness / crash recovery** - `GitSnapshotAsync`, `BuildRecoveryPromptAsync` (no LLM).

---

## 5b. Actuation (structured-intent): the Wingman acts, the Director writes

The Wingman can act on a session's input prompt - type, press named keys, or submit a line -
without ever being handed a write tool. The split is the whole point:

1. **Decide (LLM, tool-less, read-only).** `WingmanService.DecideSessionActionAsync` runs a
   strong-model `claude --print --tools ""` side-call over the session's live screen + cursor
   (`Session.SnapshotScreenRowsWithCursor`), pending question, and state. It returns a
   `WingmanAction` JSON: `none | type | send_keys | submit`. The model proposes; it cannot
   touch the terminal. Fail-closed: anything ambiguous, low-confidence, or unparseable becomes
   `none`. The model is told to choose `none` for any decision the user owns.
2. **Execute (trusted C#).** `WingmanActionExecutor.Execute` is the **only** code under
   `src/CcDirector.Core/Wingman/` allowed to write to a session. It maps the validated action
   to bytes (`KeyChords` for named keys) and writes through the same `Session.SendInput` path a
   human keystroke uses - so Claude Code's UX is unchanged (terminal stays sacred).

Endpoint: `POST /sessions/{sid}/wingman/act` (decide + execute). `?decideOnly=true` returns the
decision without executing it - a dry run for tests and tooling.

Request-driven and always-on: the Wingman never triggers itself - it acts only when a user
request hits the endpoint (invariant 8). There is no per-session enable flag and no
confirm-first gate, so when it IS asked, it just acts. The safeguards that remain are not
permission gates, they are correctness:

- **Audit.** Every performed action is logged to `FileLog` and to `Session.RecentWingmanActions`
  (surfaced in `GET /sessions/{sid}/wingman`), so you can always see what the Wingman typed and why.
- **Self-injection guard.** Before writing, the executor calls `Session.SuppressActivityFor`
  (the same window the resize path uses) so the `TerminalStateDetector` does not mistake the
  echo/repaint of the Wingman's own keystrokes for fresh agent output and loop on it.
- **Idempotency / cooldown.** The executor refuses to act twice on an unchanged screen within a
  short window (`LastActedScreenHash` + `ActionCooldown`), so a repeated request (e.g. a
  double-tap) cannot inject onto a screen the Wingman just acted on.

---

## 6. The session recorder (corpus for offline learning)

`TerminalSessionRecorder` (observe-only, on by default; `CC_DIRECTOR_RECORD_SESSIONS=0` to
disable) logs every session's resolved terminal grid - one JSONL frame per change, each with
the activity state and the raw rows - to `%LOCALAPPDATA%/cc-director/session-recordings/`,
capped per session. A general-purpose corpus of what sessions actually looked like.

## 6b. State-change log + the Desktop Wingman tab (observability)

Every state transition (blue<->red) is recorded so a session's history is inspectable:

- **In-memory ring** (`Session.RecentStateChanges`, newest first, capped 100): each entry is
  `{ time, from-state, to-state }`, recorded by `Session.SetActivityState` on every real
  transition.
- **Durable log** (`StateChangeLog`): one append-only JSONL per session at
  `%LOCALAPPDATA%/cc-director/state-changes/<sessionId>.jsonl`, each record carrying the
  timestamp, the from/to state, and the colour. Written by `SessionStatusWingman`. On by
  default; `CC_DIRECTOR_STATE_LOG=0` keeps only the in-memory ring.
- **`Session.LastOutputAtUtc`**: the raw "the terminal moved" timestamp, updated on every
  buffer write.

The **Desktop Wingman tab** (`WingmanView`, right-panel `TabControl` beside Screenshots)
renders this live for the selected session: the current colour, a once-a-second
**"Terminal moved: Ns ago" silence clock** with the current activity state, and the
state-change timeline. The silence clock is the key diagnostic - it shows exactly the
silence the 10s rule is counting. It is read-only - it observes the session, never writes
to it (invariant 2).

## 7. What we deleted, and why (do not resurrect)

- **The LLM turn-state judge** (`ClassifyTerminalStateAsync`,
  `ClassifyTerminalStateViaSessionAsync`, `ColorFromVerdict`, `MapVerdictToActivityState`,
  the `BuildTerminalState*Prompt` builders, and the 100-state synthetic / fixtures corpus):
  removed. The judge read the screen with an LLM each time the terminal went quiet and
  mapped a verdict to a colour. It worked but added latency (a model call per turn-end),
  cost, and non-determinism, and it was a second classifier that could disagree with the
  byte gate. Replaced by the dumb 10s timer in section 3.
- **The competing colour heuristics in `SessionStatusWingman`**: the byte-burst
  `OutputActivityWatcher` (blue on a burst), the buffer question-marker scan
  (`BufferShowsUserGate` / `PromotePendingQuestion`), and the turn-summary colour voting
  (`ApplyTurnSummary`). Each was a separate path that could set the colour, and together they
  flip-flopped the badge. Removed so the colour has a single source: `ColorFromActivityState`.
- **Regex screen-parsing** (`ClaudeScreenReader`, `FinishDetectorCore`, `FinishDetector`):
  removed earlier; never resurrect pattern-matching footers/glyphs/menus.
- **Hook-based finish detection:** in the default terminal-driven mode the Director
  deliberately does NOT install Claude Code hooks (`App.axaml.cs`), and `ClaudeAgent`
  launches with only `--session-id`. Detection is terminal-only by design.

---

## 8. Known limits / open items

- **The timer is deliberately dumb.** A clean, finished turn goes silent and flips to red
  ("needs you") after 10s just like a turn that is genuinely blocked - the detector does not
  distinguish them. This is the accepted trade-off for having one simple, predictable,
  zero-LLM rule. If red-fatigue becomes a problem, the lever is the threshold or a smarter
  rule, not a second classifier.
- **Two colours.** The badge is blue or red (plus gray for an exited session). Green/yellow
  are no longer produced by the detector.
- **A genuinely stuck session reads red the same as one waiting for you.** The silence clock
  in the Wingman tab is how you tell a long-running-but-alive turn from a finished one at a
  glance.
- `SessionStatusWingman` writes via `Session.SetStatusColor`, which still carries a
  source-precedence guard left over from the multi-writer era. With a single writer it never
  fires; it is harmless but vestigial and can be removed.
