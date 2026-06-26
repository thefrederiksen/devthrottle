<!--
Per-agent reference for the agent-expert skill. Internal, not shipped.
ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with an inline source.
All source links are collected in the Sources section.
-->

# OpenAI Codex CLI (AgentKind.Codex)

> OpenAI's terminal coding agent. npm package `@openai/codex`, binary `codex`.
> Our integration status: CodexDriver / CodexAgent / CodexAgentPlugin, with a
> CodexTranscriptReader that exists but is NOT yet wired as a declared driver capability.
> Mechanism family A (shell-command hook). Closest twin to the wired Claude Code path.

This file describes how Codex behaves and how CC Director drives it. Codex hooks and the
App Server are recent and churning; every field name below should be re-checked live against
the installed binary before we depend on it (see section 11).

---

## 1. Identity and install

- Binary name: `codex`. [VERIFIED from docs] https://developers.openai.com/codex/cli/reference
- npm package: `@openai/codex` (also a standalone native installer). [VERIFIED from docs] https://github.com/openai/codex
- Source repository: https://github.com/openai/codex (the CLI engine is Rust under `codex-rs/`).
  [VERIFIED from docs] https://github.com/openai/codex/blob/main/codex-rs/core/src/config/mod.rs
- Official documentation home: https://developers.openai.com/codex [VERIFIED from docs]
- Codex home directory: `~/.codex` by default, overridable with the `CODEX_HOME` environment
  variable. [VERIFIED from docs] https://developers.openai.com/codex/guides/agents-md
- Windows install location of the launchable shim (npm global install): our plugin probes
  `%APPDATA%\npm\codex.cmd` first, then bare `codex` on PATH.
  [VERIFIED from source] src/CcDirector.Core/AgentPlugins/CodexAgentPlugin.cs (DefaultNpmCliPath)
- Version-probe command: `codex --version`. Our validation metadata uses `--version` with an
  8-second timeout. [VERIFIED from source] CodexAgentPlugin.ValidationMetadata

---

## 2. Command-line interface

Codex has an interactive terminal UI and a non-interactive `exec` mode, plus `resume` and an
`app-server` subcommand.

### Modes

- `codex` - interactive terminal UI (the default). Accepts global flags and an optional initial
  prompt / image attachments. This is the mode CC Director launches into a ConPTY.
  [VERIFIED from docs] https://developers.openai.com/codex/cli/reference
- `codex exec` (alias `codex e`) - non-interactive / scripted. Runs a task without human
  interaction and streams results to stdout or JSONL. This is the headless equivalent of Claude's
  `--print`. CC Director does NOT use exec mode today. [VERIFIED from docs] (same)
- `codex resume` - continue a previous interactive session by id, or resume the most recent.
  [VERIFIED from docs] (same)
- `codex app-server` - JSON-RPC server mode (see section 6). [VERIFIED from docs]
  https://developers.openai.com/codex/app-server

### Flag table (global unless noted)

| Flag | Values | Purpose | Notes |
|------|--------|---------|-------|
| `--model`, `-m` | string | Override configured model | [VERIFIED from docs] cli/reference |
| `--profile`, `-p` | profile name | Layer a named profile from config.toml | [VERIFIED from docs] |
| `--config`, `-c` | key=value | Override any config value, repeatable | [VERIFIED from docs] |
| `--cd`, `-C` | path | Set working directory before processing | [VERIFIED from docs] |
| `--sandbox`, `-s` | read-only / workspace-write / danger-full-access | Sandbox policy for shell commands | [VERIFIED from docs] |
| `--ask-for-approval`, `-a` | untrusted / on-request / never | Approval timing | [VERIFIED from docs] |
| `--json` | (boolean) | Emit newline-delimited JSON events | [VERIFIED from docs] |
| `--output-last-message`, `-o` | path | Write the final assistant message to a file | [VERIFIED from docs] |
| `--output-schema` | path | JSON Schema to validate the response shape | [VERIFIED from docs] |
| `--ephemeral` | (exec) | Run without persisting session files | [VERIFIED from docs] |
| `--ignore-rules` | (exec) | Skip execpolicy rule files | [VERIFIED from docs] |
| `--image`, `-i` | path | Attach images to the initial message | [VERIFIED from docs] |
| `--last` | (resume) | Skip the picker, continue the most recent session | [VERIFIED from docs] |
| `--all` | (resume) | Include sessions outside the current directory | [VERIFIED from docs] |

### Session id / resume

- There is a documented `codex resume <SESSION_ID>` and `codex resume --last`.
  [VERIFIED from docs] https://developers.openai.com/codex/cli/reference
- A top-level `--session-id` flag for the interactive `codex` command is NOT confirmed in the
  public CLI reference. Resume is done by the `resume` subcommand (by id or `--last`), and the
  App Server resumes by thread id via `thread/resume`. [INFERRED/UNCERTAIN] - we do not pass a
  preassigned session id; CodexDriver.BuildLaunchSpec explicitly ignores any resume id and
  returns PreassignedSessionId=null. [VERIFIED from source] CodexDriver.BuildLaunchSpec
