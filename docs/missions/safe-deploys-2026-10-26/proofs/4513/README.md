# Proof for issue #4513 - the deploy proves the built image boots before production is touched

Mission: Safe Deploys - never break production (#4517). Recorded 2026-10-26 on devlinux (Docker 29.9.2,
32 cores) and on a GitHub-hosted runner.

## What was built

- `scripts/prove-image-boots.sh <image> <expected-commit>` starts a throwaway `postgres:16`, then the image
  with the hosted contract satisfied (`CC_GATEWAY_HOSTED=1`, auth left on, `CC_GATEWAY_PUBLIC_URL=https://smoke.invalid`,
  `CC_GATEWAY_DB_CONNECTION` to that database). It passes only on **three consecutive HTTP 200s on `/healthz`,
  three seconds apart, each a JSON object whose `commit` equals the expected commit**, inside a bounded wait
  (`BOOT_TIMEOUT_SECONDS`, default 180). An empty or unparseable body, another commit, a non-200 or no answer
  resets the count. A container that exits fails at once. Every failure prints the container's state and
  its full stdout and stderr, then exits 1. Both containers and the network are removed whatever happens.
- `.github/workflows/deploy-hosted-gateway.yml`: a new step, "Prove the built image boots and serves before
  production is touched", in the `deploy` job after "Read the built image digest" and the contract-refusal
  proof, and before "Deploy the new image in place and measure the outage". It runs the script on the exact
  digest about to be pinned, expecting `steps.short.outputs.short`.
- `src/CcDirector.Core.UnitTests/Release/HostedImageBootsBeforeEveryDeployTests.cs`: in the default gate. It
  fails if the step disappears, moves before the digest or after the production pin, gains any key besides
  `env` and `run` (so no `if:` or `continue-on-error:`), stops using the digest or the run's commit, or if
  the script's pass condition is weakened.

## The diagnostic for #4516: commit 86b37ace5 boots cleanly here

Both images were built from the repository-root Dockerfile at their commits and booted with the script.

