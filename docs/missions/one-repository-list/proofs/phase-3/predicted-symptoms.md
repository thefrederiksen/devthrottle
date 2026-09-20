# Phase 3 - what each revert should break, written down BEFORE any of them was run

A proof nobody has watched fail is decoration, and a prediction written after the fact is not a
prediction. This file is committed before the first revert is run, exactly as phase 2 committed its own.
What actually happened is in `watched-it-fail.md`, beside it.

Four reverts, each aimed at a different load-bearing part of the change. Each is the change on its own,
put back the way it was or broken in the one way that matters, with everything else left alone.

## Revert A - put the used-only filter back

`KnownRepositoryStore.OrderOneList` regains `.Where(row => row.LastUsedUtc is not null)`, which is the
product exactly as phase 2 left it: the never-opened half is stored and not served.

**Predicted:** every test about the union fails, and nothing else does.

- `OneRepositoryListOrderTests.ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom`
  - three paths served where five were expected; the two `/roots/alpha/...` entries are gone.
- `OneRepositoryListOrderTests.OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast`
  - three of five.
- `OneRepositoryListOrderTests.OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame`
  - the expected order collapses to the two used ones.
- `OneRepositoryListOrderTests.OrderOneList_TwoNeverOpenedRepositoriesShareAName_AreOrderedByPath`
  - empty.
- `OneRepositoryListOrderTests.ReadForMachine_TheDirectorThatFoundThemIsGone_StillServesThem`, and the
  two machine-partition tests that read a never-opened list - empty.
- `DiscoveredRepositoryCatalogTests.ReadForMachine_DiscoveredRepository_IsServedBeneathTheUsedHalf`
  - one row served, not two.
- End to end: `OneRepositoryListTunnelProofTests.MixedMachine_...` (two rows, not four),
  `TheDirectorGoesAway_...`, `ANeverOpenedRepositoryIsOpened_...`, and the two phase-2 tunnel assertions
  that now read the served list.
- **Stays green:** everything about what is STORED - the phase-2 catalog and observer tests - because
  phase 3 changed no writer. That is the point of the split, and it should show.

## Revert B - the null goes first, which is what PostgreSQL would do

The ordering key becomes `row.LastUsedUtc ?? DateTime.MaxValue`, so a never-opened repository sorts
above everything. This is not a hypothetical: it is the exact list a `ORDER BY ... DESC` on PostgreSQL
would produce, reproduced in C# so that it can be watched here.

**Predicted:** every never-opened repository appears at the TOP of every list.

- `ReadForMachine_MixOfUsedAndNeverOpened_PutsTheNeverOpenedOnesAtTheBottom` - the served paths start
  `/roots/alpha/kilo`, `/roots/alpha/zulu` and the three used ones follow.
- `OrderOneList_HandedRowsInPostgresNullsFirstOrder_StillPutsNeverOpenedLast` - the same inversion. This
  is the test that exists for this defect and it must be one of the ones that fails.
- `OrderOneList_WhateverOrderTheRowsArriveIn_TheServedOrderIsTheSame` - the orders still AGREE with each
  other (it is a consistent order, just the wrong one), so it fails on its second assertion: the
  expected list is the mission's order, not merely a stable one. If that assertion were missing this
  test would pass through the defect, which is why it is there.
- End to end: `MixedMachine_...` - `kilo` and `zulu` at positions 0 and 1.
- **Stays green:** the two-halves-meet tests, the machine-partition tests and everything phase 2 wrote -
  none of them mixes a used repository with a never-opened one in one list.

## Revert C - stop stamping the verdict

`NeverOpened` is always false, so the flag no longer says what the row is and a client would have to
read the missing date and decide for itself.

**Predicted:** the flag assertions fail and the ORDER assertions do not.

- `ReadForMachine_MixOfUsedAndNeverOpened_...`, `ReadForMachine_DiscoveredRepository_IsServedBeneathTheUsedHalf`,
  and end to end `MixedMachine_...`, `TheDirectorGoesAway_...`, `ANeverOpenedRepositoryIsOpened_...` and
  the phase-2 tunnel test that now reads the served flags.
- The order itself is untouched, so no test fails on a path list. That separation is the thing being
  checked here: the verdict has a proof of its own and is not riding on the order's.

## Revert D - drop the last tiebreak

`.ThenBy(row => row.Path, StringComparer.Ordinal)` is removed, so two repositories with the same name
fall back to whatever order the rows arrived in.

**Predicted:** exactly one test fails -
`OrderOneList_TwoNeverOpenedRepositoriesShareAName_AreOrderedByPath` - and it fails by serving
`/roots/beta/devthrottle` before `/roots/alpha/devthrottle`, which is the order they were handed over
in. Everything else stays green, because no other test has two rows that tie all the way down.

If that test does NOT fail, the tiebreak is not doing anything the test can see and the test is
decoration; that would be a finding about the test, and it would be said here rather than quietly
dropped.
