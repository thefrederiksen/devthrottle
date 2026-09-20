# An aborted test run reports "Passed!", and I nearly merged on one

Found by the Delivery Lead on 20 September 2026 while verifying phase 4. Recorded because the failure
mode is in the **instrument**, not in any change, and because it very nearly worked.

## What happened

Verifying phase 4, the Gateway suite reported:

    Passed!  - Failed: 0, Passed: 1115, Skipped: 1, Total: 1116, Duration: 16 s

That line says *passed*. It is also wrong in a way nothing in it admits: the suite is **6,591 tests and
takes about two minutes**. This run did 1,115 in sixteen seconds, because the test host **crashed** and
the run was **aborted**:

    The active test run was aborted. Reason: Test host process crashed : Unhandled exception.
    Microsoft.Data.Sqlite.SqliteException (0x80004005):
        SQLite Error 1: 'cannot start a transaction within a transaction'
      at CcDirector.Gateway.Fleet.FleetOutcomeStore.Answer(...)
      at ...FleetOutcomeStoreTests.Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins

The abort notice is at the **top** of the log. The word "Passed!" is at the **bottom**, which is where
every reader and every script looks.

## Why it nearly worked

The verification script captured the summary line and not the exit code. A reader who greps for
`^(Passed|Failed)!` - which is the obvious thing to grep for, and what had been used all night - sees
`Passed!` and moves on. Nothing about that line says a quarter of the suite ran.

It was caught only because the **count and the duration were implausible** against numbers seen many
times that night. Had this been the first run of that suite, or had it aborted at 6,000 tests instead of
1,115, it would have passed unremarked.

**This is the "check that fails open" pattern living in the tooling itself.** The pass condition was an
absence - no `Failed!` line - and an absence is exactly what a run that never finished produces.

## What was done

Re-run, with the exit code captured this time: **exit 0, 6,591 passed, 8 skipped, no abort**. So the
crash did not belong to the change under test - phase 4 is a browser-side change and touches nothing in
`FleetOutcomeStore` - and phase 4 was merged on the honest run.

## The family this belongs to

It is the third sighting of SQLite transaction and connection lifetime trouble in this suite, and the
first that aborted a whole run:

1. Phase 3's Developer saw a test host crash - `ObjectDisposedException` on `SQLitePCL.sqlite3` from
   `SqliteTransaction.RollbackExternal`, a connection **finalized rather than disposed**.
2. The Delivery Lead characterised an intermittent catalogue failure that passed in isolation and failed
   only at load average 24 - recorded at `the-parked-suite-nobody-runs.md`, named as the test's fault
   with the mechanism stated as probable rather than proven.
3. This: `cannot start a transaction within a transaction`, from a test named
   `Answer_ConcurrentCallersOnTwoInstances_ExactlyOneWins` - a concurrency test, crashing the host.

All three are the same area. None is this mission's to fix, and none was chased.

## What should change, for whoever picks this up

Two things, and the second matters more than the first:

1. **Read the exit code, not the summary line.** `dotnet test` exits non-zero on an aborted run even
   while printing `Passed!`. Every script in this repository that greps the summary is exposed.
2. **A suite that reports far fewer tests than it has is not a pass.** The count is the honest signal
   and it was ignored, by a script and by a reader, until the number looked strange. A gate that knows
   how many tests a suite should have would have caught this on the first run rather than the third
   glance.
