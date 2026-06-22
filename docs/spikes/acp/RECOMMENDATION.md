# Spike Recommendation: ACP (Agent Client Protocol) as a Cross-Agent Transport

**Issue:** #627 - Evaluate ACP using GitHub Copilot CLI `--acp` as the subject.
**Status:** COMPLETE - live evidence captured.
**Subject binary:** GitHub Copilot CLI 1.0.63 (`copilot --acp`).
**Date of runs:** 2026-06-22.
**Author:** Developer Agent (CenCon Development Method), research spike.

> This is a decision document. It ships NO production code. The only artifact under
> `docs/spikes/acp/` besides this file is a clearly-marked THROWAWAY proof-of-concept
> (`poc/`) and the real wire transcripts it produced (`poc/transcripts/`).

## How the evidence was produced (so it can be re-run)

A throwaway C# console client (`docs/spikes/acp/poc/`, project `AcpPoc.csproj`, deliberately
NOT added to `cc-director.sln`) launches `copilot --acp` and acts as a full JSON-RPC 2.0 peer
over the agent's stdio: it sends client-originated requests (`initialize`, `session/new`,
`session/prompt`, `session/cancel`) and answers the agent's own requests
(`session/request_permission`, and the `fs/*` / `terminal/*` client methods). Every line in both
directions is written verbatim to a transcript file.

Auth: Copilot CLI accepts `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN`. The token used was
the GitHub CLI OAuth token (`gh auth token`, a `gho_...` token) - explicitly a supported type
("OAuth tokens from the GitHub CLI (gh) app"). Classic personal access tokens (`ghp_...`) are NOT
supported. No `COPILOT_GITHUB_TOKEN` was pre-set in the environment; it was supplied for the run
from `gh auth token`.

Re-run (from `docs/spikes/acp/poc/`):

```
dotnet build AcpPoc.csproj
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=cancel      dotnet run --no-build -- transcripts/acp-live-transcript.txt
COPILOT_GITHUB_TOKEN="$(gh auth token)"  ACP_POC_SCENARIO=permission  dotnet run --no-build -- transcripts/acp-permission-transcript.txt
```

The PoC runs the agent with `cwd` set to a fresh throwaway sandbox directory under the OS temp
folder - NEVER this repository. The two captured transcripts are committed alongside this document:

- `poc/transcripts/acp-live-transcript.txt` - lifecycle + a trivial prompt turn + a cancellation.
- `poc/transcripts/acp-permission-transcript.txt` - lifecycle + a tool-using turn with a full
  `session/request_permission` round-trip and `tool_call` / `tool_call_update` streaming.

All JSON quoted below is copied verbatim from those two files.

---

## 1. Observed ACP wire transcript (real messages)

### 1a. Lifecycle - `initialize` (client -> agent, and the agent's reply)

```json
[SEND] {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":true,"writeTextFile":true},"terminal":true}}}
[RECV] {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"mcpCapabilities":{"http":true,"sse":true},"promptCapabilities":{"image":true,"audio":false,"embeddedContext":true},"sessionCapabilities":{"list":{}}},"agentInfo":{"name":"Copilot","title":"Copilot","version":"1.0.63"},"authMethods":[{"id":"copilot-login","name":"Log in with Copilot CLI","description":"Run `copilot login` in the terminal", ...}]}}
```

Note: `initialize` succeeds and returns capabilities **before** any auth is asserted. The
`authMethods` array is the agent telling the client how a human would log in; with a valid token in
the environment the subsequent `session/prompt` turn runs without any interactive login.

### 1b. Session creation - `session/new` (client -> agent)

```json
[SEND] {"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"C:\\...\\acp-poc-sandbox-557bb7fb","mcpServers":[]}}
```

Before the response, the agent pushes two `session/update` notifications
(`available_commands_update` - the full Copilot slash-command catalogue), then replies with the
session id, the model catalogue, the session modes, and config options:

```json
[RECV] {"jsonrpc":"2.0","id":2,"result":{"sessionId":"403a9e11-3505-4e78-b04e-b8a38873c528","models":{"availableModels":[{"modelId":"auto","name":"Auto",...},{"modelId":"gpt-5-mini",...},{"modelId":"claude-haiku-4.5",...}],"currentModelId":"gpt-5-mini"},"modes":{"availableModes":[{"id":".../session-modes#agent","name":"Agent",...},{"id":".../session-modes#plan","name":"Plan",...},{"id":".../session-modes#autopilot","name":"Autopilot",...}],"currentModeId":".../session-modes#agent"},"configOptions":[ {"id":"mode",...},{"id":"model",...},{"id":"reasoning_effort",...},{"id":"allow_all",...} ]}}
```

