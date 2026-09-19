# Phase 2: the proof

Written by the Delivery Lead, 19 September 2026. Everything here was run on this machine by the seat
that built the phase; a Reviewer from a different agent family reads the phase and re-runs what it
chooses to before it merges, because this document is self-testimony.

**Nothing was removed from the owner's machine.** The only things run against it are a read-only scan
and a read-only recommendation. The code to remove anything does not exist in this phase.

## 1. The suite

`dotnet test src\CcDirector.Reclaim.Tests` - **193 passed, 0 failed, 0 skipped**, on a build the run
made itself. Phase 1 left 135; phase 2 adds 58.

Two of those 58 were added by fix round one, answering the review's finding; section 8 records it.

The new tests, and what each group is for:

| File | Tests | What they hold |
|---|---|---|
| `RuleFoldTests` | 9 | That a rule which could not do its work reports broken and offers nothing |
| `OrphanedInstallerPackagesRuleTests` | 11 | The comparison, the age gate, and four separate ways the record set can fail |
| `PackageCacheRuleTests` | 6 | The whole cache as one item, a cache that is not on the machine, an unreadable folder, a link |
| `TestScratchFoldersRuleTests` | 10 | Mostly what the rule declines: a name that is not ours, a link, a folder too young, a folder with a file open |
| `RuleSelectionTests` | 6 | Which rules apply to the folder asked about, including the folder-name prefix trap |
| `RecommendationBuilderTests` | 9 | No rules at all, a broken scan, one broken rule among good ones, the reach lines |
| `RunnerTests`, added | 4 | The command end to end, and every field a machine reads |
| `CommandLineTests`, added | 3 | The recommend command, its flags, and its refusals |

Every fixture tree is built by the test and destroyed by it. **No test points at anything on the real
machine** - not at the registry, not at the real package cache, not at the real temporary folder.

## 2. The BROKEN case, which the mandate names

Four tests hold it, each for a different way the same failure arrives, because each would otherwise
leave the tool confidently recommending the deletion of the entire Windows package cache:

- `Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing` - the record set is empty.
- `Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken` - the record set loaded but nothing in
  it can be matched, so every file in the folder looks like an orphan.
- `Examine_APackageFolderWithNoPackagesInIt_ReportsBroken` - the other side of the comparison is empty.
- `Fold_ARuleThatCountedNothingAtAll_ReportsBroken` - a rule that reports no controls has said nothing
  about whether it worked, so a rule written later cannot fail open by simply not counting.

And `Fold_ABrokenRule_OffersNothingEvenThoughItCollectedCandidates` holds the consequence: whatever a
rule gathered before it discovered it was broken is not a recommendation and never reaches a reader.

**The revert proof.** The emptiness check in `RuleFold.BrokenReasonFor` was deleted by hand, the
project rebuilt, and the affected tests run: red. The fix was restored, rebuilt, and they ran green.
The exact commands and their output are in section 6.

## 3. The read-only run on the owner's machine - the mission's own check

Section 7 of the mission document sets this check, and says exit code and parsed machine-readable
output decide, never a search of the printed text.

    cc-cleanup-storage scan "C:\"              exit 0, 1406.2 seconds
    cc-cleanup-storage recommend "C:\" --json  exit 0, 116 seconds

**What is committed, exactly.** The recommend run is committed whole, as
`evidence/recommend-c-2026-09-19.json` and `.txt`. The scan run is committed as
`evidence/scan-c-2026-09-19.json`, which is the header of the index that run wrote - what it did, how
long it took, what it saw, and all 245 folders that refused a listing - with its three bulk lists
reduced to their lengths. The whole index is 2,670,455 bytes and is a machine index rather than a
record of a run, so it is not committed.

An earlier draft of this document said both runs were committed when only the recommend run was, and
the file and folder counts below were read off a console that was not kept. The review caught it. The
scan evidence was committed in answer, and every number in the table below now comes from it.

The scan's own measure of itself is 1,406.2 seconds; the 1,409 an earlier draft carried was the whole
process timed from outside, including startup and the index write. Both are true of different things
and the rule's own number is the one quoted.

**The three criteria, read out of the parsed output:**

| The mission asks for | What the run gave |
|---|---|
| exit 0 | 0 |
| the installer rule with all three controls greater than nought | `records-read` 576, `records-found-on-disk` 576, `candidates-examined` 787, and all three are declared as controls that must not be empty |
| the unseen-gap line | `unseen: 77178528063 bytes (71.9 gigabytes) that the volume counts as used and this scan did not see` |

