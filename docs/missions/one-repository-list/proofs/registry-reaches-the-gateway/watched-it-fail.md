# Watched it fail

Mission: One repository list, held on the Gateway. The completeness gap: **the registry reaches the
Gateway.**

The predictions in `predicted-symptoms.md` were committed first, in `e09b24df2`, before any of these
ran. Each revert was applied alone, the tests were run, and the code was restored before the next one.

**Where a prediction was wrong, it says so below rather than being quietly corrected.** Two were, and
both were wrong in the same direction: I over-predicted how many tests a revert would take down. Each
time the tests that survived were the ones genuinely insensitive to that revert, which is worth more
than a prediction that happened to be round.

---

## Revert A - the Director never folds its registered list into the push

`ControlApiHost.SnapshotRepositories` put back to mapping the scan alone. This is the whole defect,
restored.

| Suite | Result |
|---|---|
| the three new unit classes (28 tests) | **Failed: 3, Passed: 25** |
| `RegistryReachesTheGatewayTunnelProofTests` (7) | **Failed: 6, Passed: 1** |

**The symptoms, as reported:**

- `AHandAddedRepositoryUnderNoWatchedFolder_IsServedByTheOneRoute_BeneathEverythingUsed`:
  `Assert.Equal() Failure: Values differ. Expected: 3, Actual: 2` - the hand-added repository is simply
  not on the route.
- `AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway`:
  `Expected: [".../only-one", ".../only-two"], Actual: []` - the machine with no watched folders is
  served an empty list, which is exactly the user this work exists for.

**PREDICTION WRONG, in a way worth keeping.** I predicted all five `TheDirectorPushesItsRegisteredListTests`
would fail. **Three did.** The two that survived are the two that cannot see this revert:
`A_host_with_no_registry_pushes_exactly_what_it_always_pushed` (there is no registered list either way)
and `A_repository_that_is_both_registered_and_watched_is_pushed_once_with_its_status` (the scan reports
it either way). Both are guards against a DIFFERENT regression, and it is right that they held.

**The contrast this revert was aimed at held exactly as predicted:** all 12
`DirectorRepositorySnapshotTests` and all 11 `TheRegistryReachesTheCatalogTests` stayed green. The union
function still works perfectly - nothing calls it. That is the whole reason
`TheDirectorPushesItsRegisteredListTests` exists, and without it this change could be lifted back out of
the Director with twenty-three tests still passing.

## Revert B - the Gateway hands identity-only rows to the two status observers

`DirectorHub.PushRepoSnapshot` passing `set` to all three observers instead of `measured`.

| Suite | Result |
|---|---|
| the three new unit classes (28) | **Failed: 3, Passed: 25** |
| `RegistryReachesTheGatewayTunnelProofTests` (7) | **Failed: 1, Passed: 6** |

Exactly the four predicted, by name:
`PushRepoSnapshot_AnIdentityOnlyRow_NeverReachesTheStoreThatReportsRepositoryStatus`,
`PushRepoSnapshot_AnIdentityOnlyRow_NeverBecomesADailyDriftRow`,
`PushRepoSnapshot_NothingButIdentityOnlyRows_StillLeavesTheStatusSurfacesEmpty`, and end to end
`AHandAddedRepository_DoesNotAppearOnTheStatusSurface`.

**The finding worth carrying forward.** Every catalog assertion in both suites stayed green, in both
suites, as predicted. The catalog is handed the whole set either way, so **a proof that looked only at
the catalog would have passed straight through this defect** - and the defect it would have missed is
the morning report quietly gaining drift measurements for repositories nobody ever read. The containment
needed its own tests; it does not come free with the feature's.

## Revert C - the registered list is folded in before the first scan has finished

`DirectorRepositorySnapshot.Union` with the `scanHasCompleted` guard dropped.

| Suite | Result |
|---|---|
| the three new unit classes (28) | **Failed: 3, Passed: 25** |
| `RegistryReachesTheGatewayTunnelProofTests` (7) | **Failed: 1, Passed: 6** |

The three unit failures were the three predicted, by name.

**THE END-TO-END TEST WAS CHANGED BECAUSE OF WHAT THIS REVERT SHOWED, and that is the most useful thing
that came out of the exercise.** On its first run,
`ARestartBeforeTheFirstScanHasRun_DoesNotEraseWhatTheScanHadAlreadyFound` failed at
`Assert.Empty(coldPush)` - it stopped on the GUARD and never reached the damage the guard prevents. A
proof that only restates the mechanism cannot tell a reader what the mechanism is for, and it would have
kept passing if the guard moved somewhere that did not actually protect the Gateway.

So the assertions were reordered to put the CONSEQUENCE first, and the revert was run again:

```
Assert.Equal() Failure: Values differ
Expected: 3
Actual:   1
```

**Three repositories became one.** The two the scan had found under the watched folder were reconciled
away by a Director that had not yet looked at its own disk, leaving only the hand-added one. That is the
symptom, and it is now the one the test reports.

## Revert D - the completed-scan fact inferred from the model instead of recorded

`public bool HasCompletedAScan => Snapshot().Count > 0;` - the plausible-looking substitute.

| Suite | Result |
|---|---|
| `RepositoryMonitorHasCompletedAScanTests` (6) | **Failed: 2, Passed: 4** |
| `RegistryReachesTheGatewayTunnelProofTests` (7) | **Failed: 2, Passed: 5** |

The two Core failures were the two predicted: `AScanThatFoundNothing_StillCounts` and
`AWarmStartCacheAlone_DoesNotCount`. And `BeforeAnyScan_ItIsFalse_EvenThoughNoScanIsRunning` passed, as
predicted - an unscanned monitor has no rows either way, so that test alone would never have caught the
substitute.

**PREDICTION WRONG, and this one found something.** I predicted ONE end-to-end failure
(`AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway`, with
`Expected: [...], Actual: []`, which is what happened). **A second test failed that I had not
predicted:** `OneSourceShrinking_RemovesOnlyItsOwnRows`. It un-watches the folder half way through,
which leaves the monitor with an empty model - and under the substitute that reads as "this Director has
never scanned", so its whole registered list drops out of the push. The same defect, reached by a
different route, in a test written for something else entirely. I had not seen that the two cases
touch.
