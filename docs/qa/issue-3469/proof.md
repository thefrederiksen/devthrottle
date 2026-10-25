# Issue #3469 proof: a scheduled Claude Code session never waits on the folder-trust question

Date: 2026-10-24, on devlinux (Linux), commit `4978080bf` (this branch, a clean tree).

## The rig

`scripts/session-rules-rig.sh` (on main) with `RIG_DIR=~/.local/state/session-rules-rig-3469`, `RIG_PORT=7965`,
`RIG_SLOT=9`: a Gateway and a slot-9 test Director built from this branch's worktree, each with its own storage root
and home folder. A real Claude Code, signed in through the rig's copy of the owner's sign-in (deleted by `down`), with
the owner's `skipDangerousModePermissionPrompt: true`. Claude Code under the rig's home trusted no folder at all
(`projects` empty in the rig's `~/.claude.json`).

The rig Director's repository list (`<root>/instances/default/config/director/repositories.json`) held one
repository, `repo-a`. A second folder, `scratch-c`, was neither a repository of the machine nor trusted.

Without this change, both folders stop the agent at "Is this a project you created or one you trust?" - the same rig,
built from the #3392 branch, held a seed there for the full 600 seconds (docs/qa/issue-3392/proof.md on that branch).

## A schedule in a registered repository: the folder is trusted, and the run runs

`cc-devthrottle schedule run cj_d05cce` at 23:31:46 UTC:

```
19:31:45.905 [SessionCommandExecutor] marked .../repos/repo-a trusted for Claude Code: a scheduled session in a repository of this machine
19:31:50.266 [Session] DeliverFirstPromptAsync: session=16298077-..., gate=Ready: the agent read a keystroke after 4.3s
```

`proof-3469.txt` appeared in `repo-a` holding `RAN` by 23:31:56, ten seconds after the fire; the run's result is `ok`.
The rig's Claude Code configuration now records `repo-a` as trusted, and nothing else.

## A schedule in a folder neither registered nor trusted: refused at once, in words

`cc-devthrottle schedule run cj_b87a9a` at 23:31:46 UTC:

```
  Infra:   not-started
  Ending:  did not start a session
  Reason:  it did not start a session: director returned BadRequest: not started: the folder '.../repos/scratch-c' is not one of the repositories of devlinux, and Claude Code has never been told to trust it, so the scheduled session would wait at the folder-trust question with nobody to answer it. Add the folder as a repository of devlinux, or open it once in DevThrottle and answer the question; the next run then starts.
```

The run is recorded as `problem` / `did-not-run` at the moment it fired - not a session left at a question for ten
minutes. No session was started, and the folder was not marked trusted.

## What this does not prove

- A worktree of a registered repository: its trust is Claude Code's own walk to the main checkout
  (`ClaudeFolderTrust.Covers`), unchanged here.
- The words of the refusal on the Schedule page: with pull request #4441 (issue #4436), a reason starting "not started:"
  reads as "This run did not start: the folder ...", and the owner is told.
