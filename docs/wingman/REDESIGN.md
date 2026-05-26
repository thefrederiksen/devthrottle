# Wingman Redesign (scorched earth)

Status: DESIGN approved (2026-05-25). Decisions locked (see section 6): (1) FUSE the
`Stop` hook with positive terminal idle-confirmation; (2) the Wingman speaks ONLY when the
user is needed - otherwise the badge updates silently; (3) start with one Wingman call per
finished turn, add a cheap pre-filter later only if cadence bites. Building the keystone
(finish detection) first, proven against captures, before anything else.

Guiding instruction from Soren: stop patching. Look at every piece again. Finish
detection is the most important part and we are not good at it. The Wingman is not a
summarizer - it is the thing that helps; it must decide for itself how much of the
session to read and whether to summarize or read verbatim. Keep only the pieces that are
genuinely good.

---

## 1. What we are actually building

**One Wingman.** Not a summarizer, a recap generator, a state classifier, a rules
checker, and a goal assessor that each fire their own model call. One read-only,
intelligent observer that is woken at the right moment, reads as much of the session as
it decides it needs, and produces whatever the situation calls for.

Three properties define it:

1. **It is woken, not polled.** It runs once, when a turn is genuinely finished (or when
   the user asks it something) - never on a timer, never several times per turn. The
   thing that decides "a turn finished" is a separate, deterministic, no-LLM detector
   (section 2). That detector is the keystone of the whole system.

2. **It has agency over context.** It is not handed a fixed 4000-char tail. It can read
   the entire session transcript and the repo on its own (read-only tools) and decides
   how far back to go to answer the question in front of it.

3. **It decides verbosity by need, not by a cap.** "What's happening?" -> a tight glance.
   "Read me the article we just wrote" -> the whole article, verbatim, no cap. The
   Wingman judges which is called for. It never silently summarizes content the user
   asked to hear in full.

One brain, two ways it is invoked:
- **Proactive** (a turn just finished): "what just happened, and do you need to act?"
- **On-demand** (the user spoke to it): answer the question.

Same agent, same context-agency, same model. The only difference is the prompt's task.

This is also what "help me, not in the stupid way we do it now" means: the bar is that
the Wingman keeps the user out of the raw terminal. If its output is a lossy gist the
user can't trust, it has failed, regardless of whether it returned something.

---

## 2. Finish detection - the keystone

This is the most important component and the one we are currently worst at. Everything
else depends on it: if we wake the Wingman mid-turn, every output is wrong; if we miss a
turn-end, the user is never told. It must be reliable, deterministic, and free (no LLM).

### 2.1 Why we are inconsistent today

The current detector (`TerminalStateDetector`) infers finish from **terminal silence**:
no bytes for 5 seconds, with the `"esc to interrupt"` footer gone, then it asks an LLM to
confirm. This is fragile by construction:

- **Silence is not finish.** A working agent blocked on a long Bash, a network wait, or a
  sub-agent stops emitting bytes while still working. Sometimes the working footer stays
  up (handled), sometimes Claude shows a live spinner *without* that footer (NOT handled)
  - so a mid-turn pause can look finished.
- **5 seconds is a guess.** A genuine think-pause longer than 5s looks finished; a tool
  that pauses under 5s then resumes hides a real boundary.
- **It leans on an LLM to confirm**, which is exactly the per-turn cost we are trying to
  remove, and the LLM can be wrong too.
- **Screen-scraping is brittle**: ANSI, grid resolution, varied footers.

### 2.2 The signal we are ignoring: Claude Code's own hooks

CC Director **already installs Claude Code hooks** (`HookInstaller`): `SessionStart`,
`UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Notification`,
`SubagentStart`, `SubagentStop`, **`Stop`**, `PreCompact`, `SessionEnd`. `Stop` is the
authoritative "the main agent finished its turn" signal; `UserPromptSubmit` is the
authoritative turn-start; `PermissionRequest`/`Notification` mark a real gate.

