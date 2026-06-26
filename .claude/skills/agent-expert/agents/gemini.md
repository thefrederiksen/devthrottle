<!--
Per-agent reference for the Google Gemini CLI.
Rules: ASCII only. No Unicode, no emoji, no em-dashes (use " - ").
Every non-trivial fact is marked [VERIFIED from docs] or [INFERRED/UNCERTAIN] with an inline source.
All links are collected in the Sources section.
-->

# Google Gemini CLI (Gemini)

> Google's open-source terminal coding agent. npm `@google/gemini-cli`, binary `gemini`.
> Our integration status: GenericDriver (no bespoke driver yet) / GeminiAgentPlugin / history provider kind TerminalBuffer.

IMPORTANT recency note up front: the Gemini CLI moves fast and the locally installed
binary on this machine is version 0.1.11 (probed live: `gemini --version` -> `0.1.11`,
installed at `C:\Users\soren\AppData\Roaming\npm\gemini.cmd`). That 0.1.11 build PREDATES
the hooks system, `--output-format`, `--resume`, `--approval-mode`, and `--include-directories`.
Most of this document describes the CURRENT documented CLI (geminicli.com/docs and the
main branch of the source repo), which is well ahead of 0.1.11. Treat every "newer feature"
as not present until the installed binary is upgraded and re-probed. See section 11.

---

## 1. Identity and install

