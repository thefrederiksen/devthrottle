# Watching it fail - the three reverts, and what actually happened

The predictions are in `predicted-symptoms.md`, committed as `585b36a44` BEFORE any revert was made, so
nothing below could be fitted to the output afterwards.

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon. `dotnet` is at `~/.dotnet/dotnet`.
Each revert was applied, run, and restored one at a time; the restore was verified by grepping the marker
comment out of the file and re-running the same tests green.

---

## Revert 1 - the hub no longer calls the observer

One line commented out in `DirectorHub.PushRepoSnapshot`.

**PREDICTED:** the four end-to-end tests fail - two with `Assert.Equal(2, rows.Count)` actual 0, one with
an empty collection, one with actual 1 - and every unit test stays green.

**HAPPENED: exactly that.**

```
Failed:     4, Passed:     0   CcDirector.Gateway.Tests.DiscoveredRepositoryTunnelProofTests

  RootFolderScan_PushedUpTheTunnel_...        Assert.Equal() Expected: 2  Actual: 0
  DiscoveredRepositories_OutliveTheDirector_  Assert.Single() Failure: The collection was empty
  RootFolderRemoved_...                       Assert.Equal() Expected: 2  Actual: 0
  RepositoryThatIsUsed_KeepsItsLastUsedTime_  Assert.Equal() Expected: 2  Actual: 1
```

And the unit suites, unchanged:

```
Failed:     0, Passed:    32   DiscoveredRepositoryCatalogTests + DiscoveredRepositoryObserverTests
                               + KnownRepositoryStoreTests + KnownRepositoryMigrationTests
```

**This is the finding worth keeping.** Thirty-two unit tests over the store and the observer pass with the
feature completely unwired - they call those two types directly and never go through the hub. A missing
wire between the accepted push and the catalogue is invisible to every one of them. That is why the
end-to-end proof exists, and it is the answer to "the store tests are green, isn't that enough".

## Revert 2 - a discovered repository is written WITH a last-used time

`LastUsedUtc = null` became `LastUsedUtc = seen` on the inserted row. Null IS "found but never opened";
this breaks that one rule.

**PREDICTED:** seven named tests fail, each at the assertion named in the table; the route the phone reads
would start serving never-opened repositories.

**HAPPENED: nine of the thirty-two unit tests failed, and all four end-to-end tests failed.** Every
prediction in the table landed on the assertion named:

```
ObserveDiscovered_RepositoryNeverOpened_IsHeldWithNoLastUsedTime
    Assert.Null() Failure  Expected: null  Actual: 2026-09-19T12:00:00.0000000Z
ReadForMachine_DiscoveredRepository_IsStoredAndIsNotServedYet
    Assert.Single() Failure: The collection contained 2 items
    [{ Path = "/roots/alpha/never-opened" }, { Path = "/repos/used" }]
ObserveDiscovered_RootFolderRemoved_RemovesOnlyTheNeverOpenedRows       Assert.True() Failure
ObserveDiscovered_WindowsPathSpellingDiffers_StaysOneRow                Assert.Equal() Strings differ
ObserveSnapshot_RepositoryUnderARootFolder_IsHeldAsFoundButNeverOpened  Assert.Null() Failure
ObserveSnapshot_RepositoryThatIsUsed_KeepsItsLastUsedTime               Assert.Null() Failure
```

and end to end:

```
RootFolderScan_...   Assert.All() Failure: 2 out of 2 items ... Assert.Null() Expected: null
                     Actual: "2026-09-20 02:43:38.559722"
RepositoryThatIsUsed_...  Assert.Single() Failure: the served list contained 2 items
                     [{ Path = "D:\Repos\beta" }, { Path = "D:\Repos\alpha" }]
```

That last one is the symptom the mission cares about most: with the rule broken, `GET
/directors/{id}/known-repositories` - the route the phone reads today, and which phase 2 must not change -
starts serving a repository nobody has ever opened. The proof sees it.

**Two more failed than were predicted**, and both are the prediction being too narrow rather than wrong:
`ObserveDiscovered_NameChanges_...` and `ObserveDiscovered_UnchangedScan_RefreshesTheLastSeenStamp...`
also fall, because a row that now carries a last-used time is treated as the untouchable used half, so
the refresh path is never reached at all. Recorded rather than quietly folded in.

## Revert 3 - the reconciliation guard is removed

`var reconcile = found.Count > 0 && !sawProvisional;` became `var reconcile = true;`, and the
short-circuit that answers an empty push without a database read was deleted.

**PREDICTED:** three observer tests and one end-to-end test fail; every `DiscoveredRepositoryCatalogTests`
test stays green, because the guard lives in the observer and the store takes the decision as an argument.

**HAPPENED: exactly that.**

```
Failed:     3, Passed:    29
  ObserveSnapshot_EmptyPush_RemovesNothing            Assert.Single() Failure: The collection was empty
  ObserveSnapshot_AllProvisionalPush_RemovesNothing   Assert.Single() Failure: The collection was empty
  ObserveSnapshot_MixedPush_InsertsWhatItSawAndReconcilesNothing
      Expected: ["/roots/alpha/one", "/roots/alpha/two"]   Actual: ["/roots/alpha/one"]

Failed:     1, Passed:     3   end to end
  RootFolderRemoved_..._AndAnEmptyPushRemovesNothing  Assert.Single() Failure: The collection was empty
```

Every catalogue test stayed green, as predicted. A cold start before the first live scan, and a warm-cache
push, both erase the machine's whole never-opened list without this guard.

## Restored

All three files restored from the copies taken before each edit, verified by grepping each `REVERT n`
marker out (zero hits in all three), and re-run:

```
Failed:     0, Passed:    32   CcDirector.Gateway.UnitTests  (the four repository-catalogue classes)
Failed:     0, Passed:     4   CcDirector.Gateway.Tests.DiscoveredRepositoryTunnelProofTests
```
