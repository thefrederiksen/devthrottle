# Review of pull request #4521: refuse to deploy onto a production site that is already down (issue #4515)

Reviewer seat on the Mission "Safe Deploys - never break production". Reviewed on 2026-10-26 against the
pull request head 2bcae2f19, checked out detached at `/home/soren/ReposFred/devthrottle-review-4521`,
on top of origin/main ee943d301. Since that base origin/main has moved by one commit (6fa361f57, a
factory page change) which touches none of the files in this pull request.

## Verdict

**Approve. No blocking finding.** The gate does what issue #4515 asks, it is in the right place, it
cannot be skipped without the run saying so, and the guard test fails under every weakening I tried.
Five observations follow; none of them shows a harm that must be fixed before merge, and I say for each
why not.

## Scope

**What I read, in full:** issue #4515, pull request #4521, the whole diff from ee943d301 to the head
(five files, 616 added lines, nothing deleted), `scripts/check-production-healthy.sh`,
`scripts/check-production-healthy.test.sh`, the new step and the new input in
`.github/workflows/deploy-hosted-gateway.yml`, the step map of the whole deploy workflow (every step
name, every `if:`, every `az webapp` and `az acr` command, every `/healthz` read),
`ProductionHealthGateRunsBeforeEveryDeployTests.cs`, the inputs of `rollback-hosted-gateway.yml`, the
`/healthz` endpoint in `GatewayEndpoints.cs` on origin/main, the project list of `scripts/test-local.ps1`,
and the headings of `docs/evidence/4515-refuse-unhealthy-production.md`.

**What I ran:**

- `bash scripts/check-production-healthy.test.sh`: 14 of 14 cases pass.
- The same proof with the script deliberately broken (the non-200 branch made to return success):
  4 of 14 cases fail, so the shell proof does detect a behavioural regression. The script was restored
  and `git status` is clean.
- The three guard tests in `ProductionHealthGateRunsBeforeEveryDeployTests`, built with the .NET 10 SDK
  on devlinux: 3 of 3 pass on the unchanged workflow.
- The same three tests under six weakenings of the workflow, each applied and then reverted: adding an
  `if:` to the step, adding `continue-on-error: true`, removing the step, moving the step after the deploy
  step (so after the image pin), moving the step after the outgoing reads, and flipping the input default
  to true. **Every one of the six makes the guard fail** (1 failed, 2 passed each time). The workflow was
  restored and `git status` is clean.
- A read-only probe of live production: both `https://devthrottle-gw.azurewebsites.net/healthz` and
  `https://gateway.devthrottle.com/healthz` returned HTTP 200 with commit `b6d6acb` in about 0.13 seconds.
  The script run against the real `GATEWAY_URL` passes with exit 0.

**What I could not reach:** no run on a GitHub runner, because dispatching the deploy workflow is out of
bounds and production cannot be made unhealthy on purpose; the pull request says the same. `shellcheck`
is not installed on this machine, so the scripts were read by eye for runner portability instead. I did not
rebase the head onto the moved origin/main; the one new commit touches none of these files, so there is
nothing to conflict.

## The Manager's five questions, answered

**1. Does the gate run before anything mutates production, on every path, and can it be skipped only by
the deliberate input?** Yes. The workflow has exactly one trigger, `workflow_dispatch`, and one job that
changes production, `deploy`. In that job the only commands that change production are
`az webapp config appsettings set` (the Teams switch write) and `az webapp config container set` (the
image pin), both inside the step "Deploy the new image in place and measure the outage". The new gate step
sits before the two outgoing reads and before that deploy step. Everything that runs before the gate only
reads, logs in, or writes to the container registry, never to the production site. The step has no `if:`
and no `continue-on-error:`; the escape hatch is an environment variable handed to the script, which still
probes and still reports. The `watch` and `retain` jobs read production and tag images in the registry;
neither changes the site.

**2. Is the pass condition a presence, and is "no answer" bounded?** Yes. The script returns success only
when curl exits 0, the HTTP code is exactly 200, the body is non-empty, `jq` parses it, and `.commit` is a
non-empty, non-null string. Exit 28 (timeout), any other curl failure, code 000, any non-200 code including
502 and the Gateway's own 503 "starting" answer, an empty body, a non-JSON body, a missing commit and an
empty commit all refuse, and each was exercised by the fake server. The per-request timeout is 10 seconds
with 3 attempts and a 2 second pause, so the worst case is 34 seconds, well under the 224 second front-end
hold the issue describes. The fake server's hang case proves the bound (3 attempts at 2 seconds finished in
6 seconds).

