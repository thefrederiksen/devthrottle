# Foreground Only

**Agents and sessions run in the FOREGROUND. Never in the background, never detached.**

This is a fleet rule, not a preference. It applies on every machine, in every repo, to every agent.

## The rule

Do not run an agent, a model session (Claude, Codex, Pi, Gemini, ...), or a long agent-driven command
as a background or detached task. Run it in the foreground, where a human can watch it - or give it
its own tracked session. There is no third option.

Specifically forbidden unless the human asks for it in the current conversation:

- `run_in_background: true` on a shell tool, especially for `codex` or any other agent CLI
- An agent/subagent tool's background default - pass the foreground flag explicitly every time
  (Claude Code's Agent tool backgrounds subagents unless told `run_in_background: false`)
- Piping a long-running command through `tail` / `head` - the pipe buffers the entire run, so the
  output file stays empty and the work is invisible even though it is nominally in the foreground
- Wrapping a loop, drain, or queue in one background supervisor sub-agent
- "Start it and check back later"

## Why

Sessions exist so that work is **logged, tracked, reviewable and improvable**. A background process
delivers none of that:

- You cannot distinguish working from hung from burning the machine. A hung agent and a thinking agent
  look identical from outside.
- There is nothing to inspect afterwards. If the transcript was never surfaced, a bad or truncated run
  cannot be diagnosed.
- It strands processes. A session that finishes or reaps itself while a child agent is still running
  leaves that child orphaned, where it goes on to block later jobs. Two concurrent `codex exec`
  processes, for example, deadlock each other at zero CPU indefinitely.
- The human loses the thread. Mission Control shows sessions; it cannot show a subprocess you hid
  inside one.

## What to do instead

Run it in the foreground and let the output stream.

If the job is too long to sit and watch, that is a reason to **narrow its scope** or to **give it its
own session** - never a reason to hide it:

```
cc-devthrottle session spawn <repo> --agent Codex --controlled-by self \
  --name "<what this session is for>" \
  --prompt "<the task>"
```

That session appears in the fleet, is logged, and can be read afterwards. `--agent` accepts
ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot and RawCli, so a non-Claude agent gets the same
tracking as everything else.

`cc-devthrottle session spawn` has **no background or detached option, by design.** If one ever
appears, it is a bug to remove, not a facility to use.

## If you think your case is the exception

Ask first. Do not decide it yourself, and do not infer permission from a skill or script that tells
you to background something - several older skill files still do, and they are wrong. Fix the skill.
