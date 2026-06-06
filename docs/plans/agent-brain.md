# Agent Brain - reusable C# client for a warm headless Claude session

Issue #172 follow-up. The Python harness (playground/headless-brain/) proved the
mechanism; this plan productizes it in C#: a reusable library any of our C#
programs can call, plus a desktop control panel to exercise every operation by
hand, plus a screenshot-backed QA pass.

## v2 (2026-06-06, issue #184): in-process on HostedAgent, REST transport RETIRED

The "REST-only client" decision below was declared wrong on 2026-06-05: the brain
must live INSIDE the Gateway process and cannot depend on a Director being up.
The whole Gateway-as-tray-app conversion (v0.6.6) exists so the Gateway runs as
the user and can host claude.exe with the user's Max OAuth.

What changed:

- **AgentBrainClient is DELETED** (with AgentBrainOptions and its fake-HTTP test
  project). `CcDirector.AgentBrain` now holds only the shared contract:
  `IAgentBrain`, `AskResult` / `ClearResult` / `BrainHealth`,
  `AgentBrainException`, `BrainLog`.
- **The implementation is `HostedAgent`** (src/CcDirector.HostedAgent): it owns
  claude.exe via an embedded ConPty and the IAgentDriver layer. The three
  determinism rules survive unchanged, measured at the source: quiet gate on the
  backend's own buffer clock, the JSONL transcript as the answer channel (direct
  file reads via the driver), and file-level transcript rediscovery after /clear
  (no relink - we own the working directory).
- **`BrainSupervisor`** (src/CcDirector.HostedAgent/BrainSupervisor.cs) owns ONE
  brain for a long-lived host: create on demand (first GetAsync spawns),
  RestartAsync as the recovery verb, graceful kill on dispose. Tested against
  the fake driver/backend pair in CcDirector.HostedAgent.Tests.
- **The Gateway tray app owns the brain's lifecycle**: GatewayTrayController
  creates the supervisor (workdir `%LOCALAPPDATA%\cc-director\brain`, logs into
  the gateway's FileLog); the Settings window shows brain health (state /
  session / idle / context tokens) and a RESTART BRAIN button.
- **The Panel is hosted-only**: the Director (REST) mode and `--mode-director`
  are gone; the panel is the manual test harness for the in-process brain.
  HOST PROCESS WARNING still applies: launch it from a clean process (Task
  Scheduler / desktop), never from inside a Claude Code terminal (nested-ConPty
  trap).

Everything below this section is the v1 historical record.

## Decisions (made 2026-06-05)

- **REST-only client.** The library talks to a Director's Control API over
  HTTP. No PTY code, no filesystem access to the JSONL. Works cross-machine
  over the tailnet, and keeps Directors dumb (locked fleet architecture).
- **Zero Director changes.** Everything needed already exists:
  - full reply text: `GET /sessions/{sid}/turns` (Text widgets are not truncated;
    `/summary` is capped at 2000 chars and is NOT used for answers)
  - clear detection + relink: `GET /sessions/{sid}/turns` exposes the live
    `ClaudeSessionId`; `GET /claude-sessions?repo=` discovers the post-/clear
    transcript remotely; `POST /sessions/{sid}/relink` repoints the Director
  - readiness gating: `SessionDto.idleSeconds` + `activityState`
- **UI framework: Avalonia** (same stack as the Director; screenshot/UI-drive
  tooling already proven via scripts/capture-window.ps1 + ui-drive.ps1).
- **The panel never spawns claude.exe itself** - it is a REST client. The
  Director that owns the session must be launched via the cc-director-launch
  scheduled task (slot 5), per CLAUDE.md.

## Phase 1 - CcDirector.AgentBrain (class library)

New project `src/CcDirector.AgentBrain/`, references only
CcDirector.Gateway.Contracts. Public surface:

