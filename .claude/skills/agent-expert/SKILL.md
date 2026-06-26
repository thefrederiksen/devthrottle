---
name: agent-expert
description: INTERNAL reference, NOT shipped with the installer. The in-repo expert on every coding-agent CLI CC Director can run (Claude Code, Codex, Gemini, OpenCode, Grok, Copilot, Cursor, Pi). Knows each agent's command line, config files, context-injection points, lifecycle hooks and events, SDK/API/server mode, transcript format, session semantics, and how CC Director drives it. Triggers on "agent-expert", "agent expert", "how does <agent> inject context", "<agent> hooks", "<agent> events", "<agent> CLI reference", "which agents support X", "agent capability matrix".
---

# Agent Expert (internal super-agent-manager)

This skill is the single place that knows everything we need to build drivers and plugins
for the coding-agent CLIs CC Director supervises, and to make sessions talk to each other.
It exists ONLY in this repository, for our own use. It is NOT deployed to customers and must
be excluded from the installer skill bundle (see "Do not ship" below).

## What it is for

When you need to know, for any agent, how to:
- launch it (command line, headless mode, session id, model flag, output format),
- configure it (config files and their precedence),
- inject context at launch (instruction files, system-prompt flags, hook additionalContext),
- get a signal when it clears or compacts its context (lifecycle hooks / event bus),
- read its transcript, or drive it through an SDK / server / RPC channel,
- and exactly how CC Director integrates it today plus the gaps -

read the per-agent reference file. Do not re-derive this from the web each time; that is the
delay this skill removes. If a fact is missing or stale, research it, then update the file.

## How to use

1. Open `agents/README.md` for the at-a-glance cross-agent matrix.
2. Open `agents/<agent>.md` for the deep reference on one agent. These files are authoritative;
   the matrix is only the summary.
3. Each file marks every fact [VERIFIED from docs] or [INFERRED/UNCERTAIN] and links the source.

## The agents

| Agent | AgentKind | File |
|-------|-----------|------|
| Claude Code | ClaudeCode | [agents/claude-code.md](agents/claude-code.md) |
| OpenAI Codex | Codex | [agents/codex.md](agents/codex.md) |
| Google Gemini | Gemini | [agents/gemini.md](agents/gemini.md) |
| opencode | OpenCode | [agents/opencode.md](agents/opencode.md) |
| xAI Grok ("Grok Build") | Grok | [agents/grok.md](agents/grok.md) |
| GitHub Copilot | Copilot | [agents/copilot.md](agents/copilot.md) |
| Cursor (cursor-agent) | Cursor | [agents/cursor.md](agents/cursor.md) |
| pi | Pi | [agents/pi.md](agents/pi.md) |

To add an agent, copy [agents/_template.md](agents/_template.md) and fill every section.

## The one thing this skill is really about: context injection and reset detection

Every agent reaches the fleet through its own Director (CC_DIRECTOR_API) and gets a launch
preamble (its identity plus the cc-* fleet commands). The hard part is keeping that preamble
present after the agent clears or compacts its context. The CLIs fall into four mechanism
families - see `agents/README.md` for which agent is in which:

- Family A - shell-command hook that injects context. A config file registers a command we
  own; the command prints the preamble as additionalContext. This is what is already wired
  for Claude (`ClaudeHookInstaller` plus the `GET /sessions/{sid}/fleet-preamble` endpoint).
- Family B - out-of-process event bus. We subscribe to the agent's event stream and push the
  preamble when we see a new/cleared/compacted session.
- Family C - in-process extension or plugin we author and load into the agent.
- Family D - instruction file the agent re-reads every prompt (passive, self-healing, no event).

The shared piece across all families is the Director endpoint `GET /sessions/{sid}/fleet-preamble`,
which already exists and is agent-agnostic.

## Do not ship

This skill is internal. Before any installer/skill-bundle change, confirm `agent-expert` is
excluded from the shipped skill set. It documents competitor tooling and our internal
integration plans and has no place on a customer machine.