**3. Is the refusal plain, does it name what `/healthz` returned, and does it point at rollback? Is the
escape hatch loud?** Yes. The refusal begins "REFUSED: production is already unhealthy", prints the
observed answer in words (for example "HTTP 502, body: ..." or "no answer within 10s (timed out; HTTP
000)"), and prints `gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good`. I
checked the rollback workflow: its input is named `commit`, is required, and documents `last-known-good`
as an accepted value, so the suggested command is valid. The same text goes to the run summary. With the
escape hatch set and production unhealthy, the run summary gets a heading beginning "### WARNING: deployed
onto UNHEALTHY production - the production health gate was SKIPPED" with the observed answer in bold; the
fake-server proof checks that heading and the "HTTP 502" text in the summary.

**4. Will it behave the same on the GitHub ubuntu runner?** I see nothing that would differ. `curl`, `jq`,
`mktemp`, `seq` and `head` are all present on `ubuntu-latest`. The script sets `set -uo pipefail`
deliberately without `-e`, handles curl's exit code itself, and sets `OBSERVED` on every return path of
the probe, so `set -u` cannot trip in normal use. The step is `run: bash scripts/check-production-healthy.sh
"$GATEWAY_URL"`; `actions/checkout@v5` runs earlier in the job, `GATEWAY_URL` is a workflow-level `env`,
and the boolean input renders as the string `true` or `false` in `env:`, which is what the script compares
against. The input is placed in `env:` rather than interpolated into `run:`, so there is no shell
injection path. The default step shell on Linux is `bash -e`, so the script's exit 1 fails the step.

**5. Does the unit test fail if the step is removed, moved after the pin, or made skippable?** Yes, by
experiment, not by reading: all six weakenings listed under "What I ran" make the guard fail. The guard
checks the step by name, its position relative to both outgoing reads, the deploy step and the first
`az webapp config container set`, and that the step's keys are exactly `env` and `run`. It also checks the
input is a boolean defaulting to false, and that the script carries the pass condition, the refusal words,
the rollback workflow name (and that the rollback workflow file exists) and the summary warning.
`Core.UnitTests` is in the default project list of `scripts/test-local.ps1`, so the guard runs in every
default local gate.

## Observations, none blocking

**A. The shell proof runs nowhere automatically.** `check-production-healthy.test.sh` is not referenced by
`scripts/test-local.ps1` or any workflow under `.github/workflows`. The C# guard checks that certain strings
are present in the script, not what the script does. I broke the script (the non-200 branch made to return
success) and the C# guard still passed; only the hand-run shell proof caught it. Why not blocking: the
behavioural proof exists, is committed beside the script, and does catch regressions when run; the gap is
that nobody is made to run it. Suggested follow-up: run the shell proof from the Linux job of `ci.yml`, or
say in the script header that it is hand-run and must be run on every change to the script.

**B. The `commit=` step output is dead.** The script writes `commit=<live commit>` to `GITHUB_OUTPUT` on a
pass, but the step has no `id:`, so no later step can read it, and the guard forbids adding one because it
requires the step's keys to be exactly `env` and `run`. Why not blocking: it is harmless dead output; the
following step re-reads the outgoing commit itself. Either drop the write or allow `id` in the guard.

**C. The guard pins the gate ahead of the image pin but not ahead of the Teams switch write.**
`az webapp config appsettings set` also changes production and restarts the site. Today it lives inside the
deploy step, so the "gate before the deploy step" assertion covers it. If that write were ever split into
its own earlier step, the guard would not notice. Why not blocking: no such step exists; a one-line extra
assertion on the first `az webapp config appsettings set` would close it.

**D. A 503 "starting" answer is refused with rollback advice.** The Gateway answers 503 with
`"status":"starting"` while its database is opening. The gate correctly refuses, but the refusal always
says the next action is recovery, when on a 503 of this shape the right action may be to wait a minute and
rerun. Why not blocking: the refusal prints the body snippet, so the operator can see "starting" and
decide; a deploy onto a Gateway that is mid-restart is also not a safe deploy, so refusing is right.

**E. The `firstPin` assertion searches the raw text.** It finds the first occurrence of
`az webapp config container set` anywhere in the file, including comments. The one comment mention today
is after the gate. A future comment above the gate that quoted the command would fail the guard falsely.
Why not blocking: a false failure is loud and immediately understood, never a silent pass.

## Nothing to fix before merge

The change meets the issue's definition of done as far as it can be met without a runner: a deploy onto
unhealthy production stops before anything is read or pinned, with a message naming what `/healthz`
returned; a deploy onto healthy production is unaffected (proved against live production, read-only); the
escape hatch works and writes a loud heading into the run summary. The first real deploy after merge is the
first run on a runner, as the pull request itself states.
