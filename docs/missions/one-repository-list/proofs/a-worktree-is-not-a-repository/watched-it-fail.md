# Watching it fail - eight attacks, and the two that did not fail

The predictions were written and committed first (`predicted-symptoms.md`, `4e84dddbc`). Each attack was
applied on its own, the named suites were run, and the change was reverted with `git checkout --` before
the next one. Three are reverts of load-bearing lines; five are wrong rules or removed guards that
nobody had deleted.

**Two of the eight did not fail, and both taught me something the tests were claiming falsely.** They are
reported first, because a prediction that comes true only confirms what was already believed.

---

## THE TWO THAT DID NOT FAIL

### F - the collapse moved to AFTER the forgetting rule. EVERYTHING STAYED GREEN.

I predicted `AWorktreeThatIsBothGoneAndNamed_...` would fail with the repository's time stuck at its old
value. It did not:

```
Passed!  - Failed: 0, Passed: 40, Skipped: 0, Total: 40   (the collapse tests and the forgetting tests together)
```

**Why, and it is the honest answer rather than the comfortable one.** Both rules read the same
materialized `rows` list, and the collapse takes the worktree's time off the entity whether or not the
forgetting rule has already marked that entity for removal. Removing the same entity twice is a no-op.
**So the order changes nothing about the outcome.** What it changes is the count and the log line: run
first, a row that is accounted for is never also reported as forgotten.

The code claimed more than that ("doing this first means the time is not lost"), and the test's name
claimed to hold the ordering. **Both have been corrected rather than left standing**: the comment now
says the order is written for clarity and an honest count and that no test holds it, and the test is
renamed `AWorktreeThatIsBothGoneAndNamed_KeepsItsTimeOnTheRepository` - which is what it actually proves.

### G - a warm-start push's cached worktree list believed. EVERYTHING STAYED GREEN.

I moved the worktree loop ABOVE the provisional filter in `DiscoveredRepositoryObserver`, so that a
warm-start row's cached worktrees are taken as statements.

```
Passed!  - Failed: 0, Passed: 37, Skipped: 0, Total: 37
```

**The provisional filter is belt and braces, not the guard.** What holds it shut is the reconciliation
guard: one provisional row anywhere in the push makes `reconcile` false, and the collapse does not run on
a push that is not a real observation. My test `ObserveSnapshot_AProvisionalPush_CollapsesNothing` passes
for a reason its name did not name - which is this mission's "the proof covers the wrong thing" in
miniature, found in my own work by attacking it.

The filter is kept, because a later change to the reconciliation guard would otherwise remove the last
thing standing here. But the code and the test now say plainly which one is load-bearing.

---

## THE SIX THAT FAILED AS PREDICTED

### A - REVERT: the stamp is not taken at session creation

```
[FAIL] AWorktreeIsNotARepositoryTests.ASessionStartedInAWorktree_CarriesTheRepositoryItIsAWorktreeOf
[FAIL] AWorktreeIsNotARepositoryTests.ARestoredSessionIsResolvedAgainstTheDiskAsItIsNow_NotAsItWas
[FAIL] AWorktreeIsNotARepositoryTests.ASessionInAWorktree_MovesTheREPOSITORYUpTheList_NotTheWorktree
[FAIL] AWorktreeIsNotARepositoryTests.ARepositoryRegisteredThroughASymbolicLink_IsNotMatched
Failed!  - Failed: 4, Passed: 47, Skipped: 0, Total: 51            (Core)
Passed!  - Failed: 0, Passed: 49, Skipped: 0, Total: 49            (Gateway unit)
[FAIL] AWorktreeIsNotARepositoryTunnelProofTests.SessionsInWorktrees_AreServedAsTheirOneRepository
Failed!  - Failed: 1, Passed: 5, Skipped: 0, Total: 6              (PARKED, end to end)
```

Predicted and confirmed, including the part worth carrying: **with the rule entirely unwired from the
product, 47 Core tests and all 49 Gateway tests still pass.** Every one of those hands itself the answer.
That is why the tunnel proof and the wire tests exist.

One prediction was slightly wrong and is corrected: I said both tunnel flow tests would fail. Only one
did. The other - the collapse - deliberately reports sessions the way a Director too old to resolve its
own worktrees does, so removing the stamp changes nothing for it. That is the test being right, not
wrong.

### B - REVERT: the answer is not put on the wire

One line deleted from `ControlEndpoints.Map`.

