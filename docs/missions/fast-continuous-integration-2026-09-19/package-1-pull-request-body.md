# Package 1: move the hosted image contract test into the hosted deploy

*The text below is the pull request body for this package. It lives here so the record of what was
claimed, and what was not, is committed beside the code rather than only in a pull request.*

---

## What this changes

`HostedImagePublishedArtifactTests` proves that every runnable entry executable in the **published**
hosted image refuses to start when the hosted contract is missing - the hole Codex found in pull request
#1968, where `dotnet CcDirector.Gateway.dll` from the published output served `/healthz` 200 with every
hosted variable unset.

It cost **16 minutes 32 seconds** of a **103 minute 9 second** .NET job on every pull request and every
push to main, to prove a property of an artifact only the hosted deploy ever publishes.

So it moves:

- into its own project, `src/CcDirector.Gateway.HostedImage.Tests`, deliberately **not** in
  `cc-director.sln`. The continuous integration test step is `dotnet test cc-director.sln`, so leaving the
  project out of the solution is what takes the test out of that step - a structural exclusion, with no
  filter argument for anyone to drop;
- into a new reusable workflow, `.github/workflows/hosted-image-contract.yml`, which
  `deploy-hosted-gateway.yml` calls as a job its `deploy` job declares `needs:` on. GitHub does not start a
  job whose `needs` did not succeed, so a failure, an error, a timeout or a skip stops the deploy.

## It fails closed, and the pass condition is a presence

`dotnet test` exiting zero is an **absence** - it is satisfied by a run that discovered no tests, by a
skipped test, and by a result file that was never written. So the verdict is not that. The workflow:

1. reads the list of tests the **built assembly itself declares** (`dotnet test --list-tests`), so the
   inventory is derived from the artifact under test and cannot go stale the way a typed list would;
2. runs them with a result file;
3. refuses unless **every declared test has a result and every one of those results says `Passed`**. An
   empty declared list, a missing result file, and a test recorded as `NotExecuted` are each a refusal.

And before that verdict is believed, a control step runs it against those three known-bad inputs and stops
the job if any of them is accepted. A check only ever exercised on the state you hope passes has
demonstrated nothing.

## Proof

Everything below is from runs on GitHub's machines, on this branch. The gate was rehearsed by giving the
new workflow a temporary `push` trigger for this branch and a temporary caller workflow; **both temporary
files are removed in the final commit**, so the merged workflow differs from the rehearsed one only by the
absence of those two triggers.

**1. The test runs and passes in the new workflow.** Run **35483939371**, job green,
1 minute 50 seconds end to end (an earlier identical run, 35483737437, took 1 minute 50 seconds too).
The verdict step printed:

```
Tests the built assembly DECLARES: 2
  declared: ...HostedImagePublishedArtifactTests.Every_published_entry_executable_fails_closed_without_the_hosted_contract
  declared: ...MutationProofPinGuardArmedTests.TheGuardRanInThisProcess
Test results RECORDED: 2
  Passed: ...Every_published_entry_executable_fails_closed_without_the_hosted_contract
  Passed: ...TheGuardRanInThisProcess
PASSED: all 2 test result(s) covering all 2 declared test(s) say Passed.
```

**2. The gate refuses a SKIPPED test, which `dotnet test` reports as a pass.** Run **35483844691**, on a
commit where the test carried `[Fact(Skip = ...)]`. Step 8 was **green**:

```
Passed!  - Failed: 0, Passed: 1, Skipped: 1, Total: 2, Duration: 643 ms
```

and step 9 was **red**:

```
NotExecuted: ...Every_published_entry_executable_fails_closed_without_the_hosted_contract
REFUSED: these tests did not pass. A skipped or not-executed test is a refusal here, not a pass -
the hosted image is only proven by a test that actually ran:
```

The job failed, so the deploy would not have started. That is the exact fail-open this gate exists to
close, shown closing.

**3. The call the deploy makes is wired up, including its main-only input.** Run **35483737565** called the
contract workflow exactly as `deploy-hosted-gateway.yml` does - by relative path, with `require_main: true` -
from a branch. It refused at the first step and skipped every step after it, and the job concluded
`failure`. A `needs:` on that job stops the deploy.

**4. The control refuses all three known-bad inputs, on every run.** Quoted from run 35483939371:

