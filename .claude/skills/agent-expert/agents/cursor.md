<!--
Per-agent reference for the agent-expert skill. ASCII only. No Unicode, no emoji, no em-dashes.
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with inline source links.
All links are collected in the Sources section.
-->

# Cursor Agent (Cursor)

> The Cursor command line agent, binary `cursor-agent` from cursor.com, marketed as "Cursor Agent" / "Agent".
> Our integration status: driver class `CursorDriver` / agent class `CursorAgent` / plugin class `CursorAgentPlugin`.
> IMPORTANT: the `cursor-agent` binary is NOT installed on our Windows dev machine and there is no
> native Windows build - spawn attempts fail with "CreateProcess failed". Everything in section 10
> that is marked "needs live verification" cannot be checked here yet.

---

## 1. Identity and install

- Binary name: `cursor-agent`. [VERIFIED from docs](https://cursor.com/docs/cli/overview)
- Vendor / docs home: cursor.com, docs at https://cursor.com/docs/cli/overview. [VERIFIED from docs](https://cursor.com/docs/cli/overview)
- Install (Unix): `curl https://cursor.com/install -fsS | bash`. [VERIFIED from docs](https://cursor.com/docs/cli/headless)
- NO native Windows build. The official installer (`https://cursor.com/install`) is a bash
  script that accepts only `uname -s` of `Linux*` or `Darwin*`; `https://cursor.com/install.ps1`
  is not a real installer (returns the marketing homepage). On Windows it runs only under WSL or
  Linux/macOS. [VERIFIED from our prior survey](D:\ReposFred\devthrottle\docs\design\agent-history-capture\drivers\cursor.md)
- Install location (Unix): symlink at `~/.local/bin/cursor-agent`; versioned binary under
  `~/.local/share/cursor-agent/versions/<version>/`. [VERIFIED from our prior survey](D:\ReposFred\devthrottle\docs\design\agent-history-capture\drivers\cursor.md)
- Version probe: `cursor-agent --version` (also `-v`). [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)
  Our plugin probes with `--version`, timeout 8 seconds. Observed version in WSL: `2026.06.24-00-45-58-9f61de7`.
- Update in place: `cursor-agent update`. [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)
- Auth: `cursor-agent login` (device flow) / `logout` / `status` (alias `whoami`); or headless via
  `--api-key <key>` or the `CURSOR_API_KEY` environment variable. [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)

## 2. Command-line interface

Interactive (default): `cursor-agent` opens a terminal UI (TUI). Headless / non-interactive:
add `-p`/`--print`. [VERIFIED from docs](https://cursor.com/docs/cli/headless)

Flag table (all [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters) unless noted):

| Flag | Meaning |
| --- | --- |
| `-p`, `--print` | Print responses to console; non-interactive scripting mode. |
| `--output-format <fmt>` | `text`, `json`, or `stream-json`. Only works with `--print`. |
| `--stream-partial-output` | Stream partial output as individual text deltas (adds `timestamp_ms`/`model_call_id` to assistant events). [VERIFIED from docs](https://cursor.com/docs/cli/reference/output-format) |
| `--resume [chatId]` | Resume a chat session by id (load prior context). |
| `--continue` | Continue the previous session; alias for `--resume=-1`. |
| `--model <model>` | Model to use. (We currently pass no model flag - see section 3.) |
| `--list-models` / `models` | List available models. |
| `--mode <mode>` | Set agent mode: `plan` or `ask`. |
| `--plan` | Start in plan mode (shorthand for `--mode=plan`). |
| `-f`, `--force` | Force allow commands unless explicitly denied. |
| `--yolo` | Alias for `--force`. |
| `--sandbox <mode>` | `enabled` or `disabled`. |
| `--approve-mcps` | Automatically approve all MCP servers. |
| `--trust` | Trust the workspace without prompting (headless mode only). |
| `--workspace <path>` | Workspace / repository root to use. |
| `--plugin-dir <path>` | Load a local plugin directory (repeatable). |
| `-w`, `--worktree [name]` | Run in a new Git worktree under `~/.cursor/worktrees/<reponame>/<name>`. |
| `--worktree-base <branch>` | Branch/ref to base the new worktree on. |
| `--api-key <key>` | API key (or `CURSOR_API_KEY`). |
| `-H`, `--header <hdr>` | Add a custom header (`Name: Value`) to agent requests. |
| `-v`, `--version` | Print version. |
| `-h`, `--help` | Help. |

Subcommands: `agent [prompt...]`, `login`, `logout`, `status`/`whoami`, `models`, `mcp`
(`login`/`list`/`list-tools`/`enable`/`disable`), `ls` (resume picker), `resume` (resume latest),
`sandbox` (`enable`/`disable`/`reset`/`run`), `update`. [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)

Model selection at runtime is also done with the `/model` slash command (`/model auto`,
`/model gpt-5`, `/model sonnet-4-thinking`). [VERIFIED from docs](https://cursor.com/docs/cli/reference/configuration)

What we pass at launch (`CursorAgent.BuildLaunchSpec`): user/preset args, plus `--resume="<chat-id>"`
when resuming, plus `-p --output-format stream-json` when in Studio (stream-json) mode. The
"Automatic" preset adds the force/auto-approve arg (`AgentToolCatalog.CursorForceArg`).

## 3. Configuration

- Global config: `~/.cursor/cli-config.json` (Windows: `%USERPROFILE%\.cursor\cli-config.json`).
  [VERIFIED from docs](https://cursor.com/docs/cli/reference/configuration)
- Project config: `<project>/.cursor/cli.json` (permissions only). [VERIFIED from docs](https://cursor.com/docs/cli/reference/configuration)
- Environment overrides: `CURSOR_CONFIG_DIR` (custom dir), `XDG_CONFIG_HOME` on Linux/BSD
  (`$XDG_CONFIG_HOME/cursor/cli-config.json`). [VERIFIED from docs](https://cursor.com/docs/cli/reference/configuration)
- Config keys: `version` (=1), `editor.vimMode`, `permissions.allow` / `permissions.deny`,
  `model` (object), `notifications`, `display.showThinkingBlocks`, `approvalMode`
  (`allowlist` or `unrestricted`), `network.useHttp1ForAgent`,
  `attribution.attributeCommitsToAgent`. [VERIFIED from docs](https://cursor.com/docs/cli/reference/configuration)
- Precedence (config): enterprise/system > project `.cursor/cli.json` > global `~/.cursor/cli-config.json`.
  [INFERRED/UNCERTAIN] - the docs describe the files but do not give an explicit ordered precedence
  list for `cli-config.json` vs `cli.json` beyond "project = permissions only".
- Auth env var: `CURSOR_API_KEY`. [VERIFIED from docs](https://cursor.com/docs/cli/headless)
- Our config: `AgentOptions.CursorPath` holds the executable path; we set no model
  (`DefaultModel = ""`, `ModelFlag = ""`, no known-models list).

## 4. Context injection (how to inject a preamble)

Cursor reads rules / instruction files at the start of context (highest-leverage injection point):

- `.cursor/rules` (project rules directory), `AGENTS.md`, and `CLAUDE.md` are read and inserted at
  the start of the conversation context. [VERIFIED from docs](https://cursor.com/docs/cli/using)
  - Survives `/clear` (new conversation): [INFERRED/UNCERTAIN] - rules files are re-read when a new
    conversation context is built, so they should re-apply, but this is not explicitly documented for
    the CLI. Needs live verification.
  - Survives `/compact` (summarize/compress): [INFERRED/UNCERTAIN] - compaction summarizes the
    conversation; rules are part of system context rather than the conversation turns, so they are
    expected to persist, but not documented. Needs live verification.
- Hook-based injection: the `sessionStart` hook can return `additional_context` (a string) on stdout,
  which is "injected into the conversation's initial system context", and an `env` object that
  persists for the session. [VERIFIED from docs](https://cursor.com/docs/hooks) This is the near-exact
  analog of Claude Code's SessionStart `additionalContext`, but only for LAUNCH (see the benchmark in
  section 5).
  - Survives `/clear`: [INFERRED/UNCERTAIN] - depends entirely on whether `sessionStart` re-fires when
    a new conversation begins. UNDOCUMENTED. Needs live verification.
  - Survives `/compact`: NO re-injection path. There is only the observational `preCompact` hook,
    which cannot return `additional_context` to re-inject context after a compaction. [VERIFIED from docs](https://cursor.com/docs/hooks)
- `postToolUse` can also return `additional_context`, but that is per-tool, not a launch preamble.
  [VERIFIED from docs](https://cursor.com/docs/hooks)

## 5. Lifecycle events and hooks

Hooks config file `hooks.json`, version 1. Locations, highest-to-lowest priority: enterprise/system
(`C:\ProgramData\Cursor\hooks.json` on Windows; `/etc/cursor/hooks.json` Linux/WSL;
`/Library/Application Support/Cursor/hooks.json` macOS) > team (cloud, Enterprise) > project
`<project-root>/.cursor/hooks.json` > user `~/.cursor/hooks.json`. [VERIFIED from docs](https://cursor.com/docs/hooks)

Config shape per hook entry: `command`, `type` (`command` or `prompt`), `timeout`, `matcher`,
`loop_limit`, `failClosed`. Project hooks run from the project root. [VERIFIED from docs](https://cursor.com/docs/hooks)

Stdin/stdout contract: every command hook receives a JSON object on stdin and returns JSON on stdout.
Base input fields on all hooks: `conversation_id`, `generation_id`, `model`, `model_id`,
`model_params`, `hook_event_name`, `cursor_version`, `workspace_roots`, `user_email`,
`transcript_path`. Exit codes: `0` = success (use JSON output), `2` = block/deny, anything else =
failure and proceed (fail-open, unless `failClosed`). Hooks also receive env vars:
`CURSOR_PROJECT_DIR`, `CURSOR_VERSION`, `CURSOR_USER_EMAIL`, `CURSOR_TRANSCRIPT_PATH`,
`CURSOR_CODE_REMOTE`, and `CLAUDE_PROJECT_DIR` (alias). [VERIFIED from docs](https://cursor.com/docs/hooks)

Full event list [VERIFIED from docs](https://cursor.com/docs/hooks):
- Lifecycle: `sessionStart`, `sessionEnd`, `workspaceOpen` (output `pluginPaths`).
- Prompt: `beforeSubmitPrompt`.
- Tools (generic): `preToolUse`, `postToolUse`, `postToolUseFailure`.
- Subagents (Task tool): `subagentStart`, `subagentStop`.
- Shell: `beforeShellExecution`, `afterShellExecution`.
- MCP: `beforeMCPExecution`, `afterMCPExecution`.
- Files: `beforeReadFile`, `afterFileEdit`.
- Tab (autocomplete): `beforeTabFileRead`, `afterTabFileEdit`.
- Compaction: `preCompact` (observe only).
- Responses: `afterAgentResponse`, `afterAgentThought`.
- Completion: `stop`.

Key schemas [VERIFIED from docs](https://cursor.com/docs/hooks):
- `sessionStart` input: `session_id`, `is_background_agent`, `composer_mode`
  (`agent`|`ask`|`edit`); output: `{ "env": {...}, "additional_context": "string" }`.
- `stop` input: `status` (`completed`|`aborted`|`error`), `loop_count`; output:
  `{ "followup_message": "string" }` - when non-empty, Cursor auto-submits it as the next user
  message (respecting `loop_limit`).
- `preToolUse` output: `permission` (`allow`|`deny`), `user_message`, `agent_message`,
  `updated_input`. `postToolUse` output: `updated_mcp_tool_output`, `additional_context`.
- `beforeShellExecution` / `beforeMCPExecution` output: `permission` (`allow`|`deny`|`ask`),
  `user_message`, `agent_message`.
- `beforeReadFile` output: `permission`, `user_message`. `afterFileEdit` input includes
  `file_path` and `edits` (array of `old_string`/`new_string`).
- `subagentStart` output: `permission`, `user_message`. `subagentStop` output: `followup_message`
  (consumed only when `status == "completed"`).

Which events fire when:
- Startup: `sessionStart` (and `workspaceOpen` on workspace open). [VERIFIED from docs](https://cursor.com/docs/hooks)
- Resume: [INFERRED/UNCERTAIN] - presumably `sessionStart` fires for a resumed session too, but not
  documented. Needs live verification.
- Clear / new conversation: [INFERRED/UNCERTAIN] - whether `sessionStart` re-fires on `/clear` or a
  new conversation is UNDOCUMENTED. Needs live verification.
- Compact: `preCompact` fires (observational only; cannot re-inject context). [VERIFIED from docs](https://cursor.com/docs/hooks)

Can a hook inject model context? YES, via `sessionStart.additional_context` (launch) and
`postToolUse.additional_context` (per tool). [VERIFIED from docs](https://cursor.com/docs/hooks)

IMPORTANT HEADLESS CAVEAT: in `cursor-agent --print` (headless) mode, community reports indicate
that ONLY `sessionStart` fires - the response hooks (`afterAgentResponse`, `afterAgentThought`) and
the other in-loop hooks do NOT fire; the full hook set fires only in the interactive TUI.
[INFERRED/UNCERTAIN - forum-reported, not vendor-confirmed](https://forum.cursor.com/t/hooks-afteragentresponse-afteragentthought-not-firing-in-headless-cli/156220)
This matters: any preamble we depend on landing via hooks in headless mode can only rely on
`sessionStart`.

BENCHMARK vs Claude Code: Claude Code's SessionStart hook fires on startup AND on resume/clear/compact
and can emit `additionalContext` each time, giving a re-injection path on every reset. Cursor's
`sessionStart` + `additional_context` is the near-exact analog for LAUNCH only. The gap is RESET:
whether `sessionStart` re-fires on `/clear` or a new conversation is undocumented, and compaction
exposes only the observational `preCompact` with no re-inject hook. So Cursor matches Claude Code at
launch but has no documented equivalent of Claude's clear/compact re-injection.

## 6. SDK / programmatic API / server mode

No separate SDK package. The programmatic surface is the headless CLI plus its stream-json output.
[VERIFIED from docs](https://cursor.com/docs/cli/headless) The CLI also speaks MCP and ACP (Agent
Client Protocol). [VERIFIED from docs](https://cursor.com/docs/cli/using)

`--output-format` modes (with `--print`) [VERIFIED from docs](https://cursor.com/docs/cli/reference/output-format):
- `text` (default): final assistant message only.
- `json`: a single aggregated JSON object on completion (no intermediate events).
- `stream-json`: newline-delimited JSON (NDJSON), one event per execution step.

stream-json event types and shapes (all [VERIFIED from docs](https://cursor.com/docs/cli/reference/output-format)):
- `{ "type": "system", "subtype": "init", "apiKeySource": "env|flag|login", "cwd": "<abs>",
  "session_id": "<uuid>", "model": "<display name>", "permissionMode": "default" }` - this is where
  the session id, model, cwd, and permission mode arrive.
- `{ "type": "user", "message": { "role": "user", "content": [{"type":"text","text":"<prompt>"}] },
  "session_id": "<uuid>" }`.
- `{ "type": "assistant", "message": { "role": "assistant",
  "content": [{"type":"text","text":"<text>"}] }, "session_id": "<uuid>",
  "timestamp_ms": <n>, "model_call_id": "<id>" }` (last two fields only with `--stream-partial-output`).
- `{ "type": "tool_call", "subtype": "started", "call_id": "<id>",
  "tool_call": { "readToolCall": { "args": {...} } }, "session_id": "<uuid>" }`.
- `{ "type": "tool_call", "subtype": "completed", "call_id": "<id>",
  "tool_call": { "readToolCall": { "args": {...}, "result": { "success": {...} } } }, ... }`.
- `{ "type": "result", "subtype": "success", "duration_ms": <n>, "duration_api_ms": <n>,
  "is_error": false, "result": "<full text>", "session_id": "<uuid>", "request_id": "<opt>" }`.

NOTE: there is NO `clear` / `new` / `compact` event in the stream. The stream only describes a single
run's init -> user -> assistant/tool_call(s) -> result. Context-reset is not represented in
stream-json. [VERIFIED from docs - by absence](https://cursor.com/docs/cli/reference/output-format)

Known headless gotcha: `cursor-agent -p` has been reported to hang indefinitely and never return in
some setups. [INFERRED/UNCERTAIN - forum-reported](https://forum.cursor.com/t/cursor-agent-p-print-headless-mode-hangs-indefinitely-and-never-returns/150246)

## 7. MCP / rules / hooks / plugins

- MCP: managed via the `mcp` subcommand (`login`/`list`/`list-tools`/`enable`/`disable`) and
  auto-detected MCP server config (project `.cursor/mcp.json` and the global `~/.cursor/mcp.json`,
  consistent with the Cursor editor). [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)
  Headless: `--approve-mcps` auto-approves all MCP servers; `beforeMCPExecution`/`afterMCPExecution`
  hooks gate MCP calls. [VERIFIED from docs](https://cursor.com/docs/hooks)
  - [INFERRED/UNCERTAIN] the exact `mcp.json` filename/path for the CLI was not explicitly stated on
    the configuration page (it lists `cli-config.json` and `cli.json`); the editor's `.cursor/mcp.json`
    convention is assumed. Needs verification.
- Rules: `.cursor/rules`, `AGENTS.md`, `CLAUDE.md` (section 4). [VERIFIED from docs](https://cursor.com/docs/cli/using)
- Hooks: `hooks.json` (section 5). [VERIFIED from docs](https://cursor.com/docs/hooks)
- Plugins: `--plugin-dir <path>` (repeatable) loads a local plugin directory; the `workspaceOpen` hook
  can return `pluginPaths`. [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)

## 8. Transcript / history

- Chats are persisted and addressable by id (`--resume [chatId]`, `--continue`, `ls`, `resume`).
  [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)
- On-disk transcript LOCATION and FORMAT are NOT verified. On our prior WSL survey the chat store was
  never created (the agent was "Not logged in"), and on Windows there is no native install to inspect.
  [VERIFIED from our prior survey](D:\ReposFred\devthrottle\docs\design\agent-history-capture\drivers\cursor.md)
- Hooks expose a `transcript_path` (and `CURSOR_TRANSCRIPT_PATH` env var) when transcript recording is
  enabled - a likely future pointer to the on-disk transcript, but its format is unverified.
  [VERIFIED from docs](https://cursor.com/docs/hooks)
- Token usage: not exposed as a clean per-session counter in the docs surveyed; `result` events carry
  `duration_ms`/`duration_api_ms` but not token counts. [INFERRED/UNCERTAIN]
- Consequence for us: `CursorAgentPlugin.History` is `AgentHistoryProviderKind.None`,
  `SupportsConversationHistory = false`. We read the LIVE stream-json stdout instead of any on-disk file.

## 9. Session semantics

- new / resume: each run mints a session id, surfaced in the `system`/`init` event's `session_id`.
  Cursor cannot be told an id in advance - there is no `--session-id` preassign flag; we capture the
  id from the init event (`CursorAgent.SupportsPreassignedSessionId = false`,
  `CursorDriver.TryCaptureSessionId`). [VERIFIED from docs](https://cursor.com/docs/cli/reference/output-format)
- `--resume [chatId]` reloads a prior chat (its context); `--continue` is the alias for
  `--resume=-1` (the previous session). [VERIFIED from docs](https://cursor.com/docs/cli/reference/parameters)
- clear vs compact (interactive): `/summarize` (alias `/compress`) frees context-window space by
  summarizing; there is a `preCompact` hook for compaction. A distinct "clear to an empty new
  conversation" command for the CLI was not explicitly named in the docs surveyed (the editor uses a
  new-chat action). [INFERRED/UNCERTAIN] whether `/clear` exists as a CLI slash command and whether it
  re-fires `sessionStart` is undocumented and needs live verification.
  [VERIFIED for /summarize from docs](https://cursor.com/docs/cli/using)
- The stream-json output carries no clear/new/compact event (section 6), so a reset is invisible to a
  stream consumer; it would have to be inferred from a new `init` event. [VERIFIED by absence](https://cursor.com/docs/cli/reference/output-format)

## 10. How CC Director integrates it

Classes and declared capabilities (current state in this repo):
- Driver `CursorDriver` (`src/CcDirector.Core/Drivers/CursorDriver.cs`): `Kind = AgentKind.Cursor`;
  `Capabilities = DriverCapabilities.Interrupt` ONLY. Interrupt sends Ctrl+C (`0x03`).
  - `ClearContext` is NOT declared - `ClearContextAsync` throws `NotSupportedException` ("no verified
    in-place context-clear command").
  - `CancelAsync` (soft-cancel) throws - keystroke not live-verified (assumption A4).
  - `ShowHistoryAsync` throws - no verified in-terminal history picker.
  - `TranscriptRead` is NOT declared - `ReadWidgets`/`ReadUsage`/`ListTranscripts` all throw. Instead
    the Director parses `cursor-agent` stdout stream-json LIVE: `TryCaptureSessionId` reads the
    `system`/`init` (or bare `init`) `session_id`; `ParseStreamLine` maps `assistant` / `tool_call` /
    `result` events into `TurnWidgetDto` cards.
  - `ResolveExecutable` and `BuildLaunchSpec` on the driver throw - launch is owned by the
    Director's `CursorAgent` path, not the driver.
- Agent `CursorAgent` (`src/CcDirector.Core/Agents/CursorAgent.cs`): `ExecutablePath = options.CursorPath`;
  `SupportsPreassignedSessionId = false`; `SupportsStudioMode = true`. Studio mode prepends
  `-p --output-format stream-json`; resume adds `--resume="<chat-id>"`.
- Plugin `CursorAgentPlugin` (`src/CcDirector.Core/AgentPlugins/CursorAgentPlugin.cs`):
  built-in; detection candidate `cursor-agent` on PATH; validation `--version` (8s);
  `History = AgentHistoryProviderKind.None`, `SupportsConversationHistory = false`;
  `Launch = (SupportsPreassignedSessionId: false, SupportsStudioMode: true)`; presets Standard ("")
  and Automatic (`CursorForceArg`, i.e. `--force`/auto-approve).

Hosting reality: the `cursor-agent` binary is NOT installed on our dev machine and there is no native
Windows build, so spawn attempts fail with "CreateProcess failed". The Cursor history driver is
formally DEFERRED in `docs/design/agent-history-capture/drivers/cursor.md` - not for lack of a
transcript, but because there is nothing native to host on Windows (it would require launching through
`wsl.exe` and reading across `\\wsl$`, a platform decision, not a per-driver one).

Fleet-preamble strategy family: A (shell-hook). Concrete plan to inject the fleet preamble:
- Write a `hooks.json` with a `sessionStart` hook (project `.cursor/hooks.json` or user
  `~/.cursor/hooks.json`) whose command returns `{ "additional_context": "<fleet preamble>" }` on
  stdout. This lands the preamble in the conversation's initial system context at LAUNCH - the
  near-exact analog of Claude Code's SessionStart `additionalContext`.
- Re-injection on reset is the open problem and MUST be live-verified on an installed build:
  1. Does `sessionStart` re-fire on `/clear` or a new conversation? UNDOCUMENTED. If yes, the same
     hook re-injects for free; if no, there is no documented re-inject path on clear.
  2. Compaction has only the observational `preCompact` (no `additional_context` return), so there is
     no documented re-inject after compaction; rely on rules files (`AGENTS.md` / `CLAUDE.md` /
     `.cursor/rules`) persisting through compaction as the durable fallback - which itself needs
     verification (section 4).
  3. The headless caveat: in `--print` mode only `sessionStart` is reported to fire, so in headless
     mode the hook-based preamble can only count on `sessionStart`; the full hook set firing on the
     interactive TUI also needs live verification.
- As a belt-and-suspenders durable channel, also place the fleet preamble in a rules file
  (`AGENTS.md` / `CLAUDE.md` / `.cursor/rules`) so it is read at the start of context regardless of
  hooks.

Current gaps: no installed binary to verify against; clear/compact re-fire behavior unknown;
headless-vs-TUI hook-set difference unconfirmed; on-disk transcript location/format unknown (we rely
on live stream-json only).

## 11. Caveats and verification needed

- HIGH CHURN: the CLI is dated 2026 and changing fast (changelog Jan 2026 added "CLI Agent Modes and
  Cloud Handoff"); flag names and hook schemas may drift. Re-check before relying on any specific field.
- NOT installed here / no native Windows build - nothing in sections 4, 5, 8, 9 marked
  [INFERRED/UNCERTAIN] has been live-confirmed on our platform.
- Needs live verification on an installed build:
  1. Does `sessionStart` re-fire on `/clear` / new conversation and on `--resume`? (the reset gap)
  2. Headless `--print`: confirm only `sessionStart` fires and the rest do not (forum-reported only).
  3. Whether a CLI `/clear` slash command exists at all, and what it does to the session id.
  4. On-disk transcript path/format (and whether `transcript_path` points to a parseable file).
  5. Exact CLI `mcp.json` path (assumed `.cursor/mcp.json` from the editor).
  6. Rules files (`AGENTS.md`/`CLAUDE.md`/`.cursor/rules`) surviving `/clear` and `/compact`.
  7. `cursor-agent -p` hang reports - confirm a reliable non-hanging invocation in our harness.
- Field-name notes: our `CursorDriver.ParseStreamLine` defensively accepts several alternative
  property names for `tool_call` (`tool`/`name`, `tool_call_id`/`id`, `command`/`input`,
  `result`/`output`) that the docs' canonical shape (`tool_call.readToolCall`/`writeToolCall`) does
  NOT use - this is hedging against build-to-build drift and should be reconciled against a real
  stream once installed.

## Sources

- Cursor CLI overview: https://cursor.com/docs/cli/overview
- Using Agent in CLI: https://cursor.com/docs/cli/using
- Using Headless CLI: https://cursor.com/docs/cli/headless
- Output format reference (stream-json): https://cursor.com/docs/cli/reference/output-format
- Parameters reference (flags/subcommands): https://cursor.com/docs/cli/reference/parameters
- Configuration reference: https://cursor.com/docs/cli/reference/configuration
- Hooks: https://cursor.com/docs/hooks
- Forum (headless hooks caveat): https://forum.cursor.com/t/hooks-afteragentresponse-afteragentthought-not-firing-in-headless-cli/156220
- Forum (-p hang report): https://forum.cursor.com/t/cursor-agent-p-print-headless-mode-hangs-indefinitely-and-never-returns/150246
- Changelog (CLI Agent Modes and Cloud Handoff, Jan 16 2026): https://cursor.com/changelog/cli-jan-16-2026
- Our prior survey: D:\ReposFred\devthrottle\docs\design\agent-history-capture\drivers\cursor.md
