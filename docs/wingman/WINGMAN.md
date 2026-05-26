# The Wingman

This is the single source of truth for the Wingman: what it is, the invariants it must
hold, and how it is actually built today (as-shipped). It replaces the earlier
`CHARTER.md` (governance) and `REDESIGN.md` (a design that was largely abandoned). If you
change Wingman behavior, read this first and keep it accurate.

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

These hold for every Wingman path, present and future. The audit gate
(`CcDirector.Core.Tests/Wingman/WingmanCharterAuditTests.cs`) fails the build if any file
under `src/CcDirector.Core/Wingman/` violates the mechanical ones.

1. **Strong model only - NEVER a cheap model.** The Wingman runs on `WingmanService.Model`
   (currently `opus`). There is no Haiku/cheap tier; a weak model cannot read a screen
   faithfully or answer without summarizing. `DefaultModel`/`StrongModel` are back-compat
   aliases of `Model`. (Audited: no cheap-model literal in Wingman source.)
2. **Read-only.** The Wingman observes; it never writes to, sends to, or resizes a session.
   Any full-power Wingman session gets a read-only tool allow-list (`Read Grep Glob`) only.
   (Audited: no non-read-only `allowedTools`.)
3. **Faithful, not summarizing, when content is asked for.** Status outputs (badge, terse
   briefing) may be short, but when the user asks to *read* content ("read me the article")
   the Wingman reproduces it verbatim, complete, no length cap.
4. **Stateless side-calls.** Each Wingman call is a fresh `claude --print`
   (`--no-session-persistence`, MCP off). No hidden conversation memory between calls.
5. **Fail closed - never fabricate.** On any failure or ambiguity, return an explicit
   `unknown`/error result. Never invent a state, file, decision, or content.
6. **All Wingman code lives under `src/CcDirector.Core/Wingman/`** so the audit covers it.

---

## 3. Turn-state detection (the badge): trigger + ONE LLM call

This is how the colored status badge ("working" / "needs you" / "ready") is decided. It is
two stages, and deliberately **not** regex screen-parsing - reading a messy, version-varying
terminal screen is an LLM job.

### Stage 1 - the trigger (cheap, no LLM): `TerminalStateDetector`
A per-session watcher on the terminal byte stream. While bytes keep changing the screen the
session is "active"; when output goes quiet (`QuietThreshold`, ~5s) the gate fires - "the
turn might be over." This only decides *when* to look; it does not classify. Free.

### Stage 2 - the judge (one LLM call): `WingmanService.ClassifyTerminalStateAsync`
On a gate fire, ONE strong-model `claude --print` call is handed the terminal tail and
returns a verdict: `working | waiting_for_input | waiting_for_permission | idle | cancelled
| unknown`, plus a one-line reason and the verbatim pending question (`awaiting`). The judge
reads the screen the way a person would - it handles torn footers, the picker menus, and
Claude Code version changes because it reads meaning, not patterns.

`SessionStatusWingman.ColorFromVerdict` maps the verdict to the badge colour;
`MapVerdictToActivityState` maps it to `ActivityState`.

### The decisive rules (in the prompt, shared by both judges)
The judge's prompt is `BuildTerminalStatePrompt`; the decisive logic lives in the shared
`ClaudeCodeScreenReference` (point 5) so it is the single source of truth:
- **Read the BOTTOM-MOST footer line.** If it literally contains `esc to interrupt` ->
  **working** - even if an empty input box is also shown (the agent shows both while
  working). A stale `esc to interrupt` higher in the scrollback does NOT count.