This single response already answers the model-selection and mode-parity questions (Q5): model,
mode (agent / plan / autopilot), reasoning effort, and an `allow_all` toggle are all exposed as
structured ACP `configOptions`, not just launch flags.

### 1c. A prompt turn with streamed updates - `session/prompt` (client -> agent), `session/update` (agent -> client)

```json
[SEND] {"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"403a9e11-...","prompt":[{"type":"text","text":"What is 2+2? Reply with just the number."}]}}
[RECV] {"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"403a9e11-...","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"4"}}}}
[RECV] {"jsonrpc":"2.0","id":3,"result":{"stopReason":"end_turn"}}
```

The assistant reply streams as `agent_message_chunk` notifications; the turn terminates when the
`session/prompt` request resolves with `{"stopReason":"end_turn"}`.

### 1d. Tool-call permission round-trip (the headline result)

From `acp-permission-transcript.txt`, prompting Copilot to write a file. The agent first announces
the pending tool call, then asks the client for permission, the client answers programmatically, and
the agent reports completion:

```json
[RECV] session/update -> {"sessionUpdate":"tool_call","toolCallId":"call_RJtTNbZSTFKS8DT4NL7dRadA","title":"Creating ...notes.txt","kind":"edit","status":"pending","rawInput":{"path":"...\\notes.txt","file_text":"hello from acp."},"content":[{"type":"diff","path":"...\\notes.txt","oldText":"","newText":"hello from acp."}]}

[RECV] {"jsonrpc":"2.0","id":0,"method":"session/request_permission","params":{"sessionId":"25bd087b-...","toolCall":{"toolCallId":"call_RJtTNbZSTFKS8DT4NL7dRadA","title":"Create file","kind":"edit","status":"pending","rawInput":{...,"diff":"...@@ -1,0 +1,1 @@\n+hello from acp.\n"},"locations":[{"path":"...\\notes.txt"}]},"options":[{"optionId":"allow_once","kind":"allow_once","name":"Allow once"},{"optionId":"allow_always","kind":"allow_always","name":"Always allow"},{"optionId":"reject_once","kind":"reject_once","name":"Deny"}]}}

[SEND] {"jsonrpc":"2.0","id":0,"result":{"outcome":{"outcome":"selected","optionId":"allow_once"}}}

[RECV] session/update -> {"sessionUpdate":"tool_call_update","toolCallId":"call_RJtTNbZSTFKS8DT4NL7dRadA","status":"completed","content":[{"type":"diff",...}],"rawOutput":{"content":"Created file ...notes.txt with 15 characters",...}}

[RECV] {"jsonrpc":"2.0","id":4,"result":{"stopReason":"end_turn"}}
```

The file was genuinely created on disk in the sandbox (`notes.txt` -> `hello from acp.`), confirming
the round-trip was real and the approval took effect.