```
ok - the verdict refused a declared list with no tests in it
ok - the verdict refused a run that wrote no result file
ok - the verdict refused a declared test whose result says NotExecuted
All three known-bad inputs were refused. The verdict below can be believed.
```

**5. The guards fail against a broken tree.** `HostedImageContractRunsBeforeEveryDeployTests` was run
against three deliberate mutations before it was committed - the project added back to `cc-director.sln`,
the `needs:` line deleted from the deploy job, and `--list-tests` removed from the workflow. Each mutation
turned exactly one of the three guards red.

## What is NOT proven

- **The 16-minute saving is PREDICTED, not measured.** The removed test took 16 minutes 32 seconds of a
  103 minute 9 second job, quoted from the log of continuous integration run 35461379953 on
  19 September 2026, and the Gateway suite runs its tests one at a time - so the arithmetic is sound. But
  no continuous integration run has yet been made on this change. `ci.yml` only runs on a pull request, so
  **opening this pull request produces the measurement**, and the first run's .NET job time is the number
  that should be written into the mission record.
- **No real hosted deploy was run.** There is no grant to deploy, and none was taken. What is proven is
  that the workflow the deploy calls runs the test, passes on a good commit and fails on a bad one, and
  that the call and its input are wired up. What is not proven is a full deploy end to end.
- **The merged workflow was not itself run.** The rehearsals ran the same file plus a temporary `push`
  trigger. The `workflow_dispatch` trigger that remains cannot be exercised until this is on the default
  branch, because GitHub does not offer dispatch for a workflow that is not there yet. **After merging,
  one dispatch of "Hosted image contract" on main confirms the merged file** - and costs about two minutes.
- **The local release gate loses this test.** `scripts/test-local.ps1 -Parked -Configuration Release` never
  named this project and does not now. That is deliberate: the test is about the hosted container image,
  which the desktop release does not ship. The hosted deploy is now the only thing that runs it, and that
  is the run that publishes the artifact.
- **No full suite was run locally.** `scripts/test-local.ps1` cannot run on a Mac - the solution holds two
  Windows-only projects and the gate dies at build. What was run here, and passed: a clean build of the
  three affected projects, the new hosted image project end to end (2 tests), the three new guard tests,
  and the extended `NoProbeAndReleasePortsTests`.

## The second finding: how the deploy's check gate treats a cancelled run

Established from the workflow file and from this repository's run records, and written up in full in
[`docs/missions/fast-continuous-integration-2026-09-19/how-the-deploy-gate-treats-a-cancelled-run.md`](how-the-deploy-gate-treats-a-cancelled-run.md).

**A cancelled run is not accepted - the gate refuses it.** A cancelled job reports `completed`/`cancelled`,
never `skipped`, and `CI result` reports `completed`/`failure` alongside it, so a cancelled run puts two
refusable check runs on the commit. Checked on seven such commits; running the gate's own logic against
`640a00189` and `e627eb467` refuses both today.

**But the gate is a fail-open anyway, and it is operating right now.** Its pass condition is an absence -
"nothing has FAILED" - and a commit with no finished checks satisfies it. Of the last fifteen hosted
Gateway deploy runs, **fifteen of fifteen** passed the gate with no finished check: six saw no check runs
at all, nine saw only pending ones. Their commits' `Build & Test (.NET)` eventually concluded `cancelled`
nine times, `failure` four times, and `success` once - and even that one was not gated on it, because the
gate had already passed before the answer existed.

The clearest record is deploy run **35406585169**: the push landed at 23:39:51Z on 18 September 2026, the
deploy started at 23:40:02Z, eleven seconds later, and printed
`NOTE: commit e627eb46... has no check runs of its own yet. / Check gate passed`. That same commit is
refused by the same logic today. One commit, two opposite verdicts, decided by nothing but when the
question was asked.

**This is NOT fixed here, deliberately.** The repair - require a named, successful check rather than the
absence of a failed one - is a few lines, but today it would refuse almost every deploy, because the .NET
job takes about a hundred minutes and most runs on main are cancelled by the next push. The owner has
ruled twice against that wait, and ruling 7 of this mission's brief already sets the right order: make the
run fast, watch it hold the twenty-minute budget for a week, then make a green result required. There is
also one question inside it that needs the owner: what "tested" should mean for a commit whose run was
cancelled by a newer push and will therefore never get a verdict of its own. The note names all of it.
