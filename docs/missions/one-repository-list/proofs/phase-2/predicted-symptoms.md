# The three reverts, and the symptom predicted for each - WRITTEN BEFORE THEY WERE RUN

A proof nobody has watched fail is decoration. This file was written and saved before any revert was
made, so the predictions below cannot have been fitted to the output afterwards. What actually happened
is in `watched-it-fail.md` beside it.

Each revert is ONE line, and each is aimed at a different load-bearing part of phase 2.

## Revert 1 - the hub no longer calls the observer

In `src/CcDirector.Gateway/Streaming/DirectorHub.cs`, `PushRepoSnapshot`, comment out the
`_discoveredRepositories?.ObserveSnapshot(...)` call. The store and the observer are untouched; only the
wiring between the accepted push and the catalogue goes away.

Predicted: **the four end-to-end tests in `CcDirector.Gateway.Tests.DiscoveredRepositoryTunnelProofTests`
fail, and nothing else does.**

| Test | Predicted first failing assertion |
|---|---|
| `RootFolderScan_PushedUpTheTunnel_IsHeldAsFoundButNeverOpened_UnderTheMachineTheEndpointReads` | `Assert.Equal(2, rows.Count)` - actual 0 |
| `DiscoveredRepositories_OutliveTheDirector_WhileThePushedSnapshotGoesStale` | `Assert.Single(CatalogRows())` - the collection is empty |
| `RepositoryThatIsUsed_KeepsItsLastUsedTime_WhenTheRootFolderScanRunsOverIt` | `Assert.Equal(2, rows.Count)` - actual 1, the used row alone |
| `RootFolderRemoved_RemovesTheNeverOpenedRows_AndAnEmptyPushRemovesNothing` | `Assert.Equal(2, CatalogRows().Count)` - actual 0 |

Predicted to stay GREEN: every test in `CcDirector.Gateway.UnitTests`, including
`DiscoveredRepositoryCatalogTests` and `DiscoveredRepositoryObserverTests` - they call the store and the
observer directly and never go through the hub. **That is the point of the prediction**: the unit tests
alone cannot see a missing wire, which is exactly why the end-to-end proof exists.

## Revert 2 - a discovered repository is written WITH a last-used time

In `src/CcDirector.Gateway/History/KnownRepositoryStore.cs`, `ObserveDiscovered`, change the inserted
row's `LastUsedUtc = null` to `LastUsedUtc = seen`. Null IS "found but never opened"; this breaks that
one rule and nothing else.

Predicted failures:

| Test | Predicted first failing assertion |
|---|---|
| `DiscoveredRepositoryCatalogTests.ObserveDiscovered_RepositoryNeverOpened_IsHeldWithNoLastUsedTime` | `Assert.Null(row.LastUsedUtc)` |
| `DiscoveredRepositoryCatalogTests.ReadForMachine_DiscoveredRepository_IsStoredAndIsNotServedYet` | `Assert.Single(store.ReadForMachine(...))` - two rows served |
| `DiscoveredRepositoryCatalogTests.ObserveDiscovered_RootFolderRemoved_RemovesOnlyTheNeverOpenedRows` | `Assert.Single(AllRows())` - three rows, because a row with a time is never reconciled away |
| `DiscoveredRepositoryCatalogTests.ObserveDiscovered_WindowsPathSpellingDiffers_StaysOneRow` | `Assert.Null(row.LastUsedUtc)` |
| `DiscoveredRepositoryObserverTests.ObserveSnapshot_RepositoryUnderARootFolder_IsHeldAsFoundButNeverOpened` | `Assert.Null(row.LastUsedUtc)` |
| `DiscoveredRepositoryObserverTests.ObserveSnapshot_RepositoryThatIsUsed_KeepsItsLastUsedTime` | `Assert.Null(...LastUsedUtc)` on the discovered row |
| `DiscoveredRepositoryTunnelProofTests.RootFolderScan_...` | `Assert.All(rows, row => Assert.Null(row.LastUsedUtc))`, and then `Assert.Empty(await ServedAsync())` - the route the phone reads would start serving them |

Three more end-to-end and catalogue tests may fall out of the same break; the table above is what is
predicted to fail FIRST in each named test.

## Revert 3 - the reconciliation guard is removed

In `src/CcDirector.Gateway/History/DiscoveredRepositoryObserver.cs`, change
`var reconcile = found.Count > 0 && !sawProvisional;` to `var reconcile = true;`, and remove the
short-circuit that answers an empty push without a database read. An empty or all-provisional push is
then mistaken for "every repository was removed".

Predicted failures:

| Test | Predicted first failing assertion |
|---|---|
| `DiscoveredRepositoryObserverTests.ObserveSnapshot_EmptyPush_RemovesNothing` | `Assert.Single(AllRows())` - the collection is empty |
| `DiscoveredRepositoryObserverTests.ObserveSnapshot_AllProvisionalPush_RemovesNothing` | `Assert.Single(AllRows())` - the collection is empty |
| `DiscoveredRepositoryObserverTests.ObserveSnapshot_MixedPush_InsertsWhatItSawAndReconcilesNothing` | `Assert.Equal(new[] { ".../one", ".../two" }, paths)` - two is gone |
| `DiscoveredRepositoryTunnelProofTests.RootFolderRemoved_RemovesTheNeverOpenedRows_AndAnEmptyPushRemovesNothing` | the `await director.PushRepoSnapshotAsync();` step - `Assert.Single(CatalogRows())` is empty |

Predicted to stay GREEN: every `DiscoveredRepositoryCatalogTests` test, because the guard lives in the
observer and the store takes the decision as an argument.
