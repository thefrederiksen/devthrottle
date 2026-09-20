# Seven tests in `Core.Tests` measure the machine, not the product

Found by the phase 6 Developer on 20 September 2026, chasing one red run that had no names attached to
it. Recorded here because it is the **third sighting in this mission of the same family**, and because
the first instinct - re-run until green and publish the green one - is exactly what
`an-aborted-run-reports-passed.md` exists to stop.

The two earlier sightings: `throughput-test-measures-the-machine.md` (a wall-clock budget failing at
load average 11.93 and passing at 2.99) and `the-parked-suite-nobody-runs.md` (an intermittent catalogue
failure that passed in isolation and failed only at load average 24).

---

## What happened

Verifying phase 6, one run of `dotnet test src/CcDirector.Core.Tests` came back **Failed: 2, Passed:
4489, Skipped: 18, Total: 4509**. The total is the suite's full count, so this was NOT the aborted run
that reports "Passed!" - two tests genuinely failed.

**The names were lost**, because the run was piped through a summary filter. That was my mistake, it is
the reason this could not be named on the spot, and it is why the hunt below was needed at all.

Core.Tests then ran green six times on the same code - including a deliberate repeat of the exact
sequence it had failed in - before a repeat-run hunt reproduced it.

## The reproduction, and the whole diagnosis in two lines

| Hunt run | Duration | Result |
|---|---|---|
| 1 | **5 m 11 s** | 4,491 passed, **0 failed** |
| 2 | **10 m 2 s** | 4,484 passed, **7 failed** |

Same commit, same binary, minutes apart. **The run that failed took twice as long**, on a machine
running several other sessions' test suites at once. Nothing about the product differed between those
two runs.

## Which it is: THE TESTS. Seven of them, in two classes, all the same mechanism

Every one of the seven spawns a REAL child process and asserts against a WALL-CLOCK TIMEOUT. When the
machine is loaded, the process does not start in time and the timeout fires, so the test reports how
busy the Mac is.

**`Setup.FleetToolReachabilityTests`** - five failures. The test writes a two-line shell script to the
temporary directory and runs it, with a timeout the TEST chooses: `new(TimeSpan.FromSeconds(30), ...)`.

```
RunAsync_ToolReachesTheFleet_ReportsWorking                              [30 s]
  Assert.Equal() Failure: Values differ
  Expected: Working
  Actual:   CannotReachGateway

RunAsync_PathToolFails_AndOurOwnCopyAlsoFails_IsNotRepairableByRepointingPath  [1 m]
  Assert.Contains() Failure: Sub-string not found
  String: "timed out after 30s"
```

The product's own words give it away: `FleetToolReachability` reports `timed out after {seconds}s`, and
that is what these runs got instead of the stand-in tool's exit code. The classification under test is
*"what does the Director conclude from an exit code"*; the timeout is incidental to it, and it is the
only thing that failed.

Also failing in that class: `RunAsync_ToolFails_ReportsCannotReachGatewayWithTheToolsOwnReason`,
`RunAsync_PathToolWorks_LeavesOurOwnCopyUnasked`,
`RunAsync_PathToolFails_AndOurOwnCopyWorks_IsRepairableByRepointingPath`.

**`Settings.ToolDetectionServiceTests`** - two failures, and this pair is the harder one:

```
TestToolAsync_VersionCommandSucceeds_ReturnsOkWithVersion  [8 s]
  Assert.True() Failure
  Expected: True
  Actual:   False
```

**Eight seconds, and `ToolDetectionService.DefaultTimeout` is `TimeSpan.FromSeconds(8)`.** The test ran
exactly to the timeout and then failed. Unlike the class above, this timeout is the PRODUCT's, not the
test's, and `TestToolAsync` offers no way to override it - so this pair cannot be fixed by changing a
number in the test file. The other failure is
`IsToolValidated_CurrentConfiguredPathWasTested_ReturnsTrue`.

## The honest limit of this diagnosis

**I cannot prove the original two failures were among these seven.** Their names were lost. What is
established: the same suite, on the same machine, under the same conditions, fails with a family of
timeout-driven failures, and nothing else in 4,509 tests has failed across eight runs. Reading the
original two as members of this family is the overwhelming reading; it is not proof, and it is written
here as a reading rather than a fact.

## What this is not

**It is not caused by phase 6.** The one file in `CcDirector.Core` that phase 6 changes is
`RepositoryConfig` - one added property and one extended expression. The 27 tests that cover it
(`RepositoryRegistryTests`, `RepositoryRegistryConcurrencyTests`, `RepoStateSnapshotCollectorTests`)
pass in four seconds, in isolation and in every full run including the red ones. None of the seven
failures is in a file this mission has touched.

## What should change, for whoever picks this up

1. **`FleetToolReachabilityTests`: the timeout is the test's own and is incidental to what it proves.**
   Five of the seven go away by giving those cases a timeout that is not a stopwatch on the machine.
   The one case that genuinely tests the timeout path should keep a short one and say so.
2. **`ToolDetectionServiceTests`: the eight seconds belong to the product and should stay eight seconds
   for a user.** So either `TestToolAsync` takes an injectable timeout - the ordinary fix, and it makes
   the timeout path testable deliberately rather than by accident - or the test stops spawning a real
   process.
3. **This is now three sightings.** A fleet machine that routinely runs a dozen agent sessions will go
   red at random until wall-clock assertions stop being used to prove non-timing behaviour. The damage
   is not the red line: it is that a suite which cries wolf stops being read, and this mission has
   already recorded one occasion where a bad run nearly passed unremarked.

**Not fixed here.** It is two other missions' files, and the second item is a product change rather than
a test change. It is handed to the Delivery Lead named, measured and reproduced, rather than left as
"an intermittent failure".