- Implication for CC Director: Codex owns its own session id; we cannot pre-mint one the way
  Claude allows `--session-id`. This is why CodexAgent.SupportsPreassignedSessionId == false.
  [VERIFIED from source] CodexAgent

### Output formats

- `--json` produces newline-delimited JSON events, one per state change (the structured stream
  for exec and json mode). [VERIFIED from docs] cli/reference and the AGENTS.md guide search.
- `--output-last-message` writes just the final assistant text to a file; `--output-schema`
  enforces a JSON Schema on the structured result. [VERIFIED from docs] cli/reference

---

## 3. Configuration

- Primary config file: `~/.codex/config.toml` (under `CODEX_HOME`). [VERIFIED from docs]
  https://developers.openai.com/codex/config-reference
- Project override: `.codex/config.toml` inside a repo, loaded only for trusted projects.
  [VERIFIED from docs] (same)
- Profiles: `[profiles.NAME]` tables in config.toml, or separate `$CODEX_HOME/NAME.config.toml`
  files; selected with `--profile NAME`. [VERIFIED from docs] config-reference
- System config (Unix): `/etc/codex/config.toml`. [INFERRED/UNCERTAIN] - reported by the
  developer docs summary but Windows path not stated. https://developers.openai.com/codex/config-reference

### Precedence (highest to lowest) [VERIFIED from docs] config-reference

1. CLI flags and `--config` overrides
2. Project config files (`.codex/config.toml`, closest directory wins)
3. Profile files (`~/.codex/NAME.config.toml` or `[profiles.NAME]`)
4. User config (`~/.codex/config.toml`)
5. System config (`/etc/codex/config.toml` on Unix)
6. Built-in defaults

Project-scoped config CANNOT override provider, auth, notification, profile selection, or
telemetry settings. [VERIFIED from docs] config-reference

### Relevant keys [VERIFIED from docs] config-reference unless noted

- `model` - model id, e.g. `model = "gpt-5.5"`.
- `model_reasoning_effort` - `minimal | low | medium | high | xhigh`.
- `approval_policy` - `untrusted | on-request | never`.
- `sandbox_mode` - `read-only | workspace-write | danger-full-access`.
- `web_search` - `cached | live | disabled`. [VERIFIED from docs] config-reference (local-config page)
- `[features]` table - enable experimental capabilities (e.g. `shell_snapshot`, `memories`,
  `codex_git_commit`, `hooks`). [VERIFIED from docs] local-config and hooks pages.
- `model_instructions_file` - path to a custom instructions file that REPLACES the built-in
  default instructions. [VERIFIED from docs] config-reference, agents-md guide.
- `project_doc_fallback_filenames` - extra filenames searched when `AGENTS.md` is missing.
  [VERIFIED from docs] config-reference, agents-md guide.
- `project_doc_max_bytes` - max bytes read from the instruction chain; default 32 KiB. Codex
  stops adding files once the limit is reached. [VERIFIED from docs] agents-md guide.
- `notify` - argv array of an external notification program (see section 5, post-turn only).
  [VERIFIED from docs] config-advanced.
- `[hooks]` - inline hook configuration (see section 5). [VERIFIED from docs] config-reference, hooks.
- `[mcp_servers.<id>]` - MCP server definitions with `command`, `args`, env, `enabled_tools` /
  `disabled_tools`, `startup_timeout_sec`, `tool_timeout_sec`. [VERIFIED from docs] config-reference.
- `allow_managed_hooks_only` - top-level key set in `requirements.toml` (the managed/enterprise
  layer) to ignore user, project, and session hook configs while still allowing managed hooks.
  [VERIFIED from docs] https://github.com/openai/codex/blob/main/docs/config.md and hooks page.

---

## 4. Context injection (how to inject a preamble)

For each mechanism: does the injected text survive `/clear`, and does it survive `/compact`?

### a. AGENTS.md hierarchy (instruction files) - mechanism family D, passive

- Discovery order [VERIFIED from docs] https://developers.openai.com/codex/guides/agents-md :
  1. Global: `~/.codex/AGENTS.override.md` if present, else `~/.codex/AGENTS.md` (first non-empty
     file at this level only).
  2. Project: walk from the Git root down to the current working directory; in EACH directory
     check `AGENTS.override.md`, then `AGENTS.md`, then any name in `project_doc_fallback_filenames`.
- Concatenation: files are joined root-to-current with blank lines; files CLOSER to the current
  directory appear later and therefore override earlier guidance. [VERIFIED from docs] agents-md.
- `AGENTS.override.md` is for a temporary override without deleting the base `AGENTS.md`.
  [VERIFIED from docs] agents-md.
