# Issue #3907 proof: a scheduled session whose seat is at its limit is a failed run at once

Date: 2026-10-24, on devlinux (Linux), commit `1bb5d740a` (this branch, a clean tree).

## The rig

`scripts/session-rules-rig.sh` (on main) with `RIG_DIR=~/.local/state/session-rules-rig-3907`, `RIG_PORT=7966`,
`RIG_SLOT=10`: a Gateway and a slot-10 test Director built from this branch's worktree, each with its own storage root
and home folder. A real Claude Code, signed in through the rig's copy of the owner's sign-in (deleted by `down`), with
the owner's `skipDangerousModePermissionPrompt: true`, and the scratch repository `repo-a` trusted in the rig's own
Claude Code configuration.

## How the limit was shown

A real seat at its spend limit cannot be produced on demand. So the schedule's seed asked the real agent to reply with
the line a limited seat shows, and nothing else. What the Gateway then does - the turn end, the screen read, the
classifier, the run record - is the real path; only the source of the line is staged. The screen at the turn end:

```
  ⎿  You've hit your monthly spend limit · contact your admin to raise it
...
❯
```

## What happened

`cc-devthrottle schedule run cj_53c570` at 23:44:00 UTC started session `43cff9e7`. The rig Gateway's log:

```
19:44:09.609 [CronRunResultService] RecordSeatLimitFromScreen: run=fb02b264-..., job=cj_53c570, session=43cff9e7-...: seat at its limit (hit your monthly spend limit)
```

`cc-devthrottle schedule runs cj_53c570 --json`:

```
"infraStatus": "started",
"result": "problem",
"problem": "seat-limit",
"resultReason": "its session started on devlinux, but the agent answered that its seat is at a limit (\"hit your monthly spend limit\"), so the work could not be done",
"resultUtc": "2026-10-24T23:44:09.5879864Z",
"ending": "still-open"
```

The run is a problem 9.5 seconds after it fired. On main it reads `started`, result `pending`, and the session
`still-open`, until its shift ends - eight to ten hours on 12 October.

## What this does not prove

- The exact words a real limited seat prints on every agent. The block list is the classifier's own
  (`TerminatingFaultClassifier.UsageLimitSignatures`, measured from real screens), with the issue's "hit your monthly
  spend limit" added; a limit worded otherwise is not caught.
- The owner being told: that is pull request #4441 (issue #4436).
- The issue's third point - a one-at-a-time trigger that stays blocked behind the limited session - is not changed: the
  session is left open, as every failed run's session is.

## Run again after the review

The review asked that only the agent's own printed block count (a quote, or a seed naming the wording, must not), and
that only a pending run's screen be read. The same rig rebuilt from commit `f662df742`, the same schedule, `schedule
run cj_53c570` at 00:10:14 UTC (session `c1f966e0`):

```
"result": "problem",
"problem": "seat-limit",
"resultReason": "its session started on devlinux, but the agent answered that its seat is at a limit (\"hit your monthly spend limit\"), so the work could not be done",
"resultUtc": "2026-10-25T00:10:29.6615317Z",
"ending": "still-open"
```

The run is a problem 14.6 seconds after it fired. The quote and seed cases are unit tests
(`RecordSeatLimitFromScreen_AHealthyReplyThatQuotesALimit_IsNotABlock`, `..._ASeedThatNamesTheWording_IsNotABlock`).