```csharp
var brain = await AgentBrainClient.ConnectAsync(new AgentBrainOptions
{
    DirectorUrl = "http://127.0.0.1:7886",
    RepoPath = @"D:\...\sandbox",
});                                            // verifies /healthz

await brain.CreateSessionAsync(ct);            // POST /sessions, wait ready
await brain.AttachAsync(sessionId, ct);        // adopt an existing session

AskResult r = await brain.AskAsync(prompt, ct);
//   r.Text        - FULL reply text (from /turns)
//   r.ReplySeconds- time until the reply landed in the JSONL
//   r.ContextTokens

await brain.ClearAsync(ct);                    // /clear + relink + verified reset
await brain.RestartAsync(ct);                  // kill + recreate, same handle
await brain.KillAsync(ct);                     // DELETE /sessions/{sid}
BrainHealth h = await brain.GetHealthAsync(ct);// state, tokens, idle, alive
```

The three determinism rules live INSIDE the library, invisible to callers:

1. **Quiet gate**: every send waits for `activityState` in a ready state AND
   `idleSeconds >= 2` (server-side byte-silence clock). Prevents the
   swallowed-Enter race found in the spike.
2. **JSONL is the answer channel**: AskAsync polls `/usage`
   (assistantMessageCount) until the reply exists, then reads the full text
   from `/turns`. Never waits for the activity-state flip (10s quiet
   threshold) and never parses the terminal.
3. **Relink after /clear**: snapshot the old ClaudeSessionId, send `/clear`,
   wait for a NEW claude session id to appear in `/claude-sessions?repo=`,
   `POST /relink`, then verify `/usage` reads the fresh transcript.

Error handling: no fallbacks. Any unexpected state throws
`AgentBrainException` with the failing endpoint, HTTP status, and session
state baked into the message. RestartAsync is the caller's recovery verb.

Tests: `src/CcDirector.AgentBrain.Tests/` - unit tests against a scripted
fake HttpMessageHandler (no live Director needed): ask happy path, quiet-gate
behavior, clear relink flow, restart identity change, error mapping.

## Phase 2 - CcDirector.AgentBrain.Panel (Avalonia test app)

New project `src/CcDirector.AgentBrain.Panel/`. One window, VisualStyle.md
compliant, optimized for big obvious controls:

- Top bar: Director URL box + CONNECT button + health dot.
- Large action buttons (every library verb gets one):
  CREATE SESSION / ASK / CLEAR CONTEXT / RESTART / KILL / HEALTH CHECK
- Prompt text box + conversation log pane (questions, full answers,
  latency + context-token annotations).
- "Clear context after every ask" checkbox - the warm turn-brief pattern.
- Status bar: session id, activity state, idle seconds, context tokens,
  live-updating via a background poll.
- Responsive-UI rules: all I/O async, buttons disable while busy, <100ms
  visual feedback, never block the UI thread.

## Phase 3 - QA (autonomous, screenshot-backed)

Driven end-to-end against a slot-5 Director launched via Task Scheduler.
Panel driven with scripts/ui-drive.ps1, captured with
scripts/capture-window.ps1. Report: docs/features/agent-brain/QA_REPORT.html
(cc-html, boardroom) embedding a screenshot per case.

| # | Case | Pass criterion |
|---|---|---|
| QA-1 | Library unit tests | dotnet test green |
| QA-2 | Connect to Director | health dot green, /healthz version shown |
| QA-3 | Create session | state becomes ready; session id visible |
| QA-4 | Ask a question | full answer in the log with latency + tokens |
| QA-5 | Long answer (>2000 chars) | full text present (proves /turns path, not /summary) |
| QA-6 | Clear context | codeword stored, CLEAR pressed, recall asks returns CONTEXT-EMPTY; context tokens reset |
| QA-7 | Auto-clear mode | two asks with checkbox on; second ask has no memory of first |
| QA-8 | Restart | session id changes; agent answers afterwards |
| QA-9 | Error-state recovery | claude.exe under OUR test session killed externally; health shows dead; RESTART heals; agent answers |
| QA-10 | Kill | session gone from /sessions |

Loop: implement -> build -> test -> drive UI -> screenshot -> on any failure
fix and rerun the failed case -> regenerate report. Done when QA-1..QA-10 all
PASS with evidence.

## Out of scope (later)

- 24h soak + session-0/SYSTEM service-context run (issue #172 remaining items)
- Gateway-side brief agent built on this library (the actual consumer)
