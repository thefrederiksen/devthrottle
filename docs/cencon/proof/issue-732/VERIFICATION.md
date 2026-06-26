# History tab overhaul - verification (epic #732, QA gate #737)

Date: 2026-06-26
Build: slot-5 framework-dependent build of `feat/history-tab-desktop`
(`local_builds/cc-director5.exe`, v0.9.16, commit 1802f2c), launched via the
`cc-director-launch` Windows scheduled task. Control API on 127.0.0.1:7883.

## What was driven

A real Claude Code session was created on the slot-5 Director in this repo and
prompted to (1) print a markdown demo (level-2 heading, three-item bullet list, a
bold word, a fenced C# code block) that also references a Windows file path
(`D:\ReposFred\devthrottle-history-desktop\README.md`) and a URL
(`https://github.com/thefrederiksen/devthrottle`), and (2) start a background
shell command (`sleep`, `run_in_background: true`) that keeps running. The History
tab was then opened and captured.

## Claude Code - PASS (fully verified, screenshots attached)

- #734 markdown: the bubble renders a heading (larger/bold), a real bullet list,
  a bold word, and a monospaced fenced code block - not literal `#`, `-`, `**`,
  backticks. (See `01-markdown-links-background-running.png`.)
- #735 links: the README path, the github URL, and the background-task output
  path all render as underlined blue links.
  - Path menu = the terminal's actions: **View File / Copy Path / Open in File
    Manager** (`02-path-context-menu.png`).
  - URL menu = **Copy URL / Open in Browser** (with the browser-selection
    submenu) (`03-url-context-menu.png`).
- #736 derived state: while the background command was in flight the header showed
  a distinct purple **"history: Background running"** pill next to the green
  **live** badge, while the live session badge still read **"I need you"** (the
  byte detector is unchanged) - exactly the target scenario
  (`01-markdown-links-background-running.png`). After the background command's
  `completed` task-notification arrived, the pill cleared to **"history: Needs
  you"** (`02`/`03`) - the full launch -> completed lifecycle, captured live. The
  derivation also has a process-liveness guard (unit-tested) so a session whose
  process exited never stays "Background running".
- Core unit tests: 21 tests in `HistoryStateDeriverTests` cover launch-without-
  notification, completed/failed/killed, running-heartbeat (ignored), duplicate
  notifications (deduped), multiple concurrent launches, the liveness guard,
  background shell commands, and the last-turn (Working/Needs you/Idle)
  derivations. Full Core suite green (2462 passed).

## Per-CLI matrix

Markdown rendering (#734) and clickable links (#735) are **agent-agnostic**: they
operate on each bubble's normalized text, so every agent that has a structured
history provider renders identically to Claude. The derived background-running
state (#736) is **Claude-only by design** (only the Claude transcript carries the
`run_in_background` + `task-notification` signal; other agents fall back to
today's live heuristic).

| CLI | History provider (SessionHistoryReader) | Markdown + links | Derived bg-state | Status |
|-----|------------------------------------------|------------------|------------------|--------|
| Claude Code | Yes (transcript .jsonl) | Yes | Yes | PASS - live, 3 screenshots |
| Codex | Yes (rollout .jsonl) | Yes (agent-agnostic) | n/a (Claude-only) | Provider present; live screenshot deferred (see note) |
| Pi | Yes (session .jsonl) | Yes | n/a | Provider present; deferred |
| Grok | Yes (chat_history.jsonl) | Yes | n/a | Provider present; deferred |
| Copilot | Yes (SQLite store) | Yes | n/a | Provider present; deferred |
| OpenCode | Yes (SQLite store) | Yes | n/a | Provider present; deferred |
| Gemini | Yes, but raw terminal buffer | NO - rendered verbatim (Plain mode) | n/a | KNOWN GAP (by design) |
| Cursor | No provider | n/a - shows "History is not available for this agent yet." | n/a | Unsupported today (recorded) |

### Honest gaps / notes

1. **Gemini**: it has no structured transcript, so the History tab renders its raw
   terminal scrollback verbatim (Plain mode) to preserve monospace alignment.
   Consequently Gemini bubbles are NOT markdown-formatted and their paths/URLs are
   NOT clickable in the History tab. This is intentional (markdown parsing would
   mangle raw terminal output), but it is a real divergence from the other agents
   and is recorded as a gap, not silently skipped. (The terminal tab still makes
   Gemini's links clickable.)
2. **Cursor**: there is no history provider for Cursor today, so the History tab
   shows "History is not available for this agent yet." Recorded as unsupported.
3. **Live per-CLI screenshots (Codex/Pi/Grok/Copilot/OpenCode) deferred**: the
   per-CLI screenshot gallery was not completed live. Capturing it requires
   driving the desktop GUI, and on this machine several CC Director windows (plus
   Outlook) overlap at the same screen coordinates; coordinate-driven clicks
   during capture twice hit the wrong window (once opening a voice "Dictate"
   recording dialog on another Director, once selecting a message in Outlook).
   To avoid further disruption to the user's running sessions, the remaining
   per-CLI captures should be done interactively, or on a machine running only the
   test Director. The rendering path is shared (`MarkdownTextBlock` for every
   bubble; `LinkContextMenuBuilder` shared with the terminal), so the Claude PASS
   exercises the exact code path the other structured-history agents use.

## Regression

The live byte-based status detector (`TerminalStateDetector`) was not modified;
the derived history state is an additive, separate label. During the run the live
badge behaved exactly as before (Working while output flowed, "I need you" on
silence) independently of the new history pill.