- Size cap: `project_doc_max_bytes` (default 32 KiB); empty files skipped. [VERIFIED from docs] agents-md.
- The instruction chain is built "once per run; in the TUI this usually means once per launched
  session." [VERIFIED from docs] agents-md.
- Survives /clear? [INFERRED/UNCERTAIN] - because AGENTS.md is part of the persistent base
  instruction chain (not turn conversation), it should remain in effect after `/clear`, but the
  docs do not state /clear behavior explicitly. Note open issue #21675 asks for a per-session
  cache of additionalContext for "InstructionsLoaded parity," implying current re-load behavior
  is in flux. https://github.com/openai/codex/issues/21675
- Survives /compact? [INFERRED/UNCERTAIN] - same reasoning; the base instructions are not the
  conversation that gets compacted, so they should persist, but this is not documented. Verify live.

### b. model_instructions_file - mechanism family D, passive

- Replaces the built-in default instructions entirely. [VERIFIED from docs] config-reference, agents-md.
- Survives /clear and /compact? [INFERRED/UNCERTAIN] - it is base instruction text, so the same
  caveat as AGENTS.md applies. Verify live.

### c. SessionStart hook additionalContext - mechanism family A, active (PREFERRED)

- A SessionStart command hook can print `hookSpecificOutput.additionalContext`, which Codex adds
  as extra developer context. [VERIFIED from docs] https://developers.openai.com/codex/hooks
- The SessionStart `source` matcher fires with `startup`, `resume`, `clear`, and `compact`
  (see section 5). [VERIFIED from docs] hooks.
- Survives /clear? Yes by re-firing: SessionStart fires again with `source = "clear"`, so the hook
  re-emits additionalContext after a clear. [VERIFIED from docs] hooks.
- Survives /compact? Yes by re-firing: SessionStart fires again with `source = "compact"`. There
  are ALSO dedicated PreCompact / PostCompact events. [VERIFIED from docs] hooks.
- This is the family A path and the one we should wire (section 10). It is the direct analogue of
  Claude Code's SessionStart additionalContext.

Summary: AGENTS.md / model_instructions_file are passive and probably persist but are
undocumented for /clear and /compact; the SessionStart hook is the active, event-driven injector
that demonstrably re-fires on clear and compact.

---

## 5. Lifecycle events and hooks

Codex has a Claude-Code-style hook framework. This is the heart of the integration plan.

### Event list [VERIFIED from docs] https://developers.openai.com/codex/hooks

1. `SessionStart` - session initialization (thread scope).
2. `SubagentStart` - a subagent begins (subagent-start scope).
3. `PreToolUse` - before a tool call executes (turn scope).
4. `PermissionRequest` - when approval is needed (turn scope).
5. `PostToolUse` - after a tool completes (turn scope).
6. `PreCompact` - before conversation compaction (turn scope).
7. `PostCompact` - after conversation compaction (turn scope).
8. `UserPromptSubmit` - when the user sends a prompt (turn scope).
9. `SubagentStop` - when a subagent concludes (turn scope).
10. `Stop` - when a conversation turn stops (turn scope).

### SessionStart `source` matcher values [VERIFIED from docs] hooks

The `source` field on SessionStart is one of: `"startup"`, `"resume"`, `"clear"`, `"compact"`.
This is the same set Claude Code uses, which is what makes Codex the closest twin. A `matcher`
like `"startup|resume"` selects which sources trigger the hook.

### Which events fire when

- On startup (fresh launch): `SessionStart` with `source = "startup"`. [VERIFIED from docs] hooks
- On resume (`codex resume`): `SessionStart` with `source = "resume"`. [VERIFIED from docs] hooks
- On `/clear` (new conversation in place): `SessionStart` with `source = "clear"`. [VERIFIED from docs] hooks
- On `/compact`: `SessionStart` with `source = "compact"`, plus the dedicated `PreCompact` and
  `PostCompact` events. [VERIFIED from docs] hooks

### Config format and location [VERIFIED from docs] hooks

Discovery order (precedence high to low):
1. `~/.codex/hooks.json` (user)
2. `~/.codex/config.toml` with a `[hooks]` table (user)
3. `<repo>/.codex/hooks.json` (project)
4. `<repo>/.codex/config.toml` with `[hooks]` (project)
5. Plugin-bundled `hooks/hooks.json`

Merge behavior: higher-precedence layers do NOT replace lower-precedence hooks; ALL matching
hooks from all layers run concurrently. If a single layer has both `hooks.json` and inline
`[hooks]`, Codex merges them and warns at startup ("prefer one representation per layer").
[VERIFIED from docs] hooks

Trust: non-managed command hooks require review before they execute. Use the `/hooks` command to
inspect, review, and trust them. Managed hooks from `requirements.toml` / MDM bypass this flow.
[VERIFIED from docs] hooks. NOTE open issue #17532 reports hooks not firing in interactive
sessions when configured via repo-local `.codex/config.toml` - so prefer `~/.codex/hooks.json`
(user layer) for reliability. https://github.com/openai/codex/issues/17532

