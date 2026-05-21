# Gateway / Director Architecture (CURRENT)

**Status:** CURRENT
**Date:** 2026-05-18
**Verified against:** working tree at 2026-05-18 (branch `main`, uncommitted Recap + rename changes included). Re-verify by spot-checking the cited symbols.
**Audience:** Anyone touching `cc-director`, the Gateway, or any cross-machine "talk to all my agents" tooling.

## Related documents

- `gateway-director-overview.d2` / `gateway-director-overview.png` - one-glance topology (read first)
- `gateway-director-detail.d2` / `gateway-director-detail.png` - same topology with each component's feature list spelled out
- `../../CC_Gateway_Design.md` - original design / intent doc (predates Recap + rename)
- `../../Gateway_Dashboard.md` - operator-facing notes about the dashboard
- `../../CcDirector.Engine-Design.md` - the in-process Engine (scheduler + dispatcher) co-hosted in each Director
- `../../HowTerminalsWork.md` - ConPty / terminal internals that sit underneath every Session

---

## 1. The two processes

`cc-director` ships as two .NET executables that talk to each other over plain HTTP:

| Process | Where it runs | What it owns | Source |
|---|---|---|---|
| **cc-director.exe** (Director) | One or many per machine. Each instance has its own GUID, its own port in `7879..7898`, and its own Avalonia desktop window. | The sessions. Spawns and supervises ConPty processes wrapping `claude.exe` / `pi` / `codex` / `gemini`. Owns the live `Session` objects, the Claude hook plumbing, and the session persistence files. | `src/CcDirector.Avalonia/` (desktop UI) + `src/CcDirector.ControlApi/` (HTTP API) + `src/CcDirector.Core/` (everything stateful) |
| **cc-director-gateway.exe** (Gateway) | At most one per machine, fixed port `7878`, binds `0.0.0.0`. | A fan-out / fan-in REST layer plus the Manager Web UI. Aggregates `/directors` and `/sessions` across every Director it can discover, and proxies session-specific calls to the owning Director. **Owns no session state of its own.** | `src/CcDirector.Gateway/` |

A typical machine therefore looks like: one Gateway on `:7878`, plus 1..N Directors on `:7879..:7898`. The Avalonia desktop UI ships inside the Director - there is no separate UI executable.

---

## 2. Discovery is a directory of JSON files

The Gateway never gets told "here is a Director." It watches a directory:

```
%LOCALAPPDATA%\cc-director\config\director\instances\{directorId}.json
```

When a Director starts (`ControlApiHost.StartAsync()` -> `new InstanceRegistration(...).Register()`), it writes its own `{guid}.json` into that directory containing `directorId`, `pid`, `controlEndpoint` (e.g. `http://127.0.0.1:7881`), `machineName`, `user`, `version`. It then **re-writes the file if it goes missing every 15 s** (see `InstanceRegistration.HeartbeatInterval`).

The Gateway side:
- `DirectorRegistry.Start()` opens a `FileSystemWatcher` on that directory and loads any files already there.
- A 30 s sweeper double-checks: if the file is gone OR the registered PID is no longer alive, the entry is dropped and `OnDirectorRemoved` fires.

A second adjacent directory pins the port:
```
%LOCALAPPDATA%\cc-director\config\director\ports\{directorId}.port
```
`PortAllocator` reads this on startup so the same Director GUID keeps the same port across restarts (only allocates a fresh one if its previous port is now busy or out of range).

**Consequences:**
- No network discovery, no service registry, no broadcast. Discovery is filesystem-local-per-machine.
- The Gateway can only see Directors on **its own machine**. A "Tailscale-wide" view doesn't exist yet - the Gateway aggregates within the machine and exposes that aggregate over Tailscale to your browser.
- "Restart and the dashboard rebuilds itself" is real: just delete `instances/{guid}.json` and the Director will heartbeat it back within 15 s.

---

## 3. Division of labor today

### 3.1 What lives on the Director

These are owned exclusively by the Director - the Gateway has no copy and no cached view:

| Concern | Symbol |
|---|---|
| The map of live sessions | `SessionManager` (`_sessions` ConcurrentDictionary), `Map<ClaudeSessionId, Guid> _claudeSessionMap` |
| Session lifecycle | `SessionManager.CreateSession`, `KillSessionAsync`, `RenameSession` |
| The backing PTY / process | `ISessionBackend` (ConPty / UnixPty / Embedded / Pipe / Studio) attached to each `Session` |
| Claude Code hook integration | `HookInstaller`, `HookRelayScript`, `DirectorFileEventWatcher` |
| Activity-state derivation from hooks | `Session.HandlePipeEvent(PipeMessage)` |
| Persistence between Director restarts | `SessionStateStore`, `SessionHistoryStore`, `RepositoryRegistry` |
| The JSONL transcript reader | `ClaudeSessionReader`, `StreamMessageParser`, `WidgetBuilder`, `SummaryBuilder` |
| Recap generation + cache | `RecapGenerator` (spawns `claude --print --bare --model haiku`), `RecapCache` (in-process map keyed by session GUID) |
| The desktop Avalonia UI | `MainWindow`, `SessionViewModel`, `TerminalControl`, the source-control / workspaces / dialogs |

### 3.2 What lives on the Gateway

| Concern | Symbol |
|---|---|
| Director discovery | `DirectorRegistry` (the `FileSystemWatcher` + sweeper) |
| Per-Director HTTP client | `DirectorEndpointClient` (2 s default timeout, 3 min on `POST /recap`) |
| HTTP aggregator | `GatewayEndpoints.Map()` |
| Manager Web UI | `Web/manager.html` (single-file, embedded via `EmbeddedResources`) |
| Optional cookie/Bearer auth | `AuthMiddleware`, `GatewayAuth` |
| Cross-Director handover orchestration | `POST /handover` in `GatewayEndpoints.cs` |
| Director spawning | `POST /directors` (`Process.Start cc-director.exe --skip-workspace-picker`) |

The Gateway is **stateless across requests other than the in-memory `DirectorRegistry`**. There is no DB and no cache of session data; every `/sessions` call fans out to every Director live in the registry and aggregates the responses.

### 3.3 Where the same endpoint exists on both

This is a real source of confusion and is worth calling out. Several routes exist on both the Director's Control API and the Gateway's REST aggregator:

| Route | On Director | On Gateway | Difference |
|---|---|---|---|
| `GET /sessions` | Yes, this Director's sessions only | Yes, aggregates across all Directors | Gateway iterates the registry |
| `GET /sessions/{sid}` | Yes | Yes (`LocateSessionAsync` finds the owning Director) | Gateway is a thin proxy |
| `GET /sessions/{sid}/buffer` | Yes | Yes (proxy) | Same |
| `POST /sessions/{sid}/prompt` | Yes (always returns immediately) | Yes (proxy; if `waitForIdle=true`, the **Gateway** does the polling) | The "wait for idle" feature is Gateway-side because the Director's job is to return fast |
| `POST /sessions/{sid}/interrupt` | Yes | Yes (proxy) | Same |
| `PATCH /sessions/{sid}` | Yes (renames `Session.CustomName`, fires `OnSessionRenamed`) | Yes (proxy) | Same |
| `GET/POST /sessions/{sid}/recap` | Yes (cache + side-claude spawn) | Yes (proxy) | The actual recap work happens on the Director |
| `POST /handover` | Yes (Director-local source AND target) | Yes (handles cross-Director by reading context from source then spawning target) | **Gateway has logic the Director doesn't**: cross-Director handover |
| `POST /fanout` | `/fanout-local` (within this Director's sessions only) | `/fanout` (across all Directors' sessions) | Different routes; different scope |
| `GET /` | HTML manager.html scoped to this Director | HTML manager.html scoped to all Directors | Same file, different aggregator behind it |

The rule of thumb: **the Director knows about its own sessions; the Gateway knows about all Directors.** Whenever something needs to cross a Director boundary - cross-Director handover, "all sessions in one list," fan-out across Directors - the Gateway handles it.

---

## 4. End-to-end flows

The diagram lives in `gateway-director-current.png`. Below are the canonical flows the diagram is trying to tell.

### 4.1 You open the dashboard

```
Browser GET http://<host>:7878/
  -> Gateway serves Web/manager.html
  -> manager.html JS calls GET /directors (aggregate)
  -> manager.html JS calls GET /sessions   (aggregate)
  -> setInterval refresh every 1.5 s
```

The Gateway's `/directors` returns `DirectorRegistry.ListDirectors()` directly. `/sessions` iterates the registry and, for each `DirectorDto`, calls `client.ListSessionsAsync(d.ControlEndpoint)` over HTTP. Slow or down Directors fail fast (2 s timeout) without stalling the whole response.

