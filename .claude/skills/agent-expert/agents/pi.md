<!--
Per-agent reference. ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with an inline
source link; all links collected in Sources.
-->

# Pi Coding Agent (Pi)

> The "pi" terminal coding harness from earendil-works. Standout feature: the richest
> extension and event system of any agent CC Director runs - hooks are TypeScript handlers
> loaded INTO pi, not external shell commands. Our integration status: PiDriver / PiAgent /
> PiAgentPlugin.

## 1. Identity and install

- Binary name: `pi`. [VERIFIED from docs] (README, [pi README](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/README.md))
- npm package: `@earendil-works/pi-coding-agent`. [VERIFIED from docs] (same README; [sdk.md](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/sdk.md))
- Source repository: github.com/earendil-works/pi, coding agent under `packages/coding-agent`. [VERIFIED from docs]
- Official documentation: pi.dev/docs and the repo `packages/coding-agent/docs/` markdown files. [INFERRED/UNCERTAIN] (pi.dev/docs referenced by task; docs read here were the raw repo files)
- Self-description: "Pi is a minimal terminal coding harness" extensible with TypeScript extensions, skills, prompt templates, and themes without forking the core. [VERIFIED from docs] (README)
- Default tools given to the model: `read`, `write`, `edit`, `bash`. [VERIFIED from docs] (README)
- Install (global, recommended): `npm install -g --ignore-scripts @earendil-works/pi-coding-agent`. Alternative installer: `curl -fsSL https://pi.dev/install.sh | sh`. [VERIFIED from docs] (README)
- Config directory: `~/.pi/agent/` holds settings, sessions, extensions, skills, prompts, themes. [VERIFIED from docs] (README)
- Windows launchable shim: installed by npm global as `pi.cmd` on PATH. Our PiAgent resolves it via `AgentOptions.PiPath`. [INFERRED/UNCERTAIN] (standard npm-on-Windows behavior; PiAgent.cs comment says "pi.cmd from @earendil-works/pi-coding-agent")
- Version probe: `pi --version`. [VERIFIED from docs] (README; exact version string not stated in docs)

## 2. Command-line interface (the four modes)

Pi has four operational modes. [VERIFIED from docs] ([usage.md](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/usage.md))

1. Interactive (default): full terminal UI - startup header (loaded context files, prompt
   templates, skills, extensions), messages, editor (border color = thinking level), footer
   (cwd, session name, token/cache usage, cost, context usage, model). [VERIFIED from docs]
2. Print mode (`-p`, `--print`): "Print response and exit." Also merges piped stdin into the
   initial prompt. [VERIFIED from docs]
3. JSON mode (`--mode json`): "Output all events as JSON lines" for programmatic integration.
   [VERIFIED from docs]
4. RPC mode (`--mode rpc`): "RPC mode over stdin/stdout" for embedding pi in external
   applications. Bidirectional - the host sends command objects and receives an event stream
   (see Section 6). [VERIFIED from docs]
5. Embeddable SDK: not a CLI mode but a fifth integration path - import the npm package and
   construct an agent session in-process (see Section 6). [VERIFIED from docs] (sdk.md)

Flag table [VERIFIED from docs] (usage.md):

| Flag | Meaning |
|------|---------|
| `-p`, `--print` | Print response and exit (headless single-shot) |
| `--mode json` | Emit all events as JSON lines |
| `--mode rpc` | RPC over stdin/stdout |
| `--system-prompt <text>` | "Replace default prompt; context files and skills are still appended" |
| `--append-system-prompt <text>` | Append to the default system prompt |
| `-c`, `--continue` | "Continue the most recent session" |
| `-r`, `--resume` | "Browse and select a session" |
| `--no-session` | "Ephemeral mode; do not save" |
| `--name <name>` | "Set session display name at startup" |
| `-e`, `--extension <source>` | "Load an extension from path, npm, or git; repeatable" |
| `--provider <name>` | Provider: `anthropic`, `openai`, `google`, etc. |
| `--model <pattern>` | Model pattern or ID; supports `provider/id` and `:<thinking>` |
| `--thinking <level>` | `off`, `minimal`, `low`, `medium`, `high`, `xhigh` |
| `--no-context-files`, `-nc` | Disable AGENTS.md/CLAUDE.md context discovery |
| `--approve`, `-a` | Override per-command project trust |

