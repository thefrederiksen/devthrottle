# Supervisor Ask + Persistent Session Log (Phase 5)

**Status:** PLANNED
**Date:** 2026-05-21
**Predecessor:** [SUPERVISOR_AS_BRAIN_PLAN.md](SUPERVISOR_AS_BRAIN_PLAN.md) (Phase 4 shipped)

---

## Goal

1. **Ask the supervisor questions about a session, from a dedicated desktop tab.** Each ask is one fresh, stateless Haiku call with the session's recent state piped in as context. No conversation memory inside Claude.
2. **Persist every session's full history to disk as JSONL** — the raw terminal stream and the simplified "agent view" turns. Survives Director restart. Becomes the supervisor's long-term memory and an audit trail for any future tooling.

---

## Principles (locked)

- **Supervisor has no memory.** Each ask is a fresh `claude --print --model haiku --tools "" --no-session-persistence`. Context comes in via the prompt, not via session resume. This matches what `SupervisorService.RunSideClaudeAsync` already does for turn summaries / voice cleanup / rule checks — we just add an "ask" mode.
- **The supervisor reads what THIS session has done.** Recent supervisor events, recent turn summaries, a tail of terminal buffer, basic session metadata. It does not browse other sessions or invent.
- **Persistence is append-only JSONL.** One line per record, atomic writes, never rewrite history. Disk layout under `%LOCALAPPDATA%\cc-director\session-logs\<sid>\`.
- **Logging never blocks the hot path.** Buffer-write and turn-completed hooks queue work to a background writer; if the disk hiccups, the session continues normally.

---

## ASCII layout (recap)

```
+--------------------------------------------------------------------------+
|  Terminal | Source Control | Agent | Supervisor *                        |
+--------------------------------------------------------------------------+
|  cc-director - voice       (*) green - idle, ready for next task         |
|  since 14:50  ::  last turn: "answered why-is-the-sun-yellow"            |
+--------------------------------------------------------------------------+
|  Q  what did the agent just do?                                          |
|  >  Agent answered why is the sun yellow. 3 turns ago it edited          |
|     Session.cs and ran dotnet test. No pending question.                 |
|                                                                          |
|  Q  why is this session green?                                           |
|  >  Supervisor wrote green at 14:50:32, reason "idle, ready for next     |
|     task". Previous color was blue (Working) for the answering turn.     |
+--------------------------------------------------------------------------+
|  [ Ask the supervisor about this session... ]               [Ask]        |
+--------------------------------------------------------------------------+
|  CONTEXT THE SUPERVISOR SEES                                 [+] expand  |
|  - last 50 supervisor decisions                                          |
|  - last 5 turn summaries                                                 |
|  - last 4 KB of terminal buffer                                          |
|  - session metadata + git dirty bit                                      |
+--------------------------------------------------------------------------+
```

---

## Persistence layout

```
%LOCALAPPDATA%\cc-director\session-logs\<sid>\
├── meta.json              { sessionId, repoPath, agent, createdAt, schema:1 }
├── raw.jsonl              one {ts, len, b64} record per buffer-write chunk
├── turns.jsonl            one TurnSummary object per completed turn
├── agent-view.jsonl       one widget record per agent-view item (Text, Thinking, Bash, ...)
└── supervisor-events.jsonl one SupervisorEvent per color change
```

All four are append-only, line-delimited JSON. `meta.json` is rewritten on session restore (cheap). The Director writes; nobody else writes. Anyone can read for audit / replay / supervisor-ask.

Retention: keep forever for now. If disk pressure becomes real, we add a sweeper later (gzip + rotate or trim after N days).

---

## Slices

### Slice 1 — Persistent SessionLogWriter

New `src/CcDirector.Core/Storage/SessionLogWriter.cs`. One instance per session. Owns the four file streams. Single-threaded background writer fed by a bounded `Channel<LogRecord>`. Records arrive from:
- `Session.Buffer.OnBytesWritten` -> `raw.jsonl` (base64-encoded chunk to keep the JSON valid for binary ANSI)
- `Session.OnTurnCompleted` -> `turns.jsonl` (the existing `TurnSummary`)
- `Session.OnStatusColorChanged` -> `supervisor-events.jsonl`
- Agent-view widget changes -> `agent-view.jsonl`

If the channel fills (back-pressure), drop oldest with a FileLog warning. Hot path never blocks.

**Touches:**
- New `SessionLogWriter.cs`
- New `SessionLogRecord.cs` (the shared shape)
- `SessionManager.CreateSession` and the restore path: create the writer + start it
- `Session.Dispose` (or the manager's session-removal path): stop the writer, flush

**Tests:**
- Roundtrip: write 3 records, read JSONL, parse back into shape.
- Back-pressure: flood writes, assert the channel doesn't OOM, drop policy fires.
- Restart: writer can reopen an existing dir and append (no truncate).

### Slice 2 — `SupervisorService.AskAboutSessionAsync`

New static method, sibling of `SummarizeTurnAsync`. Inputs: `Session`, user question, `claudeExePath`, optional `CancellationToken`. Returns `{ Answer, Model, LatencyMs, ContextDigest }`. The ContextDigest is short text describing what was piped in (mirrors what the UI's "context" footer shows).

Context builder gathers from in-memory caches (fast), with read-through to disk for deeper questions:
- Last 50 entries of `Session.RecentSupervisorEvents`
- Last 5 entries of `TurnSummaryCache.GetForSession(...)`
- Last 4 KB of `Session.Buffer.DumpAll()`, ANSI-stripped
- `Session.RepoPath`, `AgentKind`, `ActivityState`, `CustomName`
- `GitSnapshotAsync` (cheap — reuses Phase 4 plumbing)

Prompt template: "You are the supervisor for this CC Director session. Below is the session's state. Answer the user's question in 1-3 sentences, plainly, citing specifics from the context. If the context doesn't contain the answer, say 'I don't have that in context.'"

**Touches:**
- `SupervisorService.cs` — new method + prompt builder + parser (response is plain text, no JSON wrapper needed for the answer path; we just trim and cap length).
- New `SupervisorAskResult` record in Contracts.

**Tests:**
- Prompt builder snapshot.
- Parser cap + trim.
- Fail-open: no claude CLI -> returns `{ Answer: "Supervisor not configured", Model: "" }`.

### Slice 3 — `POST /sessions/{sid}/supervisor/ask`

Body: `{ "question": "..." }`. Response: `SupervisorAskResult`. Forwarded by the Gateway via the same pattern as Slice 4b's `GET .../supervisor` endpoint.

**Touches:**
- `ControlEndpoints.cs` — new MapPost.
- `GatewayEndpoints.cs` — read-through forwarder.
- `DirectorEndpointClient.cs` — `AskSupervisorAsync`.

**Tests:**
- Endpoint round-trip with a stub `claudeExePath`.
- Bad body / empty question -> 400.

### Slice 4 — Desktop "Supervisor" tab

- New tab button after Agent: `SupervisorTabButton` in `MainWindow.axaml`.
- New `Panel x:Name="SupervisorPanel"` containing the new control.
- New `Controls/SupervisorPanel/SupervisorPanel.axaml(.cs)`:
  - Top banner: status dot + reason + "since X ago" (binds to `Session.StatusColor` / `LastStatusReason`).
  - Middle: ObservableCollection of `AskEntry` (question + answer + ts), rendered as scrolling list.
  - Bottom: `TextBox` + Ask button. Enter submits.
  - Footer: collapsible "context the supervisor sees" panel.
- On Ask click: POST to `http://127.0.0.1:<directorPort>/sessions/<sid>/supervisor/ask` from the Avalonia process, append the answer to the collection.
- Subscribe to `Session.OnStatusColorChanged` to refresh the banner without polling.
- History is per-tab-instance, cleared on session switch.

