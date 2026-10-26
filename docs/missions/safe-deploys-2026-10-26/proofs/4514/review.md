# Review of pull request #4527 (issue #4514): roll back to last-known-good by itself when production never comes up

Reviewer seat on the Safe Deploys mission, 2026-10-26, on devlinux. Head reviewed: `41972095b`, checked out
detached on top of `origin/main` `5b1c85ec6` (the merge base is `5b1c85ec6` exactly; origin/main had not
moved when this review ended). The review was asked for by the Manager, session 128, and is written here
uncommitted as instructed. This pull request commits no proof folder of its own, so this file sits in the
mission's proofs folder beside the one for #4513.

## Verdict

**Mergeable as it stands. No code defect found that must change.** The manual rollback resolves and pins
exactly what it did before for every input it accepts, and is safer against a malformed input than it was.
The automatic path does what issue #4514 asks: the target is the image tagged last-known-good and nothing
else, it runs only after the deploy's own 600-second wait has failed, it waits by the deploy's own rule, it
ends the run red whatever happens, it raises one alert issue, it never tries a second image, and it never
dispatches a workflow from inside the held concurrency group. Every way I found to break the behaviour
failed a proof.

Two things are for the Manager to decide, and neither is a code defect:

1. **The pull request says "Closes #4514", but the issue's definition of done requires the proving run,
   and the pull request itself says that run is still owed.** Merging as written closes an issue whose
   definition of done is not met. Either the "Closes" line goes and the issue stays open until the proving
   run is attached, or the Manager accepts closing it on the fake-backed proofs alone and says so.
2. **No proof folder was committed.** Pull request #4522 for the sibling issue kept its outputs and its
   review under `docs/missions/safe-deploys-2026-10-26/proofs/4513/`. Here the 65-check dry run output and
   the local gate figures exist only in the pull request body. The scripts are committed and I re-ran them,
   so nothing is lost today, but the mission's record is thinner than the sibling's.

## Scope

**What I read.** Issue #4514 (body; it has no comments); pull request #4527, its body and both commits; the
full diff against `5b1c85ec6` (seven files); the whole of `rollback-hosted-gateway.yml` as it now stands and
the removed block it replaces, line by line; `scripts/resolve-gateway-image.sh`;
`scripts/rollback-to-last-known-good.sh`; `scripts/rollback-to-last-known-good.test.sh`;
`ProductionRollsBackToLastKnownGoodTests.cs`; the `ci.yml` change; and in `deploy-hosted-gateway.yml` the
permissions block, the deploy job header and its concurrency comment, the login step, every step from the
production health gate through the hand-over of the poll record, the metrics row in the watch job, and the
whole `retain` job. On GitHub: the failed deploy run 40397928305 (step timings and its metrics artifact),
the manual rollback run 40400973996 (its log), and the eight most recent deploy runs.

**What I ran.**
- `scripts/rollback-to-last-known-good.test.sh` as committed: ALL CASES PASSED, 65 checks, 19 seconds.
- The guard class in `CcDirector.Core.UnitTests` with a filter: 13 tests passed (two facts and the eleven
  mutation cases).
- Eight mutations of the rollback script, re-running the dry run after each and restoring the file
  (table below). Two mutations on disk against the guard (table below). The worktree is clean.
- The manual rollback's new resolve step, exactly as the workflow runs it (`set -euo pipefail`, the script,
  `tee -a` into an output file), against a fake registry, for six inputs (table below).
- A read-only search of the repository for an open alert issue with the new title: none.
- A read-only request to the production `/healthz` on both hostnames: 200, commit `b6d6acb`, teams true.

**What I could not reach.** No workflow was dispatched, no Azure command was run against anything real, and
nothing was pinned. So the two things the pull request names as owed to the live run stay owed: that the
Azure session is still valid when the rollback pins, and that the job token can write issues. I did
narrow the first one with measured timings (below). The guard was run on Linux only; it reads files with
line endings normalised, so the Windows line-ending defect found in #4513's guard does not apply here.

## The manual rollback, first and hardest

The emergency lever changed in three ways: a checkout step was added before the resolve step, the resolve
block moved into `scripts/resolve-gateway-image.sh`, and the input is passed through an environment
variable instead of being pasted into the shell text.

**The checkout.** `permissions` already grants `contents: read`, which is all `actions/checkout@v5` needs,
and it is the same version the deploy workflow uses. It runs after the Azure login and does not disturb
it: the login writes under the home directory, the checkout writes the workspace. Cost: the deploy job's
own checkout of this repository took five seconds on 2026-10-26 (20:44:45 to 20:44:50). The lever now
depends on being able to fetch the repository contents, which it did not before; the workflow file itself
already comes from the same place, so this is not a new kind of dependency.