### 4.2 You click "+ New Session"

```
Browser POST /directors/{did}/sessions { repoPath, agent }
  -> Gateway proxies to Director's POST /sessions
  -> Director's SessionManager.CreateSession(...) spawns ConPty + claude.exe
  -> RaiseSessionCreated(session) fires
     -> MainWindow.OnExternalSessionCreated wraps it in a SessionViewModel  (desktop UI updates)
  -> Returns the new SessionDto with status=201
  -> Browser refresh() picks it up on the next tick
```

This is why the New Session modal asks "On which Director?" first: the request has to be addressed to a specific Director.

### 4.3 You select a session and send a prompt

```
Browser click card  ->  enterDetailMode(sid, did)  (client-side only, no HTTP)
Browser POST /sessions/{sid}/prompt  { text, appendEnter: true, waitForIdle: false }
  -> Gateway locates owning Director, proxies POST /sessions/{sid}/prompt
  -> Director.ControlEndpoints writes bytes (with Enter) to session.Backend
  -> Session.SetActivityState(Working)  fires OnActivityStateChanged
  -> Hooks pipe events back (UserPromptSubmit -> Working, Stop -> WaitingForInput, etc.)
  -> Next /sessions poll picks up the new activityState
```

### 4.4 You rename a session

```
Browser PATCH /sessions/{sid}  { name: "..." }
  -> Gateway.PatchSessionAsync -> Director PATCH /sessions/{sid}
  -> SessionManager.RenameSession(sid, name)
     -> session.CustomName = name
     -> OnSessionRenamed fires
        -> MainWindow.OnExternalSessionRenamed
           -> vm.Rename(name)
           -> PersistSessionState()  (debounced 500 ms save)
  -> Response is the updated SessionDto (Name now populated)
```

### 4.5 You click "Refresh recap"

```
Browser POST /sessions/{sid}/recap
  -> Gateway proxies (3 min timeout)
  -> Director reads the session's .jsonl, builds a digest via SummaryBuilder.FormatAsHandoverPrompt
  -> RecapGenerator spawns:  claude --print --bare --model haiku --tools "" <digest>
  -> Side claude returns markdown on stdout
  -> RecapCache.Set(sid, entry)
  -> Returns RecapResponse  { recap, generatedAt, atTurnCount, currentTurnCount, isStale, ... }
```

Important properties:
- The live session is never poked. The recap is built from the on-disk JSONL.
- The side claude has `--bare` (no hooks, no plugin sync, no auto-memory) and `--tools ""` (no tools) so it cannot drift.
- `RecapCache` is in-process and not persisted - a Director restart wipes recaps. They are cheap to regenerate.
- `GET /sessions/{sid}/recap` never triggers generation; it only returns whatever is cached (or `status: not_cached`).

### 4.6 You hand off to another session (same Director)

```
POST /handover  { fromSessionId, toRepoPath, toAgent, archiveToVault }
  -> Gateway resolves owning Director, proxies to that Director's POST /handover
  -> Director reads source JSONL, builds the handover prompt text
  -> Creates the new Session, fires OnSessionCreated
  -> Background task waits for new Session to reach Idle, then SendTextAsync(handoverText)
  -> Returns HandoverResponse  { targetSession, contextSent, archivedAt }
```

### 4.7 You hand off across Directors (cross-machine - within the same machine today)

```
POST /handover  { fromSessionId, toDirectorId, toRepoPath, toAgent }
  -> Gateway sees fromSession lives on Director A and toDirectorId is Director B
  -> Gateway GETs /sessions/{from}/handover-context from Director A (plain-text context)
  -> Gateway POSTs /sessions to Director B with the context as PrePrompt
  -> Director B spawns the new session and dispatches PrePrompt once it reaches Idle
  -> Returns HandoverResponse  { targetSession on Director B, contextSent }
```

This is the **only** flow today where the Gateway does logic beyond proxying. Everything else is "find the owning Director and forward."

---

## 5. State ownership summary

