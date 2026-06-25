# Agent Session History Capture: Findings and Plan

Status: research complete, implementation not started.
Date measured: 2026-06-25.
Machine where measured: SORENLAPTOP.
Branch: feat/agent-history-capture.

This document records what we learned while investigating why full screen agents
break session history and Wingman, proves where each supported agent stores its
own history, and lays out the test harness we must build before implementing
anything.

---

## 1. The problem

When an agent command line tool switches the terminal into full screen mode (the
alternate screen buffer, the byte sequence ESC [ ? 1049 h), Cc Director can no
longer save that session's history, and the Wingman briefing goes blind. The
content shown inside the full screen application is never written to the local
scrollback, so the saved transcript has a hole for the entire run and Wingman only
ever sees the single live frame.

This used to be a non issue because the assumption was that the agents did not use
the alternate screen. That assumption is now false (see section 3), which is why
this became urgent.

---

## 2. Root cause in the terminal engine

Files: src/CcDirector.Terminal.Core/AnsiParser.cs, plus the snapshot and history
paths in src/CcDirector.Terminal/TerminalControl.cs,
src/CcDirector.Terminal.Avalonia/TerminalControl.cs,
src/CcDirector.Terminal.Core/Rendering/AnsiToHtmlConverter.cs, and
src/CcDirector.Core/Sessions/Session.cs.

- The parser detects the alternate screen via the private mode codes 47, 1047, and
  1049, tracked by the _altCells field and exposed as the IsAlternateScreen
  property.
- On entering the alternate screen the primary buffer is preserved by a pointer
  swap; a fresh blank grid is installed. The primary buffer is not corrupted.
- While the alternate screen is active, ScrollUp() and CommitRepaintFrame() both
  bail out ("never capture alt-screen frames"), so nothing that happens inside the
  full screen application is ever appended to the scrollback list.
- On exit the alternate grid is dropped (the primary grid pointer is restored and
  the alternate grid is released).
- The persisted history paths (GetAllTerminalText and the HTML snapshot) read the
  scrollback list plus the live grid. Because the scrollback never grew during the
  full screen run, the saved transcript has a hole for the whole session.
- Wingman reads the raw byte tail (CircularTerminalBuffer.DumpAll cleaned of escape
  sequences) plus the current visible grid (SnapshotScreenRowsWithCursor). During a
  full screen run the cleaned byte tail is mostly cursor positioning noise, and
  after the run the live grid no longer holds the conversation, so the briefing has
  no usable transcript.

Important: the raw bytes are never lost. CircularTerminalBuffer and the
SessionLogWriter raw byte log capture every byte, including the alternate screen
paints. What is lost is the clean linear scrollback structure. So recovering history
is a reconstruction problem, not a data loss problem.

---

## 3. Which agents use full screen mode

Detection method: launch the agent, capture the raw pseudo terminal byte stream, and
search for ESC [ ? 1049 h (enter alternate screen).

Measured directly this session:

| Agent | Full screen? | How measured |
| --- | --- | --- |
| Claude Code | YES | Real Director session, raw buffer emitted ESC [ ? 1049 h plus mouse and bracketed paste modes |
| Grok | YES | Standalone pseudo terminal probe, ESC [ ? 1049 h at startup |
| OpenCode | YES | Standalone pseudo terminal probe, ESC [ ? 1049 h plus full mouse and synchronized rendering |

Not yet measured (could not be launched interactively out of process, and the npm
shim launch bug in section 7 blocked launching them through the Director):

| Agent | Static claim elsewhere | Action |
| --- | --- | --- |
| Codex | Full screen by default (alternate_screen auto) | Confirm empirically in the harness |
| Copilot | Full screen (alternate screen constants in the package) | Confirm empirically |
| Pi | Normal terminal buffer | Confirm empirically |
| Gemini | Normal terminal buffer (no alternate screen switch in source) | Confirm empirically. This is the linchpin (see section 6) |

Conflict to resolve: the in tree document docs/SupportedAgentsTerminalModes.md lists
OpenCode as a normal terminal buffer application based on a static search of the
bundled command. Our empirical capture proves OpenCode does enter the alternate
screen. Empirical capture wins; the static matrix must be corrected.

The old assumption in AnsiParser ("Claude Code and most CLIs do not use the
alt-screen in practice") is wrong as of these measurements.

---

## 4. Where each agent stores its own history

Forensic survey of real on disk data on SORENLAPTOP. Verdicts: FULL means user
prompts, assistant responses, and tool calls can all be reconstructed accurately.

| Agent | Store (Windows) | Format and fidelity | Verdict |
| --- | --- | --- | --- |
| Claude Code | ~/.claude/projects/<encoded-cwd>/<session-id>.jsonl | Per line user and assistant messages (thinking, tool_use, tool_result), parentUuid tree, sessionId, cwd, gitBranch. Plus SessionStart, Stop, and SessionEnd hooks that fire in the interactive terminal and push session_id and transcript_path on standard input (sources startup, resume, clear, compact). | FULL |
| Codex | ~/.codex/sessions/.../rollout-*.jsonl | response_item entries (user and assistant messages, function_call and function_call_output), event_msg entries (clean user_message and agent_message text), session_meta, turn_context | FULL |
| Pi | ~/.pi/<timestamp>_<session-id>.jsonl | message entries with role user, assistant, or toolResult; id and parentId tree; cwd. This is the event capture mechanism. | FULL |
| Copilot | ~/.copilot/session-store.db (SQLite) | turns table with user_message and assistant_response per turn; forge_trajectory_events for tool calls; sessions table with cwd and updated_at | FULL |
| Grok | ~/.grok/sessions/<encoded-cwd>/<session-id>/chat_history.jsonl (plus events.jsonl, summary.json) | system, user, reasoning, assistant (with tool_calls and model_id), tool_result; also a search index database holding full content | FULL |
| OpenCode | ~/.local/share/opencode/opencode.db (SQLite: message, part, session) | Validated by running one real session: message row per turn (role plus model and token and cost metadata); part rows are ordered content (text, reasoning, step-start, step-finish, tool). Also opencode run --format json streams the same events, and there is a server and event bus. | FULL |
| Gemini | ~/.gemini/tmp/<cwd-hash>/logs.json (prompts) plus telemetry | logs.json holds user prompts only. Telemetry exports over OTLP gRPC; there is a log prompts option but no log responses option, and the response text is never persisted or exported. | GAP. The assistant response text is unrecoverable from Gemini's own outputs. |

Summary: six of seven agents give full, accurate, structured history from their own
stores. Gemini is the lone exception.

---

## 5. Experiments run to close the last two gaps

Experiment A, OpenCode: ran one real non interactive opencode run. The previously
empty opencode.db filled in. Format locked: the message table holds one row per
turn (user and assistant with model, token, and cost metadata); the part table holds
the ordered content parts (text, reasoning, step-start, step-finish, tool). Fully
reconstructable. Result: OpenCode confirmed FULL.

Experiment B, Gemini telemetry: ran Gemini with telemetry enabled, pointed at a
local collector, with a prompt whose answer was deliberately not present in the
prompt. Findings: telemetry only exports over OTLP gRPC to port 4317 (the file and
HTTP settings were ignored in the installed version); there is a log prompts option
but no log responses option anywhere in the command help. Combined with logs.json
holding prompts only, this confirms Gemini never persists or exports the assistant
response text. Result: Gemini gap confirmed; it must rely on the terminal path.

---

## 6. Architecture decision

One canonical history model, fed by a capability ladder, with a universal terminal
floor.

- Canonical history schema inside Cc Director: normalized turns (user message,
  assistant message, tool call, tool result) plus session lifecycle events (session
  identifier changed, cleared, compacted). Wingman and session save consume only
  this schema, never an agent specific format.
- Capability ladder per agent:
  - Tier one, transcript or event provider: read the agent's own store (proven for
    six of seven agents) and map it into the canonical schema. Prefer driver pushed
    events where available (Claude hooks) over passive file reading.
  - Tier zero, universal terminal screen reconstruction: the mandatory backstop for
    any agent without a usable provider, and for any future agent. Built from
    capturing alternate screen scroll off lines and periodic frame snapshots.
- Terminal fallback principle (the key simplification): classify each agent's
  terminal mode first. A normal terminal buffer agent does not break terminal
  capture, so the driver simply uses the existing terminal capture and needs no
  provider. A full screen agent breaks terminal capture and therefore needs a
  provider, or the screen reconstruction floor.
- All or nothing: the feature is only trustworthy if every agent is covered by some
  path. The linchpin is Gemini, the one agent with no usable transcript. If the
  harness confirms Gemini is a normal terminal buffer application, the terminal path
  covers it and the gap closes. If Gemini is actually full screen, it is the single
  hard problem and only the screen reconstruction floor can serve it.
- Session pointer must be event driven where possible. For Claude the SessionStart
  hook pushes the current session identifier and transcript path on every clear and
  compaction, which solves the long standing problem of tracking the right session
  after a clear. For the others, "newest session for this working directory since
  launch time" identifies the active session (Copilot and OpenCode have working
  directory and updated time columns; Codex, Pi, and Grok name files or directories
  by working directory and session identifier).

---

## 7. Side findings (each worth a tracked work item)

1. npm shim agent launch bug. RESOLVED 2026-06-25. The original observation (the
   live Director failed to launch the npm command shims) turned out to be only
   partly a code bug. An end to end launch test proved the committed launcher form
   (cmd.exe /s /c ""<path>"") correctly launches a .cmd shim for both space free and
   spaced paths, so Gemini, Copilot, Pi, and OpenCode launch fine from the committed
   code. The live failures were a stale local build that predated the launcher fix
   (commit 99cdf4a). The one genuine bug in the committed code was the Codex default
   executable path: it hard coded the npm codex.cmd, which does not exist for
   standalone installer users, so Codex sessions failed with "not recognized". Fixed
   on this branch by defaulting CodexPath to the bare name "codex" so PATH resolution
   finds the standalone exe or the npm shim, matching opencode, grok, and cursor. A
   reliable end to end launch regression test was added (it captures through a pipe
   rather than a pseudo console so it is not affected by the nested terminal capture
   problem).
2. No alternate screen state in the Control API. RESOLVED 2026-06-25. The parser
   tracked IsAlternateScreen internally but no Control API response exposed it.
   Session now exposes a live IsAlternateScreen property (read from the server-side
   parser under its lock), and SessionDto carries an IsAlternateScreen boolean
   populated by the Director's session mapping, so GET /sessions and GET
   /sessions/{id} report whether each session is currently in full screen mode. A
   caller can classify the terminal mode directly instead of scanning the raw buffer.
3. Concurrent uncommitted work in the main tree. The main working tree currently
   holds uncommitted changes to AnsiParser.cs and a new
   docs/SupportedAgentsTerminalModes.md that overlap this effort and partly conflict
   with it (the OpenCode classification). This branch was created from the last
   committed state and deliberately ignores that work; the conflict must be
   reconciled against the empirical evidence here.

---

## 8. Test harness plan

We must build an empirical, repeatable harness that proves accurate history capture
for every agent before implementing the feature. It also becomes a regression guard
when agents update.

Per agent, in a disposable real Director session, the harness will:

1. Classify the terminal mode by scanning the raw pseudo terminal stream for ESC [ ?
   1049 h (alternate screen) versus the normal buffer. This settles every static
   guess, including OpenCode and Gemini.
2. Inject a deterministic scripted conversation: turn one produces a unique marker;
   turn two forces a tool call (read a known file); then a clear of the session;
   then turn three produces a second marker.
3. Run the chosen history path (the transcript provider for full screen agents, or
   the terminal capture for normal buffer agents) and reconstruct the canonical
   turns.
4. Assert that both markers are present in the right order with the right roles, the
   tool call was captured, and the post clear turn was captured (proving the session
   pointer survives a clear).
5. Also score the terminal screen reconstruction on the same session, so we know how
   strong the universal floor is per agent.
6. Emit a matrix: agent by (mode, provider pass, terminal pass, screen
   reconstruction quality).

Prerequisites the harness depends on:

- The npm shim launch bug (item 7.1) is resolved; the Codex default path is fixed
  and a launch regression test guards the .cmd launch form. Once a fresh Director is
  built from this branch, all four npm based agents launch.
- Expose IsAlternateScreen through the Control API for mode classification (item
  7.2): done. SessionDto.IsAlternateScreen now reports the live full screen state.
- A reliable input injection and turn completion wait, and a clear trigger, through
  the Control API.

Per agent short plan:

| Agent | Mode (our stance) | Path to prove in the harness |
| --- | --- | --- |
| Claude | Full screen (measured) | JSONL transcript plus SessionStart and Stop hooks for the live pointer; verify across a clear |
| Grok | Full screen (measured) | chat_history.jsonl per session; verify |
| OpenCode | Full screen (measured; the in tree doc disputes this) | opencode.db message and part, or run --format json; verify and resolve the conflict |
| Codex | Full screen (claimed) | rollout jsonl; confirm mode, then verify |
| Copilot | Full screen (claimed) | session-store.db turns; confirm mode, then verify |
| Pi | Normal buffer (claimed) | If normal, terminal capture is enough; JSONL provider as a bonus; confirm mode |
| Gemini | Normal buffer (claimed); linchpin | If normal, terminal capture covers it and the gap closes; if full screen, screen floor only; must measure |
| Cursor | Not installed | Defer |
| RawCli | Unknown by nature | Terminal capture or screen floor |
| Universal screen reconstruction | The mandatory backstop | Validate quality as the floor |

---

## 9. Open decisions and review items

1. Reconcile the OpenCode classification against the empirical evidence here
   (OpenCode does enter the alternate screen).
2. Measure the modes of Gemini, Pi, Codex, and Copilot empirically. Gemini is the
   decisive one.
3. npm shim launch bug: resolved (Codex default path fixed; launch regression test
   added). Rebuild the Director from this branch to pick it up.
4. Expose IsAlternateScreen through the Control API: done (SessionDto.IsAlternateScreen).
5. Confirm the recommended scope: build transcript providers only for full screen
   agents, and use the existing terminal capture for normal buffer agents (the
   terminal fallback principle).

---

## 10. References (project memory)

- claude-code-now-uses-alt-screen
- agent-history-store-map
- npm-shim-agent-launch-bug