The permission request carries three structured options - `allow_once`, `allow_always`,
`reject_once` (each with a `kind`) - which map directly onto CC Director's existing allow / deny /
yolo model (yolo = pre-answer with `allow_always`, or use the session's `allow_all` config option).

### 1e. Cancellation - `session/cancel` (client -> agent, a notification)

From `acp-live-transcript.txt`, a turn was started and then cancelled mid-flight:

```json
[SEND] {"jsonrpc":"2.0","id":4,"method":"session/prompt","params":{"sessionId":"403a9e11-...","prompt":[{"type":"text","text":"Create a file ... then list the files ..."}]}}
[SEND] {"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"403a9e11-..."}}
[RECV] session/update -> {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Info: Operation cancelled by user"}}
[RECV] {"jsonrpc":"2.0","id":4,"result":{"stopReason":"end_turn"}}
```

`session/cancel` is a notification (no id). The agent acknowledges with a message chunk and resolves
the in-flight `session/prompt`. This is a clean, programmatic, in-band cancel - no Ctrl+C, no PTY
keystroke, no echo-verify.

### Q1 summary - the methods `copilot --acp` actually implements (all observed live)

| Direction | Method | Kind | Observed |
|-----------|--------|------|----------|
| client -> agent | `initialize` | request | yes (1a) |
| client -> agent | `session/new` | request | yes (1b) |
| client -> agent | `session/prompt` | request | yes (1c) |
| client -> agent | `session/cancel` | notification | yes (1e) |
| agent -> client | `session/update` | notification | yes - `agent_message_chunk`, `tool_call`, `tool_call_update`, `available_commands_update`, `config_option_update` |
| agent -> client | `session/request_permission` | request | yes (1d) |

**Not observed:** the agent never called `fs/read_text_file`, `fs/write_text_file`, or any
`terminal/*` method even when it edited a file - see Section 3 / Q4. (The PoC implements handlers for
all of them, so any such call would have appeared in the transcript. The grep count was zero.)

---

## 2. Capability comparison: ACP transport vs. the #625 terminal + JSONL transport

The #625 transport is `ISessionBackend` (a PTY/terminal) + `IAgentDriver`
(`src/CcDirector.Core/Drivers/IAgentDriver.cs`): the driver submits prompts as keystrokes, cancels
with a keystroke, and reads replies/usage by parsing the tool's on-disk JSONL transcript files.

| Dimension | Terminal + JSONL (#625, `CopilotDriver`) | ACP transport (observed) | Winner |
|-----------|------------------------------------------|--------------------------|--------|
| Turn / transcript fidelity | Read after the fact from the tool's JSONL file; `ReadWidgets` parses it; timing depends on file flush | Live, structured `session/update` stream (`agent_message_chunk`, `tool_call`, `tool_call_update`) with explicit `stopReason` | **ACP** |
| Prompt submission | Keystroke injection with tool-specific submit semantics (blind or echo-verified) | Single structured `session/prompt` request; no keystroke timing | **ACP** |
| Tool-call permission handling | None programmatic - the permission prompt is just text in the terminal a human reads | First-class `session/request_permission` request with structured `allow_once`/`allow_always`/`reject_once` options, answerable in code | **ACP (decisive)** |
| Cancellation | `Ctrl+C` keystroke (Interrupt) / soft cancel keystroke; relies on the TUI honoring it | In-band `session/cancel` notification; agent acks and resolves the turn | **ACP** |
| Robustness | Fragile at the keystroke/echo + PTY-scrape boundary (the issue's core complaint) | No keystrokes, no PTY scrape; framed JSON-RPC lines | **ACP** |
| Session resume | `--resume`/`--continue` launch flags; `PreassignedSessionId` capability | `initialize.agentCapabilities.loadSession:true` + `sessionCapabilities.list` observed - resume is in-protocol | **ACP (tie+)** |
| Model / mode selection | `--model` flag (`ModelSelection`); mode via launch flags | Full `models`, `modes` (agent/plan/autopilot), `reasoning_effort`, `allow_all` returned as structured `configOptions` from `session/new` | **ACP** |
| Live terminal view / "follow the agent" | Native - a real terminal tab the human watches | None - ACP is headless; CC Director must render its own view from `session/update` | **Terminal** |
| Per-agent driver cost | One driver per CLI, each encoding submit/cancel/transcript quirks | One transport for every ACP agent (see Section 4) | **ACP** |
| Maturity in this codebase | Shipped (#625), tested | Greenfield; needs a new .NET JSON-RPC stdio client | **Terminal (today)** |

Net: ACP wins every *correctness/robustness* dimension and the *permission* and *cancellation*
dimensions decisively. Terminal wins only on the *live terminal UX* and *already-shipped* axes.

---

## 3 & Q4. Client-burden list - what CC Director must implement for a production ACP transport

ACP nominally assumes the client owns the editor surface, so the spec defines client-side `fs/*` and
`terminal/*` methods. **The live finding is that Copilot `--acp` does NOT delegate those** - it
applied the file edit with its own internal tool and only asked the client for *permission* (Section
1d; zero `fs/*` / `terminal/*` calls in the transcript). So the *mandatory* client surface to get a
prompt answered is much smaller than the full spec implies.

Effort tags: S = trivial, M = moderate, L = large.

| Agent -> client method | Needed to get a prompt answered? | Effort | Notes (from live evidence) |
|------------------------|----------------------------------|--------|----------------------------|
| Respond to `initialize` (declare client capabilities) | Mandatory | **S** | One static reply object. |
| Handle `session/update` notifications | Mandatory (else no output) | **M** | Must parse `agent_message_chunk`, `tool_call`, `tool_call_update`, `available_commands_update`, `config_option_update` into `TurnWidgetDto`. |
| `session/request_permission` -> respond with an option | Mandatory if any tool runs | **M** | Map to allow/deny/yolo. Auto-answer for yolo via `allow_always`. UI prompt otherwise. |
| `fs/read_text_file` | Optional for Copilot (never called) | **S** | Required only if a future ACP agent delegates reads. |
| `fs/write_text_file` | Optional for Copilot (never called) | **S** | Same; Copilot writes internally. |
| `terminal/create` / `output` / `wait_for_exit` / `kill` / `release` | Optional for Copilot (never called) | **L** | A real terminal host is the large item *if* an agent delegates exec. Copilot does not, so this is deferrable. |

Client-originated calls CC Director must *send* (not a burden so much as the protocol surface):
`initialize`, `session/new`, `session/prompt`, `session/cancel` - each **S**. Optional but valuable:
read `loadSession` / `sessionCapabilities.list` for resume (**S**).

**Q4 answer (Expected vs Actual).** Expected (from the spec): the client must implement filesystem
read/write/edit and terminal/exec just to function. Actual: to get a prompt answered against Copilot,
the client only MUST handle `initialize`, `session/update`, and `session/request_permission`. The
`fs/*` and `terminal/*` methods were never invoked because Copilot owns those operations internally
and surfaces them as permission requests + diff content. The heavy `terminal/*` host is therefore
*deferrable* for Copilot and becomes mandatory only for an agent that chooses to delegate exec to the
client.

---

## 4 & Q6. Generality finding - which CC-Director-relevant agents speak ACP

**Verified live in this spike:** GitHub Copilot CLI 1.0.63 (the whole of Sections 1-3).

**Verified from Zed's published external-agents documentation** (which connects to external agents
*over ACP*): Claude Agent, Codex, Gemini CLI, OpenCode, Copilot, Cursor, and Pi Coding Agent are all
reachable over ACP. (The issue independently cites Zed, JetBrains AI Assistant, Kiro, OpenCode, and
Gemini CLI as ACP participants.) These overlap heavily with the agents CC Director cares about.

Caveat held honestly: only **Copilot** was exercised live here. The others are documented as ACP
agents but were not driven by this PoC. Whether one `AcpDriver` serves them depends on each agent's
`session/update` vocabulary (Copilot adds non-spec `available_commands_update` /
`config_option_update`), but the *lifecycle, prompt, permission, and cancel* shapes are the shared
ACP core and would be one transport.

**Maintenance win (Q6, Expected vs Actual).** Expected: one ACP transport could replace N terminal
drivers. Actual: plausible and large. A terminal driver per CLI must encode submit semantics, cancel
keystrokes, transcript-file location and parsing, and echo-verify quirks (see `IAgentDriver`'s 13
members). An ACP transport replaces all of that with one JSON-RPC peer; per-agent work shrinks to a
thin capability map plus handling that agent's `session/update` dialect. For the ~6-7 ACP-capable
agents above, that is roughly "one transport + 6 small dialect adapters" versus "6 full terminal
drivers."

---

## 5 & Q7. UX-parity note - what replaces the live terminal tab

Today each session shows a real terminal tab (`src/CcDirector.Terminal.Avalonia`) the human watches -
the agent "followed" live. ACP is headless: there is no terminal to show. To reach parity CC Director
must render a **conversation/permission view** built from the `session/update` stream:

- A streamed transcript from `agent_message_chunk` (assistant text), `tool_call` /
  `tool_call_update` (tool activity, with the `diff` content ACP already provides), and the
  `available_commands_update` / `config_option_update` metadata.
- A permission affordance that surfaces `session/request_permission` (title, diff, the three option
  buttons) and posts the chosen `optionId` back.
- Model/mode/effort pickers driven by the `configOptions` already returned at `session/new`.

**Cost (Q7, Expected vs Actual).** Expected: lose the live terminal, must build a viewer. Actual:
confirmed - the loss is the raw terminal tab and native "follow"; the replacement is a new Avalonia
conversation view (governed by `docs/VisualStyle.md`) plus a permission control. The data to drive it
is richer and more structured than PTY scrape (real diffs, structured tool calls, explicit stop
reasons), so the view is *better* once built - but it is net-new UI, the single largest cost item of a
production transport, and it is **M-L**. This is why the recommendation is hybrid, not rip-and-replace.

---

## 6 & Q9. Recommendation

**HYBRID, with a fast follow to build a production ACP transport alongside the terminal drivers -
not instead of them.**

Rationale, tied to evidence:

- ACP decisively wins the two things the issue says terminal-driving does badly: **programmatic
  tool-call permission handling** (Section 1d) and **clean cancellation** (Section 1e). Both were
  observed live, end to end.
- The **mandatory** client burden is small (Section 3): `initialize` + `session/update` +
  `session/request_permission`. The expensive `terminal/*` host is deferrable because Copilot does
  not delegate exec.
- The **generality** upside is real and large (Section 4): one transport plausibly serves Copilot,
  Gemini CLI, OpenCode, Codex, Cursor, Claude Agent, and Pi.
- The one true loss is the **live terminal tab** (Section 5), which is also the largest build item.
  That is exactly why this is hybrid: keep terminal drivers (and their live tab) for agents/users who
  want them, add ACP for the robustness + multi-agent win, and only retire a terminal driver per
  agent once the ACP conversation view is at parity.

### Q8 - cost & shape (Expected vs Actual)

- **Shape.** Expected: "is it a driver or a transport beneath the driver?" Actual: it is a **parallel
  transport beneath the driver abstraction**, not another `IAgentDriver`. `IAgentDriver` is built for
  keystroke-submit + transcript-file reading over an `ISessionBackend` (PTY); ACP has no PTY and no
  keystrokes. The clean fit is a new `IAgentTransport`-style seam (terminal transport vs. ACP
  transport), with a thin per-agent ACP capability/dialect map taking the role the driver plays today.
- **Cost.** Rough estimate for a production .NET ACP transport:
  - JSON-RPC-over-stdio peer (framed line reader, request/response correlation, notification
    dispatch): **M**. The PoC is a working skeleton of exactly this in ~300 lines.
  - Client handlers: `session/update` -> `TurnWidgetDto` (**M**), `session/request_permission` ->
    allow/deny/yolo (**M**), optional `fs/*` (**S**), `terminal/*` host (**L**, deferrable).
  - Avalonia conversation + permission view (the UX-parity item): **M-L**.
  - Wiring into the host/session lifecycle + tests: **M**.
  - Total: a **medium-large** body of work, dominated by the conversation UI, with the protocol core
    being the *small* part (the PoC proves the core in a day).

### Sketched follow-up scope (the "build" issue this spike would open)

1. `IAgentTransport` seam separating terminal transport from ACP transport beneath the driver/host.
2. A production `AcpStdioPeer` (framed JSON-RPC stdio: send `initialize`/`session/new`/
   `session/prompt`/`session/cancel`; dispatch incoming `session/update` and answer
   `session/request_permission`). Port the PoC's structure; add reconnection, backpressure, logging
   per `docs/CodingStyle.md`.
3. Map `session/update` -> `TurnWidgetDto` and `configOptions` -> the existing model/mode pickers.
4. Map `session/request_permission` -> the existing allow/deny/yolo model (yolo answers
   `allow_always` / sets the `allow_all` config option).
5. New Avalonia conversation + permission view (per `docs/VisualStyle.md`) to replace the terminal tab
   for ACP sessions.
6. Land Copilot as the first ACP agent; defer `terminal/*` host and additional agents (Gemini CLI,
   OpenCode) to subsequent issues.
7. CenCon impact to record in that issue: `core_services` gains `Transport/Acp`; `avalonia_ui` gains a
   conversation/permission view. (This spike changes neither - docs only.)

---

## Question scorecard (Q1-Q9, Expected vs Actual, all with evidence above)

| Q | Question | Answer (Expected vs Actual) | Evidence |
|---|----------|------------------------------|----------|
| Q1 | Surface | Expected the spec's lifecycle/session/prompt/update/permission/cancel. Actual: all present and observed live; Copilot adds non-spec `available_commands_update` / `config_option_update`. | Section 1, transcripts |
| Q2 | Permissions | Expected a permission prompt. Actual: structured `session/request_permission` with `allow_once`/`allow_always`/`reject_once`; answered in code; maps cleanly to allow/deny/yolo. | Section 1d |
| Q3 | Cancellation | Expected a cancel path. Actual: `session/cancel` notification cleanly aborts the turn in-band; no Ctrl+C. | Section 1e |
| Q4 | Client burden | Expected mandatory fs+terminal+exec. Actual: only `initialize`+`session/update`+`request_permission` mandatory for Copilot; `fs/*`/`terminal/*` never called (Copilot owns them). | Section 3 |
| Q5 | Parity | Expected some features CLI-flag-only. Actual: session resume (`loadSession`), model, mode (agent/plan/autopilot), reasoning effort, allow-all are all in-protocol `configOptions`. | Section 1b |
| Q6 | Generality | Expected several ACP agents. Actual: Copilot verified live; Gemini CLI/OpenCode/Codex/Cursor/Claude Agent/Pi documented (Zed). One transport plausibly serves them. | Section 4 |
| Q7 | UX | Expected to lose the terminal. Actual: lose the live terminal tab; gain a richer structured conversation/permission view at **M-L** cost - the single largest build item. | Section 5 |
| Q8 | Cost & shape | Expected driver-vs-transport question. Actual: a transport beneath the driver abstraction (`IAgentTransport` seam), not an `IAgentDriver`; total **medium-large**, dominated by the UI; protocol core is small. | Section 6 / Q8 |
| Q9 | Recommendation | **Hybrid** - build a production ACP transport alongside terminal drivers, retire terminal per agent only at UI parity. | Section 6 |
