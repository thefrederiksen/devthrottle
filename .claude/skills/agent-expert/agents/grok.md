<!--
Per-agent reference for the xAI "Grok Build" CLI (binary: grok).
ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN].
"Shipped user-guide" = the markdown docs xAI ships inside the installed binary at
~/.grok/docs/user-guide/*.md. These were read directly from grok 0.2.67 on this machine
and are the most authoritative source available (more complete than the public web pages).
-->

# Grok Build (Grok)

> xAI's official "Grok Build" terminal coding agent. A deliberate Claude Code clone: reads
> CLAUDE.md, Anthropic-format skills, and Claude-style hook JSON. Our integration:
> GenericDriver / GrokAgent / GrokAgentPlugin. Mechanism family A was assumed, but see the
> headline finding in section 5 - Grok's SessionStart hook stdout is IGNORED, so context
> injection must use family D (instruction files), NOT a hook.

IMPORTANT - which Grok this is: this documents xAI's OFFICIAL "Grok Build" CLI, installed
from x.ai/cli, binary `grok`, config under `~/.grok/`. It is NOT the community
`@vibe-kit/grok-cli` / `superagent-ai/grok-cli` npm package (a separate, unaffiliated
open-source project with a different config format `~/.grok/user-settings.json`, different
flags, and no hooks/ACP). Several web "cheat sheets" conflate the two and report `config.json`
/ `hooks.json` / `XAI_API_KEY`-only auth - those are wrong for Grok Build. Trust the shipped
user-guide over third-party blogs.

Version this was verified against: `grok 0.2.67 (03e13f9928) [stable]`.

---

## 1. Identity and install

- Binary name: `grok` [VERIFIED from docs - overview]. On Windows it installs to
  `%USERPROFILE%\.grok\bin\grok.exe` [VERIFIED locally - `where grok` returned
  `C:\Users\soren\.grok\bin\grok.exe`].
- Install (Windows PowerShell): `irm https://x.ai/cli/install.ps1 | iex` [VERIFIED from docs].
  Install (macOS/Linux): `curl -fsSL https://x.ai/cli/install.sh | bash` [VERIFIED from docs].
- Config / data home: `~/.grok/` (Windows `%USERPROFILE%\.grok`), overridable with the
  `GROK_HOME` env var [VERIFIED from docs - 14-headless-mode.md, 05-configuration.md].
- Default model: `grok-build` (the doc model id; marketing calls the weights `grok-build-0.1`,
  256K-token context, text + image input) [VERIFIED from docs/news; the config key is
  `grok-build`].
- Documentation home: https://docs.x.ai/build . The authoritative copy is shipped inside the
  binary at `~/.grok/docs/user-guide/01..22-*.md` [VERIFIED locally].
- Version-probe command: `grok --version` [VERIFIED locally]. Our plugin uses `--version`
  with an 8s timeout (GrokAgentPlugin ValidationMetadata).
- Eligibility: early beta gated to SuperGrok / X Premium Plus, or an `XAI_API_KEY` from
  console.x.ai for headless/CI [VERIFIED from docs - 14-headless-mode.md].

---

## 2. Command-line interface

Three run shapes [VERIFIED from docs - 15-agent-mode.md, 14-headless-mode.md]:
1. Interactive TUI: bare `grok` (alternate-screen pager by default).
2. Headless / non-interactive: `grok -p "<prompt>"` (also triggered by `--prompt-json` /
   `--prompt-file`). Prints one result and exits.
3. Agent/server (ACP): `grok agent stdio` (see section 6).

### Headless flag table [VERIFIED from docs - 14-headless-mode.md unless noted]

| Flag | Meaning |
|------|---------|
| `-p, --single <PROMPT>` | Send one prompt (enters headless mode). Aliases for input: `--prompt-json <JSON>` (content blocks), `--prompt-file <PATH>`. |
| `-m, --model <MODEL>` | Model id, e.g. `grok-build`. |
| `-s, --session-id <ID>` | Set/choose session id - INTERACTIVE TUI ONLY. Ignored in headless mode (documented explicitly). |
| `-r, --resume <ID>` | Resume an existing session by id; errors if it does not exist. |
| `-c, --continue` | Continue the most recent session in the current directory. |
| `--cwd <PATH>` | Working directory (project root is then discovered by walking up to `.git`). |
| `--output-format <FMT>` | `plain` (default), `json`, or `streaming-json`. See section 6. |
| `--yolo` | Auto-approve all tool executions. This is the real flag name. `--always-approve` is the documented alias on the `grok agent` subcommand; in headless the spelled flag is `--yolo`. |
| `--rules <TEXT>` | Append text to the system prompt (alias `--append-system-prompt`); wrapped in a `<human_rules>` block. |
| `--system-prompt-override <TEXT>` | Replace the system prompt verbatim (alias `--system-prompt`); skips default prompt and `--rules`. |
| `--tools <CSV>` | Allowlist of built-in tool ids (headless only). Names are internal ids (e.g. `run_terminal_cmd`, `read_file`), not `Bash`. |
| `--disallowed-tools <CSV>` | Denylist; supports `Agent`, `Agent(explore)`, `Agent(explore, plan)` to block subagents (headless only). |
| `--allow <RULE>` / `--deny <RULE>` | Permission rules, `ToolPrefix(glob)` syntax e.g. `Bash(npm*)`, `WebFetch(domain:host)`; repeatable; deny wins. Work in TUI + headless. |
| `--permission-mode <MODE>` | Parsed in both modes; only `bypassPermissions` currently takes effect via this flag. |
| `--max-turns <N>` | Cap agentic turns (headless only). |
| `--effort <LEVEL>` / `--reasoning-effort <EFFORT>` | `low|medium|high|xhigh|max` (headless only). |
| `--agent <NAME>` / `--agents <JSON>` | Named agent / inline subagent defs (headless only). |
| `--check` / `--self-verify`, `--best-of-n <N>` | Verification loop / run N ways pick best (headless only). |
| `--no-plan`, `--no-subagents`, `--no-memory`, `--disable-web-search` | Feature toggles. |
| `--worktree [NAME]`, `--ref <REF>` / `--worktree-ref <REF>` | Run session in a fresh git worktree. |
| `--sandbox <PROFILE>` | Sandbox profile (off, workspace, devbox, read-only, strict, or custom). |
| `--trust` | Trust the current folder for project hooks/MCP/LSP at launch (= `/hooks-trust`). |
| `--no-alt-screen` | Run inline, no alternate-screen takeover. RELEVANT TO US (see section 10). |
| `--no-auto-update` | Skip background update checks for this session. |
| `--verbatim` | Send the prompt exactly as given. |

Subcommands: `grok login [--device-auth|--device-code]`, `grok logout`, `grok inspect [--json]`
(see section 7/8), `grok sessions list|search`, `grok agent stdio|serve|headless` (section 6)
[VERIFIED from docs].

Exit codes (headless) [VERIFIED from docs - 14-headless-mode.md]: `0` success, `1` error
(auth/network/runtime), `130` SIGINT (Ctrl+C), `143` SIGTERM. On interrupt, session state is
saved up to the last completed tool call but file edits are NOT rolled back.

---

## 3. Configuration

Format is TOML, not JSON (blogs claiming `config.json`/`user-settings.json` are describing the
community fork) [VERIFIED locally - read `~/.grok/config.toml`; and 05-configuration.md].

Precedence (highest first) [VERIFIED from docs - 05-configuration.md]:
1. CLI flags
2. Environment variables (`XAI_API_KEY`, `GROK_MEMORY`, `GROK_HOME`, the `[compat]` toggles, etc.)
3. `~/.grok/config.toml` (user / global)
4. Remote/managed settings (enterprise GrowthBook)
5. Built-in defaults

Files [VERIFIED from docs - 05-configuration.md, 17-sessions.md]:
- `~/.grok/config.toml` - main config. Notable sections: `[cli] auto_update`, `[models] default`,
  `[ui] permission_mode|simple_mode|vim_mode`, `[session] auto_compact_threshold_percent` (default
  85), `[skills] paths/ignore/disabled`, `[plugins] paths/disabled`, `[mcp_servers.<name>]`,
  `[model.<id>]` (custom OpenAI-compatible endpoints), `[compat.claude]` / `[compat.cursor]`
  (scan `.claude/`/`.cursor/` for skills/rules/agents/mcps/hooks - all default `true`),
  `[memory]`, `[subagents]`, `[telemetry]`, `[ui.notifications]`, `[hints]`.
- `~/.grok/pager.toml` - TUI appearance, including `[terminal] alt_screen = "auto"|"always"|"never"`.
- Project-scoped `.grok/config.toml` - ONLY contributes `[mcp_servers]`, `[plugins]`, and
  `[permission]` rules; all other sections load only from `~/.grok/config.toml`. Priority for
  those sections: `./.grok/config.toml` > `<repo-root>/.grok/config.toml` > `~/.grok/config.toml`.
- Enterprise layering: system `requirements.toml` / `managed_config.toml` can pin values above
  the user config.

Headless update-suppression matrix [VERIFIED from docs]: `--no-auto-update` (session) /
`GROK_DISABLE_AUTOUPDATER=1` (process) / `[cli] auto_update = false` (persistent) / non-TTY
stderr (auto). Update text goes to stderr so `--output-format json` stdout stays clean.

`grok inspect` prints the resolved view of a directory: config sources, instruction files (with
token counts), skills, plugins, hooks, MCP servers, and harness-compat state. `grok inspect
--json` is machine-readable [VERIFIED from docs - 05/08/12].

---

## 4. Context injection (how to inject a preamble)

For each mechanism: "survives /clear?" - remember `/clear` is an ALIAS for `/new`, i.e. it
starts a brand-new session (section 9). "survives /compact?" - compaction summarizes the
transcript but keeps the system prompt.

| Mechanism | How | Survives /clear (=/new) | Survives /compact |
|-----------|-----|-------------------------|-------------------|
| Instruction files: `AGENTS.md`, `AGENT.md`, `Agents.md`, `CLAUDE.md`, `Claude.md`, `CLAUDE.local.md` | Auto-loaded from repo root down to cwd at session start; deeper files win on conflict; accumulate into context [VERIFIED from docs - 12-project-rules.md]. | YES - re-read when the new session starts [INFERRED/UNCERTAIN - reload on /new not stated verbatim but follows from "loaded at session start"; verify live]. | YES - they are project rules folded into the system prompt; compaction preserves the system prompt [INFERRED/UNCERTAIN]. |
| Rules directories: `.grok/rules/*.md` (and `.claude/rules/`, `.cursor/rules/` when compat on) | Every `*.md` loaded at each dir level root->cwd [VERIFIED from docs - 12]. | YES (same as above) [INFERRED]. | YES [INFERRED]. |
| `--rules <TEXT>` / `--append-system-prompt` | Appended to system prompt at launch, wrapped in `<human_rules>` [VERIFIED from docs - 12]. | UNCERTAIN - it is a launch-time flag; whether `/new` within the same TUI process re-applies it is undocumented. Verify live. | YES (in system prompt) [INFERRED]. |
| `--system-prompt-override` | Replaces the system prompt verbatim [VERIFIED from docs - 12]. | UNCERTAIN (same as `--rules`). | YES [INFERRED]. |
| ACP `session/new` `_meta.rules` / `_meta.systemPromptOverride` | Per-session injection over the protocol [VERIFIED from docs - 15-agent-mode.md]. | N/A - injected per session by the client. | YES [INFERRED]. |
| SessionStart HOOK stdout (the Claude Code `additionalContext` pattern) | NOT SUPPORTED. See section 5. | n/a | n/a |
| Cross-session memory (`[memory]`, `--experimental-memory`/`GROK_MEMORY=1`) | Auto-injects relevant memories on the first turn [VERIFIED from docs - 05/04]. | Depends on retrieval, not a deterministic preamble - not suitable for our fleet preamble. | n/a |

Notes:
- `AGENTS.override.md` is NOT a recognized filename in this version. The shipped list is exactly
  `Agents.md`, `Claude.md`, `CLAUDE.md`, `CLAUDE.local.md`, `AGENT.md`, `AGENTS.md` [VERIFIED from
  docs - 12-project-rules.md; binary scan found no `AGENTS.override` string]. Custom names such as
  `AGENTS.local.md` are NOT discovered as top-level instruction files. Treat the "AGENTS.override.md"
  in the task brief as not-present until proven otherwise on a newer build.
- Files ignored by `.gitignore` are skipped during instruction discovery (use `CLAUDE.local.md`
  gitignored for personal overrides). No size cap; whole file is loaded [VERIFIED from docs - 12].
- The robust, self-healing injection for a supervised TUI session is the instruction-file path
  (family D): it is re-read on every new session and lives in the system prompt across compaction.

---

## 5. Lifecycle events and hooks

Grok's hook system is a near-clone of Claude Code's: nested JSON, `matcher`, `{decision}` for
blocking. [VERIFIED from docs - 10-hooks.md.]

### Events [VERIFIED from docs - 10-hooks.md]

| Event | Fires when | Blocking? |
|-------|-----------|-----------|
| `SessionStart` | A session starts | No |
| `UserPromptSubmit` | You submit a prompt | No |
| `PreToolUse` | A tool is about to run | YES - can `deny` |
| `PostToolUse` | A tool completed successfully | No |
| `PostToolUseFailure` | A tool failed | No |
| `PermissionDenied` | Permission system denied a tool call | No |
| `Stop` | An agent turn ends (completed/cancelled/error) | No |
| `StopFailure` | A turn ended due to an API error | No |
| `Notification` | The agent sends a notification | No |
| `SubagentStart` / `SubagentStop` (`SubagentEnd` alias) | A subagent starts / finishes | No |
| `PreCompact` / `PostCompact` | Conversation compaction about to run / completed | No |
| `SessionEnd` | The session ends | No |

Only `PreToolUse` can block. Cursor camelCase event names and per-operation hooks
(`beforeShellExecution`, `afterFileEdit`, ...) are accepted and mapped onto these generic events
[VERIFIED from docs - 10].

### Locations and trust [VERIFIED from docs - 10-hooks.md]

- Global (always trusted): `~/.grok/hooks/*.json`. Also `~/.claude/settings.json`(+`.local`) and
  `~/.cursor/hooks.json` when compat is on.
- Project (require trust): `<repo>/.grok/hooks/*.json` (+ `.claude/settings.json`,
  `.cursor/hooks.json`). Trust a folder with `/hooks-trust` or launch flag `--trust`; recorded in
  `~/.grok/trusted_folders.toml`. The same gate covers project MCP/LSP. `GROK_FOLDER_TRUST=0` or
  `[folder_trust] enabled = false` ungates everything. All `*.json` files in a hooks dir are merged.
- Plugin-bundled hooks: trusted per-plugin.

### JSON format [VERIFIED from docs - 10-hooks.md]

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Bash",
        "hooks": [ { "type": "command", "command": "bin/safety-check.sh", "timeout": 10 } ] }
    ],
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "bin/setup.sh" } ] }
    ]
  }
}
```

- `type`: `"command"` (script/one-liner) or `"http"` (POST the event envelope to a `url`).
- `matcher`: regex; applies to tool events (tests tool name) and `Notification` (tests type);
  lifecycle events reject a matcher. Claude tool names are aliased (e.g. `Bash` ->
  `run_terminal_command`, `Read` -> `read_file`, `Edit`/`Write`/`MultiEdit` -> `search_replace`).
- `timeout`: seconds, default 5. `env`: per-hook extra vars (cannot override reserved vars below).
- `${VAR}`/`$VAR` expansion is supported in `command` and `url`.

### stdin / stdout / exit-code contract [VERIFIED from docs - 10-hooks.md]

- stdin: the event JSON, e.g. `{"hookEventName":"pre_tool_use","sessionId":"...","cwd":"...",
  "workspaceRoot":"...","toolName":"run_terminal_command","toolInput":{...},"timestamp":"..."}`
  (PreToolUse also includes `toolUseId`, `toolInputTruncated`).
- stdout (blocking PreToolUse only): `{"decision":"allow"}` or
  `{"decision":"deny","reason":"..."}`.
- Exit codes: `0` = success/allow; `2` = explicit deny (blocking hooks only); any other =
  FAIL-OPEN (failure logged for the UI, tool call NOT blocked). A `deny` decision in stdout JSON
  is honored regardless of exit code. All hook failures (timeout, crash, malformed output, missing
  required env var) fail open.

### Env vars injected into every hook process [VERIFIED from docs - 10-hooks.md]

`GROK_HOOK_EVENT` (snake_case event, e.g. `session_start`, `pre_tool_use`), `GROK_HOOK_NAME`
(configured hook name, with plugin prefix), `GROK_SESSION_ID`, `GROK_WORKSPACE_ROOT`, and
`CLAUDE_PROJECT_DIR` (Claude-compatible alias for the workspace root). Plugin hooks also get
`GROK_PLUGIN_ROOT` and `GROK_PLUGIN_DATA`. These are reserved - values set via the hook `env`
map are stripped at load time.

### HEADLINE FINDING - the benchmark question (verify live, but strongly indicated)

Claude Code's SessionStart hook can emit `hookSpecificOutput.additionalContext` and that text
becomes model context. THE OPEN QUESTION for Grok is whether its SessionStart hook stdout does the
same. Per the shipped hooks guide it does NOT:

> "Passive Hooks: For events like SessionStart or PostToolUse, stdout is ignored. Just exit 0 on
> success." [VERIFIED from docs - 10-hooks.md].

Corroborating: a full-text scan of the `grok.exe 0.2.67` binary and of every shipped doc found
ZERO occurrences of `additionalContext`, `hookSpecificOutput`, or `additional_context` [VERIFIED
locally via grep]. (One community write-up claimed SessionStart supports
`hookSpecificOutput.additionalContext`; that claim is NOT supported by the shipped binary and
appears to be Claude-Code carryover. Treat it as wrong unless a newer build proves otherwise.)

Consequence: the "mechanism family A shell-hook that injects additionalContext" pattern - which
works for Claude Code and Codex - DOES NOT WORK for Grok in this version. Grok hooks are for
side effects (block a tool, log, notify, run setup, export env in the hook's own process) only.
There is no documented stdout-to-context channel on any Grok event. This contradicts the current
agent-expert capability matrix row for Grok ("Strong (SessionStart hook)") - that row should be
downgraded. The verification to do live: write a SessionStart hook that prints
`{"hookSpecificOutput":{"additionalContext":"PROBE-TOKEN"}}` and also a plain-text variant, start
a session, and ask the model whether it can see PROBE-TOKEN. Expectation per docs: it cannot.

---

## 6. SDK / programmatic API / server mode

### Headless output formats [VERIFIED from docs - 14-headless-mode.md]

- `plain` (default): human text on stdout.
- `json`: one object after completion:
  `{"text":"...","stopReason":"EndTurn","sessionId":"abc123","requestId":"xyz789"}` (plus a
  `thought` field when reasoning present). On failure instead: `{"type":"error","message":"..."}`
  - check `type` before reading `text`. The `sessionId` here is what you pass to `-r/--resume`.
- `streaming-json`: newline-delimited events, each a self-contained object with a `type`:
  `{"type":"text","data":"..."}`, `{"type":"thought","data":"..."}`,
  `{"type":"end","stopReason":"EndTurn","sessionId":"...","requestId":"..."}`,
  `{"type":"error","message":"..."}`. May also emit `max_turns_reached`, `auto_compact_*`;
  switch on `type` and treat the list as non-exhaustive. Stdin is NOT read into the prompt; pass
  context via command substitution or `--prompt-file`.

### ACP (Agent Communication / Agent Client Protocol) [VERIFIED from docs - 15-agent-mode.md]

- `grok agent stdio` - JSON-RPC 2.0 over stdin/stdout. Primary integration mode (Zed, Neovim,
  Emacs, marimo; JetBrains "coming soon").
- `grok agent serve --bind 127.0.0.1:2419 --secret <token>` - WebSocket server (token via flag,
  printed if omitted, or `GROK_AGENT_SECRET`). Persists across reconnects.
- `grok agent headless --grok-ws-url wss://relay/ws` - dials out to a relay for web clients.
- `grok agent` options (before the mode): `-m/--model`, `--always-approve` (alias `--yolo`),
  `--reauth`, `--agent-profile <PATH>`.
- Lifecycle: `initialize` -> `session/new` (params `cwd`, `mcpServers`; optional `_meta.rules`,
  `_meta.systemPromptOverride`, `_meta.agentProfile`) -> `session/prompt`
  (`prompt:[{type:"text",text}]`) -> `session/update` notifications.
- `session/update.sessionUpdate` values: `agent_message_chunk`, `agent_thought_chunk`,
  `tool_call`, `tool_call_update`, `plan`. `session/load` resumes by id.
- xAI extension methods under `x.ai/*`: `x.ai/fs/*`, `x.ai/git/*`, `x.ai/git/worktree/*`,
  `x.ai/search/*`, `x.ai/terminal/*`, `x.ai/session/*`, plus `prompt_history`, `rewind/*`,
  `compact_conversation`. Discover the live set from the `initialize` response.
- Official ACP SDKs: TypeScript `@agentclientprotocol/sdk`, Rust `agent-client-protocol`, Python,
  Go, Kotlin. Spec at agentclientprotocol.com.

Auth for programmatic/headless: `XAI_API_KEY` (console.x.ai), or `grok login --device-auth`, or
browser `grok login` [VERIFIED from docs - 14/02].

---

## 7. MCP / extensions / skills / plugins

- MCP [VERIFIED from docs - 05/07]: `[mcp_servers.<name>]` in `~/.grok/config.toml` (command/args/
  env, or `url`+`headers` for HTTP/SSE; `{{session_id}}` templating). Per-project in
  `.grok/config.toml` (override by name = full replacement). Manage in TUI via `/mcps`. Logs under
  `~/.grok/logs/mcp/`.
- Skills [VERIFIED from docs - 08-skills.md]: ANTHROPIC FORMAT - a directory with `SKILL.md`
  (YAML frontmatter: `name`, `description`, optional `when-to-use`, `allowed-tools`,
  `argument-hint`, `user-invocable`, `disable-model-invocation`, `model`, `effort`, ...) plus
  markdown body. Discovered (priority high->low) from `./.grok/skills/`,
  `<repo>/.grok/skills/`, `~/.grok/skills/`, and (compat) `~/.claude/skills/`, `~/.cursor/skills/`;
  also `.agents/skills/` at each tier and additional `[skills] paths`. NOTE: the task mentioned
  `~/.agents/skills` - the shipped doc says user-level Claude/Cursor dirs plus `.agents/skills` at
  each tier; `~/.agents/skills` specifically is plausible but state as [INFERRED/UNCERTAIN].
  User-invocable skills appear as `/skill-name` (qualified `/local:`, `/repo:`, `/user:`,
  `/plugin:` on collision). Bundled skills (e.g. `/create-skill`, `/help`, `/docx`) are extracted
  to `~/.grok/skills/`.
- Plugins + marketplaces [VERIFIED from docs - 05/09]: a plugin bundles skills, agents, hooks, MCP
  and LSP servers. Discovered from `~/.grok/plugins/`, `./.grok/plugins/`,
  `~/.grok/plugins/marketplaces/`, `[plugins] paths`, or `--plugin-dir`. Marketplaces from
  `[[marketplace.sources]]` (the xAI Official one auto-installs:
  `github.com/xai-org/plugin-marketplace.git`) [VERIFIED locally in config.toml]. Manage via
  `/plugins`, `/marketplace`, `/hooks`, `/skills`, `/mcps` (one extensions modal).
- LSP: `~/.grok/lsp.json` / `.grok/lsp.json` / plugin-provided; the optional `lsp` tool is behind
  `[features] lsp_tools` [VERIFIED from docs - 05].
- Claude/Cursor harness compatibility is on by default and is the reason CLAUDE.md, `.claude/`
  skills/rules/hooks/MCP all "just work" [VERIFIED from docs - 05]. `/import-claude` migrates
  `~/.claude` settings.

---

## 8. Transcript / history

Per session, under `~/.grok/sessions/<url-encoded-cwd>/<session-id>/` [VERIFIED from docs -
17-sessions.md, locally confirmed the encoded-cwd directory layout]:

| File | Contents |
|------|----------|
| `summary.json` | Index/metadata: `info` (id+cwd), `session_summary`, `generated_title`, `created_at`/`updated_at`, `num_messages`/`num_chat_messages`, `current_model_id`, `parent_session_id` (fork/restore), `agent_name`. |
| `updates.jsonl` | AUTHORITATIVE conversation log - one ACP `session/update` event per line; drives `/resume` and restore. |
| `chat_history.jsonl` | Raw chat messages sent to the model. (This is what our plugin reads.) |
| `plan.json` | TODO/task list state. |
| `rewind_points.jsonl` | File snapshots for `/rewind`. |
| `signals.json` | Token usage + tool/turn counters. |
| `feedback.jsonl` | Ratings/feedback. |
| `compaction_checkpoints/`, `subagents/` | Compaction state; per-subagent meta. |

- Format is JSONL (append-only, streamable, each line valid JSON) [VERIFIED from docs - 17].
- Token usage IS available (`signals.json`; also `/context`, `/session-info`) [VERIFIED from docs].
- Session ids are UUIDv7 when Grok generates them; a client may supply its own with `-s`
  (interactive only) [VERIFIED from docs - 17].
- A SQLite FTS5 index (`~/.grok/sessions/session_search.sqlite`) backs `grok sessions search`
  [VERIFIED from docs - 17; file present locally]. `grok sessions list [--limit N]` lists by cwd.

---

## 9. Session semantics

- `/new` (alias `/clear`): clears context and starts a NEW session (new id). It is NOT an in-place
  context wipe of the same session id - it is a fresh session. [VERIFIED from docs - 17/04.]
- `/compact [context]`: compress history within the SAME session; optional text says what to
  preserve. Auto-compacts at `[session] auto_compact_threshold_percent` (default 85%). `PreCompact`
  and `PostCompact` hooks fire around it. [VERIFIED from docs - 17/04/10.]
- `/fork`: branch into a peer session that copies history up to now (`parent_session_id` set);
  optional worktree. `/rewind`: restore files + truncate history to an earlier prompt (touches
  disk). `/resume`: load a prior session from disk. [VERIFIED from docs - 17.]
- New TUI launch = new session each time. Resume: `grok -r <id>` (errors if missing) or `grok -c`
  (most recent in cwd). Headless ignores `-s`; use `-r`/`-c` to carry context. [VERIFIED from docs.]
- So for reset detection: a `/clear` becomes observable as a `SessionEnd` + `SessionStart` (new id)
  pair, and a compaction as `PreCompact`/`PostCompact` (same id). Both event pairs exist; what they
  CANNOT do is inject context (section 5).

---

## 10. How CC Director integrates it

Our current classes (verified in repo):
- Agent: `src/CcDirector.Core/Agents/GrokAgent.cs` - `AgentKind.Grok`,
  `SupportsPreassignedSessionId=false`, `SupportsStudioMode=false`. Launch builds args from user
  text only; Director-initiated resume and Studio stream-json wrapper are explicitly ignored in
  v1 (each launch = fresh ephemeral session).
- Plugin: `src/CcDirector.Core/AgentPlugins/GrokAgentPlugin.cs` - built-in; detects
  `~/.grok/bin/grok.exe` or `grok` on PATH; validates with `--version` (8s); history provider
  `AgentHistoryProviderKind.TranscriptFile`, `SupportsConversationHistory=true`, source
  "Grok chat_history.jsonl under ~/.grok/sessions"; `DefaultModel=""`.
- Driver: `GenericDriver` via `AgentDrivers.For(AgentKind.Grok)`
  (`src/CcDirector.Core/Drivers/GenericDriver.cs`). For Grok it sets
  `EmitsContinuousIdleOutput=true` (Grok repaints an animated idle footer / pager animation, so the
  byte-level idle detector must tolerate continuous repaint). It declares only `Cancel` and
  `Interrupt` and THROWS on `ClearContext` (we do not drive `/clear` programmatically).
- Mechanism family currently labelled: A (shell hook in `~/.grok/hooks`).

REVISED plan in light of section 5. The original plan - "register a SessionStart (and PostCompact)
hook that fetches `GET /sessions/{sid}/fleet-preamble` and emits it as additionalContext" - WILL
NOT inject context on Grok 0.2.67, because SessionStart/PostCompact stdout is ignored and there is
no `additionalContext` channel. Concrete options, best first:

1. PRIMARY - family D, instruction file (self-healing, survives /clear and /compact). Write the
   fleet preamble into a Grok-discovered instruction file in the session's cwd before launch -
   e.g. a gitignored `CLAUDE.local.md` or a `.grok/rules/zz-fleet-preamble.md` (rules dirs load
   every `*.md`). Grok re-reads instruction files when a new session starts (so it covers
   `/clear`=`/new`) and keeps them in the system prompt across `/compact`. This is the same
   universal-fallback family the matrix lists for everyone, and for Grok it is the ONLY reliable
   in-band channel today. Refresh the file when the preamble changes; Grok picks it up on the next
   session/turn.
2. LAUNCH-TIME - pass `--rules "<preamble>"` (or `--append-system-prompt`) at spawn. Simple, but
   it is launch-scoped; whether `/new` inside the running TUI re-applies it is unverified (section
   4). Good for headless one-shots, weaker for long-lived supervised TUI sessions.
3. ACP path (future) - if/when we drive Grok over `grok agent stdio`, inject via `session/new`
   `_meta.rules` / `_meta.systemPromptOverride` on every new session. This is the cleanest
   deterministic per-session injection but requires moving Grok off the raw-TUI driver.
4. Hooks remain useful for SIDE EFFECTS only: a `SessionStart`/`SessionEnd`/`PreCompact`/
   `PostCompact` hook can still PING the Director (so we observe new/cleared/compacted sessions via
   `GROK_SESSION_ID`/`GROK_WORKSPACE_ROOT`), and a `PreToolUse` hook can enforce policy. They just
   cannot carry the preamble into the model.

The shared Director endpoint `GET /sessions/{sid}/fleet-preamble` stays the source of the preamble
text for whichever channel we use.

Current gaps:
- TranscriptRead capability is not declared for GenericDriver, so cross-agent `cc-ask` (Claude ->
  Grok) still will not work even though `chat_history.jsonl`/`updates.jsonl` are parseable. A
  TranscriptFile history reader for Grok is a clear future win.
- We do not preassign session ids or resume; Grok owns session state. Fine for v1.

---

## 11. Caveats and verification needed

1. THE BIG ONE: confirm live that no Grok event injects model context (the section-5 PROBE-TOKEN
   test). Everything in section 10 hinges on this. If a newer build adds `additionalContext`, we
   can switch Grok back to family A.
2. The agent-expert capability matrix (`README.md`) currently rates Grok "Strong (SessionStart
   hook)". Per this version that is wrong - downgrade to "instruction-file (family D); hooks are
   side-effect only; no stdout-to-context channel". Update when confirmed live.
3. Fast churn: this is beta (0.2.67). Flag/section names move between releases. The shipped
   `~/.grok/docs/user-guide/*.md` is the per-build source of truth - re-read it after upgrades.
4. `--yolo` vs `--always-approve`: `--yolo` is the headless flag; `--always-approve` is documented
   as the alias on `grok agent`. Confirm which the installed build accepts in headless before
   wiring auto-approve.
5. `-s/--session-id` is interactive-only and ignored headless - do not rely on preassignment.
6. `AGENTS.override.md` and `~/.agents/skills` (both named in the task brief) are NOT confirmed in
   0.2.67 (override filename absent from the supported list; `.agents/skills` is supported per-tier
   but the exact `~/.agents/skills` home path is inferred). Verify before depending on them.
7. Many third-party "Grok Build cheat sheets" describe the community `grok-cli` fork (config.json,
   user-settings.json, different flags). Do not import their facts into our integration.
8. EmitsContinuousIdleOutput is essential: without it the idle detector would never see Grok as
   idle because of the animated footer. Keep it set; re-check if Grok adds `--no-alt-screen`-style
   static output we could prefer for capture.

## Sources

Shipped in-binary user-guide (authoritative for grok 0.2.67, read locally at
`C:\Users\soren\.grok\docs\user-guide\`):
- 01-getting-started.md, 04-slash-commands.md, 05-configuration.md, 08-skills.md, 09-plugins.md,
  10-hooks.md, 12-project-rules.md, 14-headless-mode.md, 15-agent-mode.md, 17-sessions.md.
- Local binary string scan of `C:\Users\soren\.grok\bin\grok.exe` (no `additionalContext` /
  `hookSpecificOutput` / `AGENTS.override`).
- Local `C:\Users\soren\.grok\config.toml` (marketplace source, permission_mode).

Public xAI docs and announcement:
- https://docs.x.ai/build/overview
- https://docs.x.ai/build/cli/headless-scripting
- https://docs.x.ai/build/modes-and-commands
- https://docs.x.ai/build/features/skills-plugins-marketplaces
- https://docs.x.ai/build/enterprise
- https://x.ai/news/grok-build-cli
- https://x.ai/cli

Community/secondary (corroboration only; lower trust - some conflate the community fork):
- https://mer.vin/2026/05/grok-build-cli-xai-terminal-coding-agent-with-plan-mode-subagents-and-headless-ci/
- https://www.aimadetools.com/blog/grok-build-cheat-sheet/
- https://github.com/manaflow-ai/cmux/issues/4220 (reverse-engineered hook events)
- ACP spec: https://agentclientprotocol.com

Repo (our integration):
- src/CcDirector.Core/Agents/GrokAgent.cs
- src/CcDirector.Core/AgentPlugins/GrokAgentPlugin.cs
- src/CcDirector.Core/Drivers/GenericDriver.cs
