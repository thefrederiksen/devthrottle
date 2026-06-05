# HostedAgent - a brain that hosts its own claude.exe

Successor to the AgentBrain REST client (docs/plans/agent-brain.md). The REST design
requires a running Director - an external process dependency the consumers must not
have. HostedAgent owns its claude.exe directly: the DLL spawns the process, types into
its own pseudoconsole, and reads answers from the transcript. Many HostedAgents can
live in one host process (one claude.exe child each), exactly like the Director runs
many sessions today.

## Decisions

- **Name:** `CcDirector.HostedAgent`, class `HostedAgent`. AgentBrain (REST) stays in
  the tree untouched - same verbs, different transport; callers choose.
- **Shared contract:** a new `IAgentBrain` interface (Ask/Clear/Restart/Kill/Health +
  SessionId) lives in CcDirector.AgentBrain next to the result models, implemented by
  BOTH AgentBrainClient and HostedAgent. The panel codes against the interface.
- **Dependency choice:** HostedAgent references CcDirector.Core (option 1 from the
  discussion). The primary consumers (Gateway, Director) already reference Core, so
  the weight costs nothing today. Extracting a lean hosting package is a later
  refactor if an external consumer ever needs it.
- **Engine reuse, no duplication:** ConPtyBackend (spawn + typing semantics incl. the
  large-input temp-file trick and explicit CR), ClaudeSessionReader (JSONL paths +
  ListTranscripts), StreamMessageParser/WidgetBuilder (reply extraction),
  SessionTokenUsage (token accounting). NOT SessionManager/Session - the brain does
  not need the activity-state machine, wingman, or hooks; its quiet clock is
  `Buffer.LastWriteAtUtc`, the same primitive source.

## How each verb maps (vs the REST version)

| Verb | REST AgentBrainClient | HostedAgent |
|---|---|---|
| Start | POST /sessions (Director spawns) | `new ConPtyBackend().Start(claude.exe, "--dangerously-skip-permissions --session-id <guid>", repo, cols, rows)`; the pre-assigned guid means the JSONL path is known from birth - no discovery |
| Quiet gate | GET /sessions/{sid} idleSeconds | `Buffer.LastWriteAtUtc` directly |
| Ask | POST /prompt; poll /turns | `backend.SendTextAsync(prompt)`; poll the JSONL file (parse -> widgets -> new Text widget + stability) |
| Usage | GET /usage | `SessionTokenUsage.ComputeFromFile(jsonlPath)` |
| Clear | type /clear; discover via /claude-transcripts; POST /relink | type /clear; `ClaudeSessionReader.ListTranscripts(repo)` locally; update own current id - NO relink, we are the owner |
| Restart | DELETE + POST /sessions | dispose backend (graceful shutdown) + fresh Start |
| Kill | DELETE /sessions/{sid} | `GracefulShutdownAsync()` + dispose |
| Health | GET /sessions + /usage | `HasExited` / `IsRunning` + idle clock + usage |

## Project layout

- `src/CcDirector.HostedAgent/`
  - `HostedAgent.cs` - the class; ctor takes `HostedAgentOptions` (+ internal seams
    `Func<ISessionBackend>` backend factory and `ITranscriptReader` for tests)
  - `HostedAgentOptions.cs` - ClaudeExecutablePath (explicit or resolved from PATH -
    fail loud if missing), WorkingDirectory, ClaudeArgs (default
    `--dangerously-skip-permissions`), Cols/Rows (120x40), Quiet/Ask/Clear/Start
    timeouts, PollInterval, ReplyStableSeconds, Log sink
  - `TranscriptReader.cs` - thin wrapper over ClaudeSessionReader +
    StreamMessageParser + WidgetBuilder + SessionTokenUsage (the `ITranscriptReader`
    seam; real impl reads disk, tests use an in-memory fake)
- `src/CcDirector.AgentBrain/IAgentBrain.cs` - the shared interface (additive)
- `src/CcDirector.HostedAgent.Tests/` - hermetic unit tests with FakeBackend +
  FakeTranscriptReader: start readiness, ask happy path + stability + timeout, clear
  id-switch + stale rejection, restart identity, crash surfaced via HasExited,
  multiple instances in one process
- Panel: `CcDirector.AgentBrain.Panel` gains a mode picker - "Hosted" (claude path +
  working dir, START HOST button) vs "Director (REST)" (existing URL + CONNECT).
  Everything below the top bar codes against IAgentBrain and stays identical.

## Known traps carried in

1. **Nested ConPty:** a HostedAgent whose host process lives inside a Claude Code
   ConPty produces grandchild claudes that exit in ~3s (the --print stdin error). For
   QA the panel/smoke host MUST be launched via Task Scheduler, same as
   cc-director-launch. Document on the class.
2. **Hooks:** Director-spawned claudes get CC_DIRECTOR_API/CC_SESSION_ID env vars for
   the state-detector hooks. HostedAgent sets NEITHER - hooks that check those vars
   no-op. Verify live that no hook noise leaks into the terminal.
3. **Auth:** claude.exe reads Max credentials from the hosting user's profile. A
   service host must run as the user. (Unchanged from issue #172.)

## QA (rerun of the AgentBrain QA, hosted mode)

Panel launched via a scheduled task (`agent-brain-panel-launch`), driven with
ui-drive.ps1/qa-step.ps1, screenshots embedded in
docs/features/hosted-agent/QA_REPORT.html:

| # | Case |
|---|---|
| HQ-1 | HostedAgent unit tests green |
| HQ-2 | Panel hosted mode: START HOST spawns claude.exe as the PANEL's child (proof: process tree) |
| HQ-3 | Ask returns full reply + latency + tokens |
| HQ-4 | Long answer (>2000 chars) intact |
| HQ-5 | Clear: codeword -> /clear -> CONTEXT-EMPTY; transcript id switched; process never restarted |
| HQ-6 | Auto-clear mode isolation |
| HQ-7 | Restart: new claude.exe pid + answers |
| HQ-8 | Crash recovery: claude.exe killed externally -> health DEAD -> restart heals |
| HQ-9 | Kill: claude.exe child gone |
| HQ-10 | Two HostedAgents in ONE process answer independently (console smoke via scheduled task, output to file) |

## Out of scope

- Deleting the REST AgentBrainClient (kept; Director-attached scenarios still want it)
- The Gateway brief agent built on HostedAgent (next consumer)
- macOS/UnixPty hosting (ConPty first; the backend seam keeps the door open)