Key distinction from `--system-prompt`: it REPLACES the default prompt, but context files and
skills are STILL appended afterward. [VERIFIED from docs] (usage.md)

## 3. Configuration

Two-tier settings, project overrides global, nested objects are merged (not replaced).
[VERIFIED from docs] ([settings.md](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/settings.md))

- Global settings: `~/.pi/agent/settings.json` (all projects). [VERIFIED from docs]
- Project settings: `.pi/settings.json` (current directory). [VERIFIED from docs]
- Trust decisions: `~/.pi/agent/trust.json`. Pi prompts before trusting folders containing
  project settings or `.agents/skills`. `defaultProjectTrust` = `"ask"` (default) | `"always"`
  | `"never"`; `--approve`/`-a` overrides per command. [VERIFIED from docs]

Notable settings keys [VERIFIED from docs] (settings.md):
- Model/thinking: `defaultProvider`, `defaultModel`, `defaultThinkingLevel`, `hideThinkingBlock`,
  `thinkingBudgets`.
- UI: `theme`, `quietStartup`, `defaultProjectTrust`, `collapseChangelog`, `doubleEscapeAction`,
  `treeFilterMode`, autocomplete/editor padding, telemetry toggles.
- Network: `httpProxy` (global only).
- Retry: agent-level and provider-level retry with exponential backoff.
- Delivery: `steeringMode`, `followUpMode`, `transport`, timeout settings.
- Resources: `packages`, `extensions`, `skills`, `prompts`, `themes` arrays (glob patterns and
  exclusions supported) - this is how settings.json adds extra extension/skill/prompt/theme paths.
- Sessions: custom `sessionDir` location.

## 4. Context injection (how to inject a preamble)

