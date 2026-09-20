# Phase 3: the proof

Written by the Developer seated for the phase 3 revert proofs, 19 September 2026, on the branch
`reclaim/phase3-removal-and-holding`. Every number below came out of a run made on that day on the
owner's Windows machine. Nothing here is an intention: where a run was not made, section 8 says so.

**The short of it.** Thirteen mutations were made, one at a time, each against the whole Reclaim test
project. Seven refusals are held outright. Three refusals are held only on their name, because a
later check still refuses the item, and for two of those that is how the gate is built. **Three
mutations left every test green: the check made again immediately before each move, and the two
lists the reclaim command hands the gate.** Tests that can see all three were written, committed, and
shown red under the same mutations. Nothing on this machine was removed, and the built tool was never
run with the apply flag.

## 1. The baseline

`dotnet test src/CcDirector.Reclaim.Tests` on tip `3677958dd`, before anything was touched:

```
Passed!  - Failed:     0, Passed:   253, Skipped:     0, Total:   253
```

That matches the Tech Lead's own measurement. After the tests this task added, the same command on
the committed tree says:

```
Passed!  - Failed:     0, Passed:   261, Skipped:     0, Total:   261
```

Eight tests were added: two that watch the check made again at the move, two that watch what the
command line hands the gate, one in which refusal 5 is the only check that refuses, one for the
second way an item changes, and two failure cases of an owner's own command. The whole flow test was
rewritten in place and is still one test.

## 2. The numbered refusal tests, by name

Pasted from `dotnet test src/CcDirector.Reclaim.Tests --list-tests` on the committed tree. The ten,
in the mandate's order:

```
 1. ReclaimRefusalTests.Reclaim_AnItemUnderAVaultPathTheResolverNames_IsRefusedEvenWhenARuleMatched
 2. ReclaimRefusalTests.Reclaim_AnItemInsideAGitWorkingTree_IsRefusedAndPointsAtCcWorktrees
 3. ReclaimRefusalTests.Reclaim_AnItemUnderTheUsersOwnFolders_IsRefused
 4. ReclaimRefusalTests.Reclaim_AnItemReachedThroughALinkOrJunction_IsRefused
 5. ReclaimRefusalTests.Reclaim_AnItemYoungerThanItsRulesAgeGate_IsRefusedAgainAtTheMomentOfTheMove
 6. ReclaimRefusalTests.Reclaim_AnItemWithAFileOpenInIt_IsRefused
 7. ReclaimRefusalTests.Reclaim_AnItemOfARuleWhoseControlsAreEmptyAtTheMove_IsRefused
 8. ReclaimRefusalTests.Reclaim_AnItemThatChangedSinceItWasRecommended_IsRefused
 9. ReclaimRefusalTests.Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed
10. ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused
```

Beside them, the refusal tests that are not one of the ten:

```
ReclaimRefusalTests.Reclaim_AnItemInsideAWorkingTreeMarkedByAGitFile_IsRefusedToo
ReclaimRefusalTests.Reclaim_AHoldingRootOnADifferentVolume_IsRefusedAsABrokenConfiguration
ReclaimRefusalTests.Reclaim_AnUnresolvablePath_IsRefusedWithEveryCheckBeforeTheTenthNotReached
ReclaimRefusalTests.Reclaim_AnUnlistableFolderOnTheWayToTheItem_IsRefusedBecauseTheCheckCouldNotAnswer
ReclaimRefusalTests.Reclaim_AFolderThatCannotBeAskedAboutItsFiles_IsRefused
ReclaimRefusalTests.RefusalGate_ARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked
ReclaimRefusalTests.Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible
ReclaimRefusalTests.Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas   (new)
ReclaimRefusalTests.Reclaim_AnItemInsideAnItemThatJustMovedInTheSameRun_IsCheckedAgainstTheDiskAsItIsNow            (new)
ReclaimRefusalTests.Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould            (new)
ReclaimRefusalTests.Reclaim_AnItemWrittenToWithoutChangingItsSize_IsRefusedAsChanged                                (new)
ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheRealResolversConfigFolder_IsRefusedByRefusalOne              (new)
ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheOneDriveFolder_IsRefusedByRefusalThree                       (new)
RunnerTests.Run_ReclaimJsonForARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked
```

## 3. How every revert proof was made

The method is the one in `phase-2-proof.md` section 6 and the rulings on this phase, followed without
exception:

1. Everything was committed before the mutation, so a restore could not eat it.
2. The check was DELETED by hand. Never `if (false)`: warnings are errors here, unreachable code
   fails the build, and a build that does not run has said nothing about a test.
3. The expected number of red tests was written down BEFORE the run.
4. The build ran, and then the WHOLE project: `dotnet test src/CcDirector.Reclaim.Tests`. Never a
   filter. No run was piped through anything that buffers it.
5. The file was restored with `git checkout --`, the project was REBUILT, and the whole project ran
   again. Never `--no-build`. `git status` and `git diff HEAD` were read after every restore and
   showed nothing but the untracked `fixbackslashes.py`, which this task never touched.
