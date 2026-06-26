<!--
Per-agent reference for CC Director's agent-expert skill. INTERNAL, not shipped.
ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with an inline source.
All links collected in Sources.
-->

# opencode (OpenCode)

> Open-source terminal coding agent with a true client/server split: the TUI is just one
> client of a local HTTP server that exposes an OpenAPI surface, a server-sent-events bus,
> and a TypeScript plugin system. Our integration status: GenericDriver (no bespoke driver) /
> OpenCodeAgent / OpenCodeAgentPlugin.

The headline contrast with Claude Code: Claude injects context with an in-process SessionStart
shell hook that prints `additionalContext` to stdout. opencode does NOT have a shell-hook
equivalent. It favors an OUT-OF-PROCESS model: stand up `opencode serve`, subscribe to its SSE
bus over HTTP, and push context back through the same HTTP API; or load an in-process TypeScript
plugin. There is no "run my shell command and read its stdout as extra context" mechanism. This
shapes our whole strategy (Section 10).

---

## 1. Identity and install

- Binary: `opencode`. Project site `opencode.ai`, source `github.com/sst/opencode` (the SST team).
  [VERIFIED - https://opencode.ai/docs/]
- Install: npm `npm i -g opencode-ai`, or `curl -fsSL https://opencode.ai/install | bash`, or
  Homebrew/Scoop/paru per platform. [VERIFIED - https://opencode.ai/docs/]
- SDK / plugin npm packages: `@opencode-ai/sdk` (client) and `@opencode-ai/plugin` (plugin types).
  [VERIFIED - https://opencode.ai/docs/sdk/, https://opencode.ai/docs/plugins/]
- Windows shim: our detection looks for `%APPDATA%\npm\opencode.cmd` first, then bare `opencode`
  on PATH. [VERIFIED - src/CcDirector.Core/AgentPlugins/OpenCodeAgentPlugin.cs DefaultNpmCliPath]
- Version probe: `opencode --version`. We run it with an 8 second timeout.
  [VERIFIED - OpenCodeAgentPlugin.cs ValidationMetadata = new("--version", 8s)]

## 2. Command-line interface

opencode is primarily an interactive full-screen TUI; a bare `opencode` launch opens the TUI in
the current working directory. It also has subcommands and a non-interactive run path.
[VERIFIED - https://opencode.ai/docs/cli/]

Selected commands and flags (the ones that matter to a supervisor):

| Command / flag | Purpose |
|----------------|---------|
| `opencode` | Launch interactive TUI in cwd. [VERIFIED - docs/cli] |
| `opencode run "<msg>"` | Non-interactive: run one message and print the result (headless). [VERIFIED - docs/cli] |
| `opencode run --continue` / `-c` | Continue the most recent session in run mode. [VERIFIED - docs/cli] |
| `opencode run --session <id>` / `-s` | Target a specific session id. [VERIFIED - docs/cli] |
| `opencode serve` | Start the headless HTTP server (no TUI). [VERIFIED - https://opencode.ai/docs/server/] |
| `opencode serve --port <n>` | Server port, default 4096. [VERIFIED - docs/server] |
| `opencode serve --hostname <h>` | Bind host, default 127.0.0.1. [VERIFIED - docs/server] |
| `opencode serve --cors <origin>` | Allow a CORS origin (repeatable). [VERIFIED - docs/server] |
| `opencode serve --mdns` / `--mdns-domain` | Optional mDNS discovery. [VERIFIED - docs/server] |
| `--model <provider/model>` / `-m` | Model selection (run/tui). [INFERRED/UNCERTAIN - docs/cli, confirm exact flag] |
| `--agent <name>` | Start with a named agent. [INFERRED/UNCERTAIN - docs/cli] |
| `opencode auth login` | Provider credential setup. [VERIFIED - docs/cli] |

Our `OpenCodeAgent.BuildLaunchSpec` passes user args through verbatim and does nothing else: no
session-id preassignment, no Director-initiated resume, no Studio stream-json wrapper. A resume id
or studioMode flag is logged and ignored. [VERIFIED - src/CcDirector.Core/Agents/OpenCodeAgent.cs]

## 3. Configuration

- File: `opencode.json` (JSON or JSONC). Schema marker `"$schema": "https://opencode.ai/config.json"`.
  [VERIFIED - https://opencode.ai/docs/config/]
- Locations and precedence (later overrides earlier, merged per-key, not wholesale):
  1. Global `~/.config/opencode/opencode.json`.
  2. Project `opencode.json` in the project root (also `.opencode/opencode.json`).
  3. `OPENCODE_CONFIG` env var points at a custom file.
  Project config has the highest precedence among the standard files; non-conflicting keys from
  all sources are preserved. [VERIFIED - docs/config]
- Top-level keys: `model`, `small_model`, `provider`, `agent`, `command`, `mcp`, `instructions`,
  `plugin`, `permission`, `theme` (deprecated, moved to `tui.json`). [VERIFIED - docs/config]
- Separate `tui.json` holds terminal-UI config (themes, keybinds). [VERIFIED - docs/config]

## 4. Context injection (how to inject a preamble)

Each row notes whether the injected text survives `/clear`-equivalent and `/compact`. Note opencode
has no `/clear`; its reset-to-empty is `/new` (a brand-new session). See Section 9.

| Mechanism | Survives /new (fresh session)? | Survives /compact? | Notes |
|-----------|-------------------------------|--------------------|-------|
| `AGENTS.md` (project root, walks up) | Yes - re-read for every session and prompt | Yes - re-read | Primary instruction file. Created/updated by `/init`. [VERIFIED - https://opencode.ai/docs/rules/] |
| Global `~/.config/opencode/AGENTS.md` | Yes | Yes | Personal rules across all sessions. [VERIFIED - docs/rules] |
| `CLAUDE.md` (legacy fallback) | Yes (only if no AGENTS.md) | Yes | opencode falls back to `CLAUDE.md`, and global `~/.claude/CLAUDE.md`, when no native file exists. Disable with `OPENCODE_DISABLE_CLAUDE_CODE`. [VERIFIED - docs/rules] |
| `instructions` array in opencode.json | Yes | Yes | Accepts file paths, glob patterns (e.g. `.cursor/rules/*.md`), and remote URLs (5 second fetch timeout). [VERIFIED - docs/rules] |
| Per-agent `prompt` file (`"{file:./path}"`) | Yes (for that agent) | Yes | Replaces/sets the agent system prompt. [VERIFIED - https://opencode.ai/docs/agents/] |
| Injected message via HTTP/SDK (`session.prompt` with `noReply`/`noReply:true`) | N/A - per call | N/A | Pushes context into the live session without triggering a reply. This is the dynamic path. [VERIFIED - https://opencode.ai/docs/sdk/] |
| Plugin `experimental.chat.system.transform` hook | Yes (runs every chat) | Yes | EXPERIMENTAL. Mutates the system prompt array in-process. [VERIFIED - https://opencode.ai/docs/plugins/] |

Key point: the file-based mechanisms (AGENTS.md, instructions array, agent prompt) are passive and
self-healing - re-read every prompt - so they inherently survive both reset and compaction. They
are the equivalent of family D. The dynamic mechanisms (HTTP message inject, plugin hooks) are how
you respond to a live event. There is NO SessionStart-style shell hook that prints additionalContext.
[VERIFIED - docs/rules, docs/plugins; absence confirmed by docs/plugins hook list]

## 5. Lifecycle events and hooks (and the event bus)

opencode does not use a stdin/stdout JSON shell-hook contract. Two distinct extension surfaces exist:
(A) the HTTP server-sent-events bus, consumed out-of-process; and (B) the in-process TypeScript
plugin hooks. Both observe the SAME underlying event types.

### 5a. The event bus (out-of-process, over HTTP)

- `GET /event` - server-sent-events stream. The FIRST event is always `server.connected`, then bus
  events follow. [VERIFIED - https://opencode.ai/docs/server/]
- `GET /global/event` - a global (cross-project) SSE stream. [VERIFIED - docs/server]
- The SDK exposes the same stream via `client.event.subscribe()`. [VERIFIED - https://opencode.ai/docs/sdk/]

Event types carried on the bus (the `event` property `type` field). [VERIFIED - https://opencode.ai/docs/plugins/ event list]:

- Server: `server.connected`.
- Session: `session.created`, `session.updated`, `session.compacted`, `session.idle`,
  `session.deleted`, `session.error`, `session.status`, `session.diff`.
- Message: `message.updated`, `message.removed`, `message.part.updated`, `message.part.removed`.
- Permission: `permission.asked`, `permission.replied`.
- Tool/command/file/LSP/todo/installation: `command.executed`, `file.edited`,
  `file.watcher.updated`, `lsp.client.diagnostics`, `lsp.updated`, `todo.updated`,
  `installation.updated`, `shell.env`.

Which event fires when (the load-bearing ones for us):
- New session (`/new`, or first launch, or an API `session.create`): `session.created`.
  [VERIFIED - docs naming; live-confirm pending]
- Compaction (`/compact`, or auto-compact at context limit): `session.compacted`. [VERIFIED - naming]
- Idle / turn finished: `session.idle`. [VERIFIED - naming]
- IMPORTANT: there is NO `session.cleared` event. opencode has no in-place clear; a "clear" is a
  new session and therefore surfaces as `session.created`. [VERIFIED - event list has no cleared type;
  see Section 9]

### 5b. Plugin hooks (in-process TypeScript)

A plugin function returns a hooks object. Exact hook names from the `@opencode-ai/plugin` Hooks
interface [VERIFIED - https://opencode.ai/docs/plugins/ and packages/plugin/src/index.ts]:

- `event` - catch-all: `(input: { event: Event }) => Promise<void>`. Receives EVERY bus event in
  process. This is the in-process analogue of subscribing to `GET /event`.
- `config` - `(input: Config) => Promise<void>`. Observe/adjust resolved config at load.
- `chat.message` - fires when a new message is received.
- `chat.params` - modify the parameters sent to the model (temperature, etc.).
- `chat.headers` - mutate outgoing provider HTTP headers.
- `permission.ask` - `(input: Permission, output: { status: "ask" | "deny" | "allow" })` - approve
  or deny tool/permission requests programmatically.
- `tool.execute.before` - `(input: { tool, sessionID, callID }, output: { args })` - inspect/mutate
  tool args before a tool runs.
- `tool.execute.after` - receives the tool result (title, output, metadata).
- `tool.definition` - `(input: { toolID }, output: { description, parameters })` - rewrite a tool's
  schema.
- `experimental.chat.system.transform` - EXPERIMENTAL. Modify the system prompt array. This is the
  in-process re-injection hook we care about.
- `experimental.session.compacting` - EXPERIMENTAL. "Called before session compaction starts. Allows
  plugins to customize the compaction prompt." Our re-inject-on-compact hook.
- `experimental.compaction.autocontinue` - EXPERIMENTAL. Controls the synthetic continue message
  after compaction.
- `experimental.chat.messages.transform` - EXPERIMENTAL. Transform the message array.
- `experimental.provider.small_model`, `experimental.text.complete` - EXPERIMENTAL, not relevant here.

All `experimental.*` hooks are unstable and may be renamed or removed; pin the opencode version and
re-verify before depending on them. [VERIFIED - they carry the experimental prefix in the type]

## 6. SDK / programmatic API / server mode (the key section)

This is opencode's defining capability and the basis of our integration. The TUI is one client of
a local HTTP server; you can run that server yourself and drive it.

### 6a. Server mode

- Start: `opencode serve --port 4096 --hostname 127.0.0.1` (those are the defaults).
  [VERIFIED - https://opencode.ai/docs/server/]
- Auth: set `OPENCODE_SERVER_PASSWORD` (required to enable auth) and optionally
  `OPENCODE_SERVER_USERNAME` (default `opencode`). HTTP basic auth. Without the password var, the
  loopback server is unauthenticated. [VERIFIED - docs/server]
- OpenAPI 3.1 spec is served at `GET /doc` - the authoritative, machine-readable contract; generate
  a client from it if needed. [VERIFIED - docs/server]

Selected endpoints (full list is in `/doc`) [VERIFIED - docs/server]:

- Events: `GET /event` (per-instance SSE, first event `server.connected`), `GET /global/event`.
- Sessions: `GET /session`, `POST /session` (create), `GET /session/status`, `GET /session/:id`,
  `DELETE /session/:id`, `PATCH /session/:id`, `GET /session/:id/children`,
  `POST /session/:id/init`, `POST /session/:id/fork`, `POST /session/:id/abort`,
  `POST /session/:id/summarize` (this is the compact/summarize trigger),
  `POST /session/:id/revert`, `POST /session/:id/unrevert`, `GET /session/:id/diff`,
  `GET /session/:id/todo`, share endpoints.
- Messages: `GET /session/:id/message` (list), `POST /session/:id/message` (send),
  `GET /session/:id/message/:messageID`, `POST /session/:id/prompt_async` (async prompt),
  `POST /session/:id/command`, `POST /session/:id/shell`,
  `POST /session/:id/permissions/:permissionID` (answer a permission ask).
- Config / project / providers: `GET|PATCH /config`, `GET /config/providers`, `GET /provider`,
  `GET /project`, `GET /project/current`, `GET /path`, `GET /agent`, `GET /command`, `GET|POST /mcp`.
- TUI control surface (drive a running TUI from outside): `POST /tui/append-prompt`,
  `POST /tui/submit-prompt`, `POST /tui/clear-prompt`, `POST /tui/execute-command`,
  `POST /tui/open-sessions`, `POST /tui/open-models`, `POST /tui/show-toast`,
  `GET /tui/control/next`, `POST /tui/control/response`. These map to the plugin `tui.prompt.append`
  / `tui.command.execute` / `tui.toast.show` surface. They let an external process type into and
  drive the user's live TUI. [VERIFIED - docs/server]

### 6b. SDK (@opencode-ai/sdk)

- Client only (attach to an existing server):
  `import { createOpencodeClient } from "@opencode-ai/sdk"` then
  `const client = createOpencodeClient({ baseUrl: "http://localhost:4096" })`. Default base URL is
  `http://localhost:4096`. [VERIFIED - https://opencode.ai/docs/sdk/]
- Full setup (spawn a server and get a client):
  `import { createOpencode } from "@opencode-ai/sdk"; const { client } = await createOpencode()`.
  [VERIFIED - docs/sdk]
- Core methods: `client.session.create({ body })`, `client.session.prompt({ path, body })`,
  `client.session.messages({ path })`, `client.event.subscribe()` (async-iterable SSE stream),
  `client.auth.set({ path, body })`. [VERIFIED - docs/sdk]
- Context-without-reply: `session.prompt` with `body.noReply: true` injects content into the session
  WITHOUT triggering a model response. This is exactly the primitive for pushing a fleet preamble.
  [VERIFIED - docs/sdk]

Contrast with Claude Code: Claude's programmatic surface is the Agent SDK plus a SessionStart shell
hook on the local process. opencode's is a long-lived HTTP server with an OpenAPI contract and an
SSE bus - you talk to it over a socket, not through a child process's stdio. This is why CC Director
treats opencode with mechanism family B (subscribe over HTTP) rather than family A (shell hook).

## 7. MCP / extensions / plugins / commands

- MCP: configured under the `mcp` key in opencode.json; also `GET|POST /mcp` and the `/mcp` slash
  command manage servers at runtime. [VERIFIED - docs/config, OpenCodeSlashCommands.cs]
- Plugins: TypeScript/JavaScript modules in `.opencode/plugin/` (project) and
  `~/.config/opencode/plugin/` (global), auto-loaded at startup. npm plugins install via Bun and
  cache under `~/.cache/opencode/node_modules/`. Load order: global config, project config, global
  plugins, project plugins. A plugin is `async ({ project, client, $, directory, worktree }) => ({ ...hooks })`
  where `client` is an SDK client bound to the running server, `$` is Bun's shell. [VERIFIED -
  https://opencode.ai/docs/plugins/]
  NOTE: docs prose has used both `plugin/` and `plugins/` for the directory name - confirm the exact
  folder against the installed binary before relying on it. [INFERRED/UNCERTAIN - docs/plugins]
- Agents: primary agents (Build, Plan; Tab to cycle) and subagents (General, Explore, Scout;
  `@name` to invoke). Defined as markdown in `.opencode/agent/` and `~/.config/opencode/agent/`, or
  under the `agent` key. Per-agent fields: `description` (required), `prompt` (`"{file:./path}"`),
  `model`, `temperature`, `mode` (`primary`/`subagent`/`all`), `permission`. [VERIFIED -
  https://opencode.ai/docs/agents/]
- Custom slash commands: markdown in `.opencode/command/` and `~/.config/opencode/command/`, or
  under the `command` key. Frontmatter: `description`, `agent`, `model`, `subtask` (bool, forces a
  subagent to avoid context pollution). Body supports `$ARGUMENTS` / `$1`,`$2`,...,
  shell injection `` !`command` ``, and file embedding `@filename`. Built-ins include `/init`,
  `/undo`, `/redo`, `/share`, `/help`. [VERIFIED - https://opencode.ai/docs/commands/]
  Our maintained TUI command catalog (OpenCodeSlashCommands.cs, captured 2026-06-18) also lists
  `/model`, `/models`, `/provider`, `/agent`, `/sessions`, `/session`, `/new`, `/continue`, `/fork`,
  `/compact`, `/theme`, `/stats`, `/export`, `/import`, `/mcp`, `/plugin`, `/quit`.
  [VERIFIED - src/CcDirector.Core/Drivers/OpenCodeSlashCommands.cs]

## 8. Transcript / history

- opencode persists conversations in a local SQLite store, not per-session JSON files. Our reader
  uses `~/.local/share/opencode/opencode.db` (same path on Windows under the user profile).
  [VERIFIED - src/CcDirector.Core/OpenCode/OpenCodeHistoryReader.cs DefaultDatabasePath]
- Schema we rely on: a `session` row per session (with a `directory` column = the repo path and
  `time_updated`); a `message` row per turn (`data` JSON blob carries `role` = user/assistant); and
  ordered `part` rows per message (`data` JSON blob with `type`: `text`, `reasoning`, `tool`, plus
  turn markers `step-start`/`step-finish`). A `tool` part carries both call and result via
  `state.input` / `state.output` / `state.error`. [VERIFIED - OpenCodeHistoryReader.cs class doc]
- The DB is written by a live process (a `-wal` file is present), so we snapshot-copy before opening
  to avoid locking the writer (SqliteSnapshotReader). The TUI runs full-screen, so terminal
  scrollback is empty - the SQLite store is the only conversation source. [VERIFIED -
  OpenCodeHistoryReader.cs]
- There is no per-session file to locate: we resolve the active session as the newest `session` row
  whose normalized `directory` equals the Director session's repo path. [VERIFIED -
  OpenCodeHistoryReader.cs ResolveSessionId]
- Token usage / cost: available via `/stats` in the TUI and surfaced in message metadata over the
  API; not currently parsed by our reader. [INFERRED/UNCERTAIN - OpenCodeSlashCommands `/stats`]
- Our plugin declares history provider kind `SqliteStore`, SupportsConversationHistory = true.
  [VERIFIED - OpenCodeAgentPlugin.cs HistoryMetadata]

## 9. Session semantics

- `/new` - starts a brand-new session. New session id. Emits `session.created`. This is opencode's
  closest thing to a "clear": there is no in-place context clear that keeps the same session id.
  [VERIFIED - OpenCodeSlashCommands.cs `/new`; docs event naming]
- `/compact` (a.k.a. summarize, `POST /session/:id/summarize`) - compacts/summarizes the current
  conversation in place, KEEPING the session id. Emits `session.compacted`. [VERIFIED - docs/server
  summarize endpoint; event naming]
- `/continue` - resume the most recent session. [VERIFIED - OpenCodeSlashCommands.cs]
- `/fork` (`POST /session/:id/fork`) - branch the current session into a new one. [VERIFIED -
  docs/server, OpenCodeSlashCommands.cs]
- `/sessions` - browse/switch sessions. [VERIFIED - OpenCodeSlashCommands.cs]
- CRITICAL for reset detection: there is NO `session.cleared` event and no clear-in-place command.
  A user "clearing" really means `/new`, which we see as `session.created`. So our reset trigger is
  `session.created` (new session in this repo) and our compaction trigger is `session.compacted`.
  [VERIFIED - event list absence; OpenCodeSlashCommands.cs has /new, /compact, no /clear]

## 10. How CC Director integrates it

Current wiring [VERIFIED from source]:
- Driver: GenericDriver, constructed for AgentKind.OpenCode (no bespoke OpenCodeDriver). It declares
  only `DriverCapabilities.Cancel | Interrupt`. Cancel sends Esc (0x1B); Interrupt sends Ctrl+C
  (0x03). `ClearContextAsync`, `ShowHistoryAsync`, `ReadWidgets`, `ReadUsage`, `ListTranscripts`,
  `BuildLaunchSpec`, `ResolveExecutable` all throw NotSupported - the conservative "these two
  keystrokes are all we have verified" contract. (GenericDriver.cs)
- Agent: OpenCodeAgent - passes user args through, no preassigned session id, no resume, no Studio
  mode. (OpenCodeAgent.cs)
- Plugin: OpenCodeAgentPlugin - detection (`%APPDATA%\npm\opencode.cmd` then `opencode`), validation
  (`--version`, 8s), history provider `SqliteStore`, single Standard command preset, no default
  model. (OpenCodeAgentPlugin.cs)
- History: OpenCodeHistoryReader over the SQLite store (Section 8).
- Shared endpoint: the Director exposes `GET /sessions/{sid}/fleet-preamble` (FleetPreamble.cs,
  ControlEndpoints.cs) - agent-agnostic, returns the preamble text for a session. Every mechanism
  family pulls from here. [VERIFIED - src/CcDirector.Core/Sessions/FleetPreamble.cs]

Fleet-preamble strategy. opencode is the canonical family B agent, with family C as a robust
alternative. Both target the same trigger pair: inject at session start, re-inject on
`session.created` (the reset) and `session.compacted` (the compaction).

Plan B - out-of-process event bus (preferred, matches opencode's architecture):
1. For an opencode session, launch via `opencode serve` (or attach to the TUI's own server) so a
   local HTTP server with an SSE bus exists. Capture the chosen port.
2. From the Director, open `GET /event` and read the SSE stream. Discard the leading
   `server.connected`, then watch for `session.created` and `session.compacted` scoped to this
   session/repo.
3. On either event, fetch the preamble from `GET /sessions/{sid}/fleet-preamble`, then push it into
   the opencode session with `POST /session/:id/message` (or SDK `session.prompt` with
   `noReply: true`) so context lands WITHOUT generating a model reply.
4. If auth is enabled, supply `OPENCODE_SERVER_USERNAME`/`OPENCODE_SERVER_PASSWORD` basic auth.

Tradeoffs of B: cleanest fit for opencode (it is built to be driven over HTTP); fully out of
process, so no code runs inside the agent; handles auto-compaction we did not trigger. Costs: we
must own a server lifecycle and a port, manage the SSE connection (reconnect on drop), and the
`session.created`/`session.compacted` scoping to the right session must be verified live. It only
works when a server is reachable - a plain TUI launch may need `--server`/serve coordination.

Plan C - in-process opencode plugin (alternative / belt-and-suspenders):
1. Ship a small first-party plugin into `~/.config/opencode/plugin/` (or per-project
   `.opencode/plugin/`).
2. In its `experimental.chat.system.transform` hook, fetch the Director preamble (the plugin's
   `client`/`$`/`fetch` can call `GET /sessions/{sid}/fleet-preamble`) and append it to the system
   prompt - this self-heals on every chat, covering both fresh sessions and post-compaction turns.
3. Optionally also implement `experimental.session.compacting` to fold the preamble into the
   compaction prompt so it survives summarization explicitly, and the `event` catch-all to react to
   `session.created`/`session.compacted`.

Tradeoffs of C: passive and self-healing (no event plumbing, no reconnects); naturally survives
compaction. Costs: relies on EXPERIMENTAL hooks that can be renamed/removed (pin the version);
requires installing a TypeScript plugin on the user's machine (Bun/node_modules footprint); runs
in-process, which is a higher trust surface than B.

Recommended: B as the primary (subscribe to `GET /event`, inject on `session.created` /
`session.compacted` via `noReply` message), with the file-based AGENTS.md / `instructions` array as
the always-on passive backstop (family D) so even a server-less plain TUI launch still carries the
preamble. C is the upgrade if/when we want survival across auto-compaction without owning a server.

Why not family A (the Claude path): opencode has no SessionStart shell hook that reads our command's
stdout as additionalContext. Attempting to emulate Claude's model here would be inventing a
mechanism opencode does not expose. Use the bus or a plugin.

Current gaps in our wiring:
- No bespoke OpenCodeDriver: ClearContext/history-picker/transcript-via-driver all throw; history
  comes only through OpenCodeHistoryReader, not the driver.
- No event-bus subscriber implemented yet: we do not start `opencode serve`, do not read `/event`,
  and do not push the preamble. The plan above is design, not code.
- No opencode plugin shipped.
- We do not parse token usage from the SQLite store.

## 11. Caveats and verification needed

- High churn: opencode ships fast and the SST team renames things. Treat every event name, endpoint,
  and config key as version-pinned; re-check against the installed binary and against `GET /doc`
  (the live OpenAPI) before depending on it. [INFERRED/UNCERTAIN]
- Plugin directory name: docs prose has used both `plugin/` and `plugins/` (and `agent/` vs
  `agents/`, `command/` vs `commands/`). Confirm the exact singular/plural folder on disk before
  shipping a plugin. [INFERRED/UNCERTAIN - docs/plugins, docs/agents, docs/commands]
- The `noReply` field name on `session.prompt` (context-inject-without-response) must be confirmed
  against the installed SDK/OpenAPI - exact casing/spelling is load-bearing for Plan B. [VERIFIED in
  docs as `noReply`; confirm against installed `/doc`]
- All `experimental.*` plugin hooks are unstable by name. Pin and re-verify. [VERIFIED experimental]
- `session.created`/`session.compacted` payload shape (does it carry sessionID and directory so we
  can scope to the right Director session?) needs a live capture off `GET /event`. [INFERRED/UNCERTAIN]
- Whether the TUI launched by a bare `opencode` exposes a reachable `/event` server (and on which
  port) versus needing `opencode serve` separately must be verified live; our OpenCodeAgent launch
  does not currently start a server. [INFERRED/UNCERTAIN]
- SQLite path on Windows: code uses `~/.local/share/opencode/opencode.db`; confirm opencode writes
  there on Windows rather than under `%LOCALAPPDATA%`/`%APPDATA%`. [INFERRED/UNCERTAIN -
  OpenCodeHistoryReader.cs assumes the Unix-style path under the user profile]

## Sources
- https://opencode.ai/docs/ (overview, install)
- https://opencode.ai/docs/cli/ (commands and flags)
- https://opencode.ai/docs/config/ (opencode.json, precedence, keys)
- https://opencode.ai/docs/rules/ (AGENTS.md, CLAUDE.md fallback, instructions array, /init)
- https://opencode.ai/docs/agents/ (primary vs subagents, per-agent prompt/model/mode fields)
- https://opencode.ai/docs/commands/ (custom slash commands, frontmatter, argument/shell/file syntax)
- https://opencode.ai/docs/server/ (opencode serve, flags, auth env vars, /doc, /event, /global/event, endpoint list, /tui control surface)
- https://opencode.ai/docs/plugins/ (plugin locations, signature, hook list including experimental.*, event catch-all, bus event types)
- https://opencode.ai/docs/sdk/ (@opencode-ai/sdk, createOpencodeClient/createOpencode, session.create/prompt/messages, event.subscribe, noReply)
- https://github.com/sst/opencode , packages/plugin/src/index.ts (Hooks interface)
- src/CcDirector.Core/Agents/OpenCodeAgent.cs (our agent)
- src/CcDirector.Core/AgentPlugins/OpenCodeAgentPlugin.cs (our plugin metadata)
- src/CcDirector.Core/Drivers/GenericDriver.cs (our driver)
- src/CcDirector.Core/Drivers/OpenCodeSlashCommands.cs (TUI command catalog)
- src/CcDirector.Core/OpenCode/OpenCodeHistoryReader.cs (SQLite history reader)
- src/CcDirector.Core/Sessions/FleetPreamble.cs , src/CcDirector.ControlApi/ControlEndpoints.cs (shared fleet-preamble endpoint)