**The moved block, line by line.** The three-way `case` on the input, the `@` versus `:` reference, the
`az acr repository show` call with its query, the error text and the tag listing on a miss, the
`ci-<commit>` shortcut, the `az acr manifest list-metadata` lookup for a moving tag or a digest, and the
"its absence must not refuse" rule are all unchanged. Three differences, none of which changes what is
pinned: everything meant for a person now goes to standard error, so standard output holds exactly the two
`GITHUB_OUTPUT` lines; the registry's answer must start with `sha256:` or the step fails before production
is touched; and the tag listing on a miss has its own standard error silenced. The two downstream steps
still read `steps.target.outputs.digest` and `steps.target.outputs.commit`, and both are still written,
including the empty `commit=` that drives the manual workflow's "health only, not which commit" path.

**The step, run by hand for every input form.** A fake `az` answered `ci-abc1234` with `sha256:aaaa`,
`last-known-good` with `sha256:bbbb` carrying a `ci-lkg0000` tag, and the digest `sha256:cccc` with no
`ci-` tag.

| Input | Step exit | What reached GITHUB_OUTPUT | Production |
|---|---|---|---|
| `last-known-good` | 0 | `digest=sha256:bbbb`, `commit=lkg0000` | would be pinned to that digest, as before |
| `abc1234` | 0 | `digest=sha256:aaaa`, `commit=abc1234` | as before |
| `sha256:cccc` | 0 | `digest=sha256:cccc`, `commit=` (empty) | as before; the recover step then checks health only |
| `nosuch` | 1 | nothing | untouched; the miss text and the tag list are in the log |
| `last-known-good; echo INJECTED` | 1 | nothing | untouched; treated as the tag name `ci-last-known-good; echo INJECTED`, nothing executed |
| empty | 2 | nothing | untouched |

`pipefail` is what makes a miss fail the step through `tee`, and the step sets it explicitly (the default
step shell sets it too). The fifth row is the improvement: the old block pasted the input into the shell
text, so that input would have been executed.

**The real lever worked on 2026-10-26.** Run 40400973996 resolved `last-known-good` to commit `b6d6acb`,
digest `sha256:a29857b7...`, and production serves `b6d6acb` now. So the tag the automatic path depends on
exists in the registry today and is trustworthy.

## The automatic path, against the issue

**Is the target always last-known-good?** Yes. The failure branch hands the script `FAILED_COMMIT`,
`FAILED_DIGEST`, `RUN_URL`, `GH_TOKEN` and `GH_REPO` and nothing else; the script's only resolution is
`bash "$RESOLVER" last-known-good`; and the dry run sets the outgoing values in every name the workflow has
for them, in the exact 2026-10-26 shape (commit `unknown`, image equal to the failed image), and checks
what was pinned: exactly one pin, of the last-known-good digest, and the failed digest never. A second case
makes the outgoing image a third, healthy image and checks it is still not chosen.

**Does it run only in the failure branch, after the sustained-200 watch fails?** Yes. It sits inside
`if [ "$streak" -lt 3 ]` after the image pin's 600-second loop. The earlier failure branch in the same step,
for a Teams-switch write that does not come back, does not call it, which is right: the image has not been
changed there. A passing deploy never reaches it. One scope point to be clear about: a deploy that comes
up and then dies during the ten-minute `watch` job is not rolled back automatically. That matches the issue,
which names the readiness failure branch and nothing else, but nobody should expect more.

**The same rule, red whatever happens, a loud issue, the permission.** The wait is three consecutive 200s,
three seconds apart, inside 600 seconds, and never sooner than 150 seconds after the pin, which is the
manual workflow's settle rule. With the commit known only that commit counts; with it unknown (the `ci-` tag
purged) any commit except the failed one counts and the result says the commit was not checked; both are in
the dry run, including the failed container answering 200 under an unknown commit, which is not a recovery.
The branch ends `exit 1` after `|| rc=$?`, and the dry run cuts the branch out of the workflow text and runs
it under `bash -e` the way GitHub does, in both the recovered and the unresolved shape: both exit 1, and the
unresolved one still prints its line instead of being cut short. The alert issue looks up an open issue by
its fixed title and comments on it, else opens one; its body names the failed commit and digest, the target
and its commit, the commit production serves now, and the run. `issues: write` is granted at workflow level,
the deploy is `workflow_dispatch` only, and the guard fails if the grant goes. One thing I could prove
without a run: outputs of a failed job do reach the watch job, because the 2026-10-26 run's metrics artifact
carries `build_seconds` 194 and `commit_short` from the deploy job that failed. So
`rollback: automatic-<outcome>` will land in the metrics row.