6. One mutation at a time.

No deletion broke the build, so no build failure is recorded anywhere below as a red test. Five
deletions left a private method with no caller (`FindGitEntry`, `FindLinkOnTheWayDown`, `Measure`,
`PathExists`, and in the tool `TheUsersOwnFolders`); the compiler does not object to that, the build
ran, and nothing further had to be deleted.

**Every mutation was run twice.** First against the 253 tests as they stood, which is how the three
unheld ones were found. Then, after the new tests were committed, all thirteen were run again against
the suite as it now stands, so that every count below is a count of the committed suite. Mutations 1
to 8 and 10 to 13 were re-run at 259 tests; the last two tests (an owner's command that will not
start, and one that fails) were added after that, and mutation 9 - the only one whose path they go
near - was run a third time at 261. Every prediction made before a run matched the run.

The three kinds of red, in the Tech Lead's words:

- **HELD** - the item would have MOVED or been reported ELIGIBLE.
- **NAME ONLY** - the item was still refused, by a different refusal, and the test went red only on
  the name of the refusal.
- **UNHELD** - nothing went red.

## 4. The thirteen revert proofs

### Summary

| # | What was taken out | First run (253 tests) | Kind | Final run | Kind now |
|---|---|---|---|---|---|
| 1 | under a protected path | 1 red | HELD | 2 red of 259 | HELD |
| 2 | inside a git working tree | 5 red | HELD | 5 red of 259 | HELD |
| 3 | under the user's own folders | 1 red | HELD | 2 red of 259 | HELD |
| 4 | through a link or junction | 1 red | NAME ONLY | 1 red of 259 | NAME ONLY |
| 5 | younger than the age gate | 1 red | NAME ONLY | 2 red of 259 | HELD, by a new test |
| 6 | an open file inside | 2 red | HELD | 2 red of 259 | HELD |
| 7 | the rule's controls are empty | 1 red | NAME ONLY | 1 red of 259 | NAME ONLY |
| 8 | changed since the recommendation | 1 red | HELD | 3 red of 259 | HELD |
| 9 | no apply flag | 5 red | HELD | 5 red of 261 | HELD |
| 10 | not canonical after resolution | 1 red | HELD | 1 red of 259 | HELD |
| 11 | the check made again at the move | **0 red** | **UNHELD** | 2 red of 259 | HELD, by new tests |
| 12 | the command's protected paths | **0 red** | **UNHELD** | 1 red of 259 | HELD, by a new test |
| 13 | the command's user folders | **0 red** | **UNHELD** | 1 red of 259 | HELD, by a new test |

Every restore run returned to the full count: 253 of 253 in the first round, 259 of 259 in the second,
261 of 261 after the last.

Mutations 1 to 8 and 10 are in `src/CcDirector.Reclaim/Removal/RefusalGate.cs`, in `Check`. Mutations
9 and 11 are in `src/CcDirector.Reclaim/Removal/ReclaimRunner.cs`, in `Run`. Mutations 12 and 13 are
in `tools/cc-cleanup-storage/src/CcCleanupStorage/Runner.cs`, in `Reclaim`. Test names below are
given without their shared prefix `CcDirector.Reclaim.Tests.`.

### 1. Under a protected path - HELD

Deleted: the ten lines that look the item up in `_options.ProtectedPaths` and set refusal 1.
Expected red: 1, then 2 with the new wiring test.

- `ReclaimRefusalTests.Reclaim_AnItemUnderAVaultPathTheResolverNames_IsRefusedEvenWhenARuleMatched`
  said `Assert.False() Failure. Expected: False, Actual: True` - on `item.Gate.Eligible`. An item under
  a stand-in for a protected path was reported ELIGIBLE.
- `ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheRealResolversConfigFolder_IsRefusedByRefusalOne`
  said `an item under the config folder must never be eligible`.

### 2. Inside a git working tree - HELD

Deleted: the nine-line block that calls `FindGitEntry` and sets refusal 2. Expected red: 5, both runs.

- `ReclaimRefusalTests.Reclaim_AnItemInsideAGitWorkingTree_IsRefusedAndPointsAtCcWorktrees` said
  `Assert.False() Failure. Expected: False, Actual: True` - the item was ELIGIBLE.
- `ReclaimRefusalTests.Reclaim_AnItemInsideAWorkingTreeMarkedByAGitFile_IsRefusedToo` said
  `Expected: GitWorkingTree, Actual: null` - nothing fired at all; the item was ELIGIBLE.
- `ReclaimRefusalTests.RefusalGate_ARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked` said
  `Expected: Refused, Actual: Passed`.
- `RunnerTests.Run_ReclaimJsonForARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked` said
  `Assert.False() Failure. Expected: False, Actual: True` - on `eligible` in the written answer.
- `ReclaimRefusalTests.Reclaim_AnUnlistableFolderOnTheWayToTheItem_IsRefusedBecauseTheCheckCouldNotAnswer`
  said `Expected: GitWorkingTree, Actual: OpenFile`. This one is name only: the folder that would not
  be listed was still refused, by refusal 6.

