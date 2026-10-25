# Issue #3392 proof: a scheduled run whose prompt never reached the agent is a failed run

Date: 2026-10-24, on devlinux (Linux). First run on commit `962d53964`; run again after the review on commit `b7f83032c`
(the first prompt's own fact, below) - both from a clean tree.

## The rig

`scripts/session-rules-rig.sh` (on main) with `RIG_DIR=~/.local/state/session-rules-rig-3392`, `RIG_PORT=7964`,
`RIG_SLOT=8`: a Gateway and a slot-8 test Director built from this branch's worktree, each with its own storage root
and home folder, the Gateway with authentication on. A real Claude Code, signed in through the rig's copy of the
owner's sign-in (deleted by `down`). The rig's home was given the owner's `skipDangerousModePermissionPrompt: true`.

## How the prompt was made to fail, for real

The schedule's folder, the scratch repository `repo-a`, had never been opened in Claude Code under the rig's home, so
the agent comes up asking "Is this a project you created or one you trust?" - issue #3469 - and its composer is never
drawn. That is a genuine way a seed never reaches the agent: the first-prompt gate waits, then gives up.

`cc-devthrottle schedule run cj_f113d5` at 23:17:07 UTC started session `fc65f222`; its screen:

```
Quick safety check: Is this a project you created or one you trust? ...
❯ No, exit
  Yes, I trust this folder
Enter to confirm · Esc to cancel
```

## What happened (the test Director's and the Gateway's logs)

```
19:17:07.562 [Session] DeliverFirstPromptAsync: session=fc65f222-..., driver=ClaudeCode, len=270, limit=600s
19:27:07.700 [Session] DeliverFirstPromptAsync: session=fc65f222-..., gate=TimedOut: the agent's composer was not drawn within 600s ...
19:27:39.080 [CronRunHistoryStore] RecordResult: run=4d7392b9-..., job=cj_f113d5, result=problem, problem=prompt-not-delivered
19:27:39.080 [CronRunResultService] Sweep: run=4d7392b9-..., job=cj_f113d5, problem=prompt-not-delivered
```

(The Director logs local time, UTC-4: 19:27 is 23:27 UTC.)

`cc-devthrottle schedule runs cj_f113d5 --json`, at 23:27:41 UTC:

```
"infraStatus": "started",
"result": "problem",
"problem": "prompt-not-delivered",
"resultReason": "its session started, but the prompt it was given never reached the agent: The first prompt was not typed: the agent's composer was not drawn within 600s (a dialog, a menu, or an agent still starting); nothing was typed. Last screen: composer MenuOpen, cursor hidden; bottom rows: Security guide | ❯ No, exit | Yes, I trust this folder | Enter to confirm · Esc to cancel",
"resultUtc": "2026-10-24T23:27:39.0146143Z",
"ending": "still-open"
```

The run is a problem 32 seconds after the Director gave up on the prompt, while the session is still open. On main it
would have read `started`, result `pending`, until the end of its shift.

## What this does not prove

- How soon a failure is known is the first-prompt gate's limit (10 minutes, `FirstPromptGate.MinimumWait`), plus up to
  one sweep. This change does not shorten the gate.
- The owner being told: that is pull request #4441 (issue #4436), which tells every failed run. With it, this run is
  told with its reason as the detail.

## Run again after the review: the first prompt's own fact

The review found that the session row's delivery flag is about the LAST send of any source, so the change now reads a
fact the Director keeps for the first prompt alone (`SessionDto.FirstPromptFailedAtUtc` and its reason). The same rig,
rebuilt from the reworked branch, the same never-trusted folder, `schedule run cj_f113d5` at 23:52:59 UTC (session
`9318cca4`):

```
20:02:59.322 [Session] DeliverFirstPromptAsync: session=9318cca4-..., gate=TimedOut: the agent's composer was not drawn within 600s ...
20:02:59.329 [Session] first prompt NOT delivered: session=9318cca4-...: The first prompt was not typed: the agent's composer was not drawn within 600s ...
```

```
"infraStatus": "started",
"result": "problem",
"problem": "prompt-not-delivered",
"resultReason": "its session started, but the prompt it was given never reached the agent: The first prompt was not typed: the agent's composer was not drawn within 600s (a dialog, a menu, or an agent still starting); nothing was typed. Last screen: ... Yes, I trust this folder | Enter to confirm · Esc to cancel",
"resultUtc": "2026-10-25T00:03:45.1223281Z",
"ending": "still-open"
```

The run is a problem 46 seconds after the Director recorded the first prompt as not delivered.