- Binary: `gemini`. [VERIFIED from docs - https://geminicli.com/docs/]
- npm package: `@google/gemini-cli` (install: `npm install -g @google/gemini-cli`, or run via
  `npx https://github.com/google-gemini/gemini-cli`). [VERIFIED from docs - https://github.com/google-gemini/gemini-cli]
- Source repository: https://github.com/google-gemini/gemini-cli (Apache-2.0, Google).
  [VERIFIED from repo - https://github.com/google-gemini/gemini-cli]
- Official documentation home: https://geminicli.com/docs/ (mirrors the in-repo `docs/`
  tree at https://github.com/google-gemini/gemini-cli/tree/main/docs). [VERIFIED]
- Core engine package: `@google/gemini-cli-core` (the reusable backend the CLI is built on).
  [INFERRED/UNCERTAIN - package exists in the monorepo; not independently re-verified here]

Windows install location of the launchable shim (probed live on this machine):
- `C:\Users\soren\AppData\Roaming\npm\gemini` (Unix-style shim) and
  `C:\Users\soren\AppData\Roaming\npm\gemini.cmd` (the Windows launcher CC Director should spawn).
  [VERIFIED live - `where gemini`]
- Version-probe command: `gemini --version` (alias `-v`). Probed: `0.1.11`. [VERIFIED live]

---

## 2. Command-line interface

Default invocation `gemini` with no prompt opens the INTERACTIVE terminal UI (a React/Ink TUI).
[VERIFIED live - `gemini --help` first line: "Launch an interactive CLI, use -p/--prompt for non-interactive mode"]

Non-interactive (headless) mode is triggered by ANY of: passing `-p`/`--prompt`, piping data
on stdin (for example `echo "query" | gemini`), or running in a non-TTY environment.
[VERIFIED from docs - https://geminicli.com/docs/cli/headless/]
Note: `--prompt` text is APPENDED to whatever arrives on stdin, so `cat file | gemini -p "..."`
sends both. [VERIFIED live - help text: "Prompt. Appended to input on stdin (if any)."]

Flag table. Flags marked (0.1.11) are present in the installed build; flags marked (current)
are documented on main but NOT in 0.1.11 help output.

| Flag | Purpose | Status |
|------|---------|--------|
| `-m, --model <id>` | Model id (0.1.11 default `gemini-2.5-pro`) | (0.1.11) [VERIFIED live] |
| `-p, --prompt <text>` | One-shot non-interactive prompt; appended to stdin | (0.1.11) [VERIFIED live] |
| `-i, --prompt-interactive <text>` | Run a prompt, then stay in the interactive UI | (current) [VERIFIED from docs - https://geminicli.com/docs/cli/headless/] |
| `--output-format <text\|json>` | `json` returns one object `{response, stats, error}`; streaming is JSONL (`init`,`message`,`tool_use`,`tool_result`,`error`,`result`) | (current) [VERIFIED from docs - https://geminicli.com/docs/cli/headless/] |
| `-r, --resume [index\|uuid]` | Resume a saved session: no arg = most recent, integer = by index, UUID = by session id | (current) [VERIFIED from docs - https://geminicli.com/docs/cli/session-management/] |
| `-y, --yolo` | Auto-accept all actions (YOLO) | (0.1.11) [VERIFIED live] |
| `--approval-mode <text>` | Finer-grained approval policy (default / auto-edit / yolo) | (current) [INFERRED/UNCERTAIN - referenced in headless docs, not in 0.1.11 help] |
| `-s, --sandbox` / `--sandbox-image <uri>` | Run tools in a sandbox | (0.1.11) [VERIFIED live] |
| `-a, --all-files` | Include ALL files in context | (0.1.11) [VERIFIED live] |
| `--include-directories <dirs>` | Add extra workspace directories | (current) [INFERRED/UNCERTAIN - not in 0.1.11 help] |
| `-c, --checkpointing` | Snapshot project state before file edits | (0.1.11) [VERIFIED live] |
| `-e, --extensions <list>` | Restrict to named extensions | (0.1.11) [VERIFIED live] |
| `-l, --list-extensions` | List extensions and exit | (0.1.11) [VERIFIED live] |
| `--allowed-mcp-server-names <list>` | Allowlist MCP servers | (0.1.11) [VERIFIED live] |
| `-d, --debug` | Debug mode | (0.1.11) [VERIFIED live] |
| `--show-memory-usage` | Show memory usage in status bar | (0.1.11) [VERIFIED live] |
| `--telemetry*` | Telemetry target / endpoint / log-prompts | (0.1.11) [VERIFIED live] |
| `-v, --version`, `-h, --help` | Version / help | (0.1.11) [VERIFIED live] |

Headless exit codes: `0` success, `1` general error / API failure, `42` input error
(bad prompt/args), `53` turn limit exceeded. [VERIFIED from docs - https://geminicli.com/docs/cli/headless/]

In-session slash commands relevant to context and sessions:
- `/memory show` | `/memory add <text>` | `/memory refresh` (also documented as `/memory reload`).
  [VERIFIED from docs - https://geminicli.com/docs/cli/gemini-md/ and https://geminicli.com/docs/cli/tutorials/memory-management/]
- `/clear` (clear the conversation/screen), `/compress` (replace history with a summary),
  `/chat save <tag>` / `/chat resume <tag>` / `/chat list` / `/chat share <file.md|file.json>`,
  `/resume` (Session Browser), `/quit`. [VERIFIED from docs - https://geminicli.com/docs/reference/commands/ and https://geminicli.com/docs/cli/session-management/]
- `@path` references include a file/dir in the next prompt; `!` runs a shell command. [VERIFIED from docs - https://geminicli.com/docs/reference/commands/]

---

## 3. Configuration

Primary config is `settings.json` (JSON). Three file scopes, plus env vars and CLI args.
[VERIFIED from docs - https://geminicli.com/docs/reference/configuration/]

| Scope | Path |
|-------|------|
| System | Windows `C:\ProgramData\gemini-cli\settings.json`; Linux `/etc/gemini-cli/settings.json`; macOS `/Library/Application Support/GeminiCli/settings.json` |
| User | `~/.gemini/settings.json` (Windows `%USERPROFILE%\.gemini\settings.json`) |
| Project | `.gemini/settings.json` in the project root |

[VERIFIED from docs - https://geminicli.com/docs/reference/configuration/]

Precedence, lowest to highest: hardcoded defaults -> system defaults file -> USER settings ->
PROJECT settings -> SYSTEM settings (overrides all files) -> environment variables ->
command-line arguments. NOTE the unusual rule: the SYSTEM settings file overrides user and
project, the opposite of the GEMINI.md context hierarchy (where more-specific wins). [VERIFIED from docs - https://geminicli.com/docs/reference/configuration/]

Keys that matter to us:
- `context.fileName` - string or array of strings; the context filename(s) to load instead of /
  in addition to `GEMINI.md`. Example: `{"context": {"fileName": ["AGENTS.md","GEMINI.md"]}}`.
  [VERIFIED from docs - https://geminicli.com/docs/cli/gemini-md/]
- `hooks` - object keyed by event name; each value is an array of hook entries. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- `hooksConfig.enabled` - master on/off toggle for the entire hooks system. [VERIFIED from docs - https://geminicli.com/docs/reference/configuration/]
- `mcpServers` - MCP server definitions (see section 7). [VERIFIED from docs]

---

## 4. Context injection (how to inject a preamble)

For each mechanism: "survives /clear?" = is the content still present in the model context
immediately after `/clear`; "survives /compact?" = is it still present after `/compress`
summarization.

1. GEMINI.md hierarchical context files. The CLI discovers context files and CONCATENATES
   them and sends the combined block to the model WITH EVERY PROMPT. Hierarchy: global
   `~/.gemini/GEMINI.md` -> project root and ancestor `GEMINI.md` -> subdirectory `GEMINI.md`
   (just-in-time, scanned as tools touch files). Supports `@path.md` imports to modularize.
   [VERIFIED from docs - https://geminicli.com/docs/cli/gemini-md/]
   - Survives /clear: YES. Because the file is re-read and re-sent on every prompt, a `/clear`
     that wipes conversation does not remove GEMINI.md content - it returns on the next turn.
     [INFERRED/UNCERTAIN - follows directly from "sent with every prompt"; verify live]
   - Survives /compact: YES, for the same reason - GEMINI.md is re-injected each prompt
     regardless of history compression. This is why CC Director uses GEMINI.md for the
     compaction-survival path (Family D). [INFERRED/UNCERTAIN - verify live]
   - Filename configurable via `context.fileName`. [VERIFIED from docs - https://geminicli.com/docs/cli/gemini-md/]

2. `/memory add <text>` / `/memory show` / `/memory refresh`(`reload`). `/memory add` appends
   to memory at runtime; `/memory refresh` forces a re-scan and reload of all GEMINI.md files
   and updates the model with the latest content; `/memory show` prints the concatenated memory.
   [VERIFIED from docs - https://geminicli.com/docs/cli/gemini-md/ and https://geminicli.com/docs/cli/tutorials/memory-management/]
   - Caveat: there is a known bug class where `/memory refresh` did not always update the live
     system instruction in an existing chat (issue #10702, PR #12136). [VERIFIED - https://github.com/google-gemini/gemini-cli/issues/10702]

3. SessionStart hook `hookSpecificOutput.additionalContext` (newer builds). A SessionStart
   hook command can print JSON whose `hookSpecificOutput.additionalContext` string is injected
   into the model context. In INTERACTIVE mode it is "injected as the first turn in history";
   in NON-INTERACTIVE mode it is "prepended to the user's prompt". [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
   - Survives /clear: YES by re-fire - SessionStart fires again with `source: "clear"` after a
     `/clear`, so the hook re-injects. [VERIFIED from docs - SessionStart fires "after a /clear command" - https://geminicli.com/docs/hooks/reference/]
   - Survives /compact: NO directly - there is NO SessionStart `compact` source and no
     re-fire of SessionStart on `/compress`. Because `additionalContext` is injected as a
     history turn (not re-sent each prompt), compaction can summarize it away. The supported
     compaction signal is the separate PreCompress hook (see section 5). This is the key gap;
     see the benchmark note below. [VERIFIED from docs - source list is startup/resume/clear only]

Benchmark vs Claude Code: Claude Code's SessionStart hook emits
`hookSpecificOutput.additionalContext` and fires with four sources - startup, resume, clear,
AND compact - so a single hook re-injects after compaction. Gemini's SessionStart emits the
SAME field name (`hookSpecificOutput.additionalContext`) but its source set is ONLY
startup/resume/clear - there is NO `compact` source. Gemini handles compaction differently:
the PreCompress hook fires before summarization (trigger `auto`/`manual`), and GEMINI.md
persistence (re-sent every prompt) is what actually keeps a preamble alive across a compaction.
So Gemini matches Claude on launch and clear, but NOT on compact-via-hook-reinjection; you
must lean on GEMINI.md (Family D) for compaction durability. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]

---

## 5. Lifecycle events and hooks

Hooks are shell commands registered in the `hooks` block of `settings.json` (and bundled by
extensions via `hooks/hooks.json`). Master toggle `hooksConfig.enabled`. All hooks read a JSON
object on stdin and may write a JSON object on stdout; stderr is for logs / rejection reasons.
[VERIFIED from docs - https://geminicli.com/docs/hooks/reference/ and https://geminicli.com/docs/hooks/writing-hooks/]

Full event list (11): [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- SessionStart - application startup, session resume, or after `/clear`.
- SessionEnd - CLI exits or a session is cleared.
- BeforeAgent - after the user submits a prompt, before the agent begins planning.
- AfterAgent - once per turn after the model generates its final response.
- BeforeModel - before sending a request to the LLM.
- AfterModel - immediately after an LLM response chunk is received.
- BeforeToolSelection - before the LLM decides which tools to call.
- BeforeTool - before a tool is invoked.
- AfterTool - after a tool executes.
- Notification - when the CLI emits a system alert.
- PreCompress - before the CLI summarizes history to save tokens.

Settings shape (two documented forms appear in the docs; the matcher form is the general one):
```json
{
  "hooks": {
    "SessionStart": [
      { "type": "command", "command": "my-preamble-hook" }
    ],
    "PreCompress": [
      { "matcher": "", "hooks": [ { "type": "command", "command": "my-precompress-hook" } ] }
    ]
  }
}
```
[VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]

stdin JSON contract. Base fields common to all hooks:
```json
{
  "session_id": "string",
  "transcript_path": "string",
  "cwd": "string",
  "hook_event_name": "string",
  "timestamp": "string"
}
```
[VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- SessionStart ADDS `"source"` with exactly one of `"startup"`, `"resume"`, `"clear"`.
  There is NO `"compact"` source. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- PreCompress ADDS `"trigger"` with one of `"auto"` (CLI hit the token threshold) or
  `"manual"` (user ran `/compress`). [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]

stdout JSON contract (the bit we use):
- SessionStart can return `{"hookSpecificOutput": {"additionalContext": "..."}}`. Interactive:
  injected as the first turn in history. Non-interactive: prepended to the user's prompt.
  A `systemMessage` field is also accepted. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- PreCompress supports `systemMessage` only; flow-control fields are ignored, and it does NOT
  inject additionalContext. So PreCompress is a SIGNAL ("compaction is about to happen"), not
  an injection point. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]

Which events fire on each transition:
- Startup (new session): SessionStart `source=startup`. [VERIFIED from docs]
- Resume (`--resume`): SessionStart `source=resume`. [VERIFIED from docs]
- Clear (`/clear`): SessionEnd then SessionStart `source=clear` (re-fire). [VERIFIED from docs]
- Compact (`/compress` or auto threshold): PreCompress `trigger=manual|auto`. NO SessionStart.
  [VERIFIED from docs]

Hooks history note: the comprehensive hooking system was tracked in issue #9070 and landed
across recent PRs (for example PR #14151 added session-lifecycle and compression integration).
This is new and churning. [VERIFIED - https://github.com/google-gemini/gemini-cli/issues/9070]

---

## 6. SDK / programmatic API / server mode

- The supported programmatic surface for the stock CLI is HEADLESS mode with
  `--output-format json` (single object `{response, stats, error}`) or streaming JSONL
  (`init`/`message`/`tool_use`/`tool_result`/`error`/`result` events). This is the clean
  machine-readable channel; it is what an integrator scripts against. [VERIFIED from docs - https://geminicli.com/docs/cli/headless/]
- There is NO documented long-running local HTTP/RPC server mode in the stock CLI comparable
  to `opencode serve`. The CLI is launched per task; for streaming you read its stdout JSONL.
  [INFERRED/UNCERTAIN - absence of a server doc; an experimental Agent-to-Agent / A2A server
  has appeared in the repo at times but is not a stable integration surface - verify before use]
- The reusable backend is the `@google/gemini-cli-core` package; embedding it is a Node/TypeScript
  path, not a cross-process API. CC Director drives the CLI as a terminal child process, not via
  this library. [INFERRED/UNCERTAIN - verify package name/exports if we ever embed it]
- Auth: Google account OAuth (free tier), Gemini API key (`GEMINI_API_KEY`), or Vertex AI.
  [INFERRED/UNCERTAIN - standard Gemini CLI auth; not re-verified in this pass]

---

## 7. MCP / extensions / commands

- MCP servers: declared under `mcpServers` in `settings.json` (and bundled by extensions).
  The CLI is an MCP client; `--allowed-mcp-server-names` / `--allowed-mcp-server-names`
  allowlists, `/mcp` inspects. [VERIFIED from docs - https://geminicli.com/docs/reference/configuration/ and live help `--allowed-mcp-server-names`]
- Custom slash commands: TOML files placed in a `commands/` directory - user-level
  `~/.gemini/commands/` and project-level `.gemini/commands/`, or bundled in an extension's
  `commands/` directory. [VERIFIED from docs - https://geminicli.com/docs/extensions/reference/]
- Extensions: installed under `~/.gemini/extensions/<name>/`, each described by a
  `gemini-extension.json` manifest with fields `name`, `version`, `mcpServers`,
  `contextFileName` (defaults to `GEMINI.md` in the extension dir if omitted), and
  `excludeTools`. An extension can bundle MCP servers, custom commands (TOML in `commands/`),
  a context file (GEMINI.md), HOOKS (`hooks/hooks.json`), sub-agents, and skills. This makes an
  extension a single-package way to ship a hook + a context file + commands together.
  [VERIFIED from docs - https://geminicli.com/docs/extensions/ and https://geminicli.com/docs/extensions/reference/]
- NOTE a naming inconsistency to watch: the extension manifest field is `contextFileName`
  (camelCase, no dot), while the settings.json key is `context.fileName` (dotted). Do not mix
  them up. [VERIFIED from docs - compare the two pages above]

---

## 8. Transcript / history

Current docs: sessions ARE persisted to disk automatically. Location
`~/.gemini/tmp/<project_hash>/chats/`, where `<project_hash>` derives from the project root.
What is recorded: "Your prompts and the model's responses. All tool executions (inputs and
outputs). Token usage statistics (input, output, cached, etc.). Assistant thoughts and
reasoning summaries (when available)." [VERIFIED from docs - https://geminicli.com/docs/cli/session-management/]
- Each hook also receives a `transcript_path` pointing at the session transcript. [VERIFIED from docs - https://geminicli.com/docs/hooks/reference/]
- File format (JSON vs other) is not pinned down by the docs; needs a live read. [INFERRED/UNCERTAIN]
- Token usage is available both in the persisted `stats` and in the headless `--output-format json`
  `stats` block. [VERIFIED from docs - https://geminicli.com/docs/cli/headless/]

CONFLICT to resolve: CC Director's current history provider for Gemini is TerminalBuffer, on the
assumption "Gemini does not persist assistant responses - terminal capture only". The CURRENT docs
contradict that - newer Gemini CLI persists full transcripts including assistant responses to
`~/.gemini/tmp/<project_hash>/chats/`. The assumption likely dates from an older build (the
installed 0.1.11 may predate on-disk chat persistence). Action: verify what 0.1.11 (and whatever
we ship against) actually writes before relying on a file-based history provider. See section 11.
[VERIFIED docs vs INFERRED legacy assumption - https://geminicli.com/docs/cli/session-management/]

---

## 9. Session semantics

- New / startup: `gemini` boots a fresh session with a new `session_id`; SessionStart
  `source=startup`. [VERIFIED from docs]
- Clear (`/clear`): wipes the visible conversation/history for the SAME session; SessionEnd
  fires, then SessionStart re-fires with `source=clear`. The `session_id` is understood to
  persist across `/clear` (same session, cleared history). [VERIFIED for hook behavior; session_id
  continuity is INFERRED/UNCERTAIN - verify live]
- Compact (`/compress`, or automatic at the token threshold): replaces history with a summary
  to save tokens; PreCompress fires (`trigger=manual|auto`). No new session, no SessionStart.
  [VERIFIED from docs]
- Resume (`--resume`/`-r`, or `/resume` Session Browser, or `/chat resume <tag>`): reloads a
  prior persisted session; SessionStart `source=resume`. `--resume` with no arg = most recent,
  integer = by index, UUID = by id. [VERIFIED from docs - https://geminicli.com/docs/cli/session-management/]
- Checkpointing (`-c`/`/checkpointing`) is a SEPARATE feature: it snapshots PROJECT FILE STATE
  before tool edits so you can revert file changes; it is not conversation history. [VERIFIED from docs]

---

## 10. How CC Director integrates it

Current wiring:
- Driver class: GenericDriver (Gemini has no bespoke driver). GenericDriver declares ONLY the
  Cancel and Interrupt capabilities and THROWS on ClearContext - so today CC Director cannot
  programmatically clear a Gemini session, and has no driver-level clear/compact detection.
- Agent class: GeminiAgent. Plugin: GeminiAgentPlugin.
- History provider kind: TerminalBuffer (we scrape the terminal, we do not read a transcript file).

Mechanism families for the fleet preamble:
- Family A (settings.json SessionStart hook emitting `additionalContext`) for LAUNCH and CLEAR.
- Family D (GEMINI.md persistence) for COMPACTION survival.

Concrete plan:
1. Family A - install a SessionStart hook in USER-level `~/.gemini/settings.json`:
   ```json
   {
     "hooks": {
       "SessionStart": [
         { "type": "command",
           "command": "<helper that does: GET http://127.0.0.1:<port>/sessions/{sid}/fleet-preamble and prints {\"hookSpecificOutput\":{\"additionalContext\":\"<preamble>\"}}>" }
       ]
     }
   }
   ```
   The hook receives `session_id` and `source` on stdin; map `session_id` to our `{sid}` and
   call the existing agent-agnostic Director endpoint `GET /sessions/{sid}/fleet-preamble`.
   Emit the body as `hookSpecificOutput.additionalContext`. This covers `source=startup`,
   `source=resume`, and `source=clear` in one hook (clear re-fires SessionStart). [PLAN; field
   names VERIFIED from docs]
2. Family D - write the preamble into a USER-level `~/.gemini/GEMINI.md` (or an extension-bundled
   context file). Because GEMINI.md is concatenated and re-sent with EVERY prompt, the preamble
   survives `/compress` (where the SessionStart hook does NOT re-fire). This is the compaction
   backstop the hook cannot provide. [PLAN; persistence VERIFIED from docs]
3. Optional consolidation - ship both the hook and the context file as a single Gemini EXTENSION
   under `~/.gemini/extensions/cc-director/` (manifest `gemini-extension.json` + `hooks/hooks.json`
   + `GEMINI.md`), so install/uninstall is one unit. [PLAN]

Gaps in the current integration:
- No bespoke GeminiDriver: ClearContext throws, so we cannot drive `/clear` or detect
  clear/compact from the driver. Detection currently depends on TerminalBuffer scraping.
- The Family A hook is NOT YET wired (only Claude's `ClaudeHookInstaller` exists). We need a
  GeminiHookInstaller writing to `~/.gemini/settings.json`.
- TerminalBuffer may be the wrong history provider now that the CLI persists transcripts (section 8).

---

## 11. Caveats and verification needed

- VERSION SKEW is the headline risk. Installed binary is 0.1.11; its `--help` shows NONE of:
  hooks, `--output-format`, `--resume`/`-r`, `--prompt-interactive`/`-i`, `--approval-mode`,
  `--include-directories`. Everything in sections 4-9 that depends on those is CURRENT-docs
  behavior, not 0.1.11 behavior. Before wiring Family A, upgrade and re-probe
  (`gemini --version`, `gemini --help`, and a live hooks smoke test). [VERIFIED live - 0.1.11 help]
- Hooks are new and churning (issue #9070; PR #14151). The `hooks` settings shape appears in two
  forms in the docs (flat `[{type,command}]` and matcher `[{matcher,hooks:[...]}]`); confirm which
  the target binary accepts for SessionStart. [VERIFIED issues exist]
- `/memory refresh` has a known bug where it did not always update the live system instruction
  (#10702). If we rely on `/memory` for re-injection, test it; prefer GEMINI.md file persistence.
  [VERIFIED - #10702]
- Confirm `session_id` continuity across `/clear` (same id vs new id) live - it drives how the
  hook maps to our `{sid}`. [INFERRED/UNCERTAIN]
- Confirm transcript on-disk format and whether the shipped build writes
  `~/.gemini/tmp/<project_hash>/chats/` before swapping TerminalBuffer for a file reader.
  [INFERRED/UNCERTAIN]
- Watch the `context.fileName` (settings) vs `contextFileName` (extension manifest) naming split.
  [VERIFIED from docs]
- System `settings.json` overrides user/project (unusual). If a customer machine has a managed
  system settings file, it could override our user-level hook. [VERIFIED from docs]

## Sources

- https://geminicli.com/docs/ (documentation home)
- https://github.com/google-gemini/gemini-cli (source repository)
- https://github.com/google-gemini/gemini-cli/tree/main/docs (in-repo docs)
- https://geminicli.com/docs/cli/headless/ (non-interactive mode, output-format, exit codes)
- https://geminicli.com/docs/reference/commands/ (slash commands, @ and ! syntax)
- https://geminicli.com/docs/reference/configuration/ (settings.json locations, precedence, hooksConfig.enabled)
- https://geminicli.com/docs/cli/gemini-md/ (GEMINI.md hierarchy, context.fileName, @imports, /memory)
- https://geminicli.com/docs/cli/tutorials/memory-management/ (/memory show|add|refresh)
- https://geminicli.com/docs/hooks/ (hooks overview)
- https://geminicli.com/docs/hooks/reference/ (event list, stdin/stdout JSON, SessionStart sources, additionalContext, PreCompress trigger)
- https://geminicli.com/docs/hooks/writing-hooks/ (hook authoring; stdin/stdout/stderr contract)
- https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/writing-hooks.md (hook authoring, source)
- https://geminicli.com/docs/cli/session-management/ (session persistence path, what is recorded, --resume)
- https://geminicli.com/docs/extensions/ (extension capabilities: MCP, commands, hooks, context, sub-agents, skills)
- https://geminicli.com/docs/extensions/reference/ (gemini-extension.json fields, install path, contextFileName)
- https://github.com/google-gemini/gemini-cli/issues/9070 (comprehensive hooking system tracking issue)
- https://github.com/google-gemini/gemini-cli/pull/14151 (hook session-lifecycle and compression integration)
- https://github.com/google-gemini/gemini-cli/issues/10702 (/memory refresh system-instruction bug)
- https://github.com/google-gemini/gemini-cli/pull/12136 (fix: update system instruction on GEMINI.md load/refresh)
- Live probe on this machine: `gemini --version` -> 0.1.11; `gemini --help`; `where gemini` ->
  C:\Users\soren\AppData\Roaming\npm\gemini(.cmd)