`allow_managed_hooks_only = true` in `requirements.toml` makes ONLY managed hooks execute; user,
project, session, and plugin hooks are skipped. [VERIFIED from docs]
https://github.com/openai/codex/blob/main/docs/config.md

### stdin JSON (what a hook receives) [VERIFIED from docs] hooks

Common input fields across events:
```json
{
  "session_id": "string",
  "transcript_path": "string|null",
  "cwd": "string",
  "hook_event_name": "string",
  "model": "string",
  "turn_id": "string",
  "permission_mode": "default|acceptEdits|plan|dontAsk|bypassPermissions"
}
```
For SessionStart the payload also carries the `source` value (startup/resume/clear/compact).
[VERIFIED from docs] hooks

### stdout JSON (what a hook returns) [VERIFIED from docs] hooks

Common output fields (supported by SessionStart, PreCompact, PostCompact, UserPromptSubmit,
SubagentStop, Stop):
```json
{
  "continue": true,
  "stopReason": "optional reason",
  "systemMessage": "optional warning",
  "suppressOutput": false,
  "hookSpecificOutput": {}
}
```

### additionalContext field and which events support it [VERIFIED from docs] hooks

`hookSpecificOutput.additionalContext` is supported by:
- `SessionStart` - added as extra developer context. (This is the one we use.)
- `SubagentStart` - surfaces as extra subagent context.
- `PreToolUse` - model-visible context without blocking (not the primary use case).
- `PostToolUse` - added as developer context.
- `UserPromptSubmit` - surfaces as extra context.

Plain text on stdout is also accepted for SessionStart and added as developer context; JSON with
the `hookSpecificOutput` shape is the structured form. [VERIFIED from docs] hooks.

Note: `PreToolUse` and `PermissionRequest` support `systemMessage` but NOT `continue`,
`stopReason`, or `suppressOutput`. [VERIFIED from docs] hooks. Open issue #19385 tracks parity
gaps in PreToolUse additionalContext handling. https://github.com/openai/codex/issues/19385

### Example hooks.json with a SessionStart command hook [VERIFIED from docs] hooks

```json
{
  "hooks": {
    "SessionStart": [
      {
        "matcher": "startup|resume|clear|compact",
        "hooks": [
          {
            "type": "command",
            "command": "python3 ~/.codex/hooks/session_start.py",
            "statusMessage": "Loading fleet preamble",
            "timeout": 30
          }
        ]
      }
    ]
  }
}
```

Inline TOML equivalent of a hook (for the `[hooks]` form) [VERIFIED from docs] config-advanced:
```toml
[[hooks.PreToolUse]]
matcher = "^Bash$"

[[hooks.PreToolUse.hooks]]
type = "command"
command = '/usr/bin/python3 "$(git rev-parse --show-toplevel)/.codex/hooks/pre_tool_use_policy.py"'
timeout = 30
statusMessage = "Checking Bash command"
```

### Benchmark against Claude Code [VERIFIED from docs] hooks

Claude Code's SessionStart hook uses matchers `startup | resume | clear | compact` and emits
`hookSpecificOutput.additionalContext` that Claude injects. Codex is functionally IDENTICAL:
same four `source` values on SessionStart, same `hookSpecificOutput.additionalContext` field
name, same re-fire on clear and compact. The only differences are file location
(`~/.codex/hooks.json` vs `~/.claude/settings.json` hooks block) and the field-name casing in the
stdin payload (Codex uses snake_case `session_id`, `hook_event_name`, `turn_id`). The hook output
contract is the camelCase Claude shape (`hookSpecificOutput.additionalContext`).

### The older `notify` program - why it is NOT the answer

`notify` is an argv array in config.toml (e.g. `notify = ["notify-send", "Codex"]`). Codex
appends one JSON argument describing the event. It currently fires for ONLY ONE event,
`agent-turn-complete`, i.e. AFTER a turn finishes. [VERIFIED from docs] config-advanced.
Payload example:
```json
{
  "type": "agent-turn-complete",
  "turn-id": "12345",
  "input-messages": ["Rename foo to bar and update the callsites."],
  "last-assistant-message": "Rename complete and cargo build succeeds."
}
```
Why `notify` is NOT how we inject a preamble:
- It is post-turn only - it cannot run at SessionStart, resume, clear, or compact.
- It is notification-only - it cannot return `additionalContext` or modify the model's context;
  its stdout/return value is ignored. [VERIFIED from docs] config-advanced.
- It is one-directional alerting (toasts, webhooks). The hook framework superseded it for
  context injection. Use SessionStart hooks, not `notify`.

---

## 6. SDK / programmatic API / server mode (App Server)