**The whole answer:**

| | |
|---|---|
| verdict | ok |
| rules run | 6, none broken, none left out |
| items offered | 208 |
| reclaimable | 55,755,888,629 bytes (51.9 gigabytes) |
| unclassified, never offered | 640,187,692,748 bytes (596.2 gigabytes) |
| unseen by the scan | 77,178,528,063 bytes (71.9 gigabytes) |
| seen | 648.1 gigabytes in 3,531,656 files and 1,168,877 folders |
| folders that refused a listing | 245, every one named |

**Per rule:**

| Rule | Proof | Items | Bytes |
|---|---|---|---|
| Orphaned Windows installer packages | a system record | 204 | 28,550,125,056 (26.6 gigabytes) |
| The NuGet package caches | its own command | 1 | 11,720,780,078 (10.9 gigabytes) |
| The uv package cache | its own command | 1 | 5,926,169,194 (5.5 gigabytes) |
| The npm package cache | its own command | 1 | 4,832,445,846 (4.5 gigabytes) |
| The pip package cache | its own command | 1 | 4,726,368,455 (4.4 gigabytes) |
| DevThrottle test scratch folders | we made it | 0 | 0 |

## 4. The run lands on the design's hand measurement

The Architect measured the installer folder by hand on 18 September with a throwaway script, which is
in `evidence/installer_orphans.ps1`. The rule was written from the same idea but is not that script.

| | The design, 18 September | This rule, 19 September |
|---|---|---|
| packages Windows still points at | 576 | **576** |
| orphans found | 211 | 204 offered **plus 7 held back by the age gate = 211** |
| folders that refused a listing | 244 | 245 |

**204 plus 7 is 211, exactly.** The seven are the orphans written within the last thirty days, which
the rule declines because one of the 211 measured during the design had been written two days earlier.
That is the age gate doing precisely the job it was put there for, and it is visible as a control
(`orphans-too-young-to-offer: 7`) rather than as a quietly smaller number.

## 5. The best thing in the run is a rule that offered nothing

The test scratch rule offered nought items and nought bytes. Its controls say why:

    names-looked-for                     16
    folders-examined                 27,169
    folders-matching-one-of-our-names  4,464
    matches-too-young-to-offer         4,464
    matches-with-a-file-still-open         0
    matches-that-would-not-be-listed       0

**4,464 folders matched and every single one was inside the seven day age gate.** A tool without
controls would have printed "nothing to remove" here, which is the same answer it would print on a
machine where the name list had been emptied by mistake, or where the temporary folder could not be
read. These six numbers are the difference between "there is nothing to do" and "there is plenty, and
none of it is old enough yet".

## 6. The revert proofs

Each was run by breaking the fix by hand, rebuilding, seeing the named tests red, restoring with
`git checkout --`, rebuilding, and seeing them green. **No run used `--no-build` anywhere, including
the restores.** The work was committed before the first mutation, so a restore could not eat it, and
`git diff HEAD` is empty at the end: every hand revert was undone.

**One: the emptiness check itself.** In `RuleFold.BrokenReasonFor`, `if (empty.Count == 0) return
null;` was changed to `if (empty.Count >= 0) return null;`, so a rule with an empty control is treated
as having nothing to remove. **Five tests red**, across two files, which is the right number because
this one line is what four separate failure shapes all land on:

    Fold_AControlThatMustNotBeEmptyIsNought_ReportsBrokenRatherThanNothingToRemove   FAIL
    Fold_ABrokenRule_OffersNothingEvenThoughItCollectedCandidates                    FAIL
    Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing                             FAIL
    Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken                        FAIL
    Examine_APackageFolderWithNoPackagesInIt_ReportsBroken                           FAIL

Restored and rebuilt: 18 passed, 0 failed.

**Two: the no-controls-at-all check.** The branch that refuses a rule reporting no controls was made
to return null. `Fold_ARuleThatCountedNothingAtAll_ReportsBroken` **red**. This is the one that stops
a rule written later from failing open by simply not counting anything. Restored: green.

