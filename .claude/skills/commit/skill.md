---
name: commit
description: Create a git commit following the project's commit standards. Triggers on "/commit" or when user asks to commit changes.
---

# Commit Skill

Commit workflow: review code, list files, get approval, then drive ALL THE WAY to a merged
pull request on origin/main and park the checkout back on main.

## Trunk-based development - "done" means merged to main

This repo (like every repo here) does trunk-based development. There are three states of
work and only one counts:

1. **Committed / pushed to a side branch** - still IN PROGRESS. A backup, not a delivery.
2. **An open pull request** - still IN PROGRESS.
3. **Merged to origin/main** - DONE. Tracked, visible to every agent, carries its pull
   request record so it can be QA'd or reverted later.

So a commit request is NOT finished at `git push`. Committing still requires the user's
explicit ask (per CLAUDE.md) - but ONCE the user approves the commit, that authorizes driving
it the rest of the way: push, open a pull request, merge on green, delete the branch, and park
the checkout back on main. "I committed and pushed" is an unfinished job. Merge first, QA
after - main is GitHub, not a public release.

The four supporting rules (see the TRUNK DEVELOPMENT section of the global CLAUDE.md):
- A branch lives less than a day - merge the day it is born.
- Soft cap of 3 open pull requests at once - finish one before opening a fourth.
- One checkout per concurrent activity - never build two workstreams from one tree; a
  concurrent agent uses its own worktree off origin/main.
- Never commit directly on main. If the checkout is on main, create a branch first.

## Triggers

Invoke with /commit or when user asks to commit changes.

## Scope

Default: Only files Claude modified this session.
With "all" argument: All uncommitted changes.
With specific files: Only those files.

## Workflow

STEP 1: Determine scope and gather changes

Run git status to see all changes.
Run git log --oneline -5 to see recent commit message style.

If user specified "all", include everything.
If user specified files, use those.
Otherwise, use only files modified during this Claude Code session.

STEP 2: Run code review

Use the Skill tool to invoke the review-code skill.

Wait for the review to complete.

Read the REVIEW_STATUS line from the output.

If REVIEW_STATUS is FAIL:
- Show the blocking issues found
- Tell the user they have three options:
  1. Reply "fix issues" for help resolving them
  2. Fix manually and run /commit again
  3. Reply "commit anyway" to bypass (not recommended)
- STOP and wait for user response

If REVIEW_STATUS is PASS:
- Note any warnings
- Continue to Step 3

STEP 3: Present commit plan

Show the user:
- List of files to commit (status and path)
- Code review result summary
- Proposed commit message

Commit message format:
  [type]: [Short description]

  [Optional body explaining why]

Type prefixes: feat, fix, refactor, docs, style, test, chore

Do NOT include "Co-Authored-By" lines in commit messages.

Ask: Approve this commit? Reply "yes" to commit and push.

STEP 4: Wait for approval

DO NOT PROCEED until user says yes, ok, approved, go ahead, or proceed.

If user says no or requests changes, update the plan and re-present.

STEP 5: Put the work on a branch (never commit directly on main)

Check the current branch: git branch --show-current.

If the checkout is already on a short-lived feature branch, commit there.

If the checkout is on main, create a branch FIRST - never commit on main:
- If no other session shares this checkout: git checkout -b <type>/<short-desc>
- If this checkout is shared with other live sessions (a fleet checkout), do NOT move its
  HEAD. Create an isolated worktree off origin/main and work there:
  git worktree add ../<repo>-wt-<short-desc> -b <type>/<short-desc> origin/main
  then cd into it. (See the "Shared checkout" rules in the global CLAUDE.md.)

STEP 6: Commit and push

Stage the specific files using git add with each filename.
Never use git add . or git add -A.

Commit using HEREDOC format:
git commit -m "$(cat <<'EOF'
[message here]
EOF
)"

Run git status to verify commit succeeded.

Push with: git push -u origin HEAD.

If push fails due to remote changes, run git pull --rebase then git push.

STEP 7: Open the pull request, merge on green, and park back on main

The job is not done until the work is MERGED to origin/main. Once the user approved the
commit (Step 4), drive it home:

1. RUN THE TESTS LOCALLY FIRST. This is the gate - not GitHub (issue #1156):

       .\scripts\test-local.ps1

   Do NOT hand-roll a dotnet test invocation - the script is the one place the whole fleet gets
   faster when the suite improves.

   KNOW WHAT THE DEFAULT RUN DOES NOT COVER. It runs the suites that fit the two-minute budget,
   1,634 tests as measured on 2026-09-13. It does NOT run the three parked suites - Gateway.Tests
   (host-bound, takes a machine-wide lock), Core.Tests, and Gateway.UnitTests (parked 2026-09-13 for
   outgrowing the ceiling, issue #2824) - which need -Parked. The last of those is the one most
   likely to catch you out: a green default run no longer says anything about the Gateway's unit
   tests. It runs NO web tests and NO Python
   tests. And -Fast is a NO-OP retained for old callers; the default is already the fast run, so
   passing it gates nothing and must never be cited as though it did.

   If your change touches a parked suite, the browser shells or the Python toolbelt, run that
   coverage yourself. A green default run is not evidence about code it never executed.

   If it is not green, fix it before opening a pull request. A red local run is a red change.

2. Open the pull request: gh pr create --fill (or with a title/body matching recent PRs).

3. NEVER WAIT FOR A CONTINUOUS INTEGRATION RESULT. Not for "Build & Test (.NET)", which takes
   roughly FIFTY MINUTES, and not for any other check. There is no exception - not a release, not
   a change to the build or to continuous integration itself, not a cross-platform change. The old
   rule carried that exception list and it is deliberately gone: every exception was an invitation
   to pay the fifty minutes again.

   Do NOT justify this by telling yourself the job runs the same tests you just ran. It does not
   run the same tests as your DEFAULT local run, which omits the three parked suites and never
   touches the two installer projects. That coverage is reachable locally and on purpose -
   `-Parked` for the suites, and the two explicit `dotnet test tools/cc-director-setup*.Tests`
   commands - which is what the release gate uses.

   The reason not to wait is not that the job is redundant. It is that a verdict arriving fifty
   minutes after everyone has moved on does not get read, so it buys nothing while costing a day.
   Where its coverage matters, run that coverage yourself, deliberately, instead of receiving it
   late by accident.

   If you want to see the fast checks in passing, look once and move on - do not watch them:

       gh pr checks <number>

   If something fails, fix it and push a NEW commit (never amend, never --no-verify).

4. GET A REVIEW FROM A DIFFERENT AGENT FAMILY BEFORE MERGING. The author is the last to see the
   defect, so the reviewer must not be the writer. Codex is the default reviewer, and it runs as
   a real tracked session - never backgrounded, never hidden:

       cc-devthrottle session spawn <repo> --agent Codex --prompt "<what to review>" --name "review: <what it is>"

   This review replaces the wait, it does not sit alongside it. Waiting bought a slow re-run of
   mostly the same tests plus some coverage you are better off running deliberately; a reviewer
   reads the change itself, which is the part no test run can do - including whether the change
   says something untrue about the code it describes.

5. Merge with squash once the local run is green and the review is clean:
   gh pr merge <number> --squash --delete-branch.
   (delete_branch_on_merge is ON, but --delete-branch is explicit and harmless.)

   Continuous integration keeps running after the merge as a backstop. If it goes red on main,
   FIX IT FORWARD IMMEDIATELY. That is the whole trade, and it only works if the red is actually
   chased - a red left standing turns the backstop into noise and then nobody looks at it at all.

   Chasing a red is not waiting for a green: nothing is held open pending a check, but the web
   and Python jobs are the only place those tests run at all. If you touched the browser shells
   or the Python toolbelt, merge without waiting and then go back and read that result.
6. Park the checkout back on main:
   git checkout main && git pull
   If you worked in a worktree, remove it: git worktree remove ../<repo>-wt-<short-desc>.
7. Verify the resting state: git status --short is empty, git branch --show-current is main,
   and the feature branch is gone (git branch --list <type>/<short-desc> prints nothing).

STEP 8: Report completion

Tell the user:
- Pull request number and merge commit on main
- Number of files changed
- That the branch was deleted and the checkout is parked back on main, clean
- Any exception WITH the reason. "Checks are still running" is NOT one - nothing is ever left
  open waiting for a check.

## Handling bypass requests

If user says "commit anyway" with blocking issues:
- Warn about the risks (runtime errors, security, UI issues)
- Ask them to type "I understand the risks"
- If confirmed, add a note about bypassed review in the commit message
- Proceed with commit

## Git rules (from CLAUDE.md)

NEVER do these:
- Update git config
- Run push --force, reset --hard, checkout ., clean -f
- Skip hooks with --no-verify
- Force push to main/master
- Use git add . or git add -A
- Amend commits unless explicitly requested

ALWAYS do these:
- Use HEREDOC for commit messages
- Drive an approved commit all the way to merged-on-main, then park the checkout on main
- Create NEW commit after hook failures, never amend

---

**Skill Version:** 2.0 - trunk-based development (2026-07-11)
**Changed from 1.0:** The workflow no longer stops at `git push`. Once the user approves the
commit it drives all the way to a merged pull request on origin/main and parks the checkout
back on main: branch-if-on-main (or isolated worktree on a shared checkout), push, open the
pull request, merge on green (squash + delete branch), park on main, verify clean. Added the
trunk-based-development framing (merged-to-main is the only "done") aligned with the global
CLAUDE.md TRUNK DEVELOPMENT section and the /repo-hygiene skill.
**Adapted from:** sampleWeb commit skill
