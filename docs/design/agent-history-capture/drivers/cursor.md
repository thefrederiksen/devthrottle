# Driver plan: Cursor (history capture)

Plan for the Cursor CLI agent (`cursor-agent`, marketed as "Agent"). This driver is
DEFERRED. The reason is not a missing transcript - it is that Cursor's command line agent
has no native Windows build, so a Windows Director cannot host it the way it hosts every
other agent. This document records what was measured on 2026-06-25 and the exact condition
under which the driver becomes worth building.

Status: DEFERRED - no native Windows build. Measured, not guessed.

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Agent kind | `Cursor` (`AgentKind.Cursor = 6`), launched by cc-director as `cursor-agent` |
| Native Windows build | NONE. The official installer (`https://cursor.com/install`) is a bash script that accepts only `uname -s` of `Linux*` or `Darwin*` and exits on anything else. `https://cursor.com/install.ps1` is not an installer - it returns the Cursor marketing homepage (text/html, ~800 KB). |
| How it runs on Windows | Only under WSL (or Linux/macOS). Installed and probed in WSL Ubuntu; version `2026.06.24-00-45-58-9f61de7`. |
| Terminal mode | NOT measured. Cannot be classified on Windows because the agent does not run as a native Windows process; would have to be measured inside WSL. |
| Store | NOT captured. After install (unauthenticated) only `~/.cursor/cli-config.json` and the versioned binary under `~/.local/share/cursor-agent/` exist. The chat/session store is created only after a real authenticated session, which was not run (see below). |
| Session model | Chats with ids: `--resume [chatId]`, `--continue`. Headless mode exists: `-p/--print` with `--output-format text|json|stream-json`. Non-interactive run flags: `--trust`, `--force`/`--yolo`, `--api-key` (or `CURSOR_API_KEY`). |
| Auth | `cursor-agent status` reported "Not logged in"; no `CURSOR_API_KEY` in the machine credentials file. A real session needs an interactive `cursor-agent login` device flow or an API key. |
| Pointer / launch wiring | N/A while deferred. |

---

## 2. Why this driver is deferred

Every other supported agent (Claude, Codex, Pi, Grok, Copilot, OpenCode, Gemini) runs as a
native Windows process that cc-director launches directly, and writes its history to a
store under the Windows user profile that a Windows reader can open. Cursor's CLI does
neither on Windows:

1. There is no native Windows `cursor-agent`. The vendor ships Linux and macOS only.
2. Under WSL it works, but then the agent process lives in the Linux VM and its history is
   written to the WSL filesystem (for example `\\wsl$\Ubuntu\home\<user>\...`), not the
   Windows profile. A Windows Director would have to reach across the WSL boundary both to
   LAUNCH it (shell through `wsl.exe`) and to READ its store - a fundamentally different
   integration shape from every other driver.

So unlike Gemini (which is a fidelity problem - it runs natively but provides no usable
transcript), Cursor is a HOSTING problem: there is nothing native to host on Windows. That
makes a history driver premature - there is no Windows session to show history for yet.

---

## 3. What was measured (2026-06-25)

- Installer is Linux/macOS only; `install.ps1` is not a real script (marketing page).
- Installed in WSL Ubuntu: `cursor-agent` version `2026.06.24-00-45-58-9f61de7`,
  symlinked at `~/.local/bin/cursor-agent`, binary under
  `~/.local/share/cursor-agent/versions/<version>/`.
- Config written on first run: `~/.cursor/cli-config.json` (editor/display/permissions/
  approvalMode/sandbox settings; no session path).
- CLI surface relevant to a future driver: `--resume [chatId]`, `--continue` (so chats are
  persisted and addressable by id); `-p --output-format json|stream-json` (a clean headless
  event stream we could capture directly, like Codex's `exec` JSON); `--trust`,
  `--force`/`--yolo` (non-interactive); `--api-key`/`CURSOR_API_KEY` (headless auth).
- `cursor-agent status` = "Not logged in"; the chat store directory was therefore never
  created and its location/format remain UNKNOWN.

---

## 4. Condition to un-defer (what would make this worth building)

Either of these flips Cursor from "deferred" to "build it like the others":

- A native Windows `cursor-agent` ships (vendor adds a Windows target), so cc-director can
  launch it directly and it writes a store under the Windows profile. Then do the standard
  forensic survey: authenticate, run a session that forces a tool call, locate the chat
  store, classify the terminal mode, and write `CursorSessionLocator` + `CursorTranscriptReader`
  + the `SessionHistoryReader` branch + tests, exactly like Codex/Pi.
- OR cc-director decides to support WSL-hosted agents generally (launch via `wsl.exe`, read
  stores across `\\wsl$`). That is a broader platform decision, not a per-driver one, and
  should be its own design - the Cursor history driver would then ride on top of it.

A likely shortcut once un-deferred: Cursor's `-p --output-format stream-json` headless mode
emits a structured event stream (similar to Codex `exec`), so the reader may be able to map
that stream directly into `ConversationHistory` rather than reverse-engineering an on-disk
chat file.

---

## 5. Scope while deferred

- No code. `SessionHistoryReader` does NOT add an `AgentKind.Cursor` branch, so a Cursor
  session (if one is ever launched, e.g. via RawCli/WSL) falls through to the existing
  "History is not available for this agent yet" notice - the honest, correct behavior.
- This document plus its GitHub issue are the deliverable: the decision and its measured
  basis are recorded so the next person does not re-derive them.

---

## 6. References

- `docs/design/agent-history-capture/FINDINGS.md` (Cursor listed "not installed - deferred").
- `docs/design/agent-history-capture/drivers/IMPLEMENTATION-LOOP.md` (the driver template,
  for when Cursor is un-deferred).
- Sibling drivers as the build template once a native Windows agent exists: `codex.md`,
  `pi.md`.
