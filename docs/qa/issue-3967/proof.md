# Issue #3967 proof: a scheduled run's pasted seed has a sentence typed in front of it

Date: 2026-10-25, on devlinux (Linux), commit `c813fc932` (this branch after both review rounds' fixes, rebased on main at
`59e57f5c5`, a clean tree). Claude Code as installed on the machine, with the owner's sign-in copied into the rig's own home folder and
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
to Claude Code: it goes as a bracketed paste. `cc-devthrottle schedule run cj_204c47` at 05:26:53 UTC started
session `6483263b`.

## What the Director did (the test Director's log, local time, four hours behind)

```
01:26:53.179 [Session] 6483263b-... origin stamped: kind=schedule surface=cron parent=(none)
01:26:53.248 [Session] PrePrompt: gating on the agent reading input, sid=6483263b-..., len=622, lead=True
01:26:55.280 [Session] DeliverFirstPromptAsync: session=6483263b-..., gate=Ready: the agent read a keystroke after 2.0s
01:26:55.308 [ClaudeCode] SharedSubmit: bracketed paste submit len=622
01:26:55.309 [ClaudeCode] SharedSubmit: typing a 94-character lead in front of the paste
01:26:56.589 [SubmitVerifier] 'ClaudeCode: This is a scheduled run and nobody is watching it: carry out...': Enter pressed once; ...
01:26:57.140 [Session] arrival proof: session=6483263b-..., agent=ClaudeCode, outcome=Arrived, len=622
```

The origin is the cron surface, the one the lead is given to. Between the lead and the Enter the send waited for the
agent to take in the lead, then for it to take in the paste: 1.3 seconds in all on this idle machine.

## What the agent received and did (its own conversation file)

```
USER: This is a scheduled run and nobody is watching it: carry out the instructions that follow now. \n\n
      <pasted_content id="cc96">\nWeekly usage report - scheduled run.\n\nNo person is watching this run. ...
TOOL USE: Bash {"command": "echo \"REPORT DONE $(date +%F)\" > weekly-report.txt && cat weekly-report.txt", ...}
TOOL RESULT: REPORT DONE 2026-10-25
TOOL USE: Bash {"command": "cc-devthrottle run result ok", ...}
TOOL RESULT: Recorded: the run of weekly report went ok. This session now closes itself. ...
ASSISTANT: I created `weekly-report.txt` in the repo folder. It contains one line: `REPORT DONE 2026-10-25`. ...
```

The lead and the paste are ONE user prompt, from one Enter. `weekly-report.txt` appeared at 05:27:01 UTC, eight
seconds after the fire, holding `REPORT DONE 2026-10-25`, and `schedule runs` records the run with result `ok` at
05:27:04 UTC and the session as closed by itself. The agent asked for no go-ahead.

## A defect this proof found, and its fix

The first run on the rig, from the branch's first commit, also wrote the file and reported ok - but the arrival proof
never answered: the Director logged `WAITING 5s so far` and no outcome before the session closed at 33 seconds. The
agent records a paste inside its own `<pasted_content>` marker, so the record reads lead, marker, paste; the send
returned the lead and the paste as one text, and the proof looks for that text's first 80 letters and digits, which
are never in the record unbroken. On a run that did not close itself that quickly, the proof would have timed out
and could resend. The second commit returns the paste alone from the paste routes, and
`SharedSubmit_LongSeedWithATypedLead_ReturnsWhatTheArrivalProofFindsInTheAgentsRecord` checks the returned text
against the record shape copied from that run. A second run, after that fix, answered `Arrived` 0.5 seconds after
the Enter. The run above is from the build after both review rounds (below), with the lead's final wording; the lead, which
wraps across lines on the agent's screen, was seen at once, so the stricter echo check of the second round added no
wait.

## What the review changed, and its tests

- The paste wait could end early: a busy agent that echoed the lead only after the paste was written made the lead's
  echo look like the paste's repaint. The send now waits for the lead to be taken in first.
  `SharedSubmit_ALeadEchoedLate_IsWaitedForAndDoesNotEndThePasteWaitEarly` drives such an agent on the fake clock
  (the lead echoed two seconds late, the paste never repainted): without the wait the test fails; with it the paste
  waits for the echo and the Enter is held the paste wait's whole limit, as for a paste with no lead.
- The second round found that any output then a quiet second ended that wait, so a status-line repaint just after the
  lead passed for its echo. Only the lead's own letters end it now;
  `SharedSubmit_AStatusRepaintBeforeTheLeadsLateEcho_DoesNotEndTheLeadWait` fails without that check.
- A leaded send that did not arrive is marked with the lead and the paste, so the clear is sized for both
  (`HeldWithLead` tests).
- A workflow seat and a trigger's session carry the schedule kind too; the lead goes only to the cron surface
  (`ScheduledRunLeadTests`).
- The sentence says "the instructions that follow", true before a paste and before a file reference.

## What this does not prove

- That the old shape always failed. The agent's refusal in the issue is the model's own judgement and does not happen
  every time. What the agent in the issue named was a prompt with nothing typed beside it: then a bare file
  reference, and on main since #4379 a bare paste. This change types the lead beside either.
- A seed over the measured paste limit (51,200 characters) goes as a file reference with the lead typed in front of
  it. That route is covered by `SharedSubmit_SeedOverThePasteLimitWithALead_TypesTheLeadInFrontOfTheReference`, not by
  a run on a real agent.
- The second half of the issue - that the session which ran `schedule run` cannot answer the session it started - is
  ownership, not delivery, and is not changed here.