Per item: does it survive `/new` (pi's clear-equivalent)? does it survive `/compact`?

a. Context files AGENTS.md / CLAUDE.md - discovered at startup from: global
   `~/.pi/agent/AGENTS.md`; every parent directory walking up from cwd; the current directory.
   Disable with `--no-context-files` / `-nc`. [VERIFIED from docs] (usage.md, README)
   - Survives `/new`: YES (re-discovered at every session_start, including reason=new).
     [INFERRED/UNCERTAIN] (context-file discovery runs on session start per usage.md; re-read on
     /new not explicitly stated but `resources_discover` fires after every session_start)
   - Survives `/compact`: YES - it is part of the system prompt assembly, not conversation
     content, so compaction does not remove it. [INFERRED/UNCERTAIN]

b. System-prompt files - `.pi/SYSTEM.md` "Replace the default system prompt" (project);
   `~/.pi/agent/SYSTEM.md` global override; `APPEND_SYSTEM.md` "Append to the default prompt
   without replacing it". [VERIFIED from docs] (usage.md)
   - Survives `/new` and `/compact`: YES (system-prompt layer, reassembled each session/turn).
     [INFERRED/UNCERTAIN]

c. `--system-prompt` / `--append-system-prompt` flags - set at launch; `--system-prompt`
   replaces but still appends context files and skills. [VERIFIED from docs] (usage.md)
   - Survives `/new`: process-level flag, so it persists for the life of the pi process across
     `/new`. [INFERRED/UNCERTAIN] (flag is bound to the process, /new replaces the session not
     the process)
   - Survives `/compact`: YES (system-prompt layer). [INFERRED/UNCERTAIN]

d. Extension `before_agent_start` injection - a loaded extension can append to / replace the
   system prompt AND inject a persistent message into the session on each prompt (see Section 5).
   This is the active, programmatic injection path and the one CC Director will use.
   [VERIFIED from docs] ([extensions.md](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/extensions.md))
   - Survives `/new`: the extension stays loaded across `/new`; it re-fires on the next
     prompt, so injection self-heals. [VERIFIED from docs] (extension lifecycle: session_start
     reason=new keeps the extension runtime; before_agent_start fires per prompt)
   - Survives `/compact`: the injected persistent message may be summarized away by compaction,
     but the extension re-injects on the next prompt and can also supply a custom compaction
     summary. [INFERRED/UNCERTAIN]

Contrast with Claude Code: Claude injects context via a SessionStart SHELL-COMMAND hook - a
settings entry that runs an EXTERNAL program whose stdout (additionalContext) is fed to the
model. Pi has NO equivalent external-command hook. Pi's hooks are TypeScript extension handlers
loaded into the pi process; injection happens by an extension returning `{ systemPrompt, message }`
from `before_agent_start` or calling `pi.sendMessage`. The matcher mapping is nearly 1:1
(startup->startup, clear->new, resume->resume, compact->session_compact) but the handler must be
authored as a pi extension, not configured as a command line. [VERIFIED from docs] (extensions.md)

## 5. Lifecycle events and hooks (the centerpiece)

Extensions are TypeScript files auto-discovered from trusted locations and run with full system
permissions. [VERIFIED from docs] (extensions.md)

Loading locations [VERIFIED from docs] (extensions.md):
- Global: `~/.pi/agent/extensions/*.ts` and subdirectories with `index.ts`.
- Project-local: `.pi/extensions/*.ts` (loaded only after project trust confirmed).
- CLI: `-e ./path.ts` (path, npm, or git; repeatable).
- Settings: `settings.json` `"extensions"` array (additional paths, globs).
- Hot reload: `/reload` command reloads keybindings, extensions, skills, prompts, context files.
  [VERIFIED from docs] (usage.md, extensions.md)

Registration: `pi.on(event, handler)` subscribes; handler receives `(event, ctx)` and may return
values per event type. Also `pi.registerTool`, `pi.registerCommand`, `pi.registerShortcut`,
`pi.registerFlag` / `pi.getFlag`. [VERIFIED from docs] (extensions.md)

Startup sequence: `pi starts -> project_trust -> session_start {reason:"startup"} ->
resources_discover {reason:"startup"}`. [VERIFIED from docs]

Session replacement (`/new` or `/resume`): `session_before_switch (can cancel) -> session_shutdown
-> session_start {reason:"new"|"resume", previousSessionFile?} -> resources_discover`. [VERIFIED from docs]

Per-prompt flow: `input -> before_agent_start -> agent_start -> [message_* cycle] ->
[turn_start / context / tool_call / tool_result / turn_end] -> agent_end`. [VERIFIED from docs]

Full event list (handler `(event, ctx)`) [VERIFIED from docs] (extensions.md):

- `project_trust` - before trusting a project dir. Returns `{ trusted: "yes"|"no"|"undecided",
  remember? }`. Only user/global and CLI `-e` extensions participate; first yes/no wins.
- `session_start` - `event.reason` is `"startup" | "reload" | "new" | "resume" | "fork"`;
  `event.previousSessionFile` present for new/resume/fork. Use for init / state reconstruction.
  THIS IS THE CLEAR-EQUIVALENT SIGNAL: reason "new" == Claude's clear matcher.
- `session_shutdown` - `event.reason` is `"quit"|"reload"|"new"|"resume"|"fork"`;
  `event.targetSessionFile` for replacement flows. Cleanup/save.
- `session_before_switch` - before `/new` or `/resume`. `event.reason` `"new"|"resume"`,
  `event.targetSessionFile` (resume only). Return `{ cancel: true }` to block.
- `session_before_fork` - before `/fork` or `/clone`. `event.entryId`, `event.position`
  ("before" for /fork, "at" for /clone). Return `{ cancel: true }`.
- `session_before_compact` - before `/compact` (and automatic compaction). `event.reason` is
  `"manual" | "threshold" | "overflow"`; `event.willRetry`; `event.preparation`
  (`firstKeptEntryId`, `tokensBefore`). Return `{ cancel: true }` OR provide a custom summary:
  `{ compaction: { summary, firstKeptEntryId, tokensBefore } }`.
- `session_compact` - AFTER compaction. `event.compactionEntry`, `event.fromExtension`,
  `event.reason`, `event.willRetry`. This is the post-compact signal (Claude's compact matcher).
- `session_before_tree` / `session_tree` - before/after `/tree` navigation; can cancel or supply
  a custom summary; `session_tree` gives `newLeafId`, `oldLeafId`, `summaryEntry`, `fromExtension`.
- `resources_discover` - after session_start. `event.cwd`, `event.reason` `"startup"|"reload"`.
  Returns `{ skillPaths, promptPaths, themePaths }` to contribute resource directories.
- `before_agent_start` - after the user submits, before the agent loop. `event.prompt`,
  `event.images`, `event.systemPrompt` (current chained prompt), `event.systemPromptOptions`
  (`customPrompt`, `selectedTools`, `toolSnippets`, `promptGuidelines`, `appendSystemPrompt`,
  `cwd`, `contextFiles`, `skills`). Returns `{ message: { customType, content, display },
  systemPrompt: "..." }`. The `message` is PERSISTENT (stored in the session). Handlers chain;
  later handlers see prior modifications. THIS IS THE INJECTION HOOK.
- `agent_start` / `agent_end` - once per prompt; agent_end has `event.messages`.
- `turn_start` / `turn_end` - per LLM-response + tool-exec cycle; `turnIndex`, `message`,
  `toolResults`.
- `message_start` / `message_update` / `message_end` - message lifecycle; message_update carries
  the streaming `assistantMessageEvent`; message_end can return `{ message }` to replace it.
- `context` - before each LLM call; `event.messages` is a deep copy, return `{ messages }` to
  filter/transform non-destructively.
- `before_provider_request` / `after_provider_response` - raw provider payload / HTTP status +
  headers; can replace payload.
- `tool_execution_start` / `tool_execution_update` / `tool_execution_end` - tool exec lifecycle
  (`toolCallId`, `toolName`, `args`, `partialResult` / `result`, `isError`).
- `tool_call` - after tool_execution_start, before the tool runs. `event.input` is MUTABLE;
  return `{ block: true, reason }` to block.
- `tool_result` - after exec, before final message events; return `{ content, details, isError }`
  to modify (middleware chain).
- `model_select` / `thinking_level_select` - model/thinking changes (notification).
- `user_bash` - user `!`/`!!` commands; can intercept and return custom result.
- `input` - user input after extension-command check, before skill/template expansion.
  `event.text`, `event.images`, `event.source` (`"interactive"|"rpc"|"extension"`),
  `event.streamingBehavior`. Return `{ action: "continue"|"transform"|"handled", text? }`.

ExtensionContext (`ctx`) highlights: `ctx.ui` (select/confirm/input/editor/notify), `ctx.mode`
(`"tui"|"rpc"|"json"|"print"`), `ctx.hasUI`, `ctx.cwd`, `ctx.sessionManager` (read-only:
getEntries/getBranch/getLeafId), `ctx.getSystemPrompt()`, `ctx.compact()`, `ctx.getContextUsage()`,
`ctx.shutdown()`, `ctx.signal`. Command-only contexts add `newSession()`, `fork()`,
`switchSession()`, `navigateTree()`, `reload()`, `waitForIdle()`. [VERIFIED from docs] (extensions.md)

State/message APIs: `pi.sendMessage(message, { deliverAs: "steer"|"followUp"|"nextTurn",
triggerTurn })`, `pi.sendUserMessage`, `pi.appendEntry(customType, data)` (persist extension state,
NOT in LLM context), `pi.setSessionName`/`getSessionName`, `pi.setLabel`. [VERIFIED from docs]

Mapping to Claude Code matchers [VERIFIED from docs, derived from extensions.md]:
- startup  -> `session_start { reason: "startup" }`
- clear    -> `session_start { reason: "new" }` (pi has NO /clear; /new is the clear-equivalent)
- resume   -> `session_start { reason: "resume" }`
- compact  -> `session_before_compact` (pre) and `session_compact` (post)
The difference: Claude runs an external command; pi runs an in-process TypeScript handler.

## 6. SDK / programmatic API / RPC mode

RPC mode (`--mode rpc`) - the supervisor-facing protocol. JSONL framing, LF (`\n`) only; strip
optional trailing `\r`. [VERIFIED from docs] ([rpc.md](https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/rpc.md))

RPC event stream (agent -> host, JSON lines on stdout) [VERIFIED from docs] (rpc.md):
`agent_start`, `agent_end` (all messages from the run), `turn_start`, `turn_end`,
`message_start`, `message_update` (text/thinking/toolcall deltas), `message_end`,
`tool_execution_start`, `tool_execution_update` (accumulated output), `tool_execution_end`,
`queue_update`, `compaction_start` (reason manual/threshold/overflow), `compaction_end`,
`auto_retry_start`, `auto_retry_end`, `extension_error`, `extension_ui_request`.

RPC commands (host -> agent) [VERIFIED from docs] (rpc.md):
- Session: `{"type":"new_session"}`, `{"type":"new_session","parentSession":"/path.jsonl"}`,
  `{"type":"switch_session","sessionPath":"/path.jsonl"}`, `{"type":"fork","entryId":"abc"}`,
  `{"type":"clone"}`.
- Interaction: `{"type":"prompt","message":"...","images":[...]}`,
  `{"type":"prompt","message":"...","streamingBehavior":"steer"}`,
  `{"type":"bash","command":"ls -la"}`.
- Steering/follow-up: `steer`, `follow_up`, `abort`, `abort_bash`.
- State/config: `get_state`, `get_messages`, `set_model` (`provider`, `modelId`),
  `set_thinking_level` (`level`), `set_steering_mode`, `set_follow_up_mode`,
  `compact` (`customInstructions`), `set_auto_compaction`, `set_auto_retry`.
- Extension UI replies: `{"type":"extension_ui_response","id":"uuid","value"|"confirmed"|"cancelled"}`.

This means a supervisor in RPC mode can drive clear (`new_session`), compact (`compact`), fork,
resume (`switch_session`), and prompt entirely over stdin/stdout - no terminal keystrokes. CC
Director does NOT use RPC mode today (it drives the interactive TUI), but RPC is the cleaner
long-term path for pi specifically. [VERIFIED from docs for the protocol; INFERRED for our non-use]

Embeddable SDK (in-process, TypeScript) [VERIFIED from docs] (sdk.md):
```typescript
import { createAgentSession, SessionManager, AuthStorage, ModelRegistry,
         getModel } from "@earendil-works/pi-coding-agent";
const authStorage = AuthStorage.create();
const modelRegistry = ModelRegistry.create(authStorage);
const { session } = await createAgentSession({
  model: getModel("anthropic", "claude-opus-4-5"),
  sessionManager: SessionManager.inMemory(),
  authStorage, modelRegistry,
});
session.subscribe((event) => { /* message_update, tool_execution_*, agent_*, turn_* ... */ });
await session.prompt("What files are here?", { streamingBehavior: "steer" });
```
Key exports: `createAgentSession`, `AuthStorage`, `ModelRegistry`, `SessionManager`,
`SettingsManager`, `DefaultResourceLoader`, `defineTool`. API-key resolution order: runtime
overrides -> stored credentials -> environment variables -> fallback resolver. [VERIFIED from docs]

## 7. Skills / prompt templates / themes / MCP

- Skills, prompt templates, and themes are first-class extensible resources, discovered from
  `~/.pi/agent/` and project `.pi/`, contributable by extensions via `resources_discover`
  returning `{ skillPaths, promptPaths, themePaths }`, and configurable in settings
  (`skills`, `prompts`, `themes` arrays). [VERIFIED from docs] (extensions.md, settings.md, README)
- Input expansion order: extension commands (`/cmd`) -> `input` event -> skill commands
  (`/skill:name`) -> prompt templates (`/template`) -> agent. [VERIFIED from docs] (extensions.md)
- Project skills directory `.agents/skills` triggers the trust prompt. [VERIFIED from docs] (settings.md)
- Pi Packages: third-party bundles installable via the `packages` setting. [VERIFIED from docs] (README, settings.md)
- MCP: not described in the docs read here. Pi's tool model centers on built-in tools (read/
  write/edit/bash) plus extension-registered tools (`pi.registerTool` / `defineTool`). MCP
  support status is UNVERIFIED. [INFERRED/UNCERTAIN]

## 8. Transcript / history

- Location: `~/.pi/agent/sessions/`, organized by working directory (a cwd slug subdirectory),
  one `<uuid>.jsonl` per session. [VERIFIED from docs] (usage.md); our PiDriver comment records
  the concrete shape `~/.pi/agent/sessions/<cwd-slug>/<uuid>.jsonl` [VERIFIED from code]
  (PiDriver.cs).
- Format: JSONL, one entry per line, branching/tree-structured (entries have IDs;
  `/tree`, `/fork`, `/clone` navigate and branch). [VERIFIED from docs] (usage.md)
- Parseable: yes in principle, but CC Director does NOT parse it in v1 - the exact entry schema
  is not yet reverse-engineered, so PiDriver does not declare TranscriptRead. [VERIFIED from code]
  (PiDriver.cs: ReadWidgets/ReadUsage/ListTranscripts throw NotSupportedException).
- Token usage: available live in the interactive footer and via RPC `get_state` /
  `ctx.getContextUsage()`; not extracted from the jsonl by us. [VERIFIED from docs/code]

## 9. Session semantics

Pi has NO `/clear`. The clear-equivalent is `/new`. [VERIFIED from docs] (usage.md) [VERIFIED from
code] (PiDriver.cs ClearContextAsync submits "/new").

Command semantics [VERIFIED from docs] (usage.md):
- `/new` - start a fresh session in place (drops context; new session file). Clear-equivalent.
- `/resume` - pick and switch to a previous session.
- `/compact [prompt]` - compact context now, optional custom instructions. Keeps the session.
- `/fork` - new session starting from a chosen earlier user message.
- `/clone` - duplicate the current active branch into a new session.
- `/tree` - jump to any point in the session and continue (branching navigation).
- `/reload` - reload keybindings, extensions, skills, prompts, context files.

Keyboard map (live-verified in Director QA) - critical, differs from Claude [VERIFIED from code]
(PiDriver.cs):
- Escape = cancel/abort current turn.
- Ctrl+C = CLEAR THE EDITOR (not an interrupt).
- Ctrl+C twice = QUIT pi.
- Esc twice = open `/tree` session navigator (not a history rewind).

Session-id behavior: pi generates and owns its own session UUID; there is no Director-preassigned
session id and no Director-initiated resume in v1 (resume only via pi's own `/resume` in the TUI).
[VERIFIED from code] (PiAgent.cs: SupportsPreassignedSessionId=false; resume args ignored).

## 10. How CC Director integrates it

Classes:
- Driver: `PiDriver` (`src/CcDirector.Core/Drivers/PiDriver.cs`). Declared capabilities:
  `Cancel | ClearContext`. Cancel = send Esc byte (0x1B). ClearContext = submit `/new`.
  Interrupt is NOT declared (Ctrl+C clears the editor / quits - unsafe). History is NOT declared
  (double-Esc opens /tree, not a rewind). TranscriptRead is NOT declared (jsonl unparsed in v1).
  No model flag (pi selects model internally in v1). [VERIFIED from code]
- Agent: `PiAgent` (`src/CcDirector.Core/Agents/PiAgent.cs`). Executable = `AgentOptions.PiPath`
  (pi.cmd). `SupportsPreassignedSessionId=false`, `SupportsStudioMode=false`. Passes user args
  through verbatim; ignores resume/studio. [VERIFIED from code]
- Plugin: `PiAgentPlugin` (`src/CcDirector.Core/AgentPlugins/PiAgentPlugin.cs`). Built-in plugin;
  verified controls are cancel and clear-context; no preassigned session id, no Studio wrapper.
  [VERIFIED from code]
- Slash commands: `PiSlashCommands` (`src/CcDirector.Core/Drivers/PiSlashCommands.cs`). [VERIFIED from code]

Mechanism family: C (in-process extension we author and load), the closest analog to Claude's
SessionStart matchers and the right choice because pi has no external-command hook.

Concrete fleet-preamble plan (Family C):
1. Author a small pi extension (e.g. `~/.pi/agent/extensions/cc-director-fleet/index.ts`),
   placed in the GLOBAL extensions dir so it loads for every pi process the Director launches,
   no per-project trust needed. [INFERRED/UNCERTAIN] (global extensions load without project
   trust per extensions.md)
2. The extension handles `session_start`. On reason `"startup"` and reason `"new"` (the
   clear-equivalent), it marks "preamble needed". This single handler covers both first launch
   and clear, matching Claude's startup + clear matchers. [VERIFIED from docs for the events]
3. The extension injects on `before_agent_start`: it fetches
   `GET /sessions/{sid}/fleet-preamble` from `CC_DIRECTOR_API` (base URL + session id passed in
   as environment variables at launch), then returns
   `{ message: { customType: "cc-director-fleet-preamble", content: <preamble>, display: true },
   systemPrompt: event.systemPrompt + "\n\n" + <preamble> }`. Injecting on before_agent_start
   (not session_start) guarantees the preamble lands in-context for the very first prompt and
   re-lands after `/new` because before_agent_start fires per prompt and the extension stays
   loaded. [VERIFIED from docs for the hook contract]
4. The extension handles `session_before_compact` and/or `session_compact` to detect compaction.
   It can either let the next before_agent_start re-inject (self-healing) or supply a custom
   compaction summary that preserves the preamble via `{ compaction: { summary, ... } }`.
   [VERIFIED from docs]
5. Identifying the session id: the Director must give the extension the session id and API base.
   Options: pass as environment variables to the pi process, or have the extension read them
   from a Director-written file. Exact wiring is TBD. [INFERRED/UNCERTAIN]

History provider kind: none (TranscriptRead undeclared). `cc-devthrottle message ask` cross-agent reply does not work
to/from pi until the jsonl reader and TranscriptRead are added. [VERIFIED from code]

Current gaps in our integration: no preassigned session id, no Director-initiated resume, no
Studio/stream-json mode, no transcript parsing, no model selection, the fleet-preamble extension
is NOT yet built (Family C is planned, not wired), and pi's composer echo layout is unverified so
SubmitAsync is a blind send (no echo gate). [VERIFIED from code]

## 11. Caveats and verification needed

- The docs read are the raw repo markdown at `main`; field names and event shapes can churn.
  Verify the live event payloads against the installed pi version before depending on them.
  [INFERRED/UNCERTAIN]
- `session_start` re-firing on `/new` with reason `"new"` is documented; confirm live that the
  extension runtime survives `/new` (it should, since shutdown reason "new" precedes a
  session_start, not a process exit). [INFERRED/UNCERTAIN]
- Whether context files / system-prompt files are re-read on `/new` vs only at process start is
  not explicitly stated; verify by changing AGENTS.md mid-process and running `/new`. [INFERRED/UNCERTAIN]
- Whether a `before_agent_start`-injected persistent message survives `/compact` (vs being
  summarized away) needs a live check; if it can be lost, rely on per-prompt re-injection or a
  custom compaction summary. [INFERRED/UNCERTAIN]
- MCP support is undocumented here - verify separately if needed. [INFERRED/UNCERTAIN]
- Exact installed version and the global extensions dir auto-load behavior without project trust
  should be confirmed on the target machine. [INFERRED/UNCERTAIN]
- The transcript jsonl entry schema is unparsed; reverse-engineer it before declaring
  TranscriptRead. [VERIFIED from code that it is unparsed]

## Sources

- pi README (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/README.md
- usage.md (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/usage.md
- extensions.md (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/extensions.md
- rpc.md (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/rpc.md
- sdk.md (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/sdk.md
- settings.md (raw): https://raw.githubusercontent.com/earendil-works/pi/main/packages/coding-agent/docs/settings.md
- npm: https://www.npmjs.com/package/@earendil-works/pi-coding-agent
- pi.dev docs: https://pi.dev/docs
- CC Director code: src/CcDirector.Core/Drivers/PiDriver.cs, src/CcDirector.Core/Agents/PiAgent.cs, src/CcDirector.Core/AgentPlugins/PiAgentPlugin.cs, src/CcDirector.Core/Drivers/PiSlashCommands.cs