- `codex app-server` exposes JSON-RPC 2.0. Transports: `stdio://` (default, newline-delimited
  JSON), `ws://IP:PORT` (experimental WebSocket), `unix://`, or `off`. Selected with `--listen`.
  [VERIFIED from docs] https://developers.openai.com/codex/app-server
- Handshake: send an `initialize` request first (with `clientInfo`), then emit an `initialized`
  notification. Requests before `initialize` are rejected with "Not initialized".
  [VERIFIED from docs] app-server
- Thread lifecycle methods: `thread/start`, `thread/resume`, `thread/fork`, `thread/read`,
  `thread/list`, `thread/archive`, `thread/unarchive`, `thread/delete`, `thread/unsubscribe`.
  [VERIFIED from docs] app-server
- Turn control: `turn/start` (input array of text/images/skills, plus `model`, `effort`,
  `personality`, `sandboxPolicy`, `approvalPolicy`, `outputSchema`), `turn/steer`,
  `turn/interrupt`. [VERIFIED from docs] app-server
- Notifications streamed by the server: `thread/started`, `turn/started`, `turn/completed`
  (status completed/interrupted/failed), `item/started`, `item/completed`,
  `item/agentMessage/delta`, `thread/status/changed`. [VERIFIED from docs] app-server
- Approvals: server requests `item/commandExecution/requestApproval` and
  `item/fileChange/requestApproval`; client replies accept / decline / cancel / acceptForSession.
  [VERIFIED from docs] app-server
- Also: `config/read`, `config/value/write`, `config/batchWrite`, `model/list`, `fs/watch`,
  `command/exec`, experimental `process/spawn` / `process/writeStdin` / `process/kill`.
  [VERIFIED from docs] app-server
- WebSocket auth: capability tokens or signed bearer tokens (`--ws-auth capability-token
  --ws-token-file PATH`). [VERIFIED from docs] app-server
- CC Director does NOT use the App Server today; we drive the interactive TUI through a ConPTY.
  The App Server is a future option for higher-fidelity drive + structured event subscription
  (it would move Codex toward a family B event-bus integration). [INFERRED] - based on our
  current driver, which is terminal-only.

---

## 7. MCP / extensions / skills

- MCP: configure servers under `[mcp_servers.<id>]` in config.toml (command, args, env,
  `enabled_tools`/`disabled_tools`, timeouts). Codex can also act as an MCP server / client.
  [VERIFIED from docs] config-reference. Manage at runtime via the `/mcp` slash command.
  [VERIFIED from source] CodexSlashCommands.cs
- Agent Skills: invoked in App Server input via `$skill-name` text plus an optional `skill` input
  item. [VERIFIED from docs] app-server. Skills are also referenced in `turn/start` input arrays.
- Apps: mentioned with `$app-slug` and a `mention` input item (`app://id`). [VERIFIED from docs] app-server
- Plugins: can bundle lifecycle config via a manifest or a default `hooks/hooks.json` (the
  lowest-precedence hook layer). [VERIFIED from docs] hooks
- Subagents: `SubagentStart` / `SubagentStop` hook events exist; subagents get their own
  additionalContext via `SubagentStart`. [VERIFIED from docs] hooks, subagents guide.

---

## 8. Transcript / history

- Location: `~/.codex/sessions/<yyyy>/<mm>/<dd>/rollout-<timestamp>-<id>.jsonl`. Example:
  `~/.codex/sessions/2025/01/22/rollout-2025-01-22T10-30-00-abc123.jsonl`. [VERIFIED from docs]
  agents-md search result, and confirmed by our reader.
  [VERIFIED from source] CodexRolloutLocator.cs, CodexTranscriptReader.cs
- Format: JSON Lines (one JSON object per line). Each line has a `type` and a `payload`:
  - `session_meta` - first line; carries `cwd` and a `timestamp` (we key on `cwd` to match a
    rollout to a Director session). [VERIFIED from source] CodexRolloutLocator.cs
  - `turn_context` - per-turn settings. Skipped by our reader.
  - `event_msg` - a parallel pre-cleaned event stream. Skipped by our reader.
  - `response_item` - the actual conversation. Its `payload.type` is one of `message`
    (role user/assistant/developer/system, content array of text items), `function_call`
    (name, call_id, arguments), `custom_tool_call` (name, call_id, input),
    `function_call_output` / `custom_tool_call_output` (call_id, output), `reasoning`
    (summary text; `encrypted_content` has no plaintext and is skipped).
    [VERIFIED from source] CodexTranscriptReader.cs (this is our live, tested mapping)
- Codex appends to the rollout live; reads must use shared read access and tolerate a truncated
  final line. [VERIFIED from source] CodexTranscriptReader.cs
- `--ephemeral` (exec) runs without persisting a rollout file. [VERIFIED from docs] cli/reference
- Token usage: the rollout has `turn_context` and usage data, but our reader does NOT extract it;
  CodexDriver.ReadUsage throws NotSupported. [VERIFIED from source] CodexDriver.cs