The project moved to terminal-driven state because hooks "go stale" in specific cases
(e.g. `Stop` not firing cleanly on `/clear` or a cancel, leaving state stuck Working). So
we distrust hooks alone - correctly. But we threw out the most authoritative signal we
have instead of fixing its blind spots.

### 2.3 The proposal: fuse hooks + a positive terminal idle-check

Build a dedicated **FinishDetector** whose only job is to emit exactly one
`TurnFinished(sessionId, turnId)` event per real turn, and never mid-turn. It fuses two
deterministic sources that cover each other's blind spots:

- **Hooks give the events.** `UserPromptSubmit` opens a turn; `Stop` proposes it closed;
  `PermissionRequest`/`Notification` mark a wait; `SubagentStop` is explicitly NOT a turn
  end. Hooks know about sub-agents and tools that terminal-scraping cannot see.
- **The terminal confirms the proposal, positively.** On `Stop`, confirm the screen
  actually shows the **idle input prompt** - the specific Claude Code "ready for input"
  UI (empty input box, "? for shortcuts", cursor), with NO spinner and NO working footer
  anywhere - and that it stays stable for a short debounce. This is *positive evidence of
  finished*, not inferred silence. The terminal catches the cases the hook misses (cancel,
  `/clear`: the prompt returns to idle even when `Stop` is flaky), and the hook catches
  the cases the terminal misreads (footer-less spinner: no `Stop` yet, so not finished).

Neither alone is trusted; the agreement of the two is. No LLM. The LLM
(`ClassifyTerminalStateViaSessionAsync`) becomes at most a rare tie-breaker, not the
per-turn confirmer it is today.

### 2.4 We prove it with captures, not by hoping

Finish detection is too important to "looks right." Build a **capture harness**: record
real Claude Code sessions (hook event stream + terminal byte stream together) for the
hard cases and replay them through the FinishDetector as regression fixtures:

- plain turn end; turn ending on a question; turn ending on a permission prompt
- long quiet tool (footer up) and footer-less spinner (the current false-finish)
- sub-agent running and finishing (`SubagentStop` must NOT fire turn-end)
- cancel (Esc) mid-turn; `/clear`; rapid re-submit before the prior turn settled
- compaction

Assert: the detector fires once, at the right instant, for each. This fixture set is the
definition of "we are good at finish detection now." (We already have a fixtures pattern
under `docs/features/terminal-state-detector/` to extend.)

---

## 3. The Wingman itself

Generalize the one good shape we already proved this session - `AnswerViaSessionAsync`: a
read-only full-power session (Read/Grep/Glob, no writes, no PTY access) on the strong
model, handed the session and told its task. That becomes *the* Wingman, used for both
invocation types.

- **Context agency.** It is given the session's full transcript as a readable snapshot
  plus the repo as its working directory. It reads as far back as it needs. No pre-chosen
  tail, no pre-built digest that has already thrown away the detail.
- **Verbosity by judgment.** The prompt frames the task and the bar; the Wingman decides.
  Status-glance for "what's happening"; verbatim and complete for "read me the X". The
  one hard rule: never summarize content the user asked to receive in full.
- **One output, many facets.** A finished-turn invocation can yield: a spoken line, a
  screen headline, whether the user is needed and the verbatim question, an optional
  recap, and any rule/goal observation - but as facets of a single judgment, produced
  only when relevant, not as six mandatory separate calls.
- **Charter-bound.** Strong model only, read-only, stateless, fail-closed - already
  written in `docs/wingman/CHARTER.md` and enforced by the audit gate.

---

## 4. Scorched earth: keep / rework / delete

Honest inventory of every current Wingman-related piece.

