# Supported agents and terminal screen behavior

This document lists every agent type that Cc Director can launch today and records whether the agent uses the normal terminal buffer or a full screen alternate screen.

## What the two modes mean

| Mode | Meaning for Cc Director |
| --- | --- |
| Normal terminal buffer | Output is written into the main terminal buffer. Local scrollback can grow as the agent prints lines. The agent may still redraw parts of the visible screen, but it does not switch away from the main buffer. |
| Full screen alternate screen | The agent sends the terminal sequence that switches to the alternate screen. While this is active, local scrollback is intentionally hidden or empty; scrolling is owned by the agent itself. Cc Director should treat mouse wheel input as input for the agent, not as local scrollback navigation. |

The full screen switch is the terminal sequence `ESC [ ? 1049 h`. Leaving full screen is `ESC [ ? 1049 l`.

## Current support matrix

| Agent kind | Display name | Launch command | Default terminal mode | Evidence and notes |
| --- | --- | --- | --- | --- |
| `ClaudeCode` | Claude Code | `claude` | Full screen alternate screen | Claude Code now enters the alternate screen. Cc Director must expect no local scrollback while the alternate screen is active. |
| `Pi` | Pi | `pi` | Normal terminal buffer | Pi renders into the normal terminal buffer and lets the terminal scroll naturally. It can clear and redraw during resize or session changes, but it does not run as a full screen alternate screen application by default. |
| `Codex` | Codex | `codex` | Full screen alternate screen by default | The installed Codex command contains the `alternate_screen` setting with `auto`, `always`, and `never` values and uses a terminal user interface. Treat the default `auto` behavior as full screen in Cc Director unless the user overrides it to `never`. |
| `Gemini` | Gemini | `gemini` | Normal terminal buffer | The installed Gemini command uses Ink rendering and clears/redraws the main screen, but the application source does not show an alternate screen switch in its own startup path. Treat it as normal-buffer redraw, not alternate-screen full screen. |
| `OpenCode` | OpenCode | `opencode` | Normal terminal buffer | The installed OpenCode command did not expose an alternate screen switch in the bundled command search. Treat it as a normal-buffer interactive renderer unless a later version proves otherwise. |
| `Cursor` | Cursor | `cursor-agent` | Unknown; verify before relying on scrollback behavior | Cc Director supports Cursor as a launchable agent, including a stream output mode for Studio. This repository does not currently include a captured interactive Cursor terminal stream showing whether it enters the alternate screen. |
| `Grok` | Grok | `grok` | Full screen alternate screen by default | The installed Grok documentation says the default `alt_screen = "auto"` uses the alternate screen when supported. It also supports `--no-alt-screen` or `alt_screen = "never"` to force normal-buffer mode. |
| `Copilot` | GitHub Copilot | `copilot` | Full screen alternate screen | The installed Copilot package includes terminal renderer constants for entering and leaving the alternate screen. Treat its interactive agent as full screen unless a captured stream for a specific version proves otherwise. |
| `RawCli` | Custom command | User supplied | Depends on the command | Cc Director cannot know this in advance. A custom command may be a plain shell, a line-oriented tool, or a full screen terminal application. |

## Operational rule

Do not infer scrollback behavior from the agent name alone. Trust the live parser state when possible:

- If the parser reports alternate screen mode, hide or deemphasize local scrollback and forward wheel input to the agent when mouse reporting is enabled.
- If the parser reports normal buffer mode, local scrollback is meaningful and can be shown.
- If an agent can be configured either way, document the chosen command line in the session metadata or proof notes.

## Verification recipe

When an agent version changes, capture the first seconds of terminal output in a disposable session and search the raw byte stream for these sequences:

| Sequence | Meaning |
| --- | --- |
| `ESC [ ? 1049 h` | Enter alternate screen. Mark the agent version as full screen. |
| `ESC [ ? 1049 l` | Leave alternate screen. |
| No `1049` sequence | The agent is probably using the normal terminal buffer, though it may still clear and repaint the visible screen. |

For configurable agents, test both the default launch and any documented Cc Director preset that changes screen behavior.
