# Phase 3 - the four reverts, run, and what each one actually did

The predictions are in `predicted-symptoms.md`, committed before the first revert was run (commit
`6a9c393b1`). This file is what happened. Where a prediction was wrong or incomplete, it is said so here
rather than quietly corrected there.

Every revert is one edit to `KnownRepositoryStore.OrderOneList` with everything else left alone, and every
one was restored before the next was applied. The counts below are from two filters run each time:

- unit: `OneRepositoryListOrderTests`, `DiscoveredRepositoryCatalogTests`, `DiscoveredRepositoryObserverTests`
  in `CcDirector.Gateway.UnitTests` (35 tests);
- end to end: `OneRepositoryListTunnelProofTests`, `DiscoveredRepositoryTunnelProofTests` in the PARKED
  `CcDirector.Gateway.Tests` suite (8 tests), which is a real Gateway, a real SignalR tunnel and real HTTP.

With the change in place both are green: **35 passed, 0 failed** and **8 passed, 0 failed** (11 with
`KnownRepositoryEndpointTests` added).

---

## Revert A - put the used-only filter back

`.Where(row => row.LastUsedUtc is not null)` restored in `OrderOneList`: the product exactly as phase 2
left it.

```
Unit:        Failed: 8, Passed: 27, Total: 35
End to end:  Failed: 5, Passed:  3, Total:  8
```

The named failure, in full:

```
[FAIL] OneRepositoryListOrderTests.ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom
  Assert.Equal() Failure: Collections differ
                                                              ↓ (pos 3)
  Expected: ["/repos/newest", "/repos/middle", "/repos/oldest", "/roots/alpha/kilo", "/roots/alpha/zulu"]
  Actual:   ["/repos/newest", "/repos/middle", "/repos/oldest"]
```

The never-opened half is stored and never reaches a screen - the phase-2 state, named by the test that
exists for it. The other seven unit failures and all five end-to-end failures are the tests listed in the
prediction.

**As predicted, and the interesting half is what stayed green:** all of
`DiscoveredRepositoryObserverTests` and every phase-2 test about what is WRITTEN. Phase 3 changed no
writer, and it shows.

## Revert B - the null goes first, which is what PostgreSQL would do

`.OrderByDescending(row => row.LastUsedUtc ?? DateTime.MaxValue)` - the list a `ORDER BY ... DESC` on
PostgreSQL would produce, reproduced in C# so it can be watched on a machine that has no PostgreSQL.

```
Unit:        Failed: 4, Passed: 31, Total: 35
End to end:  Failed: 4, Passed:  4, Total:  8
```

```
[FAIL] OneRepositoryListOrderTests.OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast
  Expected: ["/repos/newest", "/repos/middle", "/repos/oldest", "/roots/alpha/kilo", "/roots/alpha/zulu"]
  Actual:   ["/roots/alpha/kilo", "/roots/alpha/zulu", "/repos/newest", "/repos/middle", "/repos/oldest"]

[FAIL] OneRepositoryListTunnelProofTests.MixedMachine_IsServedAsOneList_WithTheNeverOpenedRepositoriesAtTheBottom
  Expected: ["D:\Repos\kilo", "D:\Repos\zulu"]        <- the bottom two of the served list
  Actual:   ["D:\Repos\bravo", "D:\Repos\alpha"]      <- the two that have been USED, pushed to the bottom

[FAIL] OneRepositoryListTunnelProofTests.TheDirectorGoesAway_TheOneListIsStillServedInTheSameOrder
  Expected: ["D:\Repos\alpha", "D:\Repos\zulu"]
  Actual:   ["D:\Repos\zulu", "D:\Repos\alpha"]
```

That is the defect this phase was warned about, on the wire, through a real Gateway: every repository
nobody has ever opened standing above everything the owner actually works in.

`OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame` failed on its LAST assertion, as
predicted - the permutations still agreed with each other, because nulls-first is a consistent order, just
the wrong one. Without that last assertion the test would have passed straight through the defect.

**One thing the prediction missed:**
`DiscoveredRepositoryCatalogTests.ReadForMachine_DiscoveredRepository_IsServedBeneathTheUsedHalf` also
failed (`Expected: ["/repos/used", "/roots/alpha/never-opened"]`, `Actual:` the reverse), and the
prediction did not list it. It is a mixed list, so it SHOULD fail; the prediction was incomplete rather
than wrong. Nothing that the prediction said would stay green went red.

## Revert C - stop stamping the verdict

`NeverOpened = false` always, so the flag stops saying what the row is.

```
Unit:        Failed: 2, Passed: 33, Total: 35
End to end:  Failed: 5, Passed:  3, Total:  8
```

```
[FAIL] OneRepositoryListOrderTests.ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom
  Expected: [False, False, False, True, True]
  Actual:   [False, False, False, False, False]
```

**Exactly the predicted separation: every failure is on a flag and not one is on a path list.** The
verdict has a proof of its own and is not riding on the order's.

## Revert D - drop the last tiebreak

`.ThenBy(row => row.Path, StringComparer.Ordinal)` removed.

```
Unit:        Failed: 1, Passed: 34, Total: 35
End to end:  Failed: 0, Passed:  8, Total:  8
```

```
[FAIL] OneRepositoryListOrderTests.OrderOneList_TwoNeverOpenedRepositoriesShareAName_AreOrderedByPath
  Expected: ["/roots/alpha/devthrottle", "/roots/beta/devthrottle"]
  Actual:   ["/roots/beta/devthrottle", "/roots/alpha/devthrottle"]
```

Exactly one test, failing by serving the two rows in the order they were handed over in - which is the
whole point of the tiebreak, and which confirms the test is not decoration.

---

## Restored

The file was restored from a copy taken before the first revert and both filters were re-run:
**35 passed / 0 failed** and **11 passed / 0 failed**. The working tree then differed from the committed
change by nothing.
