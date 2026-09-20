# Predicted symptoms - written and committed BEFORE any revert was run

Mission: One repository list, held on the Gateway. "The catalogue forgets, and names a repository
usefully." Not one of the six phases.

This file is the prediction. `watched-it-fail.md` beside it is what actually happened, including
where the prediction was wrong. Nothing in here was edited after a revert ran.

Every seat on this mission has done this, and the mission has already paid for the lesson that a
revert proves the guard catches THAT defect and not the class its name claims: three seats
validated one guard by reverting the exact line they deleted, and all three missed the same defect
in the other direction. So two of the six below are not reverts at all. They are **wrong rules
substituted for a rule nothing removed** - the plausible thing a later reader might "tidy" this
into - and they attack the guards from the side the deletions cannot reach.

---

## Revert A - the Director stops sending the root-folder listing

**Change:** `ControlApiHost.RootFolderListing()` returns null always (the wiring is lifted back out
of the Director, leaving every other line of the change in place).

**Predicted RED:**
- `TheCatalogueForgetsTunnelProofTests.AFolderThatWasWorkedInAndThenDeleted_IsForgottenByTheOneRoute` -
  the deleted folder is still served. `Assert.Single` on a two-row list.
- `TheCatalogueForgetsTunnelProofTests.ARepeatedSlugAndABlankName_...` stays GREEN, because the
  name fold does not depend on the listing.
- `TheDirectorPushesItsRegisteredListTests.A_host_pushes_what_currently_exists_under_its_root_folders`
  and `...The_root_folders_are_read_fresh_on_every_push` - a null listing where one was expected.

**Predicted GREEN, and this is the finding I expect to keep:** every one of the 23
`TheCatalogueForgetsTests` store tests, and all 16 `DiscoveredRepositoryObserverTests`. They hand
the listing in themselves, so they prove what the Gateway DOES with a listing and say nothing about
whether a Director ever sends one. If the end-to-end proof did not exist, the whole feature could be
unwired at the Director and thirty-nine tests would still pass.

## Revert B - a root the Director could not read is reported as present with no children

**Change:** in `DirectorRootFolders.Build`, a null answer from the lister becomes an empty child
list instead of dropping the root.

This is the "checks that fail open" defect in its purest form: "I could not look" rendered as
"there is nothing to see", and what it authorises is deletion.

**Predicted RED:**
- `DirectorRootFoldersTests.Build_ARootThatCouldNotBeListed_IsNotReportedAtAll` - two listings where
  one was expected.
- `TheCatalogueForgetsTunnelProofTests.ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt` -
  the row under the unreadable root is GONE. Expected two paths, actual one. This is the one that
  matters: an unplugged drive erasing a year of last-access times.

**Predicted GREEN:** everything else, including the whole flow. A machine whose roots are all
readable behaves identically, which is exactly why this would have shipped unnoticed.

## Revert C - THE RULE THE BRIEF RECOMMENDED, which measurement says is wrong

**Change:** on the Gateway, `stillThere` is seeded from the pushed snapshot alone - the root
folders' child paths are not added to it. That is precisely "forget a used row under a covered root
that the Director no longer reports", the shape the brief asked for before the ground was measured.

**Predicted RED:**
- `TheCatalogueForgetsTunnelProofTests.ALiveWorktreeTheScanCannotSee_IsNotForgotten` - the live
  worktree is deleted from the catalogue. Expected two paths, actual one, the missing one being the
  worktree.
- `TheCatalogueForgetsTests.AUsedRepositoryTheScanNeverReports_ButTheRootFolderStillHolds_IsKept`
  and `...ARootTheDirectorListedAndFoundEmpty_ForgetsWhatItHeld`'s sibling rows.

**Predicted GREEN:** the flow test, every failure case about roots and Directors, and both name
tests. The defect is invisible to every test that does not contain a folder the scan cannot see -
which is what makes it worth a test of its own rather than a sentence in a document.

## Revert D - a WRONG RULE, substituted for one nothing removed: prefix instead of parent

**Change:** `ObserveDiscovered` decides "under a covered root" with a PREFIX test
(`key.StartsWith(rootKey + "/")`) instead of comparing the row's direct parent. No line of mine is
deleted; a plausible generalisation is put in place of a specific one.

It is the tidy-up a later reader would make, and it is wrong: the scan and the listing both reach
exactly one level, so a root claiming everything beneath it claims folders it never looked at. A
broad root such as `/Users/soren` would then speak for `/Users/soren/ReposFred/devthrottle`.

**Predicted RED:**
- `TheCatalogueForgetsTests.ARepositoryDeeperThanOneLevelUnderARoot_IsKept` - the nested repository
  is deleted.

**Predicted GREEN:** everything else. If that one test did not exist, this substitution would pass
the entire suite while quietly widening what may be deleted.

## Revert E - the name fold stops counting how many rows share a name

**Change:** `KnownRepositoryStore.DisplayName` replaces only a BLANK name, and keeps any non-blank
one.

**Predicted RED:**
- `TheNameARepositoryIsServedUnderTests.OneSlugAcrossManyFolders_AndABlankName_AreBothServedAsTheFolderName`
  - expected `devthrottle`, actual `thefrederiksen/devthrottle`.
- `...TheOrderFollowsTheNameThatIsServed_NotTheOneThatIsStored`,
  `...ARowWhoseSlugAndFolderNameDisagree_IsPlacedByTheFolderName`,
  `...AWindowsPath_FindsItsFolderNameOnAnyMachine`, `...NamesThatDifferOnlyInCase_AreBothReplaced`,
  `...ReadForMachine_ServesTheFoldedNames`.
- `TheCatalogueForgetsTunnelProofTests.ARepeatedSlugAndABlankName_AreServedAsDistinctFolderNames_InThatOrder`.

**Predicted GREEN:** every forgetting test. The two gaps do not lean on each other.

## Revert F - a WRONG RULE, substituted: replace EVERY name with the folder name

**Change:** `DisplayName` always answers `RepositoryPaths.FolderName(row.Path)`.

The over-eager version of the same idea, and the one a reader would reach for because it is simpler.
It throws away a name that was doing its job.

**Predicted RED:**
- `TheNameARepositoryIsServedUnderTests.ANameNoOtherRowShares_IsServedExactlyAsItIsStored` -
  expected `thefrederiksen/devthrottle_internal`, actual `devthrottle_internal`.
- `TheNameARepositoryIsServedUnderTests.ANameSharedOnlyWithAnotherMachinesRow_IsStillKept`.
- `TheNameARepositoryIsServedUnderTests.APathWithNoFolderNameInIt_KeepsTheNameItHad` - expected
  `the-root`, actual an empty string.

**Predicted GREEN:** every other name test, including the whole flow. Both wrong rules - E and F -
produce a list that looks reasonable at a glance, which is the point.
