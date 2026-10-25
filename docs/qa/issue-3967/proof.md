# Issue #3967 proof: a scheduled run's pasted seed has a sentence typed in front of it

Date: 2026-10-25, on devlinux (Linux), commit `76a7af74b` (this branch, rebased on main at `59e57f5c5`, a clean
tree). Claude Code as installed on the machine, with the owner's sign-in copied into the rig's own home folder and
deleted when the rig came down.

## The rig

`scripts/session-rules-rig.sh` (on main), with `RIG_DIR=~/.local/state/session-rules-rig-3967`, `RIG_PORT=7964`,
`RIG_SLOT=7`: a Gateway and a slot-7 test Director built from this branch's worktree, each with its own storage
root and its own home folder, the Gateway with authentication on, the Director connected to that Gateway only. Its
`spawn-session` copies the Claude Code sign-in into the rig's home folder (refused with 120 minutes or less left on
the owner's) and `down` deletes it. Two things a fresh home folder lacks were given to it, as the owner's machine
already has them:

- the scratch repository `repo-a` recorded as trusted (the folder-trust question is issue #3469, its own pull
  request, and would otherwise stop the agent before any prompt);
- `skipDangerousModePermissionPrompt: true` in the rig's own `~/.claude/settings.json`, the owner's own setting.

## The run

A schedule with a 420-character, multi-line seed, written like the seed in the issue; it says no person is watching:

```
Weekly usage report - scheduled run.

No person is watching this run. Nobody will read your answers while it runs, and nobody can answer a question.

1. Create a file named weekly-report.txt in this folder.
2. Write into it one line: REPORT DONE, followed by today's date.
3. Then report the run as finished with a result of ok.

Do not ask for confirmation at any point: this text is the whole instruction for the run.
```

The Gateway adds its run-result line, so the first prompt is 622 characters, over the 200-character limit for typing
to Claude Code: it goes as a bracketed paste. `cc-devthrottle schedule run cj_204c47` at 04:14:44 UTC started
session `efd587d5`.

## What the Director did (the test Director's log, local time, four hours behind)

```
00:14:44.153 [Session] PrePrompt: gating on the agent reading input, sid=efd587d5-..., len=622, lead=True
00:14:45.938 [Session] DeliverFirstPromptAsync: session=efd587d5-..., gate=Ready: the agent read a keystroke after 1.8s
00:14:45.964 [ClaudeCode] SharedSubmit: bracketed paste submit len=622
00:14:45.964 [ClaudeCode] SharedSubmit: typing a 96-character lead in front of the paste
00:14:47.200 [SubmitVerifier] 'ClaudeCode: This is a scheduled run and nobody is watching it: carry out...': Enter pressed once; ...
00:14:47.737 [Session] arrival proof: session=efd587d5-..., agent=ClaudeCode, outcome=Arrived, len=622
```

## What the agent received and did (its own conversation file)

```
USER: This is a scheduled run and nobody is watching it: carry out the instructions in this paste now. \n\n
      <pasted_content id="c486">\nWeekly usage report - scheduled run.\n\nNo person is watching this run. ...
TOOL USE: Bash {"command": "echo \"REPORT DONE $(date +%F)\" > weekly-report.txt && cat weekly-report.txt", ...}
TOOL RESULT: REPORT DONE 2026-10-25
TOOL USE: Bash {"command": "cc-devthrottle run result ok", ...}
TOOL RESULT: Recorded: the run of weekly report went ok. This session now closes itself. ...
ASSISTANT: The scheduled run went ok. I created `weekly-report.txt` in the repo with the line `REPORT DONE 2026-10-25`, ...
```

The lead and the paste are ONE user prompt, from one Enter. `weekly-report.txt` appeared at 04:14:51 UTC, seven
seconds after the fire, holding `REPORT DONE 2026-10-25`, and `schedule runs` records the run with result `ok` at
04:14:53 UTC and the session as closed by itself. The agent asked for no go-ahead.

## A defect this proof found, and its fix

The first run on the rig, from the branch's first commit, also wrote the file and reported ok - but the arrival proof
never answered: the Director logged `WAITING 5s so far` and no outcome before the session closed at 33 seconds. The
agent records a paste inside its own `<pasted_content>` marker, so the record reads lead, marker, paste; the send
returned the lead and the paste as one text, and the proof looks for that text's first 80 letters and digits, which
are never in the record unbroken. On a run that did not close itself that quickly, the proof would have timed out
and could resend. The second commit returns the paste alone from the paste routes, and
`SharedSubmit_LongSeedWithATypedLead_ReturnsWhatTheArrivalProofFindsInTheAgentsRecord` checks the returned text
against the record shape copied from that run. The run above is from the fixed build.

## What this does not prove

- That the old shape always failed. The agent's refusal in the issue is the model's own judgement and does not happen
  every time. What the agent in the issue named was a prompt with nothing typed beside it: then a bare file
  reference, and on main since #4379 a bare paste. This change types the lead beside either.
- A seed over the measured paste limit (51,200 characters) goes as a file reference with the lead typed in front of
  it. That route is covered by `SharedSubmit_SeedOverThePasteLimitWithALead_TypesTheLeadInFrontOfTheReference`, not by
  a run on a real agent.
- The second half of the issue - that the session which ran `schedule run` cannot answer the session it started - is
  ownership, not delivery, and is not changed here.