- If the bottom footer has no `esc to interrupt`, the turn is **over**:
  - `waiting_for_permission` ONLY for a real gate: a numbered-choice box, a `[y/n]` prompt,
    or an `Enter to select ... Esc to cancel` menu (CC Director's question pickers). The
    persistent mode footer (`shift+tab to cycle`) is NOT a gate.
  - otherwise `waiting_for_input` - including a prose question/offer ("OK to proceed?",
    "Want me to ...?") with no numbered box.
- `unknown` for blank/garbled/banner-only screens. Never fabricate `working` from garbage.

### Toggles
- `CC_DIRECTOR_TERMINAL_STATE=0` - use the Claude-Code hook path instead of terminal-driven
  (off by default; see section 7 on why hooks are not used).
- `CC_DIRECTOR_TERMSTATE_LLM=0` - run the free byte-gate alone, no LLM judge.
- `CC_DIRECTOR_TERMSTATE_FULLSESSION=1` - opt into the heavier full-power-session judge
  (`ClassifyTerminalStateViaSessionAsync`, reads a snapshot file via tools). Default OFF:
  the lighter tail-paste judge is the one proven 100/100 and is faster (less badge lag).
  Both judges share the same `ClaudeCodeScreenReference` rules.

### How it is proven
`docs/features/terminal-state-detector/synthetic/` holds 100 labeled synthetic states
(generated by `generate_states.py`) spanning working / waiting / permission / picker menus /
cancelled / unknown, including the hard real cases (torn footers, footer-less spinners,
stale scrollback, the live "Pick a joke" picker). `SyntheticStateJudgeTests` (opt-in,
`WINGMAN_SYNTH_TEST=1`) runs every state through the live judge and writes `report.html`.
Current result: **stable 100/100**.

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

- **Per-turn summary** - `SummarizeTurnAsync` (Agent View headline + structured turn).
- **Explain briefing** - `AskAboutSessionAsync(explain)` (terse "what's happening").
- **Rules / memory enforcement** - `CheckRulesAsync` (CLAUDE.md violations).
- **Goal tracking** - `AssessGoalAsync` (on-track / drifting / complete).
- **Git awareness / crash recovery** - `GitSnapshotAsync`, `BuildRecoveryPromptAsync` (no LLM).

---

## 6. The session recorder (corpus for offline learning)

`TerminalSessionRecorder` (observe-only, on by default; `CC_DIRECTOR_RECORD_SESSIONS=0` to
disable) logs every session's resolved terminal grid - one JSONL frame per change, each with
the activity state and the raw rows - to `%LOCALAPPDATA%/cc-director/session-recordings/`,
capped per session. This is the ground-truth corpus: when the judge gets a real frame wrong,
that frame becomes a new fixture for the synthetic suite and the prompt is tightened.

---

## 7. What we deleted, and why (do not resurrect)

- **Regex screen-parsing** (`ClaudeScreenReader`, `FinishDetectorCore`, `FinishDetector`):
  removed. Pattern-matching footers/glyphs/menus broke on every real variation (torn
  footers, `›` vs `❯`, the picker menus, version drift). The LLM judge replaced it.
- **Hook-based finish detection / "fuse the Stop hook":** abandoned. In the default
  terminal-driven mode the Director deliberately does NOT install Claude Code hooks
  (`App.axaml.cs`), and `ClaudeAgent` launches with only `--session-id` - so no hooks flow.
  Detection is terminal-only by design.
- **Cheap-model (Haiku) Wingman calls:** removed; the whole Wingman is one strong model.

---

## 8. Known limits / open items

- The judge is proven on the 100-state synthetic suite (static screens) and real captures,
  not yet end-to-end-verified live: i.e. that the byte-gate fires promptly on a real new
  turn (especially a picker menu) so the correct judge runs in time. Worth a live check.
- Inherent latency: badge update = quiet-gate (~5s) + one LLM call, so a just-finished turn
  can read "working" briefly before flipping.
- It is an LLM: rare non-deterministic misses on borderline frames are possible. The
  recorder corpus + synthetic suite are how we keep tightening it.
- Signatures are v2.1.150-era; the LLM generalizes across drift far better than regex, but a
  major Claude Code UI change still warrants re-running the synthetic suite.
