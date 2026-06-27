# CC Director Gateway & Manager - Implementation Plan

**Status:** Draft for review
**Date:** 2026-05-16
**Author:** Architecture planning session
**Related:** [CC_Gateway_Design.md](../CC_Gateway_Design.md), [PRD-RemoteControl.md](../PRD-RemoteControl.md), [Gateway_Dashboard.md](../Gateway_Dashboard.md)

---

## 1. Executive Summary

This plan introduces two new layers to CC Director:

1. **CC Director Gateway** - a per-machine system-tray process that exposes one local REST API for ALL running Director instances. Teams/Slack/Discord bots, scripts, and external tools talk to a single, stable endpoint instead of trying to find which Director window owns which session.
2. **Manager View** - a new view inside each Director window that gives a real-time overview of every session running on the machine (including sessions owned by other Director instances), can fan a single prompt out to many sessions in parallel, and consolidates their replies. Drop-in to any session's terminal at any time.

The two are independent but designed to compose. The Gateway is the only piece that listens on the network; the Manager is the only piece that thinks about multi-session orchestration. Sessions themselves are unchanged - all real intelligence stays in the Claude/Pi/Codex/Gemini processes they wrap.

**Request flow** (matches the PRD's `Teams -> Gateway -> Manager -> Session`):

- External callers (Teams bot, scripts) **always** talk to the Gateway. They never talk to a Director directly.
- For a single-session command, the Gateway proxies straight to the owning Director's control endpoint, then to the `Session`. The "Manager" hop is conceptual here - the Gateway IS the manager-of-routing.
- For a fan-out command, the Gateway performs the parallel dispatch itself (dumb fan-out). Consolidation is the only piece that lives in the in-Director Manager view, because consolidation needs a model call.
- The Manager view inside Director is itself a client of the Gateway and uses the same REST API. Nothing in the architecture is private to the desktop UI.

```
+-----------------+    +------------------+    +----------------------+
|  Teams / Slack  |--->| CC Director      |--->|  CC Director (#1)    |
|  Discord bot    |    | Gateway          |    |  + Manager View      |
|  scripts, curl  |    | (tray, REST API) |    |    + Session(s)      |
+-----------------+    +------------------+    +----------------------+
                              |                +----------------------+
                              +--------------->|  CC Director (#2)    |
                                               |  + Manager View      |
                                               |    + Session(s)      |
                                               +----------------------+
```

---

## 2. Existing State (verified in code)

| Fact | File / Location |
|---|---|
| Multiple Director instances can run simultaneously (single-instance mutex was removed since v1.0.0). | No `Mutex` in `src/CcDirector.Avalonia/*.cs` |
| Hook events from Claude already broadcast to ALL Directors via a shared file-event directory. | `src/CcDirector.Core/Pipes/DirectorFileEventWatcher.cs:13`, event dir `%LOCALAPPDATA%\cc-director\config\director\events` |
| `SessionManager` lives inside the Avalonia process and owns all sessions for THAT director. | `src/CcDirector.Avalonia/App.axaml.cs:21` |
| Each session has a thread-safe terminal `CircularTerminalBuffer` exposing `DumpAll()` and streaming `GetWrittenSince()`. | `src/CcDirector.Core/Memory/CircularTerminalBuffer.cs` |
| `Session.SendTextAsync(text)` is the supported way to push a prompt + Enter into a session. | `src/CcDirector.Core/Sessions/Session.cs:261` |
| `Session.ActivityState` (Idle / Working / WaitingForUser etc.) is already driven by hook events. | `src/CcDirector.Core/Sessions/Session.cs:79` |
| Director has NO HTTP/Kestrel/WebSocket today. | No `WebApplication`, `Kestrel`, `HttpListener`, `WebSocketServer` in `src/` |
| The Python scheduler (`scheduler/cc_director/gateway/`) DOES expose FastAPI on port 6060, but its scope is JOBS / CRON, not sessions. | `scheduler/cc_director/gateway/app.py` |
| Existing prior design at `docs/CC_Gateway_Design.md` predates the new PRD: it assumed one Director + named pipes + Discord-only. This plan replaces it. | - |

Implication: We do not need to invent multi-Director coordination. The file-event directory pattern is the proven shared rendezvous and we extend it for discovery.

---

## 3. Goals for Success

### 3.1 Gateway success criteria

A reasonable observer must be able to say "the Gateway works" if all of these are true:

- G1. Single REST endpoint at `http://127.0.0.1:7878/` answers `GET /healthz` within 1s of tray start.
- G2. `GET /directors` lists every Director currently running on the machine within 2 s of that Director appearing, and removes it within 10 s of it exiting (clean exit) or 30 s (crash).
- G3. `GET /sessions` aggregates sessions from all live Directors and returns within 500 ms for up to 100 sessions across 5 Directors.
- G4. `POST /sessions/{sid}/prompt {"text":"..."}` is delivered to the owning Director's `Session.SendTextAsync` and observable (a new turn appears) within 1 s on an idle session.
- G5. Gateway survives a crash of any Director (the remaining Directors are still reachable, the crashed one disappears from `/directors` within 30 s).
- G6. Binds only to loopback (`127.0.0.1`). Refuses non-loopback connections. Optional bearer-token check for every write endpoint.
- G7. Runs as a Windows system-tray app (Mac menu-bar / Login Item is a Phase 4 deliverable, not required for v1).
- G8. Zero behavior change for users who never start the Gateway. Director runs normally if Gateway is not running.

### 3.2 Manager success criteria

- M1. New "Manager" entry in the sidebar / hamburger menu opens an in-window dashboard view.
- M2. The dashboard lists every session on the machine (own + other Directors), grouped by Director, with: agent kind, repo path, `ActivityState`, last activity timestamp, last user prompt, last assistant turn header.
- M3. Updates are real time (<= 1 s after a hook fires).
- M4. "Fan-out" panel: pick N sessions, type one prompt, send. Each selected session receives the same prompt via `SendTextAsync`. Per-row send status (queued / sent / failed).
- M5. "Consolidate" panel: after a fan-out, the Manager waits until every selected session returns to `Idle` (or timeout), captures each session's last assistant turn, and runs a small summarizer prompt (configurable agent / model) to produce a single combined answer. The user sees both the per-session raw answers and the consolidated summary.
- M6. Double-click any session row to jump into that session's terminal tab (own Director only) or open a read-only buffer panel (cross-Director, via Gateway).
- M7. Manager view is feature-gated by a setting (`manager.enabled`, default true). Disabling it removes the entry and the polling cost.
- M8. Manager UI is non-blocking: opening it, refreshing it, and waiting for fan-out responses never freezes the existing terminal tabs (must obey `CLAUDE.md` rule #1: responsive UI).

### 3.3 What success is NOT

- Not a cloud relay (that's the separate Remote Control PRD).
- Not a replacement for the Python scheduler gateway on port 6060.
- Not a Teams/Slack bot itself - those are external clients of the Gateway.
- Not a rewrite of the Director UI. Manager is a new view alongside existing tabs.

---

## 4. Component 1 - CC Director Gateway

### 4.1 Deployment shape

- New .NET project: `src/CcDirector.Gateway` (console host with `Microsoft.Extensions.Hosting` and Kestrel).
- Tray UI: same project, uses `Avalonia.Tray` (already a dependency for Director).
- Single executable: `cc-director-gateway.exe`. Built into `%LOCALAPPDATA%\cc-director\bin\` by `scripts\build-tools.bat`.
- Launched manually OR auto-started by the first Director that opens (configurable). On exit it leaves no zombie state.
- Hard-coded port `7878` (TCP). If port busy, fail to start with a clear error - no fallback (per project rule "No Fallback Programming").

### 4.2 Director discovery

We do NOT use process enumeration. We extend the existing config tree:

```
%LOCALAPPDATA%\cc-director\config\director\instances\
    {director-guid}.json
```

Each Director writes its own `{guid}.json` on startup:

```json
{
  "directorId": "8d2b...-...-...",
  "pid": 12345,
  "startedAt": "2026-05-16T10:36:00Z",
  "controlEndpoint": "http://127.0.0.1:55321",
  "machineName": "MACHINE-A",
  "user": "soren",
  "schemaVersion": 1
}
```

- `controlEndpoint` is a small **internal** HTTP server (Kestrel) the Director hosts on an ephemeral port. The Gateway is the ONLY caller. CORS not needed; bind to `127.0.0.1`.
- Director writes the file in `App.OnFrameworkInitializationCompleted` AFTER `SessionManager` is ready.
- Director removes the file in `OnExit` (best effort). Gateway treats files older than 30 s with an unreachable endpoint as stale and ignores them, and also runs a stale-file sweeper every 30 s.
- The Gateway uses a `FileSystemWatcher` on this directory for instant discovery, same pattern as `DirectorFileEventWatcher`.

### 4.3 Director internal control endpoint

Each Director hosts a tiny HTTP API for the Gateway to call. Same data contracts as the Gateway's public API but scoped to that Director's own sessions only.

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/sessions` | List all live `Session` objects belonging to this Director. |
| `GET` | `/sessions/{sid}` | Get one session's metadata. |
| `GET` | `/sessions/{sid}/buffer?since=<bytes>` | Stream from `CircularTerminalBuffer.GetWrittenSince`. |
| `POST` | `/sessions/{sid}/prompt` | Body `{"text":"...","appendEnter":true}` -> `Session.SendTextAsync`. |
| `POST` | `/sessions/{sid}/interrupt` | Send Ctrl+C via backend. |
| `POST` | `/shutdown` | Request graceful shutdown (same path as user-initiated close). |
| `GET` | `/healthz` | Returns Director version + uptime. |

Implementation lives in a new namespace `CcDirector.Core.ControlApi` (server-side host class + endpoint mappers) so that the Director project owns its own contract. Kestrel is added as a transitive `WebApplication.CreateBuilder` in `App.OnFrameworkInitializationCompleted`. All work is awaited on the UI thread only when crossing into `Dispatcher.UIThread.InvokeAsync` for `SendTextAsync`.

### 4.4 Gateway public REST API

Base: `http://127.0.0.1:7878/`

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/healthz` | `{ "status":"ok", "directors": N, "sessions": M }` |
| `GET` | `/directors` | List of all known Directors, each with its `directorId`, `pid`, `controlEndpoint`, `lastSeen`. |
| `POST` | `/directors` | Body `{"workspace":"default"}` (optional). Launches `cc-director.exe`. Returns `{"directorId":...,"pid":...}` once the new Director registers (timeout 30s). |
| `DELETE` | `/directors/{id}` | Body `{"force":false}`. Sends a graceful-shutdown request to the Director's control API; falls back to `Process.Kill` only if `force=true`. |
| `GET` | `/sessions` | Aggregated list across Directors. Query params: `?director=<id>`, `?agent=claude\|pi\|codex\|gemini`, `?state=idle\|working\|...`. |
| `GET` | `/sessions/{sid}` | One session (Gateway looks up the Director and proxies). |
| `GET` | `/sessions/{sid}/buffer?lines=N` | Last N text lines (cleaned of ANSI, see 4.6) from the session's circular buffer. `?raw=true` returns raw bytes. |
| `POST` | `/sessions/{sid}/prompt` | Body `{"text":"...","appendEnter":true,"waitForIdle":true,"timeoutMs":120000}`. If `waitForIdle` true, Gateway holds the response open until `ActivityState` returns to Idle or timeout. |
| `POST` | `/sessions/{sid}/interrupt` | Send Ctrl+C. |
| `POST` | `/fanout` | Body `{"sessionIds":["...","..."],"text":"...","waitForIdle":true,"timeoutMs":300000}`. Gateway sends in parallel, returns one consolidated response. See 4.5. |
| `GET` | `/events` (SSE) | Server-Sent Events stream of `session.activity`, `director.added`, `director.removed`. For dashboards. |

`POST /directors` shells out to `cc-director.exe` with `--gateway-launched` (so the Director knows to register a hint back to the caller for instant correlation). The Director itself does the `instances/{guid}.json` write; the Gateway just polls the registry for the new entry. Process is detached - the Director outlives the Gateway.

`DELETE /directors/{id}` calls the target Director's `POST /shutdown` control endpoint (new in Phase 1). Director performs its normal graceful close including session detach-on-close behavior.

All bodies and responses are JSON. ETag / If-None-Match optional. No auth required for `/healthz` and `/directors` GET. All write endpoints (`POST`, `DELETE`) and `/sessions/*/buffer` REQUIRE `Authorization: Bearer <token>` when a token is configured. Token lives in `config/director/gateway-token.txt`, generated on first launch.

### 4.5 Fan-out semantics (Gateway-side)

`POST /fanout` is the Gateway's only piece of orchestration logic. It is intentionally dumb:

1. For each `sessionId`, look up the Director and POST to that Director's `/sessions/{sid}/prompt` in parallel.
2. If `waitForIdle=true`, poll each Director's `/sessions/{sid}` every 750 ms until `ActivityState == Idle` (or `Failed`) or per-session timeout.
3. Once all are settled, for each session call `/sessions/{sid}/buffer?lines=200&since=<sequence at send-time>` to get just the new output.
4. Return `{ "results": [ { "sessionId": ..., "status": "idle"|"timeout"|"failed", "output": "..." } ] }`.

Consolidation (running a summarizer) lives in the **Manager view**, not the Gateway. The Gateway returns the raw N answers; the Manager (or any external caller) decides what to do with them. This keeps the gateway dumb per the PRD's "Core Philosophy."

### 4.6 Output cleaning

Gateway includes a small ANSI/control sequence stripper (same patterns as the legacy `CC_Gateway_Design.md` section "Output Cleaning"). Implementation in `CcDirector.Gateway.Util.AnsiCleaner`. Unit-tested with golden inputs from real Claude sessions.

### 4.7 Tray UI

Single-icon tray:

- Left-click: shows a popover with counts (`N Directors, M sessions, K idle, W working`) and a "Open dashboard" link (opens `http://127.0.0.1:7878/` in browser - swagger UI for v1).
- Right-click: menu - Pause routing / Reload directors / Open logs / Quit.
- Tooltip: `CC Director Gateway - N directors, M sessions`.

### 4.8 Logging

Per `CLAUDE.md` rules, every public method logs entry, exit, errors. Logs go to `%LOCALAPPDATA%\cc-director\logs\gateway\gateway-{date}.log`. Standard format:

```
[GatewayHost] HandlePrompt: sid=..., bytes=42 OK in 137ms
[GatewayHost] HandlePrompt FAILED: sid=..., error=...
```

### 4.9 Failure mode

- Director's control endpoint unreachable -> Gateway returns `503 Director offline` for that session, NOT a fallback.
- No matching session -> `404`.
- Auth missing/wrong -> `401`.
- Body invalid -> `400`.

No silent retries; failures surface to the caller.

---

## 5. Component 2 - Manager View (inside Director)

### 5.1 UI placement

- Add "Manager" entry to the hamburger menu and a new sidebar button under "+ New Session".
- New control: `src/CcDirector.Avalonia/Controls/ManagerView/ManagerView.axaml`.
- Same dock pattern as `CommManagerView` - it's a content view, not a window.

### 5.2 Data source

Manager view consumes the **local Gateway** at `http://127.0.0.1:7878`. This is deliberate:

- Single code path for "all sessions on this machine."
- If Gateway is not running, Manager shows a banner: "Gateway not running. Start it to see other Directors." and falls back to own-Director-only mode (via direct `SessionManager` calls). This is the ONE allowed fallback because the alternative is a degraded but locally-correct view; we surface the missing gateway clearly to the user.
- Real-time updates come from the Gateway's `GET /events` (SSE).

### 5.3 Layout (v1, single-column)

```
+--------------------------------------------------------------+
| Manager                                          [refresh]   |
+--------------------------------------------------------------+
| Filter: [agent v] [state v] [repo v]   Selected: 3 of 12     |
+--------------------------------------------------------------+
| [x] Director #1 (8d2b...)                                    |
|   [x] claude  cc-director       idle    "fixing tests" 2m    |
|   [ ] pi      vault             idle    "..."          1m    |
| [x] Director #2 (a17c...)                                    |
|   [x] claude  marketing-funnels working  "...working..."     |
|   [ ] codex   experiments       idle                          |
+--------------------------------------------------------------+
| Fan-out prompt:                                              |
| +----------------------------------------------------------+ |
| | Type prompt to send to selected sessions                 | |
| +----------------------------------------------------------+ |
| [ ] Wait for all idle  [ ] Consolidate with: [claude-haiku v]|
|                                  [Send to 3 sessions]        |
+--------------------------------------------------------------+
| Results (after fan-out):                                     |
|  > session A: "..." (1.2s)                                   |
|  > session B: "..." (4.5s)                                   |
|  > session C: timeout after 120s                             |
|  -- Consolidated answer (claude-haiku):                      |
|  "All three reported X. B caught an extra edge case Y..."   |
+--------------------------------------------------------------+
```

### 5.4 Fan-out + consolidation flow

1. User selects N sessions in the list, types a prompt, hits Send.
2. Manager calls `POST /fanout` on the local Gateway with selected IDs.
3. Gateway returns when all are settled (or timeout).
4. If "Consolidate" is checked, Manager runs a SHORT consolidation prompt against the configured lightweight agent (default: `claude-haiku-4-5-20251001`). Implementation reuses the existing `ClaudeAgent` / API path the Director already uses for summaries (TBD: confirm which path - see Open Questions).
5. Render: each session's raw answer collapsed; consolidated summary expanded at top.
6. Persist the fan-out as a JSON record under `%LOCALAPPDATA%\cc-director\config\director\manager\fanouts\{utc}.json` for replay / debugging.

### 5.5 "Drop into a session" behavior

- Own-Director session: switch to that session's existing terminal tab.
- Other-Director session: open a read-only buffer pane (live tail from Gateway, no input). v2 can add full takeover via the Gateway.

### 5.6 Performance & threading

- Polling interval for list refresh: 1 s when window visible, paused when not visible.
- All Gateway I/O off the UI thread (`Task.Run` or `HttpClient` async).
- Updates marshaled via `Dispatcher.UIThread.InvokeAsync`.
- Per the responsiveness rule, the Manager view appears immediately with "Loading..." placeholders; the first data fill is async.

### 5.7 Settings

New section in settings dialog:

| Key | Default | Notes |
|---|---|---|
| `manager.enabled` | `true` | Hides the Manager UI entirely when false. |
| `manager.consolidator.agent` | `claude-haiku` | Used for consolidation. |
| `manager.fanout.defaultTimeoutMs` | `120000` | |
| `gateway.autostart` | `true` | Director launches Gateway if not running. |
| `gateway.port` | `7878` | Read-only in v1. |
| `gateway.token` | `<generated>` | Stored in `gateway-token.txt`. |

---

## 6. Project / file layout (proposed)

```
src/
  CcDirector.Gateway/                       # NEW project (Kestrel host + tray)
    Program.cs
    GatewayHost.cs
    Discovery/
      DirectorRegistry.cs                   # watches instances/*.json
      DirectorEndpointClient.cs             # HttpClient wrapper
    Api/
      Endpoints.cs                          # MapGroup() for all routes
      Models/
        SessionDto.cs
        DirectorDto.cs
        FanoutRequest.cs / FanoutResponse.cs
    Util/
      AnsiCleaner.cs
      AuthMiddleware.cs
    Tray/
      TrayHost.cs                           # Avalonia.Tray integration
    appsettings.json
    CcDirector.Gateway.csproj

  CcDirector.Gateway.Tests/                 # NEW xUnit project

  CcDirector.Core/
    ControlApi/                             # NEW namespace (the Director's own HTTP)
      ControlApiHost.cs
      ControlEndpoints.cs
      InstanceRegistration.cs               # writes/removes instances/{guid}.json

  CcDirector.Avalonia/
    Controls/
      ManagerView/                          # NEW view
        ManagerView.axaml
        ManagerView.axaml.cs
        ManagerViewModel.cs
        FanoutPanel.axaml(.cs)
        SessionRow.axaml(.cs)
    App.axaml.cs                            # start ControlApiHost + maybe Gateway

docs/
  plans/
    Gateway-Manager-Implementation.md       # this file
  Gateway-API.md                            # public REST reference (Phase 1 deliverable)
```

---

## 7. Implementation Phases

Each phase is independently shippable and testable.

### Phase 0 - Spec & contracts (1 short PR)

- Land this plan.
- Add `docs/Gateway-API.md` with the full REST spec (the table in 4.4 expanded with examples).
- Add `SessionDto`, `DirectorDto`, `FanoutRequest/Response` POCOs in `CcDirector.Gateway` so both server and tests share types. No behavior yet.

**Exit criteria:** Plan reviewed; DTOs compile; doc landed.

### Phase 1 - Director Control API (no Gateway yet)

- Create `CcDirector.Core.ControlApi`. Host Kestrel on ephemeral loopback port from inside Avalonia `App.OnFrameworkInitializationCompleted`.
- Write `instances/{guid}.json` on startup; delete on `OnExit`.
- Implement endpoints: `/healthz`, `/sessions`, `/sessions/{sid}`, `/sessions/{sid}/buffer`, `/sessions/{sid}/prompt`, `/sessions/{sid}/interrupt`.
- Log every endpoint per CodingStyle rules.
- Unit tests for endpoint mappers (mock `SessionManager`).
- Manual smoke test: start Director, curl `http://127.0.0.1:<port>/sessions`.

**Exit criteria:** With one Director running, you can `curl -X POST http://127.0.0.1:<port>/sessions/<sid>/prompt -d '{"text":"hi"}'` and see "hi" delivered into the session.

### Phase 2 - Gateway core (proxy only, no fan-out)

- Create `CcDirector.Gateway` project. Kestrel on `127.0.0.1:7878`.
- `DirectorRegistry` watches `instances/*.json`, maintains live map.
- Proxy endpoints: `/healthz`, `/directors`, `/sessions`, `/sessions/{sid}`, `/sessions/{sid}/buffer`, `/sessions/{sid}/prompt`, `/sessions/{sid}/interrupt`.
- Bearer-token middleware (token from `gateway-token.txt`, auto-generated).
- Tray icon with basic popover.
- Logs to `logs/gateway/`.

**Exit criteria:** Start 2 Directors -> `/directors` shows 2 -> `/sessions` aggregates -> prompt-by-id works to either.

### Phase 3 - Fan-out + SSE

- `POST /fanout` (parallel send + idle-wait + buffer diff).
- `GET /events` SSE stream emitting `director.*` and `session.*` change events.
- Gateway emits events when (a) `DirectorRegistry` changes and (b) it polls Director `/sessions` and notices a state diff. (For v1 we poll; v2 can push from the Director directly.)

**Exit criteria:** Curl-based fan-out test (see 9.2) returns the consolidated raw answers within 5 s for 3 idle sessions echoing "ping".

### Phase 4 - Manager View

- `ManagerView` Avalonia control. Calls local Gateway.
- Session list with selection, filter, real-time refresh.
- Fan-out panel + consolidation invocation.
- "Drop into session" navigation.
- Settings dialog wiring.

**Exit criteria:** A user can select 3 sessions across 2 Directors, send a prompt, see all three answers + consolidated summary inside the Manager view. No UI freeze at any point.

### Phase 5 - Hardening / Mac menu-bar

- Cross-platform tray (`Avalonia.Tray` already cross-platform; Login Item on macOS via plist).
- Crash / orphan handling: Gateway treats unreachable Director endpoint > 30 s as gone.
- Optional: `cc-director-gateway --service` Windows Service mode.
- Token rotation CLI.

**Exit criteria:** Restart any Director; Gateway notices within 5 s. Kill a Director with Task Manager; Gateway evicts within 30 s. Test app reachable from Teams bot.

---

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Per-Director Kestrel adds startup time | Lazy-start: only bring up the control API after `SessionManager.ScanForOrphans()`. Measure: target +<200ms cold start. |
| `SendTextAsync` while session is in the middle of streaming output may corrupt the prompt | The Director side checks `ActivityState != Working` and returns `409` if busy. Manager queues or warns user. |
| Two Gateways accidentally started | Port 7878 bind fails fast on the second one. The tray installer instructs to close the first. |
| File-watcher misses on slow disk | We already do periodic full sweeps in `DirectorFileEventWatcher`; same pattern in `DirectorRegistry`. |
| Token leakage | Token file is `0600` on Unix and ACL'd to current user on Windows. Token never logged. |
| Consolidator agent costs money on every fan-out | Default `claude-haiku`; disabled by default. User opts in per fan-out via the checkbox. |
| Cross-Director "drop into session" is read-only in v1 | Documented limitation; full takeover deferred to v2. |

---

## 9. Test Plan (with concrete REST commands)

The user specifically asked for "a specific way to test the new manager skill using the REST API on cc-director." This section is the canonical test for **Phase 3** (Gateway with fan-out). It exercises every Manager feature end-to-end via curl, so the manager UI is provably backed by a stable contract.

### 9.1 Pre-conditions

1. Build everything: `scripts\build-tools.bat` (produces `cc-director.exe` and `cc-director-gateway.exe` in `%LOCALAPPDATA%\cc-director\bin`).
2. Set env: `set CC_GATEWAY_TOKEN=$(type %LOCALAPPDATA%\cc-director\config\director\gateway-token.txt)`.
3. Open three Directors:
   - `cc-director.exe` (Director #1)
   - `cc-director.exe` (Director #2)
   - `cc-director.exe` (Director #3)
4. In each, create one Claude session on a small repo (e.g., `D:\ReposFred\devthrottle`). Wait for each to reach `Idle`.
5. Start gateway: `cc-director-gateway.exe` (tray icon appears).

### 9.2 Test cases

All commands use Windows curl with `%CC_GATEWAY_TOKEN%`.

**T1 - Health**
```
curl http://127.0.0.1:7878/healthz
```
Expect: `{"status":"ok","directors":3,"sessions":3}`.
Pass if: HTTP 200, directors == 3, sessions == 3.

**T2 - Director discovery**
```
curl http://127.0.0.1:7878/directors
```
Pass if: array length 3, each entry has `directorId`, `pid`, `controlEndpoint`, and `lastSeen` within last 5 s.

**T3 - Session aggregation**
```
curl http://127.0.0.1:7878/sessions
```
Pass if: array length 3, fields `sessionId`, `directorId`, `agent`, `repoPath`, `activityState=="Idle"` for all.

**T4 - Single prompt round-trip**
```
set SID=<sessionId from T3>
curl -H "Authorization: Bearer %CC_GATEWAY_TOKEN%" ^
     -H "Content-Type: application/json" ^
     -d "{\"text\":\"What is 2+2? Reply with just the number.\",\"waitForIdle\":true,\"timeoutMs\":60000}" ^
     http://127.0.0.1:7878/sessions/%SID%/prompt
```
Pass if: HTTP 200 within 60 s. Response body contains `"output"` with `"4"` and the source session's `ActivityState` returns to `Idle` in the next `/sessions/<sid>` call.

**T5 - Buffer streaming**
```
curl -H "Authorization: Bearer %CC_GATEWAY_TOKEN%" ^
     "http://127.0.0.1:7878/sessions/%SID%/buffer?lines=20"
```
Pass if: returns the last 20 lines of cleaned terminal output, no ANSI escape characters present.

**T6 - Fan-out**
```
curl -H "Authorization: Bearer %CC_GATEWAY_TOKEN%" ^
     -H "Content-Type: application/json" ^
     -d "{\"sessionIds\":[\"<sid1>\",\"<sid2>\",\"<sid3>\"],\"text\":\"In one short sentence, what is this repo for?\",\"waitForIdle\":true,\"timeoutMs\":180000}" ^
     http://127.0.0.1:7878/fanout
```
Pass if: HTTP 200 within 180 s, `results` array length 3, each result has `status: "idle"` and a non-empty `output`.

**T7 - Cross-Director isolation**
- Close Director #2. Wait 30 s.
- Repeat T2 and T3.
Pass if: directors == 2, sessions == 2, and Director #2's session is gone (not just marked offline).

**T8 - Interrupt**
- Send a long task to a session via T4 with `waitForIdle:false`.
- Immediately: `curl -X POST -H "Authorization: Bearer %CC_GATEWAY_TOKEN%" http://127.0.0.1:7878/sessions/%SID%/interrupt`
Pass if: session's `ActivityState` returns to `Idle` within 5 s.

**T9 - Auth**
- Repeat T4 without the `Authorization` header.
Pass if: HTTP 401.

**T10 - Loopback only**
- From another machine on the LAN: `curl http://<your-ip>:7878/healthz`
Pass if: connection refused / timed out. From `127.0.0.1`: 200.

**T11 - SSE event stream**
```
curl -N -H "Authorization: Bearer %CC_GATEWAY_TOKEN%" http://127.0.0.1:7878/events
```
- Leave running. From another shell, send T4.
Pass if: terminal prints at least one `event: session.activity` line within 2 s of the prompt being sent.

**T12 - Manager UI smoke (Phase 4 only)**
- Open Manager view in any Director.
- Confirm list shows 3 sessions grouped by Director.
- Select all 3, type "Say hi", check Consolidate, Send.
- Pass if: per-session answers appear, consolidated summary appears, and the UI never freezes (verify by clicking another tab during fan-out).

### 9.3 Automated suite

A new test project `tests/Gateway.E2E.Tests/` runs T1-T11 in CI against a headless Director (`--gateway-test` flag boots Director with one synthetic session that just echoes prompts). Phase 3 ships with this suite green.

---

## 10. Open Questions

1. **Consolidator implementation.** Does the Director already have an in-process Claude API client we can reuse for the small summarizer prompt, or do we need to add one? (Quick code search showed `Claude/ClaudeAccountStore.cs` and `Claude/ClaudeUsageService.cs` exist, but no obvious "send-prompt-and-get-response" path. Needs a short spike before Phase 4.)
2. **Mac launch agent.** Out of scope for v1 but we should land a plist template in Phase 5 so users don't need to write it by hand.
3. **Naming.** "Manager" is generic - is there a better name? Suggestions: "Bridge", "Conductor", "Foreman". I'd lean "Conductor" but "Manager" matches the PRD.
4. **Cross-Director takeover.** Should the Manager be allowed to send Ctrl+C / prompts to a session it doesn't own? In v1 we say yes (the Gateway already supports it). User confirmation required?
5. **Persistence of fan-out history.** v1 dumps JSON to disk. Should it also show in the Manager view as a "History" tab? (Easy add in Phase 4.)
6. **Gateway as Windows Service vs tray.** Tray is the primary form factor per the PRD; Service is a nice-to-have for headless boxes. Keep both options open.

---

## 11. Out of Scope

- The cloud-based **Remote Control** PRD (separate work, separate doc).
- The Python scheduler **Gateway_Dashboard** on port 6060 - different concern, stays as-is.
- Slack / Teams / Discord bot implementations themselves - they are clients of this Gateway.
- Multi-machine federation (one Gateway talking to Directors on other PCs). Plausible in v2; for v1 the Gateway is strictly per-machine.

---

## 12. Definition of Done (whole feature)

- Plan reviewed and merged (Phase 0).
- All five phases shipped behind a `gateway.enabled` setting (default off until end of Phase 4).
- T1-T12 all passing.
- `docs/Gateway-API.md` is the source of truth for external integrators (Teams/Slack/Discord teams).
- A 5-minute screencast in `docs/public/` showing: start Gateway -> open 2 Directors -> hit Manager view -> fan-out a prompt -> see consolidated answer.
- Release notes entry for the next version bump.
