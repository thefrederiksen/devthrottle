# `Gateway.Tests` is red on macOS on `main`, and nobody is looking

Found by the phase 3 Developer on 20 September 2026, recorded by the Delivery Lead. **It is not this
mission's defect and this mission is deliberately not fixing it.** It is written down because it was
found honestly, against the finder's own interest, and because the alternative is that someone meets it
again in six months and assumes it is their fault.

## What was measured

Phase 3's own behavioural proof lives in `CcDirector.Gateway.Tests`, so that seat ran the suite in full
rather than only its own tests - and then cut a worktree from `origin/main` to compare.

| Tree | Result |
|---|---|
| Phase 3's branch | 21 failed |
| Clean `origin/main` | 23 failed |
| **Identical, name for name** | **19** |

2,693 tests. The nineteen shared failures fall into six families with nothing to do with a repository
catalogue: fleet spawn, workflow seats, tunnel route proofs, hosted process-control denials, the voice
sweep, and the suite's own machine-wide lock test.

## Why it went unseen

`Gateway.Tests` is **parked**: `scripts/test-local.ps1` does not run it by default, and section 7 of this
mission's own check does not name it either. Continuous integration is the only other place it runs. So
a developer can hold a green local gate, a green mission check, and nineteen red tests underneath, with
nothing in the ordinary working loop saying so.

This is the same shape as the defect this mission opened with - a check that is red before anyone
touches it - one level further down. The difference is that the earlier one blocked the mission's own
check, and this one is invisible to it.

## Why this mission is not fixing it

This mission has already absorbed three preparatory repairs it was not chartered for: the web suites
under Node 26, fourteen macOS .NET failures, and the four product defects those uncovered. A fourth, in
six unrelated families of a 2,693-test suite, stops being a preparatory repair and becomes a different
mission - it would swallow the six phases the owner actually asked for.

The owner's standing ruling is zero failing tests on every platform, so this does not go away. It goes to
him by name, with the recommendation that it be chartered as its own piece of work.

## Also found, and also not this mission's

Over seven full runs of `Gateway.UnitTests` on the phase 3 branch (five clean) against three clean runs
on `main`, two intermittent events:

- `TurnVerdictVoiceMergeTests.AVoiceSessionsStop_CostsOneJudgeCall...` failed once on a cold run and
  passes when run alone. The seat did **not** capture its message, and correctly declined to guess the
  mechanism from the test's name.
- One test-host crash: `ObjectDisposedException` on `SQLitePCL.sqlite3` from
  `SqliteTransaction.RollbackExternal` - a connection **finalized rather than disposed**. That one is
  concrete, and is a defect in the test infrastructure rather than a mystery.

Neither is called unreliable here. An intermittent failure is a defect in either the test or the product,
and saying which requires evidence that was not captured for the first of these.