- Parseable: yes. We do not yet expose it as a declared driver capability (see section 10).

---

## 9. Session semantics

- new / `/new` - start a fresh conversation. [VERIFIED from source] CodexSlashCommands.cs
- `/clear` - clear the current conversation in place. Fires SessionStart with `source = "clear"`.
  [VERIFIED from docs] hooks. This is what CC Director submits for ClearContext.
- `/compact [instructions]` - summarize and shrink the conversation. Fires PreCompact, then
  SessionStart `source = "compact"`, then PostCompact. [VERIFIED from docs] hooks
- `/resume` and `/fork` - resume or branch a saved session (interactive). `codex resume <id>` /
  `--last` from the CLI; `thread/resume` / `thread/fork` in the App Server. [VERIFIED from docs]
  cli/reference, app-server. [VERIFIED from source] CodexSlashCommands.cs
- Session id behavior: Codex mints and owns the session/thread id; the rollout filename embeds it.
  CC Director cannot pre-assign one, so we resolve the rollout AFTER launch by scanning
  `~/.codex/sessions` for the newest `rollout-*.jsonl` whose `session_meta.cwd` matches the repo,
  scoped to a launch-time window so a new terminal does not bind to an older rollout.
  [VERIFIED from source] CodexRolloutLocator.cs
- Across clear/compact the rollout file generally continues (same session), so our cwd-newest
  heuristic stays valid. [INFERRED/UNCERTAIN] - confirm that `/clear` does not start a NEW rollout
  file; if it does, the locator's newest-for-cwd logic still finds it, but session-id continuity
  should be verified live.

---

## 10. How CC Director integrates it

### Current classes

- Driver: `CodexDriver` (src/CcDirector.Core/Drivers/CodexDriver.cs). Declared capabilities:
  `Cancel | Interrupt | ClearContext`. Cancel = send Esc (0x1B); Interrupt = send Ctrl+C (0x03);
  ClearContext = submit the literal `/clear`. ShowHistory, ReadWidgets, ReadUsage, and
  ListTranscripts all throw NotSupported. ModelFlag is empty and KnownModels is empty (we do not
  drive `--model`). [VERIFIED from source] CodexDriver.cs
- Agent: `CodexAgent` (src/CcDirector.Core/Agents/CodexAgent.cs). SupportsPreassignedSessionId =
  false, SupportsStudioMode = false. BuildLaunchSpec passes through user args; ignores resume id
  and studio mode. [VERIFIED from source] CodexAgent.cs
- Plugin: `CodexAgentPlugin` (src/CcDirector.Core/AgentPlugins/CodexAgentPlugin.cs). Id "codex",
  built-in. History provider kind = `TranscriptFile`, SupportsConversationHistory = true. Detection
  probes `%APPDATA%\npm\codex.cmd` then `codex`. Validation `--version`. Command presets: Standard
  (empty) and a Codex full-access preset. [VERIFIED from source] CodexAgentPlugin.cs
- Slash commands: `CodexSlashCommands` (captured 2026-06-18) - /help, /status, /model, /approvals,
  /permissions, /sandbox, /new, /clear, /compact, /init, /diff, /review, /mention, /mcp, /resume,
  /fork, /quit. [VERIFIED from source] CodexSlashCommands.cs
- Transcript: `CodexTranscriptReader` + `CodexRolloutLocator` exist and are tested, surfaced
  through the Director History tab / SessionHistoryReader. [VERIFIED from source]

### Gap: TranscriptRead not declared on the driver

`CodexTranscriptReader` is fully functional, but `CodexDriver.Capabilities` does NOT include a
TranscriptRead flag, and the driver's transcript methods throw NotSupported (history is read via
the separate SessionHistoryReader path, not the driver). Consequence: cross-agent "ask another
session and read its reply" (cc-ask, which needs TranscriptRead on the driver) does not work for
Codex yet, even though the reader exists. Wiring the existing reader into a declared
TranscriptRead capability is a low-risk follow-up. [VERIFIED from source] CodexDriver.cs + README.md

### Mechanism family and fleet-preamble plan

Codex is family A (shell-command hook) and is the closest twin to the already-wired Claude
implementation. The shared, agent-agnostic Director endpoint already exists:
`GET /sessions/{sid}/fleet-preamble`. [VERIFIED from source] README.md (agents folder).

Concrete plan to register a SessionStart hook that injects the fleet preamble:

1. On Codex session launch, write (or ensure) a SessionStart hook in the USER layer
   `~/.codex/hooks.json` (user layer, not repo-local, to dodge issue #17532 where repo-local
   `.codex/config.toml` hooks do not fire in interactive sessions). Use matcher
   `"startup|resume|clear|compact"` so it re-fires on every reset that matters.
