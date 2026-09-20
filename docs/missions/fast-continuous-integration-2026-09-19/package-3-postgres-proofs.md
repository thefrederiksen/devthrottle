# Package 3: the PostgreSQL proofs get a project and a job of their own

Built 19 to 20 September 2026 on branch `ci/package-3-postgres-proofs-own-project`. Every measurement
below is from a real workflow run on GitHub's machines, named by its run number, and every log line
quoted is copied from that run.

## The defect

About sixty tests prove our own migrations, the hosted statistics schema, and what the restricted
database role the hosted Gateway logs in as can and cannot do. They lived inside
`CcDirector.Gateway.Tests` and `CcDirector.Gateway.UnitTests`, mixed in with thousands of tests that
need no database, and each one skipped itself when no connection string was set. Continuous integration
starts no database, so every one of them reported SKIPPED on every run there has ever been - and a skip
is indistinguishable from a pass in the console summary, in the result files, and in every report built
from them. The local release gate was the only place they ever ran, and it needs Docker, so a machine
without Docker could not run them at all.

## The count, and how it was reached

**56 test cases (54 test methods) need the throwaway rig** - the databases and the restricted role that
`scripts/pg-stats-proof-rig.ps1` builds, named by `CC_GATEWAY_TEST_PG_CONNECTION` and
`CC_GATEWAY_TEST_PG_STATS_CONNECTION`.

**5 more need a PostgreSQL server reached through `CC_GATEWAY_DB_CONNECTION`** - the runtime selector a
real deployment sets. The mission brief's estimate did not include these. They are the Gateway's own
startup path: four in `GatewayDatabaseLivePostgresProofTests` and one that was the single live test
inside `GatewayHostBootSmokeTests`.

**61 in total.** How the number was reached, in three steps, each checkable:

1. Every gating attribute in the repository was enumerated - `grep` for classes deriving from
   `FactAttribute` or `TheoryAttribute` in the two test projects - and each one read to see which
   environment variable it gates on. Seventeen were found; two of them gate on something other than a
   database.
2. Every use of those attributes was counted per file, and a theory's `InlineData` rows counted as the
   separate cases they become at run time. One class, `HostedSchemaRefusesAnUnownedRowTests`, is six
   test methods and eight test cases; every other gated method is one case.
3. The result was checked against the built assembly rather than against the source. After the move,
   `dotnet test --list-tests` reports **64 test methods** in `CcDirector.Gateway.Postgres.Tests`. Of
   those, five need no database at all: the linked mutation-proof pin sentinel, the two rig-gate pinning
   tests, and two model-level schema-qualification facts that read the Entity Framework model in memory.
   64 - 5 = 59 methods that need a database, which is 54 needing the rig plus 5 needing the runtime
   selector, and 61 cases once the one theory expands.

The brief's estimate was "about 56", cross-checked against one log's skips. That number is exactly
right for the rig-gated half, and it is the half the log would have shown. The five that use the
runtime selector skipped in that log too, and were not in the estimate.

## What moved

Into the new project `src/CcDirector.Gateway.Postgres.Tests`:

- Thirteen classes out of `CcDirector.Gateway.Tests`, including `HostedStatsServeTests`, which boots a
  whole hosted Gateway. The self-host control that shared its file, `HostedStatsSelfHostControlTests`,
  needs no database and stayed behind, so the file was split.
- `HostedSchemaRefusesAnUnownedRowTests` out of `CcDirector.Gateway.UnitTests` - the class the brief
  names as the whole reason that assembly ever wanted a database.
- The one live-database test inside `GatewayHostBootSmokeTests`, so that assembly opens no connection
  anywhere. It is the only test whose class changed; its name and its assertion are untouched.
- The rig helpers `PostgresRigGate`, `PostgresProofDatabase` and `PostgresRigIsPresentWhenRequiredTests`,
  which now live with the proofs instead of being linked into two assemblies.

**`CcDirector.Gateway.UnitTests` needs no database at all after this**, and its link to the rig gate is
gone with the class that needed it. So does `CcDirector.Gateway.Tests`.

No test's assertions were changed. The new assembly runs its tests one at a time
(`DisableTestParallelization`), which is what every one of them already had: several drop the statistics
schema or delete and re-migrate the proof database as their first act, and gathering them into one
assembly without that line would have let them destroy each other's rows for the first time.

## The job

One reusable workflow, `.github/workflows/postgres-proofs.yml`, with two callers: continuous integration
(`ci.yml`, started by paths) and the hosted deploy (`deploy-hosted-gateway.yml`, where it is required).
One body, two callers, because two copies of a job this consequential would drift and the copy that
drifted would be the one guarding production.

**Its verdict is a presence, not an absence.** It does not look for failures. It reads the list of tests
the built assembly declares, reads the results the run produced, and holds them to each other by name:
every declared test must appear in the results, every result must be a pass, and the declared list must
not have collapsed - a short list is a broken instrument, not a small suite. A skipped test is written
into a result file as `NotExecuted` and is counted as a failure on purpose.