| Piece | Verdict | Why |
| --- | --- | --- |
| Deterministic activity/quiet gate (byte + screen inspection) | **Rework** | The idea (a free gate decides *when*) is right; the implementation (silence-inference) is the bug. Rebuild as the positive-idle FinishDetector fused with hooks. |
| `"esc to interrupt"` footer / working-footer screen check | **Keep** | A genuinely reliable deterministic working signal. Reuse as one input to the FinishDetector. |
| Claude Code hooks (`Stop`, `UserPromptSubmit`, ...) already installed | **Keep + promote** | The authoritative turn boundary we were ignoring. Becomes the FinishDetector's primary event source. |
| `AnswerViaSessionAsync` (read-only full-power session, agency, verbatim) | **Keep + generalize** | This is the correct shape for the *entire* Wingman, not just the ask. Becomes the core. |
| `RunWingmanSessionAsync` mechanism + read-only invariant | **Keep** | Proven; charter-aligned. |
| `ClaudeCodeScreenReference` (how a CC screen looks) | **Keep** | Useful to both the detector and the Wingman. |
| Charter + audit gate + single strong `Model` | **Keep** | Just built; this is the governance the redesign needs. |
| Fixture / live-test harness pattern | **Keep + extend** | Becomes the finish-detection capture harness (section 2.4). |
| `WingmanLlmThrottle` (5s floor) | **Keep as a safety net** | Belt-and-suspenders against any accidental loop, but the FinishDetector should make it almost never trigger. |
| `SummarizeTurnAsync` (capped 280-char spoken_text, fixed JSON) | **Delete / absorb** | The capped lossy summary is the original complaint. Its job becomes a facet of the one Wingman, with verbosity by judgment. |
| `ClaudeSummarizer` (voice spoken summary, Haiku) | **Delete / absorb** | Not a separate component; the Wingman produces the spoken line. |
| `RecapGenerator` (recap, Haiku) | **Delete / absorb** | The recap is a facet of the Wingman's output, not its own model call. |
| `CheckRulesAsync`, `AssessGoalAsync` as independent per-turn calls | **Absorb** | Fold into the single finished-turn judgment so they are not extra calls. |
| `ClassifyTerminalStateAsync` (tail-paste one-shot LLM) | **Delete** | The per-turn LLM confirmer we are trying to remove; replaced by the deterministic FinishDetector. |
| `ClassifyTerminalStateViaSessionAsync` (full-session LLM judge) | **Demote** | At most a rare tie-breaker when hooks+terminal disagree, not a per-turn call. |
| `AskAboutSessionAsync` explain-vs-ask split + 4000-char `WingmanContextBuilder` tail | **Rework** | One Wingman with context agency replaces the terse-vs-faithful fork and the pre-truncated context. |
| `TurnSummaryCache` fan-out on turn-end | **Rework** | Becomes a thin "on TurnFinished, invoke the Wingman once, cache the one result" - not an orchestrator of several calls. |

Net: the system goes from "a timer-ish gate + an LLM confirmer + 5 independent per-turn
prompt functions + 2 voice summarizers" to "**a deterministic FinishDetector + one
Wingman call.**"

---

## 5. Sequencing (de-risk the keystone first)

1. **Finish detection, proven.** Build the capture harness and the FinishDetector
   (hooks + positive idle-check). Get it green on every fixture in 2.4 and validate on
   Soren's live sessions. **Nothing else proceeds until finish detection is trustworthy** -
   it is the make-or-break, exactly as Soren said.
2. **One Wingman call on TurnFinished.** Wire the generalized read-only Wingman to the new
   event for the proactive briefing; produce the one structured verdict.
3. **Migrate consumers** (badge, Agent View, voice spoken output, recap, goals, rules)
   onto that one verdict; delete the absorbed functions.
4. **On-demand questions** route to the same brain (the Ask-the-Wingman channel already
   built becomes a first-class mode of the unified Wingman, not a separate path).

---

## 6. Decisions (locked 2026-05-25)

1. **Finish signal: FUSE.** The `Stop` hook is the primary turn-end event; a positive
   terminal idle-prompt check confirms it (and catches the cancel/`/clear` cases where the
   hook goes stale). Not terminal-only.
2. **Proactivity: speak only when needed.** On a finished turn the Wingman speaks/alerts
   only when the user is actually needed - a question, an error, a decision. Otherwise the
   badge updates silently and nothing is spoken.
3. **Cadence: start one-per-turn.** One Wingman call per finished turn to begin with. Add a
   cheap deterministic "nothing interesting" pre-filter later only if the cadence proves
   too costly/slow in practice.