One thing this run showed that is not a red test.
`Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible` builds an item inside a git working
tree, and it stayed GREEN with refusal 2 deleted, because it asserts only that a dry run and an apply
agree - and with the refusal gone they agreed that the item should move, and moved it, inside the
fixture. That test proves agreement and nothing about any one refusal. It is doing its own job; it
must not be read as a second proof of refusal 2.

### 3. Under the user's own folders - HELD

Deleted: the twelve-line block that looks the item up in `_options.UserFolders` and sets refusal 3.
Expected red: 1, then 2 with the new wiring test.

- `ReclaimRefusalTests.Reclaim_AnItemUnderTheUsersOwnFolders_IsRefused` said
  `Assert.False() Failure. Expected: False, Actual: True` - the item was ELIGIBLE.
- `ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheOneDriveFolder_IsRefusedByRefusalThree` said
  `an item under the user's OneDrive folder must never be eligible`.

### 4. Through a link or junction - NAME ONLY

Deleted: the nine-line block that calls `FindLinkOnTheWayDown` and sets refusal 4. Expected red: 1,
predicted name only before the run. Both runs agreed.

- `ReclaimRefusalTests.Reclaim_AnItemReachedThroughALinkOrJunction_IsRefused` said
  `Expected: LinkOrJunction, Actual: NotCanonical`. Its first assertion, that the item is not
  eligible, PASSED. The item reached through a junction was still refused, by refusal 10.

**Why, and what it means.** On Windows the gate resolves every path to the operating system's own
final path before any check runs, and the final path of anything reached through a junction is, by
definition, somewhere other than where it was spelled. So on Windows refusal 10 always stands behind
refusal 4, and no test can be written on Windows in which refusal 4 is the only thing in the way. What
refusal 4 adds on Windows is the right name and the right sentence - it names the link and what it
points at. **On macOS and Linux that is not true**: there the resolution is `Path.GetFullPath`, which
does not follow links, so refusal 4 would be the only defence. Nothing in this phase runs there. See
section 8.

### 5. Younger than the age gate - NAME ONLY, then HELD by a new test

Deleted: the forty-one-line block that re-judges the age gate and sets refusal 5. Expected red: 1,
predicted name only, as the Tech Lead expected. Measured, not taken on anyone's word:

- `ReclaimRefusalTests.Reclaim_AnItemYoungerThanItsRulesAgeGate_IsRefusedAgainAtTheMomentOfTheMove`
  said `Expected: AgeGate, Actual: ChangedSinceRecommendation`. The item was still refused, by
  refusal 8: the scratch folder rule keeps its own age gate, so at the later moment it no longer
  offers the item at all, and "no longer offered" is refusal 8.

Unlike 4 and 7, this one CAN be the only defence: a rule that goes on offering an item inside its own
age gate. Nothing stops a rule being written that way, and phase 5 turns rules into data. So a test
was added in which the rule does exactly that, and mutation 5 was run again. Expected red: 2.

- `ReclaimRefusalTests.Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould`
  said `an item three days old must not be eligible under a seven day age gate`. The item was
  ELIGIBLE. Refusal 5 is now HELD.
- The numbered test went red on the name again, exactly as before.

### 6. An open file inside - HELD

Deleted: the seven-line block that asks `FolderMeasures.AnythingOpenIn` and sets refusal 6. Expected
red: 2, both runs.

- `ReclaimRefusalTests.Reclaim_AnItemWithAFileOpenInIt_IsRefused` said
  `Assert.False() Failure. Expected: False, Actual: True` - an item with a file held open was ELIGIBLE.
- `ReclaimRefusalTests.Reclaim_AFolderThatCannotBeAskedAboutItsFiles_IsRefused` said the same - an item
  holding a folder that would not be listed was ELIGIBLE.

### 7. The rule's controls are empty at the move - NAME ONLY

Deleted: the seven-line block that reads the fresh finding's verdict and sets refusal 7. Expected
red: 1, predicted name only, as the Tech Lead expected. Both runs agreed.

- `ReclaimRefusalTests.Reclaim_AnItemOfARuleWhoseControlsAreEmptyAtTheMove_IsRefused` said
  `Expected: EmptyControls, Actual: ChangedSinceRecommendation`. Its first assertion, that the item is
  not eligible, PASSED.

**Why, and what it means.** This one is name only by construction, on every platform. The gate folds
the rule's fresh answer through `RuleFold`, and the fold empties a broken rule's offer
(`Candidates = broken ? [] : answer.Candidates`). So the moment a rule is broken at the move, the item
is no longer among what it offers, and refusal 8 refuses it for that reason. Refusal 8 always stands
behind refusal 7, and no test can make refusal 7 the only thing in the way without first breaking the
fold, which has its own tests from phase 2. What refusal 7 adds is the true reason: "the rule could
not do its work" is a different fact from "the item changed", and the owner is owed the true one. No
test was added, because no test could see more than the existing one does.

