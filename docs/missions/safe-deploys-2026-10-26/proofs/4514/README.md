# Proof for issue #4514 - a deploy that never comes up rolls production back to last-known-good by itself

Mission: Safe Deploys - never break production (#4517). Pull request #4527. Recorded 2026-10-26 on devlinux.
The review is `review.md` beside this file; its items are answered at the end.

## What was built

- `scripts/rollback-to-last-known-good.sh`, called from the failure branch of "Deploy the new image in place
  and measure the outage" in `.github/workflows/deploy-hosted-gateway.yml`, after production has failed to
  serve three 200s in a row on the new commit for 600 seconds. It looks up the image tagged
  **last-known-good**, and nothing else. It refuses before changing anything when that tag is missing or is
  the image that just failed. Otherwise it pins that image once and waits by the deploy's own rule (three
  200s in a row on the last-known-good commit, three seconds apart, inside 600 seconds, and never sooner
  than 150 seconds after the pin). It then opens or comments on one alert issue naming the failed commit and
  the commit now serving, and stops. It never tries a second image and never loops. The step then exits 1
  whatever happened, so the run is always red.
- `scripts/resolve-gateway-image.sh` is the image lookup that used to be inside
  `rollback-hosted-gateway.yml`. The manual rollback and the automatic one both call it. The automatic one
  cannot dispatch the manual workflow, because the deploy job already holds the shared
  `hosted-gateway-production` group and the two would wait on each other.
- The comment that recorded the old "does not roll back by itself" decision now records the owner's ruling
  and the reason: an unneeded rollback costs about two minutes, while waiting for a person cost thirty.
- `src/CcDirector.Core.UnitTests/Release/ProductionRollsBackToLastKnownGoodTests.cs`, in the default gate.

**The trap.** On 2026-10-26 the deploy's outgoing record named the already-broken image, with commit
"unknown". The script is not handed the outgoing record at all, and the old error text, which suggested
rolling back to the outgoing commit, is gone.

## What is proven, and where

| Claim | Proof | File |
| --- | --- | --- |
| The target is last-known-good, never the outgoing image, including the 2026-10-26 shape (outgoing commit `unknown`, outgoing image equal to the failed image) | the dry run against a fake `az`, a fake `gh` and a fake `/healthz` checks what was **pinned**: exactly one pin, of last-known-good, and the failed image never | `dry-run-against-fakes.txt`, cases `replay-2026-10-26` and `outgoing-is-another-image` |
| A missing tag, a tag on the failed image, a refused pin, or a target that does not come up: it says so and stops, pinning at most once | the same dry run | cases `lkg-missing`, `lkg-is-the-failed-image`, `pin-refused`, `repin-does-not-come-up` |
| The wait follows the deploy's rule, and a 200 on the wrong commit is not a recovery | the same dry run | cases `streak-must-be-unbroken`, `settle-is-respected`, `serves-wrong-commit`, `lkg-without-ci-tag-serves-failed-commit` |
| One alert issue, commented on rather than duplicated, and a failed write is loud but changes nothing | the same dry run | cases `issue-already-open`, `issue-write-fails` |
| **The workflow's own failure-branch text, run under `bash -e` the way GitHub runs it, ends with production on last-known-good and exits 1**, including when the rollback itself fails | the dry run cuts the branch out of the YAML and runs it | cases `workflow-branch`, `workflow-branch-rollback-fails` |
| The shared lookup handles all three forms and a miss | the same dry run | the resolver cases |
| The wiring cannot quietly drift | the guard: eleven mutations, each required to trip its named rule | `guard-test.txt` |
| Nothing else broke | the local gate; every suite that ran had zero failures | `test-local.txt` |

The dry run passes 65 of 65 checks, and the guard passes 13 of 13. Continuous integration's web job runs
the dry run.

## What is outstanding

**The live proving run.** No one has yet run a deploy of an image that cannot start and seen it end with
production on last-known-good, a red run, and the alert issue opened. A development seat must not dispatch
either workflow, run `az` against the production site, or pin an image. This is why the pull request
refers to #4514 instead of closing it, and the issue stays open until that run is attached.

It matters more than it first looked. The #4513 review found the 2026-10-26 image boots cleanly on the
runner, so that outage came from the environment and the boot gate would not have caught it; this rollback
would have. If the environment is what failed, last-known-good may not come up either. Then the script says
so, raises the alert, and stops.

Only that run can prove two things:

- The Azure login from the job's start is still valid when the rollback pins. The review measured this on
  the failed run 40397928305: the pin would come about 14.5 minutes after login, and the last Azure command
  in the job about 26 minutes after it, in the worst case. Tokens from this login last at least 60 minutes.
- The job token can write issues once `issues: write` is granted.

## Answers to the review

1. **"Closes #4514" against a definition of done that is not met.** Changed to "Refs #4514", as the
   Manager decided. The issue stays open until the live proving run is attached.
2. **No proof folder.** This folder: this README, `dry-run-against-fakes.txt`, `guard-test.txt` and
   `test-local.txt`, beside `review.md`.

The review's notes, none of which required a change:

- **The owner's reflex during a rollback.** Taken up: the script now prints, as it starts, not to start the
  manual rollback workflow while it runs. A manual rollback would wait behind the deploy job and then
  restart a production that had just recovered. It is a printed line only, and the dry run after it passes
  65 of 65.
- **Teams-switch settings stay written after a rollback.** Unchanged, and the manual path has always done
  the same. A deploy that changes a switch and then fails leaves last-known-good running with the new
  setting. The alert issue is where a person would notice that.
- **The dry run uses real time.** True. It is not in the local gate; it runs only in the web job of
  continuous integration, with two seconds or more of margin. If it ever fails on correct code, the rule is
  to delete that case, not to widen its timings.
- **The token is written into the run text instead of `env:`.** It matches the rest of that step and is
  masked in logs. Unchanged.
- **`issues: write` at workflow level also reaches `watch` and `retain`.** Unchanged: those jobs run only
  this repository's own steps, and narrowing the grant to one job means repeating every other permission on
  that job.
- **A deploy that comes up and then dies during the ten-minute watch is not rolled back.** That is the
  issue's scope, which names the readiness failure branch only. Nobody should expect more from this change.
