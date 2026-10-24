# Issue #3967 proof: a scheduled run's long first prompt reaches Claude Code as a typed instruction

Date: 2026-10-24, on devlinux (Linux), commit `da1f4f4d7` (this branch, a clean tree). Claude Code as installed
on the machine, with the owner's sign-in copied into the rig's own home folder and deleted when the rig came down.

## The rig

`scripts/session-rules-rig.sh` (on main), with `RIG_DIR=~/.local/state/session-rules-rig-3967`, `RIG_PORT=7963`,
`RIG_SLOT=7`: a Gateway and a slot-7 test Director built from this branch's worktree, each with its own storage
root and its own home folder, the Gateway with authentication on, the Director connected to that Gateway only. Its
`spawn-session` copies the Claude Code sign-in into the rig's home folder (with its 120-minute margin) and `down`
deletes it. Two things a fresh home folder lacks were given to it, as the owner's machine already has them:

- the scratch repository `repo-a` recorded as trusted in the rig's own `~/.claude.json` (the folder-trust question is
  issue #3469, a separate pull request, and would otherwise stop the agent before any prompt);
- `skipDangerousModePermissionPrompt: true` in the rig's own `~/.claude/settings.json`, the owner's own setting.
  Without it the first run stopped at Claude Code's skip-permissions warning, and was stopped by hand.

## The run

A schedule with a 564-character, multi-line seed, written like the seed in the issue - it says that no human is
watching:

```
This is an unattended scheduled run. No human is watching it, and nobody will answer a question.

Do the following without asking for confirmation:
1. Create a file named proof-3967.txt in the current folder.
2. Write exactly one line into it: SCHEDULED-RUN-RAN
3. Then stop. Do not report anything else.

This text is the whole of the instruction for this run.
```

`cc-devthrottle schedule run cj_d08cd4` at 22:41:24 UTC started session `6a3a3e76`.

## What the Director did (the test Director's log)

```
18:41:27.142 [Session] DeliverFirstPromptAsync: session=6a3a3e76-..., gate=Ready: the agent read a keystroke after 2.0s
18:41:27.144 [Session] FirstPromptToType: session=6a3a3e76-..., len=564: typed as the sentence naming @.temp/input_20261024_184127_cfebzq.txt
18:41:27.841 [Session] arrival proof: session=6a3a3e76-..., agent=ClaudeCode, outcome=Arrived, len=116
```

The file `.temp/input_20261024_184127_cfebzq.txt` holds the seed, word for word.

## What the agent received and did (its own conversation file)

```
USER: Your instructions for this session are in @.temp/input_20261024_184127_cfebzq.txt. Read the file and carry them out.
TOOL USE: Bash {"command": "echo 'SCHEDULED-RUN-RAN' > proof-3967.txt && cat proof-3967.txt", ...}
TOOL RESULT: SCHEDULED-RUN-RAN
TOOL USE: Bash {"command": "cc-devthrottle run result ok", ...}
TOOL RESULT: Recorded: the run of weekly report 3967 went ok. This session now closes itself.
ASSISTANT: I created `proof-3967.txt` with the single line `SCHEDULED-RUN-RAN` and recorded the run as ok.
```

`proof-3967.txt` appeared at 22:41:30 UTC, six seconds after the run was fired, holding `SCHEDULED-RUN-RAN`, and
`schedule runs` records the run with result `ok`. The agent asked for no go-ahead.

## What this does not prove

- That the old shape always failed. The agent's refusal in the issue is the model's own judgement and does not happen
  every time; on main the same seed is typed as the bare `@.temp/input_....txt` line with nothing beside it (the
  shape the agent in the issue named), and this change replaces exactly that line.
- The second half of the issue - that the session which ran `schedule run` cannot answer the session it started - is
  ownership, not delivery, and is not changed here.