### Slice 5 — Web mirror

Append the same "Ask the supervisor" input + history to the existing Session View tab in `session-view.html`. Same backend endpoint. ~60 lines of JS + HTML.

### Slice 6 — Tests + smoke

- Unit tests for slices 1-3 as listed.
- Smoke via Task-Scheduler launch (rule 0b): build slot 4, launch via `cc-director-launch`, create a session, ask "what is this session doing", verify a sensible answer comes back, verify `session-logs\<sid>\*.jsonl` files exist and contain records.

---

## File-by-file change list

| File | Change |
|---|---|
| `src/CcDirector.Core/Storage/SessionLogWriter.cs` | NEW — append-only JSONL writer per session, background channel |
| `src/CcDirector.Core/Storage/SessionLogRecord.cs` | NEW — shared shape (kind, ts, payload) |
| `src/CcDirector.Core/Storage/SessionLogPaths.cs` | NEW — small helper to compute the per-session dir |
| `src/CcDirector.Core/Sessions/SessionManager.cs` | EDIT — create + dispose writer alongside sessions |
| `src/CcDirector.Core/Supervisor/SupervisorService.cs` | EDIT — `AskAboutSessionAsync` + prompt + parser |
| `src/CcDirector.Gateway.Contracts/SupervisorAskRequest.cs` | NEW |
| `src/CcDirector.Gateway.Contracts/SupervisorAskResult.cs` | NEW |
| `src/CcDirector.ControlApi/ControlEndpoints.cs` | EDIT — `POST /sessions/{sid}/supervisor/ask` |
| `src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs` | EDIT — `AskSupervisorAsync` |
| `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` | EDIT — forward `POST .../supervisor/ask` |
| `src/CcDirector.Avalonia/Controls/SupervisorPanel/SupervisorPanel.axaml(.cs)` | NEW — desktop tab content |
| `src/CcDirector.Avalonia/MainWindow.axaml(.cs)` | EDIT — Supervisor tab button + panel |
| `src/CcDirector.ControlApi/Web/session-view.html` | EDIT — Ask UI on the Session tab |
| `src/CcDirector.Core.Tests/Storage/SessionLogWriterTests.cs` | NEW |
| `src/CcDirector.Core.Tests/Supervisor/SupervisorAskTests.cs` | NEW |
| `src/CcDirector.Gateway.Tests/SupervisorAskForwardingTests.cs` | NEW |
| `docs/architecture/supervisor/SUPERVISOR_ASK_AND_LOG_PLAN.md` | NEW — this doc |

