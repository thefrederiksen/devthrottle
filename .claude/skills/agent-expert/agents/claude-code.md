<!--
Per-agent reference: Claude Code (Anthropic's `claude` CLI).
Rules:
- ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
- Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN].
- Sources linked inline and collected in the Sources section.
- Doc churn is high. Re-check anything before depending on it. See section 11.
-->

# Claude Code (ClaudeCode)

> Anthropic's official coding-agent CLI (binary: claude). Our integration: ClaudeDriver / ClaudeAgent / ClaudeAgentPlugin, history provider kind TranscriptFile. This is the reference implementation for fleet-preamble injection (mechanism Family A, shell-command hook): it is ALREADY WIRED and proven live. All other agents mirror it.

## 1. Identity and install

- Binary name: claude. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- TypeScript/npm Agent SDK package: @anthropic-ai/claude-agent-sdk (bundles a native Claude Code binary per platform as an optional dependency, so installing the SDK does not require installing Claude Code separately). Python package: claude-agent-sdk (import name claude_agent_sdk, requires Python 3.10 or later). [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview]
- Self-install / update commands: `claude install [version|stable|latest]`, `claude update`. The native binary can be pinned to a version like 2.1.118. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- Official documentation home: https://code.claude.com/docs (docs.anthropic.com and docs.claude.com URLs now redirect into code.claude.com and platform.claude.com). [VERIFIED - observed live redirects during this research, see Sources]
- Source repositories: TypeScript SDK https://github.com/anthropics/claude-agent-sdk-typescript ; Python SDK https://github.com/anthropics/claude-agent-sdk-python . The Claude Code CLI itself is distributed as a native binary, not as open source. [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview]
- Windows launchable shim: we resolve `claude.exe` from an explicitly configured path, otherwise the first `claude.exe` found on PATH. [VERIFIED from our code - src/CcDirector.Core/Drivers/ClaudeDriver.cs ResolveExecutable]
- Version-probe command: `claude --version` (alias `claude -v`). [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- Auth: `claude auth login` (subscription) or `claude auth status` (JSON, exit 0 if logged in). Headless/SDK auth uses ANTHROPIC_API_KEY or `claude setup-token` for a long-lived OAuth token; bare mode skips OAuth and keychain reads entirely. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference and https://code.claude.com/docs/en/headless]

## 2. Command-line interface

Two modes:
- Interactive TUI: `claude` or `claude "initial prompt"`. This is what CC Director drives in a terminal. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- Headless / print / non-interactive: `claude -p "query"` (alias `--print`). Runs the agent loop, prints, exits. All CLI flags work with -p. This is the Agent-SDK-via-CLI surface. [VERIFIED from docs - https://code.claude.com/docs/en/headless]

Flag table (the ones we pass or could pass at launch). [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference unless noted]

| Flag | Meaning / accepted values |
| --- | --- |
| `--print`, `-p` | Headless print mode; run and exit, no interactive TUI. |
| `--session-id <uuid>` | Use a specific session id (must be a valid UUID). We preassign this so the transcript path is known from birth. |
| `--resume`, `-r <id-or-name>` | Resume a specific session by id or name; bare `--resume` opens the picker. |
| `--continue`, `-c` | Resume the most recent session in the current directory. |
| `--fork-session` | When resuming, mint a NEW session id instead of reusing the original (use with --resume or --continue). |
| `--model <alias-or-id>` | sonnet, opus, haiku, fable, or a full model id (e.g. claude-sonnet-4-6). Overrides the `model` setting and ANTHROPIC_MODEL. |
| `--settings <file-or-json>` | Path to a settings JSON file OR an inline JSON string. Values override the same keys in settings.json files for this session; omitted keys keep file values. This is how we inject our SessionStart hook (it MERGES with the user's hooks). |
| `--setting-sources <list>` | Comma-separated sources to load: user, project, local. |
| `--output-format <fmt>` | Print mode only: text (default), json, stream-json. |
| `--input-format <fmt>` | Print mode only: text, stream-json. |
| `--include-partial-messages` | Emit partial streaming events. Requires --print and --output-format stream-json. |
| `--include-hook-events` | Include hook lifecycle events in the stream. Requires --output-format stream-json. |
| `--verbose` | Full turn-by-turn output (required for stream-json streaming examples). |
| `--append-system-prompt <text>` | Append text to the end of the default system prompt. Per-invocation only. |
| `--append-system-prompt-file <path>` | Same, from a file. |
| `--system-prompt <text>` | REPLACE the entire default system prompt. |
| `--system-prompt-file <path>` | Replace from a file. (--system-prompt and --system-prompt-file are mutually exclusive; append flags combine with either.) |
| `--mcp-config <files-or-json>` | Load MCP servers from JSON files or strings (space-separated). |
| `--strict-mcp-config` | Use only --mcp-config servers, ignore all other MCP config. |
| `--permission-mode <mode>` | default, acceptEdits, plan, auto, dontAsk, bypassPermissions. Overrides defaultMode from settings. |
| `--dangerously-skip-permissions` | Equivalent to --permission-mode bypassPermissions. This is our DefaultArgs. |
| `--allowedTools` / `--allowed-tools <list>` | Tools that run without a prompt. |
| `--disallowedTools` / `--disallowed-tools <list>` | Deny rules; a bare name removes the tool from context. |
| `--tools <list>` | Restrict which built-in tools are available. |
| `--add-dir <paths>` | Add working directories (grants file access; does NOT load their .claude config or CLAUDE.md unless CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1). |
| `--agents <json>` | Define custom subagents inline as JSON (frontmatter fields plus a `prompt` field). |
| `--agent <name>` | Select a named agent for the session. |
| `--bare` | Skip auto-discovery of hooks, skills, plugins, MCP servers, auto memory, and CLAUDE.md (fast scripted start). NOTE: this would suppress our hook - do not use it for fleet sessions. |
| `--init-only` | Run Setup and SessionStart hooks, then exit without a conversation. Useful to test our hook fires. |
| `--no-session-persistence` | Print mode only; do not write the transcript / cannot resume. |
| `--json-schema <schema>` | Print mode only; structured output into a `structured_output` field. |
| `--name`, `-n <name>` | Set a session display name (resumable with --resume <name>). |

Relevant subcommands: `claude mcp ...` (configure MCP servers), `claude agents` (agent view; `--json` lists active background sessions for scripting), `claude remote-control` (server mode, see section 6), `claude setup-token`, `claude auth ...`. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]

What CC Director passes today: new session -> `--dangerously-skip-permissions --session-id <guid>` plus our `--settings <hooks-settings.json>`; resume -> `--dangerously-skip-permissions --resume <id>`; Studio mode prepends `-p --output-format stream-json --verbose`. [VERIFIED from our code - ClaudeDriver.BuildLaunchSpec, ClaudeAgent.BuildLaunchSpec; --settings wiring in ClaudeHookInstaller / SessionManager]

## 3. Configuration

settings.json scopes and precedence (highest wins). [VERIFIED from docs - https://code.claude.com/docs/en/settings]

| Scope | Path |
| --- | --- |
| Managed (enterprise) | Windows: C:\Program Files\ClaudeCode\managed-settings.json (also a managed-settings.d/*.json drop-in dir); macOS: /Library/Application Support/ClaudeCode/managed-settings.json; Linux/WSL: /etc/claude-code/managed-settings.json. Also OS policy: Windows registry HKLM\SOFTWARE\Policies\ClaudeCode. |
| Command-line args | e.g. --settings, --model, --permission-mode (session overrides). |
| Local (per project, gitignored) | .claude/settings.local.json |
| Project (committed) | .claude/settings.json |
| User | ~/.claude/settings.json (Windows: %USERPROFILE%\.claude\settings.json) |

Precedence order high to low: Managed > command-line args > Local > Project > User. Managed settings cannot be overridden. [VERIFIED from docs - https://code.claude.com/docs/en/settings]

Key settings.json fields: `model`, `permissions` (allow/deny arrays), `env`, `hooks` (lives at root level), `outputStyle`, `effortLevel`, `autoMemoryEnabled`, `autoMemoryDirectory`, `cleanupPeriodDays`, `claudeMdExcludes`, and (managed only) `claudeMd`, `forceLoginMethod`, `forceLoginOrgUUID`, `sandbox.enabled`. The hooks key is reloaded when settings files change; `model` is not (use /model mid-session). [VERIFIED from docs - https://code.claude.com/docs/en/settings and https://code.claude.com/docs/en/memory]

ClaudeDriver reads ~/.claude/settings.json's `model` key as a display hint for the model picker (never writes it). [VERIFIED from our code - ClaudeDriver.ReadConfiguredDefaultModel]

Other config locations: MCP servers in project .mcp.json; skills in .claude/skills/*/SKILL.md; legacy commands in .claude/commands/*.md; path-scoped rules in .claude/rules/*.md; user rules in ~/.claude/rules/. Transcript/config storage root can be moved with CLAUDE_CONFIG_DIR. [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview, https://code.claude.com/docs/en/memory, https://code.claude.com/docs/en/sessions]

## 4. Context injection (how to inject a preamble)

Mechanisms, each with whether it survives /clear and /compact:

1. SessionStart hook additionalContext (OUR mechanism, Family A). A hook prints `hookSpecificOutput.additionalContext`; Claude wraps it in a system reminder and inserts it at the point the hook fired, before the first prompt. [VERIFIED from docs - https://code.claude.com/docs/en/hooks]
   - Survives /clear: YES, the hook RE-FIRES on /clear (SessionStart matcher `clear`), so the preamble is re-injected into the new context. [VERIFIED from docs - hooks matcher table]
   - Survives /compact: YES, the hook RE-FIRES on compaction (SessionStart matcher `compact`). [VERIFIED from docs - hooks matcher table]
   - This is the only listed mechanism that self-heals on BOTH boundaries via an event we control, which is why it is our reference implementation.

2. CLAUDE.md memory files (Family D, passive). Loaded into context at the start of every session as a user message after the system prompt. [VERIFIED from docs - https://code.claude.com/docs/en/memory]
   - Survives /clear: YES (re-loaded at the fresh context start; /clear starts an empty context but CLAUDE.md re-loads). [INFERRED/UNCERTAIN - docs say CLAUDE.md loads "at the start of every session"; the precise reload-on-clear behavior is not stated as explicitly as the compaction case.]
   - Survives /compact: YES for project-root CLAUDE.md - docs explicitly state it is re-read from disk and re-injected after /compact. Nested subdirectory CLAUDE.md files are NOT re-injected automatically; they reload only when Claude next reads a file in that subdirectory. [VERIFIED from docs - https://code.claude.com/docs/en/memory "Instructions seem lost after /compact"]

3. --append-system-prompt / --system-prompt (and -file variants). System-prompt-level text. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference system prompt flags]
   - Survives /clear and /compact: applies to the running process for the whole invocation, so it persists across in-session /clear and /compact within that process. But it is per-invocation only: a NEW process (e.g. a resume we relaunch) must pass it again. [INFERRED/UNCERTAIN - docs say "apply only to the current invocation"; persistence across in-process /clear is inferred, not stated.]

4. Auto memory MEMORY.md at ~/.claude/projects/<project>/memory/MEMORY.md (first 200 lines or 25KB loaded each session). Written by Claude, not a clean injection point for us. [VERIFIED from docs - https://code.claude.com/docs/en/memory]

CLAUDE.md locations and load order (broadest to most specific, all concatenated, root-to-cwd within the tree; CLAUDE.local.md appended after CLAUDE.md at each level): managed policy CLAUDE.md (Windows C:\Program Files\ClaudeCode\CLAUDE.md) > user ~/.claude/CLAUDE.md > project ./CLAUDE.md or ./.claude/CLAUDE.md > local ./CLAUDE.local.md. Imports via `@path/to/file` (relative to the importing file; max depth 4). Claude Code reads CLAUDE.md, not AGENTS.md (you import AGENTS.md from CLAUDE.md to share). [VERIFIED from docs - https://code.claude.com/docs/en/memory]

## 5. Lifecycle events and hooks

Config format and location: hooks live under the `hooks` key in any settings.json (user/project/local/managed) OR in a file/JSON passed via `--settings`. A hook entry is an array of matcher groups; each group has a `matcher` and a `hooks` array of `{ "type": "command", "command": "...", "timeout": <sec> }` (an `http` type with a URL also exists). Passing hooks via --settings MERGES with the user's own hooks rather than replacing them. [VERIFIED from docs - https://code.claude.com/docs/en/settings, https://code.claude.com/docs/en/hooks; merge behavior also relied on in our code - ClaudeHookInstaller]

Full hook event list (Claude Code has the largest event set of any agent we drive). [VERIFIED from docs - https://code.claude.com/docs/en/hooks]:
SessionStart, Setup, UserPromptSubmit, UserPromptExpansion, PreToolUse, PermissionRequest, PermissionDenied, PostToolUse, PostToolUseFailure, PostToolBatch, Notification, MessageDisplay, SubagentStart, SubagentStop, TaskCreated, TaskCompleted, Stop, StopFailure, TeammateIdle, InstructionsLoaded, ConfigChange, CwdChanged, FileChanged, WorktreeCreate, WorktreeRemove, PreCompact, PostCompact, Elicitation, ElicitationResult, SessionEnd.

stdin/stdout JSON contract:
- Common stdin fields a hook receives: `session_id`, `transcript_path`, `cwd`, `hook_event_name`, and event-specific fields. [VERIFIED from docs - https://code.claude.com/docs/en/hooks]
- SessionStart stdin: `session_id`, `transcript_path`, `cwd`, `hook_event_name` = "SessionStart", `source` (one of startup/resume/clear/compact), optional `model`, optional `agent_type`, optional `session_title`. [VERIFIED from docs - https://code.claude.com/docs/en/hooks]
- SessionStart stdout: JSON with `hookSpecificOutput` containing `hookEventName` = "SessionStart" and `additionalContext` (string added to context, wrapped in a system reminder). Other fields: `sessionTitle`, `watchPaths`, `reloadSkills`, `initialUserMessage`. Plain stdout (no JSON) is also accepted as additionalContext for SessionStart. Values over 10,000 chars are written to a file and Claude gets the path plus a preview. [VERIFIED from docs - https://code.claude.com/docs/en/hooks]

Which events fire on each boundary:
- startup (new session): SessionStart with source `startup`. [VERIFIED from docs]
- resume (--resume / --continue / /resume): SessionStart with source `resume` (re-runs on resume to refresh context). [VERIFIED from docs]
- clear (/clear): SessionStart with source `clear`. Also SessionEnd with matcher `clear`. [VERIFIED from docs]
- compact (auto or manual): SessionStart with source `compact`; PreCompact (matchers `manual`/`auto`, can block by returning {"decision":"block"} or exit 2) before, PostCompact after. [VERIFIED from docs]

additionalContext field name: `hookSpecificOutput.additionalContext` (SessionStart). This is exactly what our hook emits. [VERIFIED from docs and from our code - ClaudeHookInstaller.ScriptContent]

## 6. SDK / programmatic API / server mode

- Agent SDK (library form). TypeScript: `import { query } from "@anthropic-ai/claude-agent-sdk"` - `query({ prompt, options })` returns an async iterator of messages. Python: `from claude_agent_sdk import query, ClaudeAgentOptions` - `async for message in query(prompt=..., options=ClaudeAgentOptions(...))`. Python also exposes ClaudeAgentOptions, AgentDefinition, HookMatcher, SystemMessage, ResultMessage. [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview]
   - Note on the streaming/persistent-client class: the docs we read showed `query()` for both languages and the options object; an interactive `ClaudeSDKClient` Python class exists in the broader SDK reference but was not confirmed on the overview page. [INFERRED/UNCERTAIN - verify against https://code.claude.com/docs/en/agent-sdk/python]
   - Options (camelCase TS / snake_case Python): model, systemPrompt/system_prompt, allowedTools/allowed_tools, mcpServers/mcp_servers, permissionMode/permission_mode, hooks (callback functions keyed by event, with matcher groups), agents, resume (session id), forkSession/fork_session, settingSources/setting_sources, plugins. [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview]
   - The SDK is the same agent loop as the CLI; the TS package bundles the native binary. Sessions are captured from the `system`/`init` message's `session_id` and resumed via the `resume` option. [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview]
- Agent SDK via CLI: `claude -p` with --output-format json or stream-json is the scriptable surface (no library). [VERIFIED from docs - https://code.claude.com/docs/en/headless]
- Headless JSON shapes. With --output-format json the result payload includes (at least) `result` (text), `session_id`, `usage`, `total_cost_usd` (plus a per-model cost breakdown), and run metadata; with --json-schema the structured data is in `structured_output`. [VERIFIED from docs - https://code.claude.com/docs/en/headless]
- stream-json event types (newline-delimited JSON, one event per line): a leading `system` event with `subtype` "init" (reports model, tools, MCP servers, loaded `plugins`/`plugin_errors`), `system` with subtype "api_retry" on retryable errors (fields attempt, max_retries, retry_delay_ms, error_status, error, uuid, session_id), `system` with subtype "plugin_install" when CLAUDE_CODE_SYNC_PLUGIN_INSTALL is set, assistant/user messages, partial `stream_event` deltas (with --include-partial-messages), and a final result. [VERIFIED from docs - https://code.claude.com/docs/en/headless]
- Server mode: `claude remote-control` starts a Remote Control server (control from claude.ai or the Claude app); `claude --remote-control`/`--rc` adds it to an interactive session. This is Anthropic's own remote channel, not a local control API we would build against. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference, https://code.claude.com/docs/en/remote-control]
- Background-session supervisor: `claude agents` / `--bg` / attach/respawn/stop, with a daemon (`claude daemon status|stop`). [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]

## 7. MCP / extensions / plugins / skills

- MCP: configured via `claude mcp ...`, project .mcp.json, or --mcp-config (JSON files/strings); --strict-mcp-config restricts to only those. MCP login/logout: `claude mcp login|logout <name>` (v2.1.186+). [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference, https://code.claude.com/docs/en/mcp]
- Skills: .claude/skills/<name>/SKILL.md, auto-loaded or invoked as /<name>. User-invoked skills work in -p mode (include /skill-name in the prompt). [VERIFIED from docs - https://code.claude.com/docs/en/agent-sdk/overview, https://code.claude.com/docs/en/headless]
- Plugins: `claude plugin install ...`; per-session loads via --plugin-dir <path-or-zip> and --plugin-url <url>. Plugins bundle skills, agents, hooks, and MCP servers. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference, https://code.claude.com/docs/en/agent-sdk/overview]
- Subagents: .claude agents or inline via --agents JSON / SDK `agents` option; invoked through the Agent tool. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference, https://code.claude.com/docs/en/agent-sdk/overview]
- Rules: .claude/rules/*.md (optionally path-scoped via `paths` frontmatter); user rules ~/.claude/rules/. [VERIFIED from docs - https://code.claude.com/docs/en/memory]

## 8. Transcript / history

- Location: ~/.claude/projects/<project>/<session-id>.jsonl . [VERIFIED from docs - https://code.claude.com/docs/en/sessions]
- Project slug derivation: the working directory path with non-alphanumeric characters replaced by `-`. [VERIFIED from docs - https://code.claude.com/docs/en/sessions]
- Format: JSONL, one JSON object per line (a message, tool use, or metadata entry). [VERIFIED from docs - https://code.claude.com/docs/en/sessions]
- Parseable: yes, but the docs explicitly warn the entry format is INTERNAL and changes between versions; they recommend /export or the script interfaces (-p --output-format json, the hook `transcript_path`, or the SDK) instead of parsing JSONL directly. We DO parse it (ClaudeTranscriptReader); this is a known coupling to monitor on each Claude Code release. [VERIFIED from docs - https://code.claude.com/docs/en/sessions; coupling noted from our code - ClaudeDriver / ClaudeTranscriptReader]
- Token usage: available. Headless json/stream-json carries `usage` and `total_cost_usd`; our ClaudeDriver.ReadUsage reads usage from the transcript. [VERIFIED from docs - https://code.claude.com/docs/en/headless; and our code - ClaudeDriver.ReadUsage]
- Retention/relocation: 30-day default (cleanupPeriodDays); move with CLAUDE_CONFIG_DIR; suppress with CLAUDE_CODE_SKIP_PROMPT_HISTORY or --no-session-persistence. [VERIFIED from docs - https://code.claude.com/docs/en/sessions]

## 9. Session semantics

- A session = a saved conversation tied to a project directory, persisted continuously to the JSONL transcript. [VERIFIED from docs - https://code.claude.com/docs/en/sessions]
- /clear: starts a fresh, empty context; the previous conversation is saved and resumable. It fires SessionStart source `clear` (and SessionEnd matcher `clear`). [VERIFIED from docs - https://code.claude.com/docs/en/sessions, https://code.claude.com/docs/en/hooks] That a NEW session id and NEW transcript file are minted on /clear is asserted by our integration (it is why our pointer goes stale and the hook must report the new id) and is consistent with the SessionStart `clear` event carrying a fresh session_id, but the public docs do not state the new-id-on-clear behavior in those exact words. [INFERRED/UNCERTAIN - our code ClaudeDriver / ClaudeHookInstaller assert it; treat as our operating assumption, re-verify on version bumps.]
- /compact and auto-compact: replace history with a summary in place; fire PreCompact, then SessionStart source `compact`, then PostCompact. Project-root CLAUDE.md is re-injected after compaction. Our integration treats compaction the same as clear for pointer purposes (the hook reports the current id). [VERIFIED from docs for the events - https://code.claude.com/docs/en/hooks, https://code.claude.com/docs/en/memory; the new-id assumption is INFERRED/UNCERTAIN as above.]
- resume vs continue vs fork: --continue resumes the most recent session in the cwd; --resume <id|name> resumes a specific one (lookup scoped to the current project dir and its git worktrees); --fork-session (with --resume/--continue) copies the conversation into a NEW session id, leaving the original intact (also /branch and /rewind). [VERIFIED from docs - https://code.claude.com/docs/en/sessions, https://code.claude.com/docs/en/cli-reference]
- Sessions started with -p or the SDK do not appear in the picker but are resumable by id from the same directory. [VERIFIED from docs - https://code.claude.com/docs/en/sessions]

## 10. How CC Director integrates it (our current integration)

Classes:
- Driver: ClaudeDriver (src/CcDirector.Core/Drivers/ClaudeDriver.cs). Declares capabilities ClearContext, Cancel, Interrupt, History, TranscriptRead, PreassignedSessionId, ModelSelection. ModelFlag = --model. [VERIFIED from our code]
- Agent launch: ClaudeAgent (src/CcDirector.Core/Agents/ClaudeAgent.cs). [VERIFIED from our code]
- Plugin: ClaudeAgentPlugin (src/CcDirector.Core/AgentPlugins/ClaudeAgentPlugin.cs). [VERIFIED from our code]
- History provider kind: TranscriptFile (we read the ~/.claude/projects JSONL directly via ClaudeTranscriptReader). [VERIFIED - stated by the task; consistent with ClaudeDriver using ITranscriptReader]

Launch line we build:
- New session: `--dangerously-skip-permissions --session-id <new-guid>` (preassigned id => transcript path known from birth) plus our `--settings <hooks-settings.json>`. [VERIFIED from our code - ClaudeDriver.BuildLaunchSpec / ClaudeAgent.BuildLaunchSpec; --settings from ClaudeHookInstaller]
- Resume: `--dangerously-skip-permissions --resume <id>`. [VERIFIED from our code]
- Studio mode prepends `-p --output-format stream-json --verbose`. [VERIFIED from our code - ClaudeAgent.BuildLaunchSpec]
- Keystroke conventions: Cancel = single Esc byte (0x1B); Interrupt = Ctrl+C (0x03); ShowHistory = double-Esc with a 350ms gap (opens the Rewind picker); ClearContext = submit "/clear"; submit is echo-verified to dodge a TUI input race that can drop Enter or prepend a stray "/". [VERIFIED from our code - ClaudeDriver]

Fleet-preamble injection (ALREADY WIRED and proven live - mechanism Family A, the reference implementation):
- ClaudeHookInstaller (src/CcDirector.Core/Claude/ClaudeHookInstaller.cs) writes two STATIC files under %LOCALAPPDATA%\cc-director\claude-hooks: a PowerShell script report-session.ps1 and a hooks-settings.json that registers a SessionStart hook for matchers startup, resume, clear, compact. We pass that settings file via `--settings`, which MERGES with the user's own hooks (relied upon; see Claude Code issue #11392). [VERIFIED from our code]
- The hook command is `powershell -NoProfile -ExecutionPolicy Bypass -File "<script>"` with timeout 10. The script reads the hook event JSON from stdin and the per-session CC_SESSION_ID and CC_DIRECTOR_API from the environment the Director already injects (so nothing per-session is baked into the files). [VERIFIED from our code]
- The script does two things, both swallowing all errors and exiting 0:
  1. POSTs the current claude `session_id`, `transcript_path`, `hook_event_name`, and `source` to `POST {CC_DIRECTOR_API}/sessions/{CC_SESSION_ID}/claude-hook`, so the Director re-discovers the new session id / transcript after /clear or compaction. [VERIFIED from our code - endpoint at ControlEndpoints.cs line ~2107]
  2. GETs `{CC_DIRECTOR_API}/sessions/{CC_SESSION_ID}/fleet-preamble` and, if non-empty, writes `{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"<preamble>"}}` to stdout, so Claude injects the fleet identity + cc-* command preamble at startup/resume/clear/compact with zero turn cost. [VERIFIED from our code - endpoint GET at ControlEndpoints.cs line 222]
- The GET /sessions/{sid}/fleet-preamble endpoint is agent-agnostic and shared by all mechanism families. [VERIFIED from our code - FleetPreamble.cs, ControlEndpoints.cs]

Why this is the reference: SessionStart's four matchers cover every moment Claude's memory of the fleet would otherwise be empty (startup, resume, clear, compact), the additionalContext contract is officially documented, and the re-fire-on-clear/compact behavior is verified in the docs - so the preamble self-heals on both reset boundaries through an event we own. Other agents mirror this shape where their hook systems allow.

Current gaps:
- We parse the JSONL transcript directly, which the docs flag as internal/unstable. A Claude Code release can change the entry format and break ClaudeTranscriptReader. [VERIFIED concern - https://code.claude.com/docs/en/sessions]
- The new-session-id-on-/clear and on-compact behavior is our operating assumption, not a documented guarantee; the hook's claude-hook POST is what actually keeps us correct, so we are covered as long as the hook fires. [INFERRED/UNCERTAIN]
- `cc-devthrottle message ask` (ask another session and read its reply) needs TranscriptRead, which today only ClaudeDriver declares, so cross-agent ask is Claude -> Claude only. [VERIFIED from README matrix - agents/README.md]

## 11. Caveats and verification needed

- Doc churn is high and version-gated. Many flags carry min-version/max-version notes (e.g. --enable-auto-mode removed in 2.1.111, mcp login added 2.1.186, --bare slated to become the -p default). Probe `claude --version` and re-check before depending on a flag. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- `claude --help` does NOT list every flag; absence from --help does not mean unavailable. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference]
- The --settings-merges-with-user-hooks behavior is what our pointer/preamble tracking depends on. Confirm it still merges (not replaces) on each major Claude Code bump; our code cites issue #11392 as the basis. [INFERRED/UNCERTAIN - verify live with --init-only]
- ClaudeSDKClient (persistent/streaming Python client) was not confirmed on the overview page; check the Python SDK reference before using it. [INFERRED/UNCERTAIN - https://code.claude.com/docs/en/agent-sdk/python]
- new-session-id on /clear and /compact: not stated verbatim in public docs; verify live (watch the session_id the claude-hook POST reports before/after a /clear). [INFERRED/UNCERTAIN]
- Quickest live check that our hook fires and injects: `claude --init-only` with our --settings, then confirm the claude-hook POST arrives and the SessionStart additionalContext appears. [INFERRED - based on documented --init-only semantics]
- --bare and --safe-mode disable hooks/CLAUDE.md/skills/MCP; never use them for fleet sessions or the preamble will not inject. [VERIFIED from docs - https://code.claude.com/docs/en/cli-reference, https://code.claude.com/docs/en/headless]

## Sources

- CLI reference: https://code.claude.com/docs/en/cli-reference
- Hooks reference: https://code.claude.com/docs/en/hooks
- Memory / CLAUDE.md: https://code.claude.com/docs/en/memory
- Settings: https://code.claude.com/docs/en/settings
- Headless / print mode: https://code.claude.com/docs/en/headless
- Manage sessions / transcripts: https://code.claude.com/docs/en/sessions
- Agent SDK overview: https://code.claude.com/docs/en/agent-sdk/overview
- Agent SDK Python reference: https://code.claude.com/docs/en/agent-sdk/python
- TypeScript SDK source: https://github.com/anthropics/claude-agent-sdk-typescript
- Python SDK source: https://github.com/anthropics/claude-agent-sdk-python
- Remote Control: https://code.claude.com/docs/en/remote-control
- Our code: src/CcDirector.Core/Drivers/ClaudeDriver.cs, src/CcDirector.Core/Agents/ClaudeAgent.cs, src/CcDirector.Core/AgentPlugins/ClaudeAgentPlugin.cs, src/CcDirector.Core/Claude/ClaudeHookInstaller.cs, src/CcDirector.Core/Sessions/FleetPreamble.cs, src/CcDirector.ControlApi/ControlEndpoints.cs (GET /sessions/{sid}/fleet-preamble line 222, POST /sessions/{sid}/claude-hook line ~2107)