**The paths are wider than the brief proposed, and the brief invited the correction.** The brief named
the `Data`, `Stats` and `Tenancy` folders of the Gateway. Reading what the proofs actually exercise
found them reaching `Pairing` (the device credential import), `History` (the session history and turn
stores), `Snooze`, `Streaming`, `Wingman` and `GatewayHost` itself - eight folders and several root
files. A list of eight folders is an inventory that goes stale the day a ninth appears, so the rule is
the whole `src/CcDirector.Gateway/` project, which is a rule and cannot go stale. Added with it:
`src/CcDirector.Gateway.Tests/`, because the proofs compile five source files linked out of it.

**`Directory.Packages.props` does not exist in this repository.** The brief names it. The file that
actually decides how every test project builds is `Directory.Build.props` - it links the mutation-proof
pin guard into every project whose name ends in `.Tests` - and that is what is in the list instead,
along with `global.json`.

**The hosted deploy requires the job.** It runs on the exact commit about to ship, before the
serialisation group is taken and before anything touches Azure. That matters because the deploy's
existing gate refuses a check that FAILED and an absent check passes it - and a commit that changed
nothing database-facing carries no such check at all. Executing the proofs turns that absence into a
presence.

## The proof

Four throwaway pull requests, all closed unmerged, all on GitHub's machines.

**The job runs, and nothing inside it is skipped.** Run **35483698850**, job 106006041945, on a pull
request head that touches a migration. The job took **3 minutes 51 seconds** and printed:

```
PostgreSQL answered a query over TCP on attempt 1.
 rolsuper=false rolcreatedb=false rolcreaterole=false rolbypassrls=false
 database_CREATE=true
 public_CREATE=false
The assembly declares 64 test(s).
Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5
PASSED: all 64 declared test(s) ran, producing 66 result(s), every one of them a pass and not one of
them skipped.
```

66 results from 64 methods is the one theory expanding to its three cases. The restricted role's
measured grants are printed into the log on every run, so a reader can see what the proofs were held to
rather than taking it on trust.

**Both halves of the path filter, as a controlled pair.** Two pull requests against the same scratch
base, each differing from it by exactly ONE file, so the only thing that can explain a difference in
which jobs ran is the filter under test:

| Run | The one file changed | Classifier | PostgreSQL proofs job |
|---|---|---|---|
| **35483772377** | `src/CcDirector.Gateway.Migrations.Postgres/Migrations/20260718120027_InitialPostgres.cs` | `dotnet=true web=false tools=false postgres=true` | **ran** |
| **35483773656** | `src/CcDirector.Avalonia/AccountsDialog.axaml.cs` | `dotnet=true web=false tools=false postgres=false` | **skipped** |

The `.NET` job ran in both, which is what makes the second run a discrimination rather than a quiet
run: the change was .NET code, it was tested as .NET code, and the database was not started for it.

The scratch base exists because a pull request against `main` from a branch carrying this change has
the whole package in its diff - which is database-facing - so the job correctly starts on both halves
and neither run discriminates. The first attempt at this proof made exactly that mistake and is
recorded here rather than quietly dropped.

**The zero-skip assertion fires.** Run **35483933499**, job 106006699307, on a head identical to the
scratch base except that the three environment variables pointing the proofs at the database were
removed - the exact state continuous integration has been in every day. The test runner itself reported
a GREEN:

```
Passed!  - Failed:     0, Passed:     5, Skipped:    54, Total:    59
```

and the verdict step turned it into a job FAILURE naming all fifty-four:

```
These results are not a pass. A skipped test is written into a result file as NotExecuted, and it is
counted here as a failure ON PURPOSE: this job exists because a skipped database proof reads as a green
one everywhere else.
  CcDirector.Gateway.Tests.Data.CallerSuppliedKeyUpgradePreservesRowsPostgresTests.AfterThePostgres... -> NotExecuted
  ... (54 in all)
```

That is the whole package in two log extracts: what the tooling calls a pass, and what this job calls
it.

## The unit suite needs no database, stated as a presence

The claim is not "no database test is left in `CcDirector.Gateway.UnitTests`", which is an absence and is
satisfied by a run that looked at nothing. It is the assembly's own skip list, read off two runs of the
same job on Windows:

| | Total | Skipped | The database skips among them |
|---|---|---|---|
| main, run 35478491468 | 6373 | 8 | the six `HostedSchemaRefusesAnUnownedRowTests` methods, and `GatewayHostBootSmokeTests.HostStartupPath_ResolvesAndAppliesPostgresMigrations_OnConfiguredPostgres` |
| this change, run 35483698850 | 6364 | 1 | none - the one skip is `TenantGateArchitectureTests.DT_TEN_3_background_workers_touch_stores_only_through_TenantScopedSweep`, an architecture test that has nothing to do with a database |

The nine tests that left the assembly are named and account for exactly the difference: six from the
moved class, one live-database test moved out of `GatewayHostBootSmokeTests`, and the two
`PostgresRigIsPresentWhenRequiredTests` that were linked in and are not any more. 6373 - 9 = 6364. The
same single skip, and no database skip, was seen again in a full local run of that assembly on macOS.