**If last-known-good cannot be resolved, or does not come up.** Unresolved: exit 3, nothing pinned, the
text says production was not rolled back and is probably still down and a person must run the manual
workflow. Same image as the one that failed: exit 3, nothing pinned. Pin refused: exit 1, one pin recorded.
Pinned and not coming up: exit 1, one pin, "No other image was tried". Every one of these raises the issue
and writes the outcome. There is no loop and no second target; the mutation that adds one is caught.

**Concurrency.** The script runs inside the deploy job, which holds `hosted-gateway-production`. Neither the
branch nor the script dispatches anything; the guard rejects `workflow run`, `/dispatches` and
`createWorkflowDispatch` in both, and its mutations prove it notices. The `retain` job, which also holds the
group, is skipped on a failed readiness, so nothing else in the run contends for the group.

**The Azure session and the time limits.** Measured on the real failed run 40397928305: Azure login
20:44:53, image pinned about 20:49, the readiness step failed 20:59:17. With this change the rollback's
single Azure command, the pin, would run about fourteen and a half minutes after login, and its wait uses
only `curl`. The latest Azure command in the whole job, the Teams report after the step, would run about
twenty-six minutes after login in the worst case (a full 600-second rollback wait). Access tokens from this
kind of login are issued for at least sixty minutes and nothing here needs a refresh. The deploy job sets no
`timeout-minutes`, so GitHub's six-hour default applies; the step sets none; nothing cuts the rollback
between the pin and the verdict. The live run is still the proof of the token, as the pull request says.

**The old comment.** Rewritten, not deleted: it records the owner's ruling, the thirty minutes measured on
2026-10-26, and the asymmetry (two minutes against thirty). The guard fails if the old sentence returns or
the new one goes.

## Proving the proofs by mutation

Eight mutations of `scripts/rollback-to-last-known-good.sh`, each followed by the dry run and a restore:

| Mutation | Dry run | Which checks noticed |
|---|---|---|
| Pin the failed digest instead of last-known-good | failed, 23 checks | wrong pins; failed image pinned; exit codes; issue text |
| Prefer `OUTGOING_IMAGE` when the environment carries it | failed, 11 checks | the 2026-10-26 replay pinned the broken image |
| Skip the "same image as the one that failed" refusal | failed, 3 checks | that case pinned, exit 1 not 3, outcome word missing |
| One 200 is enough instead of three | failed, 1 check | the unbroken-streak case never reached streak 3 |
| Count a 200 on the wrong commit as recovery | failed, 2 checks | the wrong-commit case recovered |
| Pin a second image when last-known-good does not come up | failed, 1 check | pins were not exactly one |
| Lose the serving commit on the recovered path | failed, 2 checks | output and issue body lacked the commit |
| Never raise the issue | failed, 9 checks | no issue file in every shape |

Two mutations on disk against the C# guard, each followed by the guard and a restore:

| Mutation | Guard |
|---|---|
| The failure branch hands the outgoing image under a new name, `PREVIOUS_IMAGE` | failed: "hands the outgoing record to the rollback" |
| The script waits for one 200 instead of three | passed |

The second row is expected and is the honest limit of a text guard: it cannot see behaviour, and its own
summary says the behaviour is proved by the dry run, which did catch that mutation. The guard's own eleven
theory cases passed, meaning each of its rules trips on the mutation written for it.

## Findings

No finding proves harm in the code. The two items for the Manager are in the verdict above: the "Closes"
line against an unmet definition of done, and the missing proof folder.

## Notes, no change required

- **The owner's reflex during a rollback.** On 2026-10-26 the owner dispatched the manual rollback three
  minutes after the deploy failed. With this change, a manual rollback dispatched during the automatic one
  waits behind the deploy job in the same group (one waiting slot, so a queued deploy and a queued rollback
  cancel each other) and then re-pins a production that has just recovered, costing one more restart. One
  line in the failure branch telling the person watching not to dispatch the manual lever while the
  automatic one runs would save that. Not blocking.
- **Teams-switch settings** written before a failed pin stay written after the rollback, so last-known-good
  runs with the new settings. The manual path has always behaved this way.
- **The dry run uses real time**: a two-second settle, a four-second deadline, a one-second flip of the fake
  table. It runs in the web job of continuous integration, not in the local gate, and the margins are two
  seconds or more, so the risk of failing on correct code is low.
- `GH_TOKEN="${{ github.token }}"` is pasted into the run text rather than set under `env:`. It is masked in
  logs and matches the rest of the step's style.
- `issues: write` at workflow level also reaches the `watch` and `retain` jobs, which do not need it.
  Harmless.