| Commit | Verdict | First 200 | Three passes | File |
| --- | --- | --- | --- | --- |
| `86b37ace5` (#4501, the deploy that failed in production) | **boots and serves** | 22s | 28s | `boot-86b37ace5.txt` |
| `b6d6acbc6` (#4500, the last healthy production image) | boots and serves | 19s | 25s | `boot-b6d6acbc6.txt` |

**These are local rebuilds, not the production images.** Each image was rebuilt on devlinux from the same
Dockerfile at the same commit. Neither is the digest the registry served production: that registry is
private to Azure and cannot be reached from here. A rebuild pulls the base images and npm packages as they
are today, so it is not byte-identical to what production ran. The gate closes that gap: on the runner it
boots the exact digest about to be pinned (`...@${{ steps.digest.outputs.digest }}`, never a tag), and
the guard test fails if the step stops using the digest.

No exception, no crash. Both print `Migrate: applied 113 migration(s)` about 12 seconds after start, then
`startup stores loaded; now serving`.

The server-side difference between the two commits is a new data class (`FactoryWhyCardDto`), one fold
method (`FactoriesScreenFold.WhyCard`) and one line in `FactoryNewFold`. It adds no migration and does not
touch `GatewayHost`, `GatewayEntryPoint` or the database code. The only other commit in the range, #4502,
is client-side dictation code.

**So the image itself is not broken on a fresh database, and the cause of #4516 points at the production
environment.** What a fresh database and a local disk do not exercise: the existing production database
(but there is no new migration to apply), and the Azure Files share. The image sets
`CC_DIRECTOR_ROOT=/home/gateway/cc-director`; on App Service `/home` is likely the persistent share, which
holds the SQLite statistics file. An in-place restart in which the old container still holds that file
could hang the new one. That is a hypothesis this proof cannot test, not an established cause.

## A good image passes, a broken image fails and prints its output

| Case | Result | File |
| --- | --- | --- |
| Good image `86b37ace5`, expected commit `86b37ace5` | PASSED, exit 0 | `boot-86b37ace5.txt` |
| Broken image: `CcDirector.Gateway.dll` removed, so the process crashes at start | FAILED in 3s, exit 1, printed `System.IO.FileNotFoundException: Could not load file or assembly 'CcDirector.Gateway ...'` and exit code 139 | `broken-image-crashes.txt` |
| Broken image: entry point replaced by `sleep infinity`, so it is up but never answers (the production signature) | FAILED at the 180s bound, exit 1, every poll `HTTP 000` | `broken-image-never-answers.txt` |
| Good image, expected a different commit (`deadbeef0`) | FAILED at the 180s bound, exit 1, `HTTP 200 carrying commit '86b37ace5', not deadbeef0`, full log printed | `good-image-wrong-commit.txt` |

## On a real GitHub-hosted runner

A throwaway push-triggered workflow on this branch only (`boot-proof-rehearsal-4513.yml`) built the image
from the branch and ran the script twice, touching nothing in Azure:
https://github.com/thefrederiksen/devthrottle/actions/runs/40411821990

- Good image, commit `b100654`: three passes by 15s, `BOOT PROOF PASSED`.
- Broken image (`CcDirector.Gateway.dll` removed): `BOOT PROOF FAILED` in 3s, exit 1, with the
  `FileNotFoundException` printed.

Excerpt: `github-runner-rehearsal.txt`. The workflow file was deleted in the following commit and the
workflow was disabled with `gh workflow disable` (state `disabled_manually`).

**What this does NOT cover:** the deploy workflow itself was never dispatched, because it deploys
production and only the owner decides that. The first real run of the new step will be the next deploy.
The rehearsal runs the same script with the same arguments shape on the same runner image, but not
inside `deploy-hosted-gateway.yml`, and not against a registry digest.

## The guard test

`HostedImageBootsBeforeEveryDeployTests` (2 tests) and the existing `HostedImageContractRunsBeforeEveryDeployTests`
pass. Adding `continue-on-error: true` to the new step makes
`DeployWorkflow_BootsTheBuiltImage_AfterTheDigestAndBeforeTheProductionPin` fail ("Collections differ"),
so the guard catches a switched-off step.

## The review, and what was done about it

The review is `review.md` in this folder (the reviewer seat, head `2239e529a`).

**Finding 1 (defect): the guard fails on correct code on a Windows checkout. ACCEPTED, fixed two ways.**
- The guard now normalises carriage return plus line feed to line feed when it reads the workflow, the
  script and `ci.yml`, the way its two siblings do.
- `.gitattributes` gains `*.sh text eol=lf`. A Windows checkout now keeps line feeds in every shell
  script, which also matters on the runner, where a carriage return would break the script itself. All
  43 shell scripts were already stored with line feeds (`git ls-files --eol`), so the rule changes no
  file in the repository.
- Re-run of the reviewer's mutations: the script converted to carriage return plus line feed (M1) now
  passes, and so does the workflow converted the same way (M2).

**Finding 2 (low): the guard pins text, not behaviour. ACCEPTED, fixed for both shapes the reviewer found.**
- `|| true` on the run line (M9): the guard now requires the step to have exactly one `run:` line, equal
  to the whole expected command. With `|| true` appended it fails ("Failed: 1").
- An early `exit 0` in the script (M10): no text check can rule that out, so the script now has a
  behavioural proof, `scripts/prove-image-boots.test.sh`, in the same shape as #4515's
  `check-production-healthy.test.sh`. It builds fake Gateway images (a small Python server on port 7878,
  from `python:3.12-alpine`) and runs the real script against ten shapes. Each case checks the exit code,
  the words printed, and that no container or network is left behind:
  - two pass: healthy, and starting with a 503 before turning healthy;
  - eight fail: a crash (its stderr must appear in the output), up but silent, an empty body, an HTML
    body, a JSON array, no commit, the wrong commit, and flapping (200, 200, 503).

  All ten pass (`script-against-fake-images.txt`). With `exit 0` inserted after `trap cleanup EXIT`, all
  ten go red ("10 case(s) FAILED"). The web job of `.github/workflows/ci.yml` runs it beside the #4515
  proof, and the guard now fails if that line leaves `ci.yml` or the test file is deleted.
- **What this still does not cover:** the behavioural proof runs in continuous integration's web job,
  not in the local gate, so an `exit 0` reaches main green locally and goes red in the next full run.
  That matches how #4515 is proved.

## The local gate

`scripts/test-local.ps1` on devlinux, summarised in `test-local.txt`. Every suite passed except two:

- `CcDirector.Core.UnitTests`: one failure, and it was this change. `GateTestsDoNotDependOnTheMachineTests`
  flagged the word "docker" in the new guard test, which only names `docker logs` as text it looks for in
  the script. The assertion now matches ` logs "$GW" 2>&1` instead. A rerun of the whole suite passed:
  2,772 passed, 0 failed.
- `cc-director-setup.Tests` printed no summary line. It targets `net10.0-windows`, so it cannot run on
  Linux. This change touches nothing in the installer.

The script's overall verdict reads NEVER RAN because of that Windows-only suite, and it could not write
its record (`/test-gate` access denied). That is how this script always behaves on devlinux, so I read
each suite's own result instead. The parked `Gateway.UnitTests` (16,466 passed) and `Core.Tests` (4,634
passed) ran in full as part of the same run.
