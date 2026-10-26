# Review of pull request #4522 (issue #4513): prove the built image boots before production is touched

Reviewer seat on the Safe Deploys mission, 2026-10-26, on devlinux. Head reviewed: `2239e529a`, checked out
detached on top of `origin/main` `fa8a80449` (the merge base is `fa8a80449` exactly). The review was asked
for by the Manager, session 128, and is written here uncommitted as instructed.

## Verdict

**Mergeable after one fix. Two findings, one of them a defect that must change.** The step, the script and
its pass condition do what issue #4513 asks: the exact digest is booted on every deploy, before anything
that changes production, and only three consecutive healthy answers carrying the run's commit let the
deploy continue. Every failure case I could construct failed. The defect is in the guard test: on a Windows
checkout with the usual line-ending conversion it fails on correct code, which under this repository's test
standard would get the guard deleted. The second finding is that the guard pins text, not behaviour, so two
one-line edits that make the gate pass unconditionally leave it green, which is narrower than the pull
request claims.

## Scope

**What I read.** Issue #4513, pull request #4522 and both its commits; the full diff against `fa8a80449`
(eleven files, all additions); `scripts/prove-image-boots.sh`; the new step and the whole `deploy` job of
`.github/workflows/deploy-hosted-gateway.yml` (lines 1 to 560); the guard test
`HostedImageBootsBeforeEveryDeployTests.cs` and its two siblings for the contract proof and the production
health gate; `src/CcDirector.Gateway/HostedStartupContract.cs`; the `/healthz` endpoint in
`GatewayEndpoints.cs`; the `Dockerfile`; the stdout log mirror switch-off in `GatewayHost.cs`; the
machine-dependence scanner; `.gitattributes`; every file in the proof folder; the rehearsal workflow file
as it existed in commit `510711e8b`; the rehearsal run's job, steps and log on GitHub.

**What I ran.**
- The three deploy-guard test classes in `CcDirector.Core.UnitTests` with a filter: 6 tests, all pass.
- The script against the Developer's local rebuild of `86b37ace5`: passed (three 200s, migrations applied,
  "startup stores loaded; now serving"). Against the crashing image: failed in 3 seconds with the
  `FileNotFoundException` printed and exit code 139 shown.
- The script against six tiny fake servers I built, each answering on port 7878: empty body, an HTML body,
  a JSON array, a JSON object with no `commit`, a server that alternates two good answers with a 503, and
  a good server. The first five failed with the right verdict; the flapping one showed the count reset to
  zero; the good one passed. Containers and network were gone after every run, pass or fail.
- Eleven mutations of the workflow and the script, running the guard after each and restoring the files
  (table below). The worktree is clean.
- A read-only request to the production `/healthz`: 200, `commit` is `b6d6acb`, `teams` is true.

**What I could not reach.** The deploy workflow was never dispatched, so the step has never run inside it
or against a registry digest; the pull request says the same. The Azure registry is private, so I could
not boot the digest production ran on 2026-10-26. I did not run on Windows: the line-ending defect was
reproduced on Linux by converting the file, not on a real Windows checkout. I ran only the three guard
classes, not the full local gate, and did not re-run the parked suites the pull request reports.

## Finding 1 - defect: the guard fails on correct code on a Windows checkout

`BootScript_PassConditionIsThreeConsecutiveHealthyAnswersCarryingTheCommit` matches the script with
`new Regex(@"^fail\(\) \{\n(?:.*\n)*?  exit 1\n\}", RegexOptions.Multiline)` on the raw file text.

- `.gitattributes` has no rule for `*.sh` (`git check-attr -a scripts/prove-image-boots.sh` prints
  nothing). Git for Windows defaults to converting line endings on checkout, so on those machines the
  working-tree file ends its lines with carriage return plus line feed.
- Then `\{\n` and `exit 1\n` never match. Mutation M1 (convert the script to carriage return plus line
  feed, nothing else changed) makes the test fail: `Failed: 1, Passed: 1`.
- Seven other file-reading guards in `CcDirector.Core.UnitTests` normalise with
  `.Replace("\r\n", "\n")` before matching, including the #4515 sibling
  `ProductionHealthGateRunsBeforeEveryDeployTests`, which reads the same workflow and its own script.
  This test is the only one of the three deploy guards that does not.