## The Windows solution run, and the one failure that had to be chased

`Build & Test (.NET)` on run 35483698850 built the solution successfully with the new project in it, and
ran every suite. `CcDirector.Gateway.Postgres.Tests` reported 64 tests, 5 passed and 59 skipped there,
which is right and is not a regression: that job starts no database, and the job that does is the one
that asserts nothing in this assembly may be skipped.

The .NET job FAILED, with nine failures. **Eight of them are main's, not this change's**, established by
reading other runs rather than by assertion - each one appears on at least one branch that does not
carry this change, and four of them appear on a run of main itself (35478491468):

| Failing test | Also seen on |
|---|---|
| `HostedDirectorTunnelGovernanceTests.A_tunnel_push_reaches_the_ledger_and_the_morning_report` | main 35478491468, and three other branches |
| `TunnelExplicitRouteProofTests.RecapGenerate_ridesTheTunnel_andPreservesThe201AndModel` | main 35478491468, `fix/tools-venv-stage-and-swap` |
| `TunnelExplicitRouteProofTests.RecapRead_ridesTheTunnel` | `ci/package-1-hosted-image-test-to-deploy`, `mission/email-family-phase1-undetermined` |
| `TunnelExplicitRouteProofTests.Summary_ridesTheTunnel` | `ci/package-1-hosted-image-test-to-deploy` |
| `SessionSupervisorLiveWiringTests.AParkedSessionThatDiedOnAConnectionFault_IsSentContinueOverTheTunnel` | main 35478491468 |
| `VoiceServingLoopIsolationTests.Voice_sweep_reaches_only_the_owning_tenants_director` | main 35478491468, and four other branches |
| `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message` | main 35478491468, and two other branches |
| `WingmanMenuGuardProofTests.Prompt_WithoutMenuGuard_MenuOnScreen_ForwardsAndNeverReadsTheScreen` | main 35478491468, and four other branches |

**The ninth is not accounted for that way and was chased.**
`DeviceKeyAtRestTests.IssuedKey_StillAuthenticates_AndSurvivesARestart` appears in no other run examined,
and it lives in `CcDirector.Gateway.UnitTests` - the assembly whose composition this change alters. That
is exactly the case where "it was already like that" has to be earned.

What was done: a throwaway workflow ran that ONE assembly on Windows three times on the package tree
(run **35487798173**) and three times on main's tree (run **35487801757**). **All six were green.** It
also passes in isolation on macOS, and in a full local run of the assembly.

So the failure is not deterministic on this tree. What the probe does NOT reproduce is the solution run's
conditions, where several assemblies execute at once, so it does not settle the question - and the
mechanism found while looking says it will not be settled by repetition either. The test constructs a
`DeviceRegistry`, which constructs a `GatewayDatabase`, whose provider selection reads the process-global
`CC_GATEWAY_DB_CONNECTION`. Unlike `GatewayDbTestHarness` and `DatabaseOpensAfterTheBindTests`, this
class does NOT take `GatewayDbEnvironmentGate` - the lock that exists to serialise exactly this against
the test that blanks that variable. That gate's own comment describes the consequence in one sentence:
"Without it a full parallel run fails exactly one database test, a different one each time, all passing
in isolation." This failure has that shape exactly.

It is a defect in the test, not in the product, and it is not this package's to fix: the remedy is to put
that class behind the existing gate, which is a change to a file this package does not otherwise touch
and to a suite another package is about to split. It is raised rather than repaired here.

## What is NOT proven

- **The local release gate was not run.** `scripts/test-local.ps1` cannot run on this machine - the
  solution holds two Windows-only projects and the gate dies at build - and there is no Docker here
  either, so no PostgreSQL proof was executed locally at any point. Everything above is from GitHub's
  machines. The script's project lists and its documentation were edited to match; those edits are
  unexercised.
- **The hosted deploy workflow was not rehearsed.** The `postgres-proofs` job was added to it and
  `deploy` was made to need it, and the file parses, but no deploy run has been started. What is proven
  is that the same reusable workflow runs green when called from continuous integration.
- **The job has run on Linux only**, which is where it is meant to run. Nothing here says anything
  about these proofs on Windows or macOS.
- **The path list leaves out `CcDirector.Core` and `CcDirector.Gateway.Contracts` on purpose.** They are
  build inputs to the proofs, so a behavioural change in either could in principle change what the
  proofs prove without starting the job. Including them would start it on most .NET changes, which is
  what the owner's ruling rules out. The gap is narrower than it looks - the proofs project is in
  `cc-director.sln`, so the .NET job builds it on any `src` change and a compile break is caught there,
  and the deploy runs the proofs again before anything reaches production - but it is a gap and it is
  not proven to be harmless.
- **One run each.** No timing is an average; the 3 minutes 51 seconds is one measurement of one run.
- **The .NET job on this head is RED, and this change does not make it green.** Eight of its nine
  failures are shown above to be main's. The ninth is shown not to be deterministic, and is not shown to
  be unrelated - six green probe runs against one red is evidence, not proof. Nobody should read this
  package as leaving continuous integration green, because continuous integration was not green when it
  started.