---

## Done criteria

1. Per-session directory `%LOCALAPPDATA%\cc-director\session-logs\<sid>\` exists for every live session, containing `meta.json`, `raw.jsonl`, `turns.jsonl`, `agent-view.jsonl`, `supervisor-events.jsonl`.
2. Director restart preserves logs; the writer appends to existing files cleanly.
3. Desktop has a "Supervisor" tab next to Agent. Selecting a session, opening the tab, asking "what is this session doing" returns a Haiku-generated answer in 5-15 seconds. Subsequent asks are independent (no conversation memory).
4. The same Ask UI is available at the bottom of the web Session View tab.
5. The supervisor's context preview shows what it has seen (events, turn summaries, buffer tail length, metadata).
6. Hot path is never blocked by logging: a session-busy load test produces no measurable lag on activity-state event latency.

---

## Open questions

1. **Where to apply size limits.** Today buffer is 2 MB ring; raw.jsonl will grow unbounded. Recommend: no rotation in Phase 5 (keep it simple). Add a sweeper in Phase 6 if disk fills.
2. **Agent-view widgets are currently computed from Claude's JSONL on read.** Persisting them duplicates that. Recommend writing them anyway, because (a) Claude's JSONL location is fragile (Claude config changes can move it), and (b) we want a self-contained record we own. Cost is small.
3. **Ask history persistence.** Per the chat, NO — desktop tab keeps it in-memory only, clears on session switch. Confirm in implementation.

---

## Document history

| Date | Author | Change |
|---|---|---|
| 2026-05-21 | claude (cc-director assistant) | Initial PLANNED. Phase 5 = supervisor ask + persistent session log on disk. |
