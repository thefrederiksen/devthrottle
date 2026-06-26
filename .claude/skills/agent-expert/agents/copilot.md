<!--
Per-agent reference for the GitHub Copilot CLI (the agentic `copilot` binary).
ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with an inline source.
All links are collected in the Sources section.

CRITICAL SCOPE NOTE: This file is about the COPILOT CLI (npm @github/copilot, binary `copilot`,
source repo github/copilot-cli) - the newer agentic terminal agent. It is NOT about:
  - the old `gh copilot` gh-extension (suggest/explain), which is a different product, and
  - the @github/copilot-sdk TypeScript library (source repo github/copilot-sdk), which is for
    building your OWN agent.
Many GitHub doc pages blur the CLI and the SDK. When they disagree, the CLI-specific page wins for
us, because CC Director supervises the CLI, not the SDK. The single most important divergence:
the CLI's sessionStart hook output is IGNORED, while the SDK's onSessionStart additionalContext
IS injected. See sections 4, 5, and 6.
-->

# GitHub Copilot CLI (Copilot)

> The agentic `copilot` terminal agent from `@github/copilot`. Our integration: CopilotDriver /
> CopilotAgent / CopilotAgentPlugin. Mechanism family D (instruction file re-read per prompt); the
> CLI hook system canNOT inject context, unlike Claude Code.

## 1. Identity and install