**The harm.** The default gate runs on the owner's Windows machines. There it goes red on correct code,
and the owner's test standard (13 October 2026) says a test that fails on correct code is deleted, with no
repair attempt. So the guard that keeps the boot proof in the deploy would be deleted the first time it
is seen red on Windows, or the gate stays red for every Windows developer. Either way the pull request's
own promise, that the gate "cannot be quietly dropped later", is lost. The fix is the Developer's call;
the sibling's normalisation, or a `*.sh text eol=lf` rule in `.gitattributes`, would each do.

## Finding 2 - the guard pins text, not behaviour; two edits switch the gate off unseen

The pull request and the proof say the guard fails if the step is "made skippable" or "the script's pass
condition is weakened". Two mutations that make the gate pass unconditionally leave the guard green:

- M9: `run: bash scripts/prove-image-boots.sh "$IMAGE" "${{ steps.short.outputs.short }}" || true`. The
  step can never fail; the guard's `Assert.Contains` on the run line still matches because it does not
  require the line to end there. `Passed: 2`.
- M10: `exit 0` inserted after `trap cleanup EXIT` in the script. Every string the guard looks for is still
  present. `Passed: 2`.

**The harm.** The guard's claim in the record is wider than what it checks. Anyone reading the proof
folder would believe a disabled gate cannot reach main, and for these two shapes it can. Low severity: both
edits are deliberate rather than drift, and the two sibling guards share the same shape. Two directions,
for the Developer to weigh: assert the run line through its end of line, and give the script a behavioural
test against a fake, the way `scripts/check-production-healthy.test.sh` proves the #4515 probe. The fake
servers I built for this review show the shape such a test could take.

## Mutation table

| Mutation | Guard result | Caught? |
| --- | --- | --- |
| M1 script converted to carriage return plus line feed (correct code) | Failed 1 | false red: Finding 1 |
| M2 workflow converted to carriage return plus line feed (correct code) | Passed 2 | correct |
| M3 `continue-on-error: true` added to the step | Failed 1 | yes |
| M4 `if: false` added to the step | Failed 1 | yes |
| M5 `@digest` replaced by the `:ci-<short>` tag | Failed 1 | yes |
| M6 step deleted | Failed 1 | yes |
| M7 step moved after the deploy step | Failed 1 | yes |
| M8 `REQUIRED_PASSES=3` changed to 1 | Failed 1 | yes |
| M9 `|| true` appended to the run line | Passed 2 | no: Finding 2 |
| M10 `exit 0` inserted at the top of the script | Passed 2 | no: Finding 2 |
| M11 expected commit replaced by a literal | Failed 1 | yes |

## The Manager's questions, answered

**Does it boot the exact digest, on every deploy, before every mutation, with no way to skip it?** Yes.
The step's `IMAGE` is `...@${{ steps.digest.outputs.digest }}`, the digest the previous step already
pulled and verified against the push. The step has only `env` and `run`; no `if`, no
`continue-on-error`, and the workflow has no input that bypasses it. It sits after the digest read and the
contract-refusal proof, and before the production health gate, both outgoing reads, the Teams read and the
deploy step. Both commands that change production (the image pin and the app-settings write) live inside
the deploy step, so everything that mutates comes after it. The guard checks the order against the deploy
step by name; the #4515 sibling checks the two mutating commands themselves, which would also catch a
mutation moved out of that step. That is a robustness difference, not a defect.

**Is the pass condition a presence, and do the failure cases fail?** Yes. Pass means three consecutive
answers, each HTTP 200, a non-empty body, `jq -e 'type == "object"'` true, and `.commit` equal to the
expected value, with a 3-second sleep between polls and a deadline of `BOOT_TIMEOUT_SECONDS` (180) from
the Gateway container's start, checked before every sleep. Observed: empty body fails ("HTTP 200 with an
EMPTY body"); HTML and a JSON array fail ("not a JSON object"); a missing commit fails ("carrying commit
'<none>'"); the Developer's wrong-commit run fails; a crash fails at once ("exited after 3s"); up but
silent fails at the bound with "HTTP 000"; the 503 the Gateway sends before its database is open resets
the count. The real Gateway's `/healthz` only answers 200 once the database is ready, so a 200 is also a
proof the database connected and the migrations applied.