### 8. Changed since the recommendation - HELD

Deleted: the twenty-nine-line block that compares the recommended item with the fresh one and sets
refusal 8. Expected red: 1, then 3.

- `ReclaimRefusalTests.Reclaim_AnItemThatChangedSinceItWasRecommended_IsRefused` said
  `Assert.False() Failure. Expected: False, Actual: True` - an item that grew from 700 to 800 bytes was
  ELIGIBLE.
- `ReclaimRefusalTests.Reclaim_AnItemWrittenToWithoutChangingItsSize_IsRefusedAsChanged` (new) said the
  same - an item written to again at the same size was ELIGIBLE.
- `ReclaimRefusalTests.Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas`
  (new) said `an item that changed after the report pass must not move on the report pass's answer` -
  the item MOVED.

The check has three branches. The numbered test reaches the bytes branch. The new test reaches the
newest-write branch. The third branch, the item vanishing entirely, is not reached by any test and
cannot be reached on purpose: a test that deletes the item before the check is refused earlier, by
the path failing to resolve (that is `Reclaim_AnUnresolvablePath_...`), so the vanished branch only
fires if the item disappears in the instant between the path resolving and the rule examining. See
section 8.

### 9. No apply flag - HELD

Deleted: the `if (!request.Apply)` branch of `ReclaimRunner.Run`, its `else`, and the closing brace,
so that a run without the apply flag takes the move path. Expected red: 5, all three runs. Every one
of these tests works inside a fixture tree it built, so a dry run that moves things moved only fixture
files.

- `ReclaimRefusalTests.Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed` said
  `Assert.True() Failure. Expected: True, Actual: False` - at the line that asserts the item's file is
  still where it was. **The dry run MOVED the item.**
- `ReclaimRefusalTests.Reclaim_TheWholeHoldingFlow_ListsRestoresAndPurgesWithMeasuredBytesAtEachStep`
  said `Assert.False() Failure. Expected: False, Actual: True` - on `wouldMove.Moved` after the dry run.
