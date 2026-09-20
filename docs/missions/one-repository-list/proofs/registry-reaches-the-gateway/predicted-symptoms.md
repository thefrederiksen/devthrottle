# Predicted symptoms, written and committed BEFORE any revert was run

Mission: One repository list, held on the Gateway. The completeness gap: **the registry reaches the
Gateway.**

A proof nobody has watched fail is decoration. Below are the four reverts, each aimed at a different
load-bearing part of the change, and **what I predict each one will do** - which test fails, and with
what symptom. This file is committed first so the predictions cannot be written after the fact.

The results are in `watched-it-fail.md`, and where a prediction was wrong it says so rather than being
quietly corrected here.

---

## Revert A - the Director never folds its registered list into the push

`ControlApiHost.SnapshotRepositories` goes back to mapping the scan alone:

```csharp
private List<RepoStatusDto> SnapshotRepositories()
    => (_repositoryMonitor?.Snapshot() ?? Array.Empty<RepositoryStatus>())
        .Select(s => RepositoryDtoMapper.Map(s, DirectorId, Environment.MachineName))
        .ToList();
```

This is the whole defect, restored.

**Predicted:** the five `TheDirectorPushesItsRegisteredListTests` fail, and the end-to-end
`RegistryReachesTheGatewayTunnelProofTests` fails everywhere a hand-added repository is expected -
`AHandAddedRepositoryUnderNoWatchedFolder_IsServedByTheOneRoute_BeneathEverythingUsed` serving 2 rows
where 3 were asserted, `AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway`
serving an empty list.

**Predicted to stay green:** every test in `DirectorRepositorySnapshotTests` (the union function is
untouched - it is simply no longer called, which is exactly the hole the wiring test exists to cover),
and every test in `TheRegistryReachesTheCatalogTests` (those drive the hub with rows written by hand).
**That contrast is the point of the revert**: it shows the wiring test is not redundant with the
function's own tests.

## Revert B - the Gateway hands identity-only rows to the two status observers

`DirectorHub.PushRepoSnapshot` passes `set` to all three observers instead of `measured`:

```csharp
var accepted = _repositoryStore?.ApplySnapshot(..., set) ?? false;
if (accepted) { _repoHistory?.ObserveSnapshot(..., set); ... }
```

**Predicted:** `TheRegistryReachesTheCatalogTests.PushRepoSnapshot_AnIdentityOnlyRow_NeverReachesTheStoreThatReportsRepositoryStatus`
fails with two rows served where one was asserted;
`..._NeverBecomesADailyDriftRow` fails on `Assert.DoesNotContain("/elsewhere/bravo", ...)`;
`..._NothingButIdentityOnlyRows_StillLeavesTheStatusSurfacesEmpty` fails on the first assertion; and
end to end, `AHandAddedRepository_DoesNotAppearOnTheStatusSurface` fails with two rows on
`GET /repositories`, the second carrying a blank branch.

**Predicted to stay green:** every catalog assertion, in both suites. The catalog is given the whole set
either way, so this revert breaks only the containment, and a proof that looked only at the catalog
would pass straight through it.

## Revert C - the registered list is folded in before the first scan has finished

`DirectorRepositorySnapshot.Union` drops the `scanHasCompleted` guard:

```csharp
if (registered is null || registered.Count == 0)
    return rows;
```

This is the cold start that tells the Gateway everything under every watched folder has gone away.

**Predicted:** `DirectorRepositorySnapshotTests.Union_BeforeTheFirstScanHasCompleted_PushesTheScanAloneAndSaysNothingAboutTheRegistry`
fails with one row where none was asserted;
`Union_BeforeTheFirstScanHasCompleted_TheWarmStartCacheIsStillPushedUntouched` fails with two rows
where one was asserted; `TheDirectorPushesItsRegisteredListTests.A_host_whose_scan_has_not_finished_pushes_the_scan_alone`
fails on `Assert.Empty`.

**And the one that matters, end to end:**
`ARestartBeforeTheFirstScanHasRun_DoesNotEraseWhatTheScanHadAlreadyFound` fails **after** the cold
push - the served list drops from 3 rows to 1, leaving only the hand-added repository, because the
Gateway reconciled two watched repositories away on the word of a Director that had not yet looked at
its own disk. I expect it to fail on `Assert.Empty(coldPush)` first, which is the earlier assertion of
the same fact.

**Predicted to stay green:** everything in `TheRegistryReachesTheCatalogTests`, which never asks what a
Director pushes before it has scanned, and every other end-to-end test here, all of which scan first.

## Revert D - the completed-scan fact is inferred from the model instead of being recorded

`RepositoryMonitor.HasCompletedAScan` is replaced by the plausible-looking substitute - "the model has
rows in it, so a scan must have run":

```csharp
public bool HasCompletedAScan => Snapshot().Count > 0;
```

**Predicted:** `RepositoryMonitorHasCompletedAScanTests.AScanThatFoundNothing_StillCounts` fails
(`Assert.True` on a monitor whose scan found nothing), and
`AWarmStartCacheAlone_DoesNotCount` fails (`Assert.False` on a monitor holding cached rows nobody has
verified). End to end,
`AMachineWithNoWatchedFoldersAtAll_StillGetsItsRepositoriesOntoTheGateway` fails with an empty served
list - the user with no watched folders is exactly the user this substitute silently abandons.

**Predicted to stay green:** `BeforeAnyScan_ItIsFalse_EvenThoughNoScanIsRunning` (an unscanned monitor
has no rows either way), which is why that test alone would not have caught the substitute.