| What | Where | Survives Director restart? | Survives Gateway restart? |
|---|---|---|---|
| Live `Session` objects (ConPty + buffer) | Director, in-memory | No, but the ConPty was a process - it dies with the Director | N/A (Gateway has no copy) |
| `CustomName`, `CustomColor`, repo, queued prompts | Director, persisted to `SessionStateStore` JSON | Yes (restored into Embedded backend on next startup) | N/A |
| Activity state derived from hooks | Director, in-memory | No (rederived from current hook stream after restart) | N/A |
| JSONL transcript | Filesystem (`~/.claude/projects/...`) | Yes - written by `claude.exe` itself | Yes |
| Recap text | Director, `RecapCache` in-process | No, regenerated on demand | N/A |
| Director registration files | `%LOCALAPPDATA%\cc-director\config\director\instances\` | Yes (the Director rewrites on startup; also heartbeats) | Yes - Gateway reloads on its own startup |
| Port assignment | `%LOCALAPPDATA%\cc-director\config\director\ports\` | Yes (`PortAllocator` re-reuses if free) | N/A |
| Gateway in-memory `DirectorRegistry` | Gateway, in-memory | N/A | No - rebuilt from disk on startup |

The Gateway truly is stateless. Everything important lives on the Director or on the filesystem.

---

## 6. Known limitations and rough edges (current shape)

These are facts about today, not opinions about tomorrow. They are the things that would change if you wanted the Gateway to become a real cross-machine control plane.

1. **Gateway sees only its own machine.** `DirectorRegistry` watches a local filesystem path. Directors on a second machine on the same Tailnet are invisible. There is no cross-machine federation today.
2. **No persistent identity for sessions across Director restarts.** A killed-and-respawned Director assigns new session GUIDs. Session "continuity" is via Claude's own resume mechanism, not via the Director.
3. **Recap is per-Director, not per-Gateway.** Two Gateways on two machines pointed at the same Director cannot share a recap cache; each would have to ask the Director to regenerate. (Not a practical issue today because Gateway-per-machine.)
4. **Auth is optional and uniform.** Both processes share a `gateway-token.txt` model. There is no per-user identity in the Gateway today - any client that reaches `:7878` with the right cookie/token has full control.
5. **The Manager Web UI on the Director and the Manager Web UI on the Gateway are very similar but not identical.** Both load `manager.html`, but one is scoped to a single Director (the embedded copy in `src/CcDirector.ControlApi/Web/manager.html`) and the other is the aggregator (`src/CcDirector.Gateway/Web/manager.html`). They drift - changes need to be applied carefully.
6. **Fan-out is best-effort, not transactional.** `POST /fanout` writes to every selected session in parallel; partial failures return per-session statuses but there is no rollback.
7. **Handover archive is source-side only on cross-Director handovers.** The cross-Director path in `Gateway.POST /handover` skips writing to the vault archive (`HandoverArchive.Write`); only same-Director handovers archive.
8. **No event push to clients beyond `/events`.** `/events` on the Gateway only emits `director.added` / `director.removed` over SSE. Session activity changes are polled (`setInterval(refresh, 1500)`).
9. **`POST /directors` only spawns on the local machine.** The Gateway's "+ New Director" path calls `Process.Start`; it cannot spawn a Director on a remote host.
10. **Director and Gateway version skew is not negotiated.** Both stamp a `Version` field on `DirectorDto` / `HealthDto`; nothing today checks compatibility before proxying. A breaking change to the `SessionDto` shape would silently misbehave.

---

## 7. The diagrams

Two PNGs accompany this doc. Read them in this order:

### Overview (`gateway-director-overview.png`)

Six big boxes, one paragraph each: **Clients -> Gateway -> Discovery surface -> Director -> Sessions -> Side processes.** Use this when you want to remember the shape of the system in 30 seconds without rereading any source.

### Detail (`gateway-director-detail.png`)

Same six rows, but every component inside the Gateway and Director is broken out as its own box with a bullet-list of features and endpoints. Use this when you need to know exactly which symbol owns a behavior, or to find which route handler does what.

### Reading both diagrams

- **Vertical layout, top to bottom.** Clients on top, Side processes on the bottom.
- **Edge colors track ownership:** blue = Gateway path, green = Director path, yellow = shared filesystem, purple = Session / Agent, red = side-claude.
- **Solid edges are the request/response path; dashed edges are side-effects or background reads** (persistence, transcript writes, port pinning).
- Boxes inside each row are siblings owned by the same process. They are not separate processes.

### Re-rendering

```powershell
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway-director-overview.d2 gateway-director-overview.png
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk gateway-director-detail.d2   gateway-director-detail.png
```

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-05-18 | claude (cc-director assistant) | Initial CURRENT-state authoring. Captures the Gateway+Director split as of the unmerged Recap + rename branch. |