- `ReclaimRefusalTests.Reclaim_AnOwnersOwnCommand_RunsWithTheHarmlessCommandTheTestSuppliesAndHoldsNothing`
  said `Assert.False() Failure. Expected: False, Actual: True` - the dry run RAN the owner's command
  (the test's harmless one, which writes a marker file inside the fixture).
- `ReclaimRefusalTests.Reclaim_TheStandardTree_MovesExactlyWhatWasOfferedAndNothingElse` said
  `Assert.Single() Failure: The collection did not contain any matching items` - the dry run had
  already moved the item, so the apply found nothing to move.
- `ReclaimRefusalTests.Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible` said
  `Assert.Equal() Failure: Collections differ`, for the same reason.

The two command line wiring tests, which are dry runs through the real command, stayed green under
this mutation because their item is refused and so had nothing to move. After the run the root of the
volume was checked for a `cc-reclaim-holding` folder; there was none.

### 10. Not canonical after resolution - HELD

Deleted: the eight-line block that compares the resolved final path with the path as reported and
sets refusal 10. Expected red: 1, both runs.

- `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused` said
  `Assert.False() Failure. Expected: False, Actual: True` - an item named by its Windows short name was
  ELIGIBLE.

The other half of refusal 10, a path that will not resolve at all, is a separate `catch` that this
mutation left in place; it is what `Reclaim_AnUnresolvablePath_...` watches, and it stayed green.

### 11. The check made again at the moment of the move - UNHELD, now HELD

Mutation: in the apply loop of `ReclaimRunner.Run`, `var outcome = gate.Check(candidate, rule);` was
replaced with the report pass's answer for the same item (`reportOutcomes[moveIndex++]`). Expected
red: 0, predicted before the run.

**Nothing went red. 253 of 253 passed.** The mandate's sentence "the check that an item is still
disposable is made again at the moment of the move" was true of the code and watched by no test:
every test either hands the gate one moment in time, or runs an apply on a disk nothing changes.

Two tests were written that change the disk between the report pass and a move, inside one apply run,
and committed. Mutation 11 was run again. Expected red: 2.

- `ReclaimRefusalTests.Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas`
  said `an item that changed after the report pass must not move on the report pass's answer`. **The
  item MOVED on a stale answer.** The test runs two real scratch folder rules; the second is wrapped
  so that, when the report pass examines it, a hundred bytes are written into the FIRST rule's item -
  after the report pass called that item eligible and before its own move. With the second check in
  place the item is refused by refusal 8, names 700 and 800 bytes, and stays where it was with the 800
  bytes; the second item moves and is the only entry in holding.
- `ReclaimRefusalTests.Reclaim_AnItemInsideAnItemThatJustMovedInTheSameRun_IsCheckedAgainstTheDiskAsItIsNow`
  said `Assert.False() Failure. Expected: False, Actual: True` - on `Gate.Eligible`. Two offers, the
  second inside the first. Once the first has moved, the second must carry the answer of a check made
  against the disk as it then is (the path no longer resolves), not the report pass's "eligible".

The first of those went red, on its first run, on a line that pinned its own premise (how many times
the rule had examined) rather than on the line that says the item moved. The premise was moved to the
end of the test and the mutation run once more, so that the recorded red says what a missing second
check COSTS before it says how. Both runs are in this task's logs; the one quoted is the later.

### 12. The command's protected paths - UNHELD, now HELD

Mutation: in `Runner.Reclaim`, `ProtectedPaths = CcStorage.ProtectedPaths(),` became
`ProtectedPaths = [],`. Expected red: 0, predicted before the run.

**Nothing went red. 253 of 253 passed.** Refusal 1 is the most important refusal in the tool, the
gate holds it, and the one caller that ships could have handed it an empty list with every test
green. Every refusal test builds the runner's request by hand.

A test was written, committed, and mutation 12 run again. Expected red: 1.

- `ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheRealResolversConfigFolder_IsRefusedByRefusalOne`
  said `an item under the config folder must never be eligible`. **The item was ELIGIBLE.**

How that test works is in section 5.

### 13. The command's user folders - UNHELD, now HELD

Mutation: in `Runner.Reclaim`, `UserFolders = TheUsersOwnFolders(),` became `UserFolders = [],`.
Expected red: 0, predicted before the run.

**Nothing went red. 253 of 253 passed.** A test was written, committed, and mutation 13 run again.
Expected red: 1.

- `ReclaimCommandWiringTests.ReclaimCommand_AnItemUnderTheOneDriveFolder_IsRefusedByRefusalThree` said
  `an item under the user's OneDrive folder must never be eligible`. **The item was ELIGIBLE.**

**What this test does not reach:** it reaches the user folders through the OneDrive variable, which is
the one of the five the command reads from the environment. Documents, Pictures, Videos and Desktop
come from the operating system's own folder resolver, which no environment variable redirects on
Windows, so no test can stand a fixture in for them without touching the real ones. The mutation
empties the whole list and the test sees it; a mutation that dropped only `MyDocuments` from the list
would be seen by nothing. See section 8.

## 5. The two command line tests, and why they are safe

They are the only tests in this project that run the rule set this machine really runs, so how they
stay inside a fixture is worth stating exactly.

- They make the same two calls the tool's entry point makes: `CommandLine.Parse` and then
  `Runner.Run`. The arguments are `reclaim "<fixture>" --json`. **The apply flag is never among them**,
  and the test asserts the parsed request is a dry run before it runs it.
- The storage root is pointed at the fixture with `CC_DIRECTOR_ROOT`, the variable `CcStorage`
  already reads. The protected folder is then ASKED of the resolver (`CcStorage.Config()`), never
  composed by the test, and the test refuses to go on unless the answer is inside its own tree. This
  matters on this machine in particular: a session here also carries `CC_VAULT_PATH`, which points at
  the REAL vault whatever the root says, so the test uses the config folder and never the vault.
- The temporary folder is pointed into the fixture with `TMP` and `TEMP`, so the real scratch folder
  rule looks there and finds the one aged folder the test made. The reclaim command judges age against
  the real clock, so the folder and its file are stamped four hundred days old.
- The OneDrive folder is pointed into the fixture with `OneDrive`, the variable the command reads.
- The command's default holding root is `cc-reclaim-holding` at the root of the real volume, and the
  reclaim command takes no flag to move it. A dry run never creates it. Each test asserts that whether
  that folder exists is the same after the run as before, and it was checked by hand after every
  mutation run in this task, including mutation 9: it does not exist.
- Environment variables belong to the whole process and every fixture tree is made under the
  temporary folder, so these two tests sit in a collection declared with `DisableParallelization`,
  which never runs beside another test. Every variable is put back when the test ends.

## 6. The whole flow on a fixture tree, with measured bytes

### What the test measured before this task, and what it did not

`Reclaim_TheWholeHoldingFlow_ListsRestoresAndPurgesWithMeasuredBytesAtEachStep` as it stood on
`3677958dd`, read against the mandate's item 4:

- It DID assert the entry's recorded bytes (4096) and original path at the holding list, the restored
  file's length, and that the entry folder was gone after the restore and after the purge.
- It did NOT measure the holding root's bytes after the apply, after the restore, or after the purge.
- It did NOT check the rest of the tree after any step - there was no rest of the tree; the item was
  the only thing in it, so "nothing else moved" could not fail.
- It did NOT assert the run's own before and after numbers for the dry run.
- It did NOT exercise the recommend step, and its purge dry run was made INSIDE the holding period, so
  it never showed a purge dry run naming a purgeable entry and leaving it alone.
- It wrote no numbers anywhere, so a proof document could only have quoted intentions.

### What it measures now

The test builds a tree with the item (one file of 4,096 bytes) and two bystanders: a folder beside it
that nobody here made (2,048 bytes) and a file elsewhere in the tree (1,000 bytes). After EVERY step
it measures, with its own instrument - the framework's file enumeration, adding up lengths, owing
nothing to the measuring code under test - every file outside holding with its size, the bytes inside
holding, and the bytes of the whole tree. All three are asserted as exact numbers, and the list of
files outside holding is compared whole, so a bystander that moved, shrank or vanished fails the step
it happened in. Under mutation 9 this test went red at the dry run.

### The numbers, from the run

Pasted from `dotnet test src/CcDirector.Reclaim.Tests --logger "console;verbosity=detailed"` on the
committed tree. Only the fixture's folder name is shortened.

```
0 built: outside-holding-bytes=7144, holding-bytes=0, whole-tree-bytes=7144, files-outside-holding=3
1 recommend: offered=C:\Users\soren\AppData\Local\Temp\<fixture>\temp\cc-director-tests, bytes=4096
1 recommend: outside-holding-bytes=7144, holding-bytes=0, whole-tree-bytes=7144, files-outside-holding=3
2 dry run: candidate-bytes-before=4096, candidate-bytes-after=4096, bytes-moved=0, volume-free-before=179263561728, volume-free-after=179263561728
2 dry run: outside-holding-bytes=7144, holding-bytes=0, whole-tree-bytes=7144, files-outside-holding=3
3 apply: candidate-bytes-before=4096, candidate-bytes-after=0, bytes-moved=4096, record-bytes=517, volume-free-before=179263561728, volume-free-after=179263578112
3 apply: outside-holding-bytes=3048, holding-bytes=4613, whole-tree-bytes=7661, files-outside-holding=2
4 holding list: entries=1, entry-bytes=4096, from=C:\Users\soren\AppData\Local\Temp\<fixture>\temp\cc-director-tests
4 holding list: outside-holding-bytes=3048, holding-bytes=4613, whole-tree-bytes=7661, files-outside-holding=2
5 holding restore: restored=True, to=C:\Users\soren\AppData\Local\Temp\<fixture>\temp\cc-director-tests
5 holding restore: outside-holding-bytes=7144, holding-bytes=0, whole-tree-bytes=7144, files-outside-holding=3
6 apply again: outside-holding-bytes=3048, holding-bytes=4613, whole-tree-bytes=7661, files-outside-holding=2
7 purge with the apply flag inside the holding period: outside-holding-bytes=3048, holding-bytes=4613, whole-tree-bytes=7661, files-outside-holding=2
8 purge dry run after the holding period: outside-holding-bytes=3048, holding-bytes=4613, whole-tree-bytes=7661, files-outside-holding=2
9 holding purge: purged=1, bytes-freed-from-the-tree=4613
9 holding purge: outside-holding-bytes=3048, holding-bytes=0, whole-tree-bytes=3048, files-outside-holding=2
```

Read plainly:

- **A move to holding frees nothing.** The apply takes 4,096 bytes out of the tree outside holding
  (7,144 to 3,048) and the whole tree GROWS, from 7,144 to 7,661 bytes, by exactly the 517-byte
  record. That is the sentence the mandate says the caller is owed before the fact, as a measurement.
- **The restore puts back exactly what was taken.** After it, the tree is byte for byte and file for
  file the tree the test built: 7,144 bytes, three files, holding empty.
- **The purge is the one step that frees space**: 4,613 bytes, the item and its record. A purge with
  the apply flag inside the holding period removes nothing, and a purge dry run after the period names
  the entry and removes nothing.
- **The bystanders never moved.** The 2,048-byte and 1,000-byte files are in every list, at every
  step, at their own sizes.
- **The volume's free space is reported and not asserted.** Between the two readings of the apply run
  it ROSE by 16,384 bytes, on a run that freed nothing: the volume is a live one and something else on
  the machine gave space back in that instant. That is exactly why the test does not assert it, and
  why the tree's own bytes are the measurement that means something here.

The same flow through the TOOL's holding commands - list, restore, purge dry run, purge - is
`RunnerTests.Run_TheWholeHoldingFlowThroughTheTool_ListsRestoresAndPurges`, which was not changed.
The reclaim step itself is never driven through the command with the apply flag; section 8 says why.

## 7. The failure cases

A QA report that shows only the happy path is not a QA report. Every failure case the mandate or the
plan names, with the test that shows it. All of them passed in the final run.

**Each refusal firing on a fixture tree** - the ten numbered tests of section 2, and beside them:

| Test | What it shows |
|---|---|
| `Reclaim_AnItemInsideAWorkingTreeMarkedByAGitFile_IsRefusedToo` | the `.git` FILE a linked worktree leaves refuses, not only the folder |
| `Reclaim_AnUnresolvablePath_IsRefusedWithEveryCheckBeforeTheTenthNotReached` | cannot answer: a path that will not resolve is refused, and checks 1 to 9 are reported not reached |
| `Reclaim_AnUnlistableFolderOnTheWayToTheItem_IsRefusedBecauseTheCheckCouldNotAnswer` | cannot answer: a folder that will not be listed is refused, and the reason says the check could not answer |
| `Reclaim_AFolderThatCannotBeAskedAboutItsFiles_IsRefused` | cannot answer: a folder inside the item that will not be listed is answered as in use |
| `Reclaim_AHoldingRootOnADifferentVolume_IsRefusedAsABrokenConfiguration` | a holding root on another volume refuses every item, with all ten checks not reached |
| `RefusalGate_ARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked` | an unchecked refusal is never reported as a passed one |
| `RunnerTests.Run_ReclaimJsonForARefusedItem_CarriesAllTenOutcomesWithTheNotReachedOnesMarked` | the same, in the written machine-readable answer |
| `Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould` (new) | the gate keeps a rule's age gate even when the rule does not |
| `Reclaim_AnItemWrittenToWithoutChangingItsSize_IsRefusedAsChanged` (new) | a write that leaves the size alone is still a change |
| `Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_IsRefusedAtTheMoveAndStaysWhereItWas` (new) | an item that changes after the report pass does not move |
| `Reclaim_AnItemInsideAnItemThatJustMovedInTheSameRun_IsCheckedAgainstTheDiskAsItIsNow` (new) | the next item is checked against the disk as the last move left it |
| `ReclaimCommandWiringTests` (two, new) | the shipped command hands the gate the real protected paths and the real user folders |

**The dry run that moves nothing**

| Test | What it shows |
|---|---|
| `Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed` | the item stays, no holding root is created, refusal 9 is in the enumeration and in the report |
| `Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible` | the apply moves exactly what the dry run said and refuses exactly what it refused |
| `Reclaim_TheStandardTree_MovesExactlyWhatWasOfferedAndNothingElse` | a folder nobody here made and a young folder are both left, with their bytes |
| `CommandLineTests.Parse_ReclaimWithAFolder_IsADryRunByDefault` | the command line reads a reclaim as a dry run unless told otherwise |
| `CommandLineTests.Parse_HoldingPurgeByDefault_IsADryRun` | the same for purge |
| `HoldingStoreTests.Purge_ByDefault_IsADryRunThatNamesWhatIsNotYetPurgeable` | a purge dry run removes nothing |

**The incomplete entry**

| Test | What it shows |
|---|---|
| `HoldingStoreTests.List_SeparatesCompleteEntriesFromIncompleteOnesAndNamesBoth` | an entry whose record says `moving` is listed apart and named |
| `HoldingStoreTests.Purge_AlwaysRefusesIncompleteEntries_EvenPastTheirPeriod` | a purge refuses it, always |
| `HoldingStoreTests.Restore_AnIncompleteEntryWhoseMoveDidHappen_PutsTheItemBack` | restore resolves it when the move happened |
| `HoldingStoreTests.Restore_AnIncompleteEntryWhoseMoveNeverHappened_ReportsAlreadyHomeAndClearsTheEntry` | and when it never did |

**Restore refused, and nothing overwritten**

| Test | What it shows |
|---|---|
| `HoldingStoreTests.Restore_WhenSomethingNowStandsAtTheOriginalPath_RefusesAndNeverOverwrites` | the newcomer is untouched and the entry stays |
| `RunnerTests.Run_HoldingRestoreOntoAnOccupiedPath_IsRefusedAndOverwritesNothing` | the same through the tool, with its exit code |
| `HoldingStoreTests.Restore_WhenTheOriginalParentIsGone_RefusesAndKeepsTheEntry` | no parent, no restore |
| `HoldingStoreTests.Restore_AnEntryThatIsNotThere_IsRefusedNamingWhy` | an unknown entry id |

**The move and the holding root failing**

| Test | What it shows |
|---|---|
| `HoldingStoreTests.Hold_WhenTheFileSystemRefusesTheMove_KeepsTheItemAndLeavesNoEntry` | the shape of a rule that needs an administrator on a machine that is not elevated: the item is kept |
| `HoldingStoreTests.Hold_WhenTheHoldingRootCannotBeCreated_RefusesAndTheItemStays` | never a fallback location |
| `HoldingStoreTests.Hold_AnItemThatIsNotThereAnyMore_IsRefusedAndNothingIsWritten` | |
| `HoldingStoreTests.List_ARootThatExistsButCannotBeRead_IsAnErrorNamingWhy_NeverAnEmptyList` | an unreadable holding root is an error, never an empty list |
| `HoldingStoreTests.List_ARootThatDoesNotExist_IsCountZeroNotAnError`, `RunnerTests.Run_HoldingListOfAHoldingRootThatIsNotThere_SaysCountZero` | an empty holding is an honest `count: 0` |
| `HoldingStoreTests.Purge_AnEntryWhoseFolderCannotBeRemoved_IsKeptAndNamed` | a purge that cannot remove an entry keeps and names it |

**The owner's own command**

| Test | What it shows |
|---|---|
| `Reclaim_AnOwnersOwnCommand_RunsWithTheHarmlessCommandTheTestSuppliesAndHoldsNothing` | the dry run runs nothing; the apply runs it, holds nothing, creates no holding root |
| `Reclaim_AnOwnersOwnCommandThatWillNotStart_SaysSoAndTouchesNothing` (new) | it did not run, there is no exit code, the reason names the command, nothing is held or touched |
| `Reclaim_AnOwnersOwnCommandThatFails_ReportsItsExitCodeAndHoldsNothing` (new) | exit code 3 is reported as 3, and the bytes after equal the bytes before |

The last two were gaps: the plan names the owner command machinery among the failure cases and only
its success run had a test. Every command in all three is a harmless one the test supplies
(`cmd.exe /c` writing a marker file inside the fixture, `cmd.exe /c "exit 3"`, and a program that does
not exist). No test runs a real package manager's cleanup command.

**The command surface** - `RunnerTests.Run_ReclaimOfAFolderNoRuleLooksInside_EndsOnOneAndNamesWhy`,
`RunnerTests.Run_ReclaimWithARuleThatDoesNotLookInside_IsAUsageErrorNamingIt`, and the
`CommandLineTests.Parse_Reclaim...` and `Parse_Holding...` tests: a reclaim no rule applies to is
broken and says why, an unknown rule id is a usage error naming the rules that do look inside, and a
flag a command does not take is refused.

## 8. What this proof does not cover

Stated plainly, because a proof that does not say where it stops is read as covering everything.

1. **The real Windows rule set is never run with the apply flag against anything it could match.**
   Nothing proves that applying the installer rule on an elevated machine moves real orphans, or that
   any real cache command clears what it says. That is the owner's first run, from the finished tool,
   after the report.
2. **One existing test does call the real command with the apply flag**, and the plan's sentence that
   the real rule set "is never constructed with the apply flag in any test" is not literally true.
   `RunnerTests.Run_EveryAnswerThisToolCanGive_IsPlainAscii` runs
   `Runner.Run(ReclaimRequest(tree.Root, apply: true))`, which builds this machine's rules with the
   apply flag set. It is safe because of rule selection, not because of the flag: no real rule's
   folder sits inside a freshly made fixture tree, so no rule is selected and the run ends as broken
   before any item exists. I did not change that test and did not run the tool myself with the flag.
   The Tech Lead should know the safety there rests on selection.
3. **`.\scripts\test-local.ps1` and the `-Parked` gate were NOT run by this seat.** The brief forbids
   it; the Delivery Lead runs the gate in its own worktree. Every run in this document is
   `dotnet test src/CcDirector.Reclaim.Tests` and nothing else. In particular the five protected path
   tests in `src/CcDirector.Core.Tests/Storage/CcStorageProtectedPathsTests.cs` were not run by this
   seat, because this task did not touch `CcStorage` or anything else in `CcDirector.Core`.
4. **Nothing ran on macOS or Linux.** Every run was on Windows. The two command line tests and the
   short name test throw on any other platform and say why, in the house style of this suite. **On
   macOS and Linux refusal 4 would be the ONLY defence against an item reached through a link**,
   because path resolution there does not follow links - and that is exactly the refusal whose red,
   on Windows, was name only. No test here can show it holds there.
5. **Two refusals whose red was name only, and stay that way: 4 and 7.** With refusal 4 deleted the
   item was refused by refusal 10; with refusal 7 deleted, by refusal 8. On Windows neither can be
   made the only thing in the way (section 4 says why for each). What the tests prove for those two is
   that the right name and the right sentence reach the owner, not that an item would otherwise move.
   Refusal 5 was name only too and now has a test in which it is the only defence.
6. **The reclaim command is never driven end to end with the apply flag**, even on a fixture. Its
   holding root is fixed at the root of the real volume and the command takes no flag to move it, so
   an apply through the command would create a folder at the root of the owner's disk. The apply is
   proven through the engine (`ReclaimRunner.Run`) with holding inside the fixture; the command is
   proven as a dry run, and its holding commands are proven through the tool. The few lines of
   `Runner.Reclaim` that differ between a dry run and an apply - which help lines it offers - are
   watched only by the plain ASCII test.
7. **The user folders other than OneDrive are not reachable by a test.** Documents, Pictures, Videos
   and Desktop come from the operating system's folder resolver. Emptying the whole list is seen
   (mutation 13); dropping one of those four from the list would be seen by nothing.
8. **Only one protected path is driven through the command: the config folder.** The engine test
   stands a fixture in for every one of the eight paths `CcStorage.ProtectedPaths()` names; the
   command test uses one, because in a session `CC_VAULT_PATH` points the vault at the real one and a
   test must never put anything there. That the command passes the WHOLE list rather than part of it
   is read off one line of code, not proven.
9. **Refusal 8's third branch, the item vanishing entirely, has no test** and cannot be given a
   deterministic one (section 4, mutation 8).
10. **The volume's free space is never asserted**, only reported. In the run quoted it rose by 16,384
    bytes across an apply that freed nothing. The bytes of the fixture tree are what is asserted.
11. **The mutations are the thirteen named.** Other lines could be deleted - the same-volume check,
    the catch branches that turn "cannot answer" into a refusal, the record written before the move -
    and this task did not mutate them. Each has a test in section 7; none of those tests has been
    proven red with its code taken out.