**On failure, stdout and stderr; always cleaned up?** Yes. `fail()` prints the container's state
(running, exit code, out-of-memory flag) then `docker logs "$GW" 2>&1`, the whole log. `trap cleanup EXIT`
removes both containers and the network on every exit path, including `set -e` exits and the deadline. I
saw no leftovers after nine runs. One limit worth knowing: the Gateway turns its stdout mirror off the
moment it binds its port ("startup complete - standard-output log mirror off"), so the printed output
covers the startup window, which is the window this gate judges, and nothing after the bind.

**Will it behave on the GitHub runner as it did here?** The rehearsal says yes for the script. Run
40411821990 is a push run of "Boot proof rehearsal (4513, throwaway)" on branch
`safe-deploys/4513-boot-gate` at `b1006544`; its log shows the image built on the runner with
`COCKPIT_COMMIT=b100654`, `postgres:16` pulled from Docker Hub, `BOOT PROOF PASSED` 15 seconds after start,
and the broken image failing in 3 seconds with exit code 1 checked. The script at `b1006544` is
byte-identical to the one at head (the only difference between the two commits is the #4515 rebase in the
workflow). The rehearsal workflow is now `disabled_manually`. What the rehearsal did not do, as the pull
request says: run inside the deploy workflow, or boot a registry digest. On the runner specifics: the
image is already pulled by the previous step under the same registry session, so `docker run` by digest
needs no new pull; networking is a per-run user-defined bridge with the database reached by container
name; the host port is operating-system assigned (`-p 127.0.0.1::7878`), so nothing can collide; the
names carry `github.run_id`; `curl` and `jq` are on `ubuntu-latest`; the expected commit is a hexadecimal
string injected from the same `steps.short` that built the image, so quoting is safe; `set -euo pipefail`
holds throughout and I found no line that trips it (the `[ "$passes" -gt 0 ] && echo` list is not the
last command in its list, so `set -e` ignores it).

**Does the supplied contract match `HostedStartupContract.cs`?** Yes, exactly its four requirements:
`CC_GATEWAY_HOSTED=1` (also baked in the image); neither `CC_GATEWAY_NO_AUTH` nor `CC_GATEWAY_AUTH` set,
so authentication is on; `CC_GATEWAY_PUBLIC_URL=https://smoke.invalid`, which parses as an absolute https
address with a host and no query or fragment; `CC_GATEWAY_DB_CONNECTION` pointing at the throwaway
PostgreSQL. The log line "full hosted contract satisfied" and "auth-gated" confirm the image started in
hosted mode with authentication on, not an easier mode.

**Does the guard fail under mutation?** For removal, reordering, `if`, `continue-on-error`, tag instead of
digest, weakened pass count and a wrong expected commit: yes, proved above. For `|| true` and `exit 0`:
no (Finding 2). And it fails on correct code on a Windows checkout (Finding 1).

## Observations that need no change, for the mission's record

- **This gate would not have stopped the 2026-10-26 outage, by the Developer's own diagnostic and my
  rerun.** The rebuilt `86b37ace5` boots and serves here in under 30 seconds. The gate proves the image
  boots on a minimal contract against a fresh database and a local disk. It does not exercise production's
  existing database, the Azure Files share, Teams switched on, or any other production app setting. That
  is exactly what issue #4513 asked for, and it is still worth having; but the mission goal "never break
  production" is not met by this step alone, and #4516 remains open as a question about the environment.
- **The throwaway PostgreSQL is the deploy pipeline's only pull from Docker Hub**, unauthenticated and on
  the moving tag `postgres:16`; every other image in the workflow comes from Microsoft's registry. Docker's
  documentation gives unauthenticated users 100 pulls per 6 hours per address, and hosted runners share
  addresses; I found no Docker or GitHub statement that hosted runners are exempt. If a pull is refused,
  the script says so and blames its own database, not the image, and the deploy stops. That is the right
  direction of failure, and it will also stop a deploy that is itself the fix for an outage. The pull also
  happens before the 60-second database deadline starts, so it is bounded only by the job's default limit.
- The auth check in the guard looks for the text `-e CC_GATEWAY_NO_AUTH`; the `--env` spelling would pass
  it. Deliberate only, not drift.
- Where the proof folder says the Core.UnitTests scanner flagged the word "docker" and the assertion was
  changed to ` logs "$GW" 2>&1`: I read the scanner. It bans the word in gate tests by text, the test only
  reads files, and the reworded assertion still pins the whole-log print. Fine.