```
Passed!  - Failed: 0, Passed: 41, Skipped: 0, Total: 41            (Core - every Director-side test)
[FAIL] AWorktreeIsNotARepositoryWireTests.Map_CarriesTheResolvedRepositoryOntoTheDto
       Assert.Equal() Failure: Strings differ
Failed!  - Failed: 1, Passed: 48, Skipped: 0, Total: 49            (Gateway unit)
[FAIL] AWorktreeIsNotARepositoryTunnelProofTests.SessionsInWorktrees_AreServedAsTheirOneRepository
Failed!  - Failed: 1, Passed: 5, Skipped: 0, Total: 6              (PARKED, end to end)
```

**This is the joint.** One deleted line stops the feature working in the product, and 89 tests that are
not about the wire pass while it is gone - because the Gateway is forbidden to work the answer out for
itself, so if the Director does not send it, nobody has it.

### C - WRONG RULE: the guard becomes "git can answer" instead of ".git is a FILE"

```
[FAIL] LinkedWorktreeTests.APlainFolderInsideARepository_IsLeftAlone
[FAIL] LinkedWorktreeTests.ARepositoryProper_IsNotAWorktreeAndIsLeftAlone
Failed!  - Failed: 2, Passed: 39, Skipped: 0, Total: 41
```

**Two tests out of forty-one** stand between this rule and crediting a person's sub-folder to a
repository they never picked. Every worktree case passes under the wrong rule, because the wrong rule
gets all of those right. It is the Delivery Lead's condition 1, and it is why that sentence is in the
code beside the guard and not only in a record.

A prediction corrected: I also expected
`ASessionStartedInAFolderThatIsNotARepositoryAtAll_CarriesNothing` to fail. It did not - that folder is
not inside any repository, so git cannot answer for it either. The test that catches this defect is the
one with a repository ABOVE the folder, and it is the only one.

### D - GUARD REMOVED: a submodule treated as a worktree

```
[FAIL] LinkedWorktreeTests.ASubmodule_IsLeftAlone_BecauseASubmoduleIsARepository
[FAIL] LinkedWorktreeTests.RepositoryGitDirectoryOf_AnythingElse_IsNull(entry: ".../.git/modules/vendored")
[FAIL] LinkedWorktreeTests.RepositoryGitDirectoryOf_AnythingElse_IsNull(entry: ".../.git")
Failed!  - Failed: 3, Passed: 38, Skipped: 0, Total: 41
```

The protection is free today - a submodule's git directory does not end in `.git` - and the Delivery
Lead's condition 2 was to pin it anyway, because "free" is what stops being true when somebody
refactors. It is pinned.

### E - WRONG RULE: the collapse claims everything under the repository's folder

Two versions were tried, and the difference between them is the finding.

**E1, the crude version** - the named paths REPLACED by a prefix test - is caught loudly: 12 of 37 unit
tests fail and so does the end-to-end collapse. That is an author who broke the feature, not one who
widened it.

**E2, the faithful version** - the named paths PLUS everything under the repository's folder, which is
what somebody who thought "surely anything inside the repository belongs to it" would write on top of a
rule nobody removed:

```
[FAIL] TheCatalogueCollapsesAWorktreeTests.AFolderInsideTheRepository_IsNotCollapsedIntoIt
Failed!  - Failed: 1, Passed: 36, Skipped: 0, Total: 37            (Gateway unit)
Passed!  - Failed: 0, Passed: 6,  Skipped: 0, Total: 6             (PARKED, end to end)
```

**ONE unit test of thirty-seven catches it, and ALL SIX end-to-end tests pass while a person's own
working sub-folder is deleted from the catalogue and its time folded into a repository they did not
pick.** The end-to-end proof cannot see the difference between "a worktree of this repository" and
"anything under this repository" at all - the same blind spot the catalogue-forgets work found when it
attacked its own parent-versus-prefix rule.

### H - GUARD REMOVED: the named repository need not be in this push

```
[FAIL] TheCatalogueCollapsesAWorktreeTests.AWorktreeOfARepositoryThisPushDoesNotReport_IsNotCollapsed
Failed!  - Failed: 1, Passed: 36, Skipped: 0, Total: 37            (Gateway unit)
Passed!  - Failed: 0, Passed: 6,  Skipped: 0, Total: 6             (PARKED, end to end)
```

A worktree's row deleted with nothing in the push to fold it into, so its last-access time is lost
outright. One unit test; the end-to-end proof sees nothing.

---

## What I would carry out of this

1. **Attacking my own guards found two false claims in my own code and tests**, and neither was a defect
   in the product - they were tests whose names promised more than they held. That is worth more than the
   six confirmations.
2. **The end-to-end proof is nearly blind to the rules that decide what gets deleted.** Three of the
   attacks (E2, H, and the ordering) left all six tunnel tests green. It proves the road; the unit tests
   prove the rules.
3. **One line on the wire carries the whole feature**, and only two tests look at it.
