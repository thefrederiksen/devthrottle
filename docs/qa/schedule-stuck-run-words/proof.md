# Proof: the stuck-run kinds read as their own plain sentences

Date: 2026-10-25, on devlinux (Linux), commit `c43fa589e` (this branch, a clean tree).

## The rig

`scripts/session-rules-rig.sh` with `RIG_DIR=~/.local/state/session-rules-rig-words`, `RIG_PORT=7966`, `RIG_SLOT=9`:
a Gateway and a slot-9 test Director built from this branch, each with its own storage root and home folder, and a
real Claude Code signed in through the rig's copy of the owner's sign-in (deleted by `down`). The rig Director's
repository list held one repository, `repo-a`. The folder `scratch-c` was neither a repository nor trusted.

The rig's home was NOT given `skipDangerousModePermissionPrompt`, so a scheduled Claude Code session stops at the
bypass-permissions question and its composer is never drawn. On main since #4449 a folder-trust question cannot hold
a scheduled seed any more, so this is the real way used here to make a first prompt fail.

## A run whose prompt never reached the agent (issue #3392)

`cc-devthrottle schedule run cj_0e2b2a` (folder `repo-a`) at 01:59:11 UTC started session `0c3dabae`; its screen:

```
WARNING: Claude Code running in Bypass Permissions mode
...
❯ No, exit
  Yes, I accept
Enter to confirm · Esc to cancel
```

The test Director's and the Gateway's logs (local time, UTC-4):

```
22:09:11.689 [Session] DeliverFirstPromptAsync: session=0c3dabae-..., gate=TimedOut: the agent's composer was not drawn within 600s ...
22:09:55.084 [CronRunHistoryStore] RecordResult: run=1b667f6e-..., job=cj_0e2b2a, result=problem, problem=prompt-not-delivered
```

`cc-devthrottle schedule runs cj_0e2b2a --json`, the newest run:

```
"result": "problem",
"problem": "prompt-not-delivered",
"failureCause": "prompt-not-delivered",
"failureText": "The run's session on devlinux started, but its prompt never reached the agent, so no work was done. Open the session to see where it stopped; the details say why the prompt was not typed.",
"resultReason": "its session started, but the prompt it was given never reached the agent: The first prompt was not typed: the agent's composer was not drawn within 600s (a dialog, a menu, or an agent still starting); nothing was typed. Last screen: ..."
```

`cc-devthrottle schedule get cj_0e2b2a`:

```
  Last run:   Last run failed: The run's session on devlinux started, but its prompt never reached the agent, so no work was done. Open the session to see where it stopped; the details say why the prompt was not typed.
```

On main the same run reads "This run on devlinux went wrong. The details below say what happened." under the cause
`other`.

## A folder never trusted (issue #3469)

`cc-devthrottle schedule run cj_e9903a` (folder `scratch-c`) at 01:59:43 UTC was refused before any session started:

```
"infraStatus": "not-started",
"failureCause": "folder-not-trusted",
"failureText": "This run did not start: The folder '.../repos/scratch-c' is not one of the repositories of devlinux, and Claude Code has never been told to trust it, so the scheduled session would wait at the folder-trust question with nobody to answer it. Add the folder as a repository of devlinux, or open it (or the repository it is in) once in DevThrottle and answer the question; the next run then starts."
```

The sentence is the same as on main; the cause was `factory-setup`, so its hourly notice limit was shared with a
factory's setup refusals.

The other folder-trust refusal - a registered repository whose trust could not be recorded - was seen for real on the
first try of this rig, before its home had a Claude Code configuration file:

```
Ending:  did not start a session: This run did not start: The folder '.../repos/repo-a' is a repository of devlinux, but Claude Code's configuration there could not be read or written to record it as trusted, ...
```

It now carries `folder-not-trusted` too (the unit test holds that wording).

## What this does not prove

- The notice itself (webhook, phone, email): the cause and sentence are the ones the notifier uses, proved by #4441's
  own proof; it was not sent again here.
- The seat-limit kind: its sentence is in pull request #4450, which introduces that kind.