**Three: a broken rule keeping what it gathered.** `Candidates = broken ? [] : answer.Candidates` was
changed to `Candidates = answer.Candidates`. **Three tests red**, not the two an earlier draft of this
document claimed:

    Fold_ABrokenRule_OffersNothingEvenThoughItCollectedCandidates      FAIL
    Examine_NoRecordsAtAll_ReportsBrokenAndOffersNothing               FAIL
    Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken          FAIL

So the two halves are independent: one says the verdict must be broken, the other says a broken
verdict must offer nothing. Restored: green.

The wrong number was not a wrong run - it was a filtered one. The original proof ran this revert under
`--filter` with two test names in it, so it could only ever have reported those two, and the third was
invisible rather than absent. The review re-ran it unfiltered and found three; it was re-run unfiltered
again here to confirm before this correction was written. **A revert proof is run against the whole
suite, because the tests you did not think to name are the ones that tell you something you did not
know.**

**Four: rules left out being silently absent.** The line naming the rules that look outside the folder
asked about was deleted. `Build_RulesLeftOutBecauseTheyLookElsewhere_AreNamedWithWhereTheyLook`
**red**. Restored: green.

After the last restore the whole suite ran on a fresh build: **193 passed, 0 failed, 0 skipped**, and
`git diff HEAD` is empty.

## 8. Fix round one: the fifth revert proof

The review found that the fold could still be failed open by a rule written later, which is the one
thing this phase exists to make impossible. A rule that counts nothing at all was refused, but a rule
that counted only things it had declared MAY be empty was not: no count it reports can ever alarm, so
it can never report broken, so its empty answer is always believed. Folded, such a rule answered
`verdict: ok` and `items: 0` - "nothing to remove", said by a rule with nothing behind it.

`RuleFold.BrokenReasonFor` now refuses a rule that declares no control marked must-not-be-empty, in
the same breath as one that declares no controls at all. Two tests hold it:
`Fold_ARuleWhoseEveryControlMayBeEmpty_ReportsBrokenBecauseNoCountCouldEverAlarm`, and
`Fold_ARuleWithOneLoadBearingControlThatCounted_IsOkEvenWhenItOffersNothing` for the other half - the
fold asks for a control that COULD alarm, not for one that did.

**The revert proof.** The check was deleted outright, the project rebuilt, the whole suite run:
`Fold_ARuleWhoseEveryControlMayBeEmpty_ReportsBrokenBecauseNoCountCouldEverAlarm` **red**, alone, and
the other half stayed green. Restored, rebuilt: **193 passed, 0 failed**, `git diff HEAD` empty.

Switching the check off with `if (false)` was tried first and is **not** a revert proof: this
repository treats warnings as errors, so the unreachable code became a build failure, and a build
that does not run has not told you anything about a test. The check was deleted instead.

All six Windows rules declare at least one must-not-be-empty control already, so no rule that exists
today changes its answer. The check is for the rules that do not exist yet - phase 5 turns rules into
data refreshed from the Gateway, and this fold is the single gate a refreshed rule passes through.

## 9. What this proof does NOT cover

Stated plainly, because a proof that does not say where it stops is read as covering everything.

- **Nothing here ran on macOS or Linux.** The Windows rules are the only rule set that exists, and on
  another platform `MachineRules` returns none, which the engine turns into a broken report rather
  than into an answer of nothing to remove. That path has a test; it has never been run on another
  platform.
- **The registry reader has no test.** `WindowsRegistryInstallerRecordSource` is the one piece put
  behind an interface precisely because it cannot be faked, so every test uses a stub. What is proven
  about it is that the real one returned 576 referenced packages, 362 product records and 216 patch
  records on this machine, which the design's independent script also measured as 576.
- **No cache was cleared and no folder was removed**, here or anywhere. The commands the rules print
  were not run, so nothing proves that `npm cache clean --force` frees the 4.5 gigabytes the rule
  measured - only that the cache holds them.
- **The scan took 1,409 seconds**, about ten times slower than the design's own probe over a similar
  tree. It was a Debug build. The mission rules speed out of scope ("fast doesn't really matter
  because we can do this in the background, offline, and slowly"), so this is recorded rather than
  raised - but it is the number phase 5 should design the background scan around, not the probe's 181
  seconds.
- **An elevated scan was not run.** 71.9 gigabytes of the volume remains invisible and 245 folders
  refused a listing; whatever is in them is neither measured nor judged by any of this.
- The `--top` flag on `recommend` is read and passed to the scan report beneath it; no test exercises
  it on this command specifically.
