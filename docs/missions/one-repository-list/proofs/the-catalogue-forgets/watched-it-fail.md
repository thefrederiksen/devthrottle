# Watched it fail - six reverts, what each one broke, and where the prediction was wrong

Mission: One repository list, held on the Gateway. "The catalogue forgets, and names a repository
usefully."

`predicted-symptoms.md` beside this was written and **committed first** (`55a9dd855`), before any of
these ran. Four of the six are reverts of load-bearing lines; two are WRONG RULES substituted for a
rule nothing removed, because this mission has already paid for the lesson that a revert proves the
guard catches THAT defect and not the class its name claims.

Each one was applied, run, and restored before the next. The suite was green again between every
pair.

---

## Revert A - the Director stops sending the root-folder listing

`ControlApiHost.RootFolderListing()` returns null.

**RED, as predicted:**

```
AFolderThatWasWorkedInAndThenDeleted_IsForgottenByTheOneRoute
  Assert.Single() Failure: The collection contained 2 items
  Collection: [KnownRepositoryDto { Name = "goes", NeverOpened = False, ... },
               KnownRepositoryDto { Name = "stays", NeverOpened = False, ... }]
```

`goes` is the folder that was deleted from the disk. Plus the three
`TheDirectorPushesItsRegisteredListTests` wiring tests.

**The prediction was INCOMPLETE.** I predicted one end-to-end failure and got three:
`ALiveWorktreeTheScanCannotSee_IsNotForgotten` and `ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt`
also went red, because both assert on the listing's own contents on the way past and meet a null.
That is not a second defect being caught - it is two tests failing on the mechanism rather than on
the damage, which is the thing revert B then made me fix (below).

**THE FINDING WORTH KEEPING, and it held exactly:** with the feature lifted out of the Director,
**70 of the 73 unit tests still passed** - every one of the 23 `TheCatalogueForgetsTests`, all 16
`DiscoveredRepositoryObserverTests`, all 14 `DirectorRootFoldersTests` and all 11 name tests. They
hand the listing to the Gateway themselves, so they prove what the Gateway DOES with a listing and
say nothing at all about whether a Director ever sends one. Without the end-to-end proof and the
three wiring tests, this whole change could be unwired at the Director and thirty-nine tests would
have gone on passing.

## Revert B - a root the Director could not read is reported as present with no children

`DirectorRootFolders.Build` turns a null answer from the lister into an empty child list.

**RED, as predicted:** `DirectorRootFoldersTests.Build_ARootThatCouldNotBeListed_IsNotReportedAtAll`,
and end to end `ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt`.

**AND IT MADE THE PROOF BETTER, which is the second thing this revert bought.** On the first run the
end-to-end test failed on the LISTING's shape - "expected one listing, got two" - before it ever
looked at the damage. That is the wrong sentence for a reader to meet: what actually happened is
that a row was deleted. The assertions were reordered to put the consequence first, and it now
reports:

```
ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt
  Assert.Equal() Failure: Collections differ
  Expected: [<the row under the unplugged root>, <the row that stays>]
  Actual:   [<the row that stays>]
```

An unplugged drive erasing the last-access times of everything on it, said in one line.

**GREEN, as predicted:** everything else, including the whole flow. A machine whose roots are all
readable behaves identically under this defect, which is exactly how it would have shipped.

## Revert C - THE RULE THE BRIEF RECOMMENDED

On the Gateway, the roots' child paths are not added to the "still there" set - so a used row is
forgotten when it is under a covered root and absent from the pushed SNAPSHOT. That is the shape the
brief asked for, before the ground was measured.

**RED, as predicted:**

```
AUsedRepositoryTheScanNeverReports_ButTheRootFolderStillHolds_IsKept
  Assert.Equal() Failure: Collections differ
  Expected: ["/roots/work/a-live-worktree", "/roots/work/the-clone"]
  Actual:   ["/roots/work/the-clone"]
```

and end to end, `ALiveWorktreeTheScanCannotSee_IsNotForgotten`. A live worktree, deleted from the
catalogue because the root-folder scan cannot see a folder whose `.git` is a file. On the machine
this was measured on that is eleven live folders, one of them the folder the work was written in.

`AFolderWrittenTwoWays_IsRecognisedAsStillThere` and the observer's
`ObserveSnapshot_AFolderDisappearsUnderAWatchedRoot_DefeatsTheUnchangedRePushSkip` went red too -
unpredicted, and unsurprising once seen: both feed the Gateway a folder that is in the listing and
not in the snapshot.

**GREEN:** the flow, every root and Director failure case, and both name tests. **94 of 97 unit
tests passed under the wrong rule**, and six of the seven end-to-end tests. A proof set that did not
contain a folder the scan cannot see would have certified the defect.

## Revert D - a WRONG RULE nothing removed: prefix instead of direct parent

"Under a covered root" decided by `key.StartsWith(root + "/")` instead of by the row's direct parent.
No line of mine deleted; a plausible generalisation put in place of a specific one - the tidy-up a
later reader makes.

**RED, as predicted, and by exactly ONE test:**

```
ARepositoryDeeperThanOneLevelUnderARoot_IsKept
  Assert.Contains() Failure: Item not found in collection
  Collection: ["/roots/work/alpha"]
  Not found:  "/roots/work/team/nested"
```

**GREEN: 96 of 97 unit tests, and all SEVEN end-to-end tests.** This is the finding this mission
told me to go looking for. The whole end-to-end proof cannot see this defect at all, and one unit
test stands between a specific rule and a wider one that deletes folders no root ever looked at - a
broad root such as `/Users/soren` speaking for `/Users/soren/ReposFred/devthrottle`.

## Revert E - the name fold stops counting how many rows share a name

Only a blank name is replaced.

**RED, as predicted:**

```
OneSlugAcrossManyFolders_AndABlankName_AreBothServedAsTheFolderName
  Expected: ["devthrottle", "devthrottle-repo-list", "devthrottle-ci-p1", "devthrottle-fast-ci"]
  Actual:   ["thefrederiksen/devthrottle", "thefrederiksen/devthrottle",
             "thefrederiksen/devthrottle", "devthrottle-fast-ci"]
```

That Actual line is the live catalogue, reproduced: one slug saying nothing, three times over. Seven
name tests and the end-to-end name test went red; **every forgetting test stayed green**, so the two
gaps do not lean on each other.

## Revert F - a WRONG RULE nothing removed: replace EVERY name with the folder name

The over-eager version, and the simpler-looking one.

**RED, as predicted:**

```
ANameNoOtherRowShares_IsServedExactlyAsItIsStored
  Expected: ["thefrederiksen/devthrottle_internal", "something-else"]
  Actual:   ["devthrottle_internal", "other"]

APathWithNoFolderNameInIt_KeepsTheNameItHad
  Expected: ["the-root"]
  Actual:   [""]
```

The second one is the one worth having: a rule that always prefers the folder name will happily
serve a repository with NO NAME AT ALL when the path has no folder name in it.

**GREEN: 55 of 58 name and catalogue tests, and all seven end-to-end tests.** Both wrong rules, E and
F, produce a list that looks perfectly reasonable at a glance. Neither is visible to the flow.