- Binary name: `copilot`. On Windows the launchable shim is `copilot.cmd`. [VERIFIED from docs - CLI command reference, and our CopilotDriver/CopilotAgentPlugin resolve it as `copilot.cmd`]
  (https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
- npm package: `@github/copilot`, install global: `npm install -g @github/copilot`. Also installable via the `gh.io/copilot-install` script, Homebrew, or WinGet. [VERIFIED from docs - install page; mirrored in our CopilotDriver.ResolveExecutable error text]
  (https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)
- Source repository: `github/copilot-cli` (issues live here). [VERIFIED - issues referenced throughout]
  (https://github.com/github/copilot-cli)
- SDK (separate product, section 6): `@github/copilot-sdk`, repo `github/copilot-sdk`. [VERIFIED from docs]
  (https://www.npmjs.com/package/@github/copilot-sdk)
- Documentation home: docs.github.com/en/copilot, with a dedicated CLI section. [VERIFIED from docs]
  (https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli)
- Windows install location of the shim: the npm global bin directory, typically
  `%APPDATA%\npm\copilot.cmd`. Our CopilotAgentPlugin detection probes exactly that path
  (`Environment.SpecialFolder.ApplicationData` + `npm\copilot.cmd`) and falls back to `copilot` on PATH. [VERIFIED - CopilotAgentPlugin.DefaultNpmCliPath in this repo]
- Version-probe command: `copilot --version` (alias `-v`). Our CopilotAgentPlugin uses `--version`
  with an 8-second timeout for validation. [VERIFIED from docs and CopilotAgentPlugin.ValidationMetadata]
  (https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
- Version context: behavior cited here spans roughly v1.0.x. Our CopilotAgent comment records live
  verification against Copilot CLI v1.0.63. The CLI churns fast; re-verify hook/flag details against
  the installed build. [INFERRED/UNCERTAIN - versions move; see section 11]

## 2. Command-line interface

Two modes:
- Interactive: bare `copilot` starts a back-and-forth terminal session. Shift+Tab cycles modes
  (including a plan mode). [VERIFIED from docs]
  (https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli)
- Non-interactive / programmatic: `-p` / `--prompt` runs one prompt and exits after completion. [VERIFIED from docs]
  (https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)

Flag table (exact spellings from the CLI command reference). [VERIFIED from docs unless noted]
(https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)

| Flag | Meaning |
|------|---------|
| `-p PROMPT`, `--prompt=PROMPT` | Execute a prompt programmatically, then exit (non-interactive). |
| `--model=MODEL` | Set the model to use. |
| `-r`, `--resume[=VALUE]` | Resume a previous interactive session (picker, or a specific id/prefix/name when VALUE is given). |
| `--continue` | Resume the most recent session in the current working directory. |
| `--session-id=UUID` | Start a new session with a caller-chosen UUID. [VERIFIED - we pass this and it echoes in the json stream; see section 10] |
| `--allow-tool=TOOL` | Pre-allow a tool (no permission prompt). Example: `--allow-tool='shell(git)'`. |
| `--deny-tool=TOOL` | Deny a tool. |
| `--allow-all-tools` | Allow all tools without confirmation (the "yolo" path). |
| `--add-dir=PATH` | Add a directory to the allowed file-access list. |
| `--cloud` | Run the task in an isolated cloud-hosted sandbox instead of locally. |
| `--log-level=LEVEL` | none, error, warning, info, debug, all, default. |
| `--banner` / `--no-banner` | Show or hide the startup banner. |
| `--no-color` | Disable color output. |
| `--screen-reader` | Screen-reader optimizations. |
| `-v`, `--version` | Print version. |

Note on the "yolo" preset: our CopilotAgentPlugin contributes an "Automatic (yolo)" preset whose
arg is carried in `AgentToolCatalog.CopilotAllowAllArg`. [VERIFIED - CopilotAgentPlugin.Presets in this repo]
(The exact allow-all flag string lives in AgentToolCatalog; treat the doc's `--allow-all-tools` as
the canonical spelling and verify the preset matches it.) [INFERRED/UNCERTAIN - cross-check the constant]

Slash commands (interactive only). [VERIFIED from docs - CLI command reference + slash-command cheat sheet]
(https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
(https://github.blog/ai-and-ml/github-copilot/a-cheat-sheet-to-slash-commands-in-github-copilot-cli/)

- `/clear` (aliases `/new`, `/reset`) - start a new conversation / delete the current session's
  conversation history. [VERIFIED]
- `/compact` - summarize conversation history to reduce context-window usage. [VERIFIED]
- `/context` - show context-window token usage and a visualization. [VERIFIED]
- `/model`, `/models` - select the model. [VERIFIED]
- `/session` - show session information and manage sessions. [VERIFIED]
- `/resume`, `/continue` - switch to a different session from a picker. [VERIFIED]
- `/mcp` - manage MCP server configuration (subcommands: show, add, edit, delete, disable, enable). [VERIFIED]
  (https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli)
- `/agent` - browse and select custom agents. [VERIFIED]
- `/settings` - open the settings dialog, or set a setting inline. [VERIFIED]
- `/every INTERVAL PROMPT` - schedule a recurring prompt or slash command. Interval suffixes s/m/h/d;
  a bare number means minutes; minimum 10 seconds, maximum 1 day. Built-in commands like `/clear`
  cannot be scheduled. [VERIFIED from docs / blog]
  (https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli)
- `/after DELAY PROMPT` - schedule a one-shot prompt/command after a delay. [VERIFIED]
- `/login`, `/logout`, `/user` - authentication / account. [VERIFIED]
- `/usage` - session usage metrics. [VERIFIED]
- `/sandbox` (e.g. `/sandbox enable`) - configure shell-command sandboxing. [VERIFIED]
- `/add-dir`, `/list-dirs`, `/cwd` - directory access management. [VERIFIED - slash cheat sheet]
- `/delegate` - create an AI-generated pull request (cloud). [VERIFIED]
- `/share` - export the session. [VERIFIED]
- `/theme`, `/terminal-setup`, `/reset-allowed-tools`, `/feedback`, `/help` - misc. [VERIFIED]
- `/chronicle` - generate insights from local session history (subcommands). [VERIFIED - see section 8]
  (https://docs.github.com/en/copilot/how-tos/copilot-cli/chronicle)

Scheduling caveat for automation: `/every` and `/after` run only while the interactive session is
open and cannot schedule built-in commands like `/clear`. For real loops, wrap `copilot -p` in a
script. [VERIFIED from docs]
(https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli)

## 3. Configuration

- Config / state directory: `~/.copilot/` (Windows `%USERPROFILE%\.copilot\`). Holds auth tokens,
  settings, hooks, custom instructions, and session state. [VERIFIED from docs]
  (https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference)
- Override with `COPILOT_HOME` (Windows `%COPILOT_HOME%`). When set, all of the above move under it
  (e.g. `$COPILOT_HOME/hooks/`). [VERIFIED from docs]
  (https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)
- Settings files (inline `hooks` blocks also live here):
  - User settings: `~/.copilot/settings.json`. [VERIFIED from docs - hooks reference]
  - Repository settings: `.github/copilot/settings.json` and `.github/copilot/settings.local.json`. [VERIFIED from docs - hooks reference]
  (https://docs.github.com/en/copilot/reference/hooks-reference)
- Auth env vars, checked in order: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`. [VERIFIED from docs]
- Custom-provider env vars: `COPILOT_PROVIDER_BASE_URL`, `COPILOT_PROVIDER_TYPE`,
  `COPILOT_PROVIDER_API_KEY`, `COPILOT_MODEL`. [VERIFIED from docs - CLI overview]
  (https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli)
- Custom-instruction dirs env var: `COPILOT_CUSTOM_INSTRUCTIONS_DIRS` (comma-separated). [VERIFIED from docs - section 4]
- Precedence for hooks (lowest-to-highest effect order is policy, then user/project, then plugins):
  policy directory -> `.github/hooks/*.json` (repo) -> `~/.copilot/hooks/` (user) ->
  inline `hooks` in repo settings -> inline `hooks` in user settings -> plugin-contributed hooks. [VERIFIED from docs - hooks reference]
  (https://docs.github.com/en/copilot/reference/hooks-reference)

## 4. Context injection (how to inject a preamble)

The decisive fact for CC Director: with the CLI, you inject context through INSTRUCTION FILES, not
through hook output. The CLI hook system canNOT add model context at session start (section 5).

Instruction files the CLI reads (and re-reads). On every request the CLI collects all applicable
instruction files, evaluates any `applyTo` globs, and merges matching layers in priority order. [VERIFIED from docs - add-custom-instructions]
(https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions)

1. `AGENTS.md` - repo root, the current working directory, or any directory listed in
   `COPILOT_CUSTOM_INSTRUCTIONS_DIRS`. The root AGENTS.md is treated as primary instructions. [VERIFIED from docs]
   - Survives `/clear`? [INFERRED/UNCERTAIN - very likely yes: it is a file re-read per prompt, not part of the cleared conversation. Not explicitly documented for the CLI's /clear; verify live.]
   - Survives `/compact`? [INFERRED/UNCERTAIN - very likely yes for the same reason; compaction summarizes the transcript, but instruction files are re-collected per request. Verify live.]
2. `.github/copilot-instructions.md` - repo root `.github/` directory; repository-wide instructions. [VERIFIED from docs]
   - Survives /clear and /compact? Same reasoning as AGENTS.md: re-read per prompt, so it should
     self-heal after any reset. [INFERRED/UNCERTAIN - not explicitly documented for the CLI's reset path]
3. Path-scoped: `.github/instructions/**/*.instructions.md` with an `applyTo` glob in frontmatter -
   merged only for matching files, at repo root or under the working directory. [VERIFIED from docs]
   - Survives /clear and /compact? Re-evaluated per request; self-healing. [INFERRED/UNCERTAIN]
4. Personal / user-global: `$HOME/.copilot/copilot-instructions.md` - a user-level instructions
   file applied across repos. [INFERRED/UNCERTAIN - listed by the task and consistent with the
   `~/.copilot` config root, but I could not open a docs page that names this exact path; verify
   live by creating the file and confirming it is honored.]
   - Survives /clear and /compact? If it is re-read per prompt like the others, yes. [INFERRED/UNCERTAIN]

Key property for us: because instruction files are re-read on every prompt, they are SELF-HEALING.
Any context placed in them comes back automatically after `/clear` or `/compact` without an event
firing - no re-injection mechanism is needed. This is the foundation of our Mechanism Family D plan
(section 10). [VERIFIED that files are re-read per request from docs; the "survives reset" conclusion
is INFERRED/UNCERTAIN until verified live.]
(https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions)

Known gap to be aware of: issue #1433 reports `COPILOT_CUSTOM_INSTRUCTIONS_DIRS` not reliably
auto-loading AGENTS.md from the listed dirs - documented behavior vs actual behavior diverge. Prefer
a path the CLI definitely scans (repo-root AGENTS.md, or the user-global file) over relying solely on
the env var. [VERIFIED - issue #1433]
(https://github.com/github/copilot-cli/issues/1433)

Contrast with Claude Code (the benchmark): Claude Code's SessionStart hook emits `additionalContext`
that IS injected into the model. The Copilot CLI has no equivalent working hook path - its
sessionStart hook output is discarded (section 5). So for Copilot, injection MUST go through
instruction files, not hooks.

## 5. Lifecycle events and hooks

The CLI has a real hooks system, registered as JSON, fired around the session and tool lifecycle.
But it is primarily a POLICY / OBSERVABILITY system (allow/deny tools, log, notify), NOT a
context-injection system at session start.

Registration locations (also section 3 precedence). [VERIFIED from docs - hooks reference / use-hooks]
(https://docs.github.com/en/copilot/reference/hooks-reference)
(https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)
- Repository: `.github/hooks/*.json`
- User: `~/.copilot/hooks/` (or `$COPILOT_HOME/hooks/`); Windows `%USERPROFILE%\.copilot\hooks\`
- Inline `hooks` block inside `.github/copilot/settings.json` (and `.local.json`) or `~/.copilot/settings.json`
- Policy directory (Linux/macOS `/etc/github-copilot/policy.d/`, Windows
  `C:\ProgramData\GitHub\Copilot\policy.d\`); always runs, cannot be disabled by `disableAllHooks`.
- The cloud agent only loads `.github/hooks/*.json` from the cloned repo.

Config schema: top-level `{"version": 1, "hooks": { <event>: [ <entry>, ... ] }}`. Set
`"disableAllHooks": true` to skip non-policy hooks. Hook entry types: `command` (with `bash`,
`powershell`, or cross-platform `command`, plus `cwd`, `env`, `timeoutSec` default 30), `http`
(POST JSON to `url`), and `prompt` (CLI-only; submits text as if the user typed it; fires on
sessionStart for new interactive sessions only). [VERIFIED from docs - hooks reference]

Event names use two parallel conventions: native camelCase (`sessionStart`) and Claude-compatible
PascalCase (`SessionStart`, `PreToolUse`, ...). PascalCase matchers use Claude semantic tool-name
mapping. [VERIFIED from docs - hooks reference]

stdin payload: each hook receives a JSON object on stdin. Common fields: `sessionId`, `timestamp`,
`cwd`. Per event it adds more (below). [VERIFIED from docs - hooks reference]

Event list and - critically - what each hook's OUTPUT can do. [VERIFIED from docs - hooks reference]
(https://docs.github.com/en/copilot/reference/hooks-reference)

| Event (camel / Pascal) | Extra input fields | Output effect |
|------------------------|--------------------|---------------|
| `sessionStart` / `SessionStart` | `source` ("startup"\|"resume"\|"new"), optional `initialPrompt` | See the KEY FINDING below - effectively informational for the CLI. |
| `sessionEnd` / `SessionEnd` | `reason` ("complete"\|"error"\|"abort"\|"timeout"\|"user_exit") | None (informational). |
| `userPromptSubmitted` / `UserPromptSubmit` | `prompt` | None - output disregarded by the CLI. |
| `preToolUse` / `PreToolUse` | `toolName`, `toolArgs` | Allow / deny / modify: `permissionDecision` (allow\|deny\|ask), `permissionDecisionReason`, `modifiedArgs`. Command hooks are FAIL-CLOSED (crash or non-zero exit denies). Matcher: regex on toolName. |
| `postToolUse` / `PostToolUse` | `toolName`, `toolArgs`, `toolResult` | Can modify result (`modifiedResult`) or inject `additionalContext` appended to tool output. |
| `postToolUseFailure` / `PostToolUseFailure` | `toolName`, `toolArgs`, `error` | Recovery guidance via `additionalContext` (exit code 2 treated as context). |
| `preCompact` / `PreCompact` | `transcriptPath`, `trigger` ("manual"\|"auto"), `customInstructions` | None - notification only. Matcher: regex on trigger. |
| `agentStop` / `Stop` | `transcriptPath`, `stopReason` | Can block and force continuation (`decision` block\|allow, `reason`). |
| `subagentStart` | `transcriptPath`, `agentName`, ... | Cannot block; `additionalContext` is prepended to the subagent's prompt. |
| `subagentStop` / `SubagentStop` | `transcriptPath`, `agentName`, `stopReason` | Can block / force continuation. |
| `errorOccurred` / `ErrorOccurred` | `error`, `errorContext`, `recoverable` | None - informational. |
| `notification` (CLI-only) | `message`, `title`, `notification_type` | Optional `additionalContext` injected as a prepended user message; fire-and-forget, never blocks. Matcher: regex on notification_type. |
| `permissionRequest` (CLI-only) | `toolName` etc. | Allow/deny programmatically (`behavior`, `message`, `interrupt`). |

KEY FINDING - state it plainly: for the CLI, the `sessionStart` hook OUTPUT IS IGNORED. The
conceptual/tutorial docs say it directly: "Any output from this hook is ignored by Copilot CLI,
which makes it suitable for informational messages." So a sessionStart hook can print a banner but
CANNOT inject `additionalContext` into the model. This is confirmed by issue #2142 (the
`additionalContext` return value is silently ignored - fire-and-forget in the bundled source). [VERIFIED from docs - copilot-cli-hooks tutorial; VERIFIED - issue #2142]
(https://docs.github.com/en/copilot/tutorials/copilot-cli-hooks)
(https://github.com/github/copilot-cli/issues/2142)

Doc contradiction to flag: the newer hooks-reference TABLE lists sessionStart output as "Optional -
can inject additionalContext into the session." This conflicts with the tutorial/concept prose and
with issue #2142. This is exactly the kind of CLI-vs-SDK / version churn the task warns about; the
SDK fixed sessionStart injection in v1.0.11 (section 6), and the reference table may be describing
the SDK contract or an aspirational/just-landed CLI fix. DO NOT depend on CLI sessionStart injection
until verified live against the installed binary. [INFERRED/UNCERTAIN - reconcile reference table vs
tutorial vs #2142 live]
(https://docs.github.com/en/copilot/reference/hooks-reference)

Other injection-capable hooks exist but are the wrong tool for a startup preamble: `postToolUse`,
`postToolUseFailure`, `subagentStart`, and `notification` can carry `additionalContext`, but they
fire on tool calls / subagents / notifications, not at "session start". Reports #2585 (preToolUse)
and #2980 (postToolUse) note additionalContext not reaching the model in some builds, so even these
are shaky. [VERIFIED - issues exist] (https://github.com/github/copilot-cli/issues/2980)

Which events fire on each transition:
- Startup: `sessionStart` with `source: "startup"`. [VERIFIED from docs]
- Resume: `sessionStart` with `source: "resume"`. [VERIFIED from docs]
- Clear / new: not clearly documented. There is no documented hook event specifically for `/clear`.
  Because `/clear` starts a new conversation, a fresh `sessionStart` (source "new") MAY fire, but
  this is unconfirmed and tangled with bug #2491. [INFERRED/UNCERTAIN]
- Compact: `preCompact` fires before compaction with `trigger` "manual" or "auto" (auto at ~95%
  capacity). It is notification-only - it cannot re-inject context, but it IS observable. [VERIFIED from docs]

BUG to track - #2491: `sessionStart` fires on EVERY user message, not once per session (closed as a
duplicate of #991). The observed pattern is per-turn: sessionStart -> response -> sessionEnd ->
next sessionStart. Consequences: (a) you cannot treat sessionStart as a once-per-session signal,
and (b) expensive setup in a sessionStart hook runs every turn. This further undermines any plan to
key off sessionStart. [VERIFIED - issue #2491]
(https://github.com/github/copilot-cli/issues/2491)

## 6. SDK / programmatic API (@github/copilot-sdk)

This is a SEPARATE product from the CLI: a TypeScript/Node library (repo github/copilot-sdk) for
building your own agent. CC Director supervises the CLI, not the SDK, so the SDK is documented here
only to keep the CLI-vs-SDK distinction clear. [VERIFIED from docs / npm]
(https://www.npmjs.com/package/@github/copilot-sdk)
(https://github.com/github/copilot-sdk/blob/main/docs/features/hooks.md)

- Session lifecycle hooks are registered programmatically, e.g.:
  ```
  const session = await client.createSession({
    hooks: {
      onSessionStart: async (input, invocation) => { ... },
      onSessionEnd:   async (input, invocation) => { ... },
    },
  });
  ```
  [VERIFIED from docs - SDK session-lifecycle]
  (https://docs.github.com/en/copilot/how-tos/copilot-sdk/use-hooks/session-lifecycle)
- CRITICAL difference from the CLI: the SDK's `onSessionStart` returns `additionalContext` (string,
  "Context to add at session start") and `modifiedConfig` (object), and these ARE injected. The
  `input` includes `timestamp`, `cwd`, `source`, and (as of v1.0.44) `initialPrompt`. [VERIFIED from docs]
- Version note: in CLI builds before v1.0.11 the additionalContext from onSessionStart was
  fire-and-forget; the SDK path was fixed in v1.0.11. This is the version line where the SDK and CLI
  behaviors split in the docs, and the likely source of the reference-table contradiction in
  section 5. [VERIFIED from SDK docs commentary] [INFERRED/UNCERTAIN whether the CLI was also fixed]
- SDK hooks also include `onSessionEnd` and (in examples) `onUserPromptSubmitted` / `onPreToolUse`.
  Returning null means "continue with default behavior". [VERIFIED from docs]
- Net: if we ever needed reliable session-start injection for Copilot, the SDK provides it - but
  that means embedding the SDK and running our own agent loop, which is a different integration than
  supervising the stock `copilot` binary in a terminal. Out of scope for the current driver.

## 7. MCP / custom agents / instructions

- MCP: managed via `/mcp` (show, add, edit, delete, disable, enable) and stored in Copilot config.
  Standard Model Context Protocol servers. [VERIFIED from docs]
  (https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli)
- Custom agents: defined as files and selected with `/agent` or the `--agent`-style picker; the
  hooks `subagentStart` / `subagentStop` fire around them. [VERIFIED from docs - create-custom-agents-for-cli]
  (https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli)
- Custom instructions: see section 4 (AGENTS.md, .github/copilot-instructions.md, path-scoped
  .instructions.md, user-global, COPILOT_CUSTOM_INSTRUCTIONS_DIRS). These are the supported
  context-injection surface for the CLI. [VERIFIED from docs]
- Plugins can contribute hooks (lowest precedence layer). [VERIFIED from docs - hooks reference]

## 8. Transcript / history

- Every CLI session is persisted under `~/.copilot/session-state/` (honoring `COPILOT_HOME`). Each
  session is a subdirectory containing an `events.jsonl` event log and a `workspace.yaml` metadata
  file; a top-level `session-store.db` holds an FTS5 full-text index across all sessions. By default
  sessions also sync to your GitHub account. [VERIFIED from docs / DeepWiki]
  (https://docs.github.com/en/copilot/how-tos/copilot-cli/chronicle)
  (https://deepwiki.com/github/copilot-cli/3.3-session-management-and-history)
- The `/chronicle` slash command generates insights from this local history. [VERIFIED from docs]
- Parseability: `events.jsonl` is line-delimited JSON, parseable; `session-store.db` is SQLite with
  FTS5. Token usage is surfaced live via `/usage` and `/context`. [VERIFIED from docs]
- IMPORTANT for our driver: this on-disk transcript location/format is NOT yet live-verified by us,
  so CopilotDriver deliberately does NOT declare `TranscriptRead` and throws on the on-disk verbs
  (ReadWidgets/ReadUsage/ListTranscripts). What the driver DOES parse live is Copilot's
  `--output-format json` JSONL stream (one event per line), not the on-disk file. Note the tension:
  CopilotAgentPlugin.History already advertises `AgentHistoryProviderKind.SqliteStore`
  (SupportsConversationHistory true, "GitHub Copilot SQLite session store") pointing at
  session-store.db, while the driver has not wired reading it. Resolve before relying on history. [VERIFIED - this repo: CopilotDriver and CopilotAgentPlugin]

## 9. Session semantics

- new (`copilot` fresh, or `/clear` / `/new` / `/reset`): starts a new conversation, dropping prior
  turns. `/clear` "deletes the current session's conversation history". [VERIFIED from docs]
- `/compact`: summarizes the conversation to shrink context; same session continues. Auto-compaction
  fires near 95% capacity. `preCompact` hook observes it. [VERIFIED from docs]
- resume: `copilot --resume [id]` or `--continue` (most recent in cwd); interactively `/resume`.
  Resume replays a stored session by id/prefix/name. [VERIFIED from docs]
- session id: caller may preassign via `--session-id <uuid>` at launch; the id is echoed in the
  `--output-format json` stream. Resume passes the existing id. [VERIFIED - this repo + docs]
- Across transitions: a `/clear`/new conversation is a context reset within the SAME running
  process; whether it mints a new session id or keeps the launch id is not documented and not
  verified by us. `/compact` keeps the same session id. [INFERRED/UNCERTAIN for /clear id behavior]

## 10. How CC Director integrates it

Classes in this repo:
- Driver: `CopilotDriver` (src/CcDirector.Core/Drivers/CopilotDriver.cs).
- Agent: `CopilotAgent` (src/CcDirector.Core/Agents/CopilotAgent.cs).
- Plugin: `CopilotAgentPlugin` (src/CcDirector.Core/AgentPlugins/CopilotAgentPlugin.cs).

Declared driver capabilities (intentionally minimal - capability honesty): `Interrupt`
(Ctrl+C, byte 0x03) and `PreassignedSessionId` (`--session-id <uuid>`). The driver THROWS on
`ClearContextAsync` (NotSupportedException: "no verified in-place context-clear command"),
`CancelAsync` (soft-cancel keystroke unverified - use Ctrl+C), `ShowHistoryAsync` (no verified
in-terminal picker), and the on-disk transcript verbs (location/format unverified). It does NOT
declare `Cancel`, `History`, or `TranscriptRead`. [VERIFIED - CopilotDriver in this repo]

Launch: new session mints a fresh UUID and passes `--session-id <uuid>`; resume passes
`--resume <id>`. The "Automatic (yolo)" preset carries the allow-all arg via the preset args.
Studio (stream-json card UI) is NOT wired for Copilot; the driver parses Copilot's own
`--output-format json` JSONL via ParseStreamLine/TryCaptureSessionId. [VERIFIED - CopilotAgent / CopilotAgentPlugin]

Fleet-preamble strategy: Mechanism Family D (instruction file), because the CLI's hook system
cannot inject context at session start (sections 4, 5). Concrete plan:
1. Write the fleet preamble into a custom-instruction file the CLI re-reads every prompt. Two
   candidate sinks:
   a. A directory we control, exported via `COPILOT_CUSTOM_INSTRUCTIONS_DIRS` at launch, containing
      an `AGENTS.md` (and/or `.github/instructions/*.instructions.md`). Risk: issue #1433 reports
      this env var does not reliably load AGENTS.md - verify before committing to it.
   b. The user-global `$HOME/.copilot/copilot-instructions.md` (or `$COPILOT_HOME/...`). Risk: this
      exact path is not doc-confirmed (section 4); verify it is honored.
   Recommended primary: write the preamble where the CLI is GUARANTEED to scan - the working
   directory's `AGENTS.md` or `.github/copilot-instructions.md` for the session's repo - falling
   back to the global file only once verified. [INFERRED/UNCERTAIN - pick after live test]
2. Re-injection on reset is NOT needed for content: because instruction files are re-read per
   prompt, the preamble self-heals automatically after `/clear` and `/compact` (no event required).
   This is the whole point of family D. [Re-read-per-prompt VERIFIED; survives-reset INFERRED/UNCERTAIN]
3. Detect compaction via a `preCompact` hook (notification-only, but observable) registered at
   `~/.copilot/hooks/` or `$COPILOT_HOME/hooks/`, posting the event to the Director. Use it for
   telemetry / UI, not for re-injection. [VERIFIED preCompact exists and is notification-only]
4. `/clear` is NOT observable from outside: there is no documented `/clear` hook event, and any
   sessionStart re-fire is confounded by bug #2491 (fires every turn). So the Director should treat
   `/clear` as undetectable and rely on the self-healing instruction file rather than a clear signal. [VERIFIED no clear hook; #2491 confounds sessionStart]

What to verify live (gates before we depend on the plan):
- Confirm an instruction file's content actually reappears in the model's behavior after `/clear`
  and after `/compact` (the self-heal assumption).
- Confirm which sink the installed build honors: COPILOT_CUSTOM_INSTRUCTIONS_DIRS (re-check #1433),
  the user-global `~/.copilot/copilot-instructions.md`, or per-repo AGENTS.md.
- Confirm a `preCompact` hook fires and reaches our endpoint on both manual and auto compaction.
- Confirm the on-disk session-store.db / events.jsonl format before flipping the plugin's
  SqliteStore history claim into real reads (today the driver throws on transcript verbs).
- Confirm whether `/clear` changes the session id (affects resume and id tracking).

## 11. Caveats and verification needed

- Recency / churn: the CLI is on a fast v1.0.x cadence; flags, hook output contracts, and the
  reference table change between builds. Re-verify against the installed binary (our CopilotAgent
  notes v1.0.63; the broader hook discussion spans v1.0.8-v1.0.44+). [INFERRED/UNCERTAIN]
- The sessionStart-output contradiction (section 5): tutorial/concept docs and issue #2142 say the
  CLI ignores it; the hooks-reference table says it can inject. Treat CLI sessionStart injection as
  NON-WORKING until proven live. The SDK (different product) does inject. [needs live check]
- Bug #2491: sessionStart fires every user message - do not use it as a once-per-session signal.
- Issue #1433: COPILOT_CUSTOM_INSTRUCTIONS_DIRS may not auto-load AGENTS.md - verify the sink.
- additionalContext on preToolUse/postToolUse reportedly not always injected (#2585, #2980) - even
  the injection-capable hooks are shaky on some builds.
- Our driver's unverified-but-plausible spots: the exact allow-all flag string in AgentToolCatalog
  vs the doc's `--allow-all-tools`; whether session-store.db is the right history source; the
  soft-cancel keystroke; the in-terminal history picker. All deliberately left unsupported / throwing.
- `--cloud` and `/delegate` run work off the local machine; supervising those is out of the local
  terminal model and not handled by the current driver.

## Sources

- About GitHub Copilot CLI (concept / overview): https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli
- Install GitHub Copilot CLI: https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli
- Using GitHub Copilot CLI (how-to, slash commands incl. /every, /after, /mcp): https://docs.github.com/en/copilot/how-tos/use-copilot-agents/use-copilot-cli
- Use Copilot CLI (how-to index): https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli
- CLI command reference (flags + slash commands): https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
- Slash-command cheat sheet (blog): https://github.blog/ai-and-ml/github-copilot/a-cheat-sheet-to-slash-commands-in-github-copilot-cli/
- Hooks reference (events, payloads, output contracts): https://docs.github.com/en/copilot/reference/hooks-reference
- Using hooks with Copilot CLI (how-to, config locations + example): https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks
- Copilot CLI hooks tutorial (the "output is ignored / informational messages" statement): https://docs.github.com/en/copilot/tutorials/copilot-cli-hooks
- Adding custom instructions for Copilot CLI (AGENTS.md, copilot-instructions.md, .instructions.md, COPILOT_CUSTOM_INSTRUCTIONS_DIRS, re-read per request): https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-custom-instructions
- Creating and using custom agents for Copilot CLI: https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli
- Copilot CLI session data (chronicle, session-state): https://docs.github.com/en/copilot/how-tos/copilot-cli/chronicle
- Session management and history (DeepWiki): https://deepwiki.com/github/copilot-cli/3.3-session-management-and-history
- Copilot SDK session lifecycle hooks (onSessionStart additionalContext IS injected): https://docs.github.com/en/copilot/how-tos/copilot-sdk/use-hooks/session-lifecycle
- Copilot SDK hooks feature doc: https://github.com/github/copilot-sdk/blob/main/docs/features/hooks.md
- @github/copilot-sdk on npm: https://www.npmjs.com/package/@github/copilot-sdk
- copilot-cli source repo: https://github.com/github/copilot-cli
- Issue #2142 - onSessionStart additionalContext silently ignored (CLI): https://github.com/github/copilot-cli/issues/2142
- Issue #2491 - sessionStart fires on every user message: https://github.com/github/copilot-cli/issues/2491
- Issue #1433 - COPILOT_CUSTOM_INSTRUCTIONS_DIRS not loading AGENTS.md: https://github.com/github/copilot-cli/issues/1433
- Issue #2980 - postToolUse additionalContext not injected: https://github.com/github/copilot-cli/issues/2980
- Issue #2585 - preToolUse additionalContext not passed to agent: https://github.com/github/copilot-cli/issues/2585
- CC Director integration classes (this repo): src/CcDirector.Core/Drivers/CopilotDriver.cs, src/CcDirector.Core/Agents/CopilotAgent.cs, src/CcDirector.Core/AgentPlugins/CopilotAgentPlugin.cs