2. The hook command is a small Director-owned helper (a script or a `cc-*` shim) that:
   - reads the Director session id. The hook stdin gives Codex's `session_id`, `cwd`, and
     `source`; we map cwd + launch window to OUR Director session id (the same mapping
     CodexRolloutLocator already does), OR we stamp the Director session id into an env var at
     launch and the helper reads it.
   - calls `GET http://127.0.0.1:<controlPort>/sessions/{sid}/fleet-preamble`.
   - prints JSON to stdout:
     ```json
     {"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"<preamble>"}}
     ```
3. Codex injects that `additionalContext` as developer context at startup AND re-injects it on
   `/clear` (source=clear) and `/compact` (source=compact) automatically, because SessionStart
   re-fires on those sources. No extra wiring is needed for reset re-injection - this is the same
   property we rely on for Claude. [VERIFIED from docs] hooks.
4. Trust: the hook must be trusted once (Codex's `/hooks` review flow) unless deployed as a managed
   hook. For an installed-fleet scenario, consider the managed-hooks path (`requirements.toml`,
   `allow_managed_hooks_only` if we want to lock it down) so the Director hook is trusted without
   per-user prompts. [VERIFIED from docs] hooks, config.md.

Field-name parity to exploit: the hook OUTPUT contract is byte-for-byte the Claude shape
(`hookSpecificOutput.additionalContext`), so the preamble emitter we already have for Claude can
be reused almost verbatim; only the hook REGISTRATION location and the stdin field casing
(snake_case) differ. [VERIFIED from docs] hooks.

### Current gaps

- DONE: the SessionStart fleet-preamble hook is wired (CodexHookInstaller + SessionManager flag) and
  the programmatic submit bug is fixed (CodexDriver echo-verified submit). See section 11.
- TranscriptRead capability is not declared, so cc-ask does not reach Codex.
- We do not drive `--model` (ModelFlag empty), so model selection is whatever config.toml / the
  user picks via `/model`.
- We do not preassign or resume session ids from the Director side.

---

## 11. Caveats and verification needed

### LIVE FINDING 2026-06-26 (codex 0.141.0 on SOREN_NORTH) - Family A CONFIRMED WORKING

Verified the SessionStart fleet-preamble hook end to end against the installed binary. Family A is
viable for Codex; the earlier "interactive TUI does not fire" observation was a symptom of a
separate, now-fixed submit bug.

- `hooks` is a STABLE feature, enabled by default (`codex features list` -> `hooks stable true`).
- A user-layer `~/.codex/hooks.json` SessionStart hook (matcher `startup|resume|clear|compact`)
  running our PowerShell preamble emitter, launched with `--dangerously-bypass-hook-trust`,
  FIRES and INJECTS in both `codex exec` AND the interactive TUI that CC Director drives. PROOF:
  the Codex rollout (`~/.codex/sessions/.../rollout-*.jsonl`) contains a `response_item` with
  `role: "developer"` carrying the full preamble ("[CC Director fleet] You are session ... cc-sessions
  ... cc-send ..."), and the assistant then answered the test prompt. So additionalContext from the
  hook genuinely lands in Codex's model context.
- TIMING: in the interactive TUI, SessionStart fires at the FIRST TURN, not at idle process startup
  (the preamble developer message is timestamped ~4 s after launch, when the first prompt ran).
  Contrast Claude, which fires SessionStart at startup before any turn. Practical effect: the Codex
  preamble is present from the first turn onward, which is what matters; a session that never takes
  a turn simply never needed it.
- The env-var mapping is identical to Claude: the hook reads the Director-injected CC_DIRECTOR_API +
  CC_SESSION_ID and fetches `GET /sessions/{sid}/fleet-preamble`. No cwd/rollout mapping needed.

THE BLOCKER WAS A SUBMIT BUG (now fixed). The Director's REST prompt path drove Codex with the
backend's blind submit (type text, wait 50 ms, one Enter). Codex's composer repaints a cycling
placeholder, so that single Enter landed mid-repaint and was swallowed - the prompt parked
unsubmitted, no turn ran, so SessionStart never fired. Fix: `CodexDriver.SubmitAsync` now does an
ECHO-VERIFIED submit (type the text, wait until the composer echoes it, then a SEPARATE Enter), the
same pattern ClaudeDriver uses. Covered by CodexDriverTests
(`SubmitAsync_EchoVerified_TypesTextThenPressesEnterSeparately`,
`SubmitAsync_NoBufferBackend_FallsBackToBlindSendText`). The desktop UI never had this bug because
there Enter is a separate, later human keystroke.

WIRED 2026-06-26 (verified end to end). `CodexHookInstaller` (src/CcDirector.Core/Codex/) merges the
SessionStart hook into `~/.codex/hooks.json` on Codex launch (preserving the user's own hooks,
idempotent, atomic write, never clobbers a malformed file), and SessionManager appends
`CodexHookInstaller.BypassTrustFlag` (`--dangerously-bypass-hook-trust`) to the Codex command - the
analog of how Claude passes `--settings`. Proven: spawning a Codex session with NO manual args
auto-created hooks.json, the Director logged the install, a turn ran (thanks to the submit fix), and
the rollout carried the preamble as a `role:"developer"` context item. Tests: CodexHookInstallerTests
(4) + CodexDriverTests submit cases (2).

Open design point (NOT a blocker): the hooks.json is GLOBAL to the user's Codex (no `--settings`-style
scoping). It no-ops outside Director sessions (no CC_SESSION_ID) and only runs un-prompted because the
Director passes the bypass flag; the user's own non-Director Codex sessions would see an un-trusted
hook. For a shipped product, consider the managed-hooks path (`requirements.toml`) instead of writing
the user's personal config.

CLEAR / COMPACT RE-FIRE: VERIFIED LIVE 2026-06-26 (codex 0.141.0, interactive TUI). One session,
counting preamble injections (each carries "You are session <shortid>"): turn 1 = 1 injection;
after `/clear` a NEW rollout appeared with a 2nd injection; after `/compact` a 3rd injection landed
in the post-clear rollout. So SessionStart re-fires on both `source=clear` and `source=compact` and
the preamble re-injects automatically after every context reset, exactly like Claude. Nothing left to
verify on the Codex preamble path.

### Other items to confirm live

- Codex hooks are recent and churning. Confirm live against the installed binary:
  - That SessionStart actually re-fires with `source = "clear"` and `source = "compact"` in the
    interactive TUI (docs say so; verify). [VERIFIED from docs] but unverified on our binary.
  - That a USER-layer `~/.codex/hooks.json` SessionStart hook fires in an interactive session
    (issue #17532 reports repo-local config hooks NOT firing). https://github.com/openai/codex/issues/17532
  - The exact stdin field casing (`session_id`, `hook_event_name`, `turn_id`, `permission_mode`)
    and whether `source` is top-level on the SessionStart payload. [VERIFIED from docs] hooks; verify live.
  - Whether plain-text stdout vs the JSON `hookSpecificOutput` form is honored for additionalContext
    on our version. [VERIFIED from docs] hooks; verify live.
- `--session-id` as a top-level interactive flag is NOT confirmed; treat resume as
  `codex resume <id>`/`--last` and App Server `thread/resume` only. [INFERRED/UNCERTAIN]
- Whether AGENTS.md / model_instructions_file survive `/clear` and `/compact` is undocumented;
  open issue #21675 (per-session cache for additionalContext) suggests instruction reload behavior
  is in flux. Verify live. https://github.com/openai/codex/issues/21675
- PreToolUse additionalContext parity is an open question (issue #19385). Do not depend on
  PreToolUse for context injection; use SessionStart. https://github.com/openai/codex/issues/19385
- Model names in examples (`gpt-5.5`) are doc placeholders; the actual default model on the
  installed binary should be read from `codex --version` / `/status`, not assumed.
- The App Server method names (`thread/start`, `turn/start`, etc.) are from the docs; confirm
  against the installed `codex app-server` before building on them - it is marked experimental in
  places (WebSocket transport, `process/*`).
- Our rollout-locator heuristic (newest `rollout-*.jsonl` for the matching cwd within a launch
  window) is a heuristic; confirm `/clear` continuity (same rollout file vs new file) live.

---

## Sources

- Codex documentation home: https://developers.openai.com/codex
- CLI command-line reference: https://developers.openai.com/codex/cli/reference
- Hooks: https://developers.openai.com/codex/hooks
- Configuration reference: https://developers.openai.com/codex/config-reference
- Advanced configuration (notify, inline hooks): https://developers.openai.com/codex/config-advanced
- Local config / basics: https://developers.openai.com/codex/config-basic and /codex/local-config
- AGENTS.md custom instructions guide: https://developers.openai.com/codex/guides/agents-md
- App Server (JSON-RPC): https://developers.openai.com/codex/app-server
- Subagents: https://developers.openai.com/codex/subagents
- Source repo: https://github.com/openai/codex
- Config source of truth: https://github.com/openai/codex/blob/main/docs/config.md
- Config Rust implementation: https://github.com/openai/codex/blob/main/codex-rs/core/src/config/mod.rs
- Issue #17532 (repo-local config hooks do not fire interactively): https://github.com/openai/codex/issues/17532
- Issue #19385 (PreToolUse additionalContext parity): https://github.com/openai/codex/issues/19385
- Issue #21675 (per-session additionalContext cache / InstructionsLoaded parity): https://github.com/openai/codex/issues/21675
- Issue #4005 (notify payload should include cwd): https://github.com/openai/codex/issues/4005
- Our integration source: src/CcDirector.Core/Drivers/CodexDriver.cs, Agents/CodexAgent.cs,
  AgentPlugins/CodexAgentPlugin.cs, Drivers/CodexSlashCommands.cs, Codex/CodexTranscriptReader.cs,
  Codex/CodexRolloutLocator.cs
