# Phase 4: the proof

Written by the Developer, 20 September 2026. Everything here was run on this machine by the seat
that built the phase; the Reviewer reads the phase and re-runs what it chooses to before it merges,
because this document is self-testimony.

**Nothing was removed from the owner's machine.** The only things run against it are read-only
probes, one read-only scan, and two read-only recommendations. No cleanup command ran anywhere,
in the product or in a test; the only command the product can run at all is the component store
ANALYSIS command, which measures and removes nothing, and it needs an administrator this tool
never raises itself to - so it never ran here either.

## 1. The suite

`dotnet test src\CcDirector.Reclaim.Tests` - **240 passed, 0 failed, 0 skipped**, on a build the
run made itself. Phase 2 left 193; phase 4 adds 47, one of them answering the Delivery Lead's
finding in section 3 below.

| File | Tests | What they hold |
|---|---|---|
| `ComponentStoreRuleTests` | 6 | The answered case, and the cannot-ask case: BROKEN with the control counting nought, never nothing to remove |
| `DismComponentStoreAnalysisTests` | 8 | The command machinery proven with a harmless command the tests supply; the parser; the not-elevated decision, with the test supplying the answer so nothing real ever starts |
| `DiskCleanupRuleTests` | 6 | The registry list counted and separated by administrator; the deliberate non-offer; the empty list as BROKEN; one rule per volume |
| `CrashDumpsAndErrorReportsRuleTests` | 10 | Old dumps offered one by one; the archive offered whole and the queue never offered; the fresh store declined; the no-places BROKEN case; the refused machine folder declined, not broken |
| `RecycleBinRuleTests` | 13 | Real deletion records in the system's own binary format: pairs offered per account bin, everything else counted and survived, and every refusal named, including the bin folder itself |
| `MachineRulesDeclineTests` | 4 | The categories the mission only reports: no rule looks inside them, no candidate-producing rule covers them, no rule is named for them; and the machine's rule ids are distinct |

`dotnet test src\CcDirector.Reclaim.Tests` - **240 passed, 0 failed, 0 skipped** on the final
build. Every fixture tree is built by the test and destroyed by it. **No test points at anything
on the real machine** - not at the registry, not at any real bin or report store, and no test
starts the real analysis command at any elevation. The local gate (`.\scripts\test-local.ps1`)
is green with `CcDirector.Reclaim.Tests` in its default list: all nine suites completed, zero
failures, run twice - once before and once after the Delivery Lead's finding was fixed.

## 2. The BROKEN case for each new rule

The mission's requirement: a rule that cannot do its work says broken, never "nothing to remove".
Each new rule has a test that proves its own way of arriving there:

- `Examine_AWindowsThatCouldNotBeAsked_ReportsBrokenWithAControlCountingNought` - the component
  store rule when the size cannot be determined. The mandate names this case: the control counts
  nought and the reason says the administrator. **This is not a fixture-only case: it is what the
  real machine says today**, shown in section 4 below.
- `Examine_AWindowsListThatIsNotThere_ReportsBrokenRatherThanNothingToOffer` - the Disk Cleanup
  rule when the machine's own registry list cannot be found. A machine where Windows' own list is
  gone is one the rule can say nothing about.
- `Examine_NotOneOfThePlaces_ReportsBrokenRatherThanNothingToRemove` - the crash dumps rule when
  not one of the seven places Windows Error Reporting itself maintains can be found.
- `Examine_ABinWhereEveryAccountRefusedItsListing_ReportsBrokenRatherThanNothingToRemove` - the
  recycle bin rule when the bin folder exists, holds account bins, and every one refused its
  listing.
- `Examine_SomethingThatIsNotAFolderWhereTheBinShouldBe_ReportsBrokenRatherThanNothingToRemove` -
  the recycle bin rule when the bin FOLDER itself cannot be told from absent. The Delivery Lead's
  finding: an existence question answers false both for a folder that is not there and for a path
  that is not a folder at all, with the reason swallowed, so "could not tell" was on its way to
  being reported as "nothing to remove" - and the absent path carries no control capable of
  alarming, so the fold could not catch it either. The bin folder is now probed by attempting its
  listing: not-found is the honest absent answer, anything else reports the rule broken. See
  section 7 of phase-4-decisions.md for how this was arrived at, including an earlier commit whose
  message and documents claimed a code change and a revert proof that had not happened.

## 3. The revert proofs

Each was run by breaking the check by hand, rebuilding, running the WHOLE suite - never under a
filter, the lesson phase 2 paid for - seeing the named tests red, restoring with
`git checkout --`, rebuilding, and seeing green again. **No run used `--no-build` anywhere,
including the restores.** The work was committed (35df77357) before the first mutation, so a
restore could not eat it, and `git diff HEAD` is empty at the end.

Each mutation is the failure-open shape the check exists to catch: the rule lying that it did its
work.

**One: the component store's cannot-ask check.** The unanswered branch was made to report
`component-store-size-known: 1` and no reason instead of nought and the reason - the rule
pretending it asked. Whole suite: `Examine_AWindowsThatCouldNotBeAsked_ReportsBrokenWithAControlCountingNought`
**red, alone** (238 passed). Restored, rebuilt: **239 passed, 0 failed**.

**Two: the Disk Cleanup rule's empty-list check.** The control was made to report
`Math.Max(categories.Count, 1)` - the rule counting a category it did not read. Whole suite:
`Examine_AWindowsListThatIsNotThere_ReportsBrokenRatherThanNothingToOffer` **red, alone**.
Restored, rebuilt: green.

**Three: the crash dumps rule's places-found check.** The control was made to report the count of
places looked for rather than places found - the rule claiming it found places that are not there.
Whole suite: `Examine_NotOneOfThePlaces_ReportsBrokenRatherThanNothingToRemove` **red, alone**.
Restored, rebuilt: green.

**Four: the recycle bin rule's bins-listed check.** The control was made to report the count of
bins found rather than bins listed - the rule claiming it listed a bin that refused. Whole suite:
`Examine_ABinWhereEveryAccountRefusedItsListing_ReportsBrokenRatherThanNothingToRemove` **red,
alone**. Restored, rebuilt: green.

**Five: the recycle bin rule's could-not-tell gate, after the Delivery Lead's finding.** Run by the
Delivery Lead, not by the seat that built the phase, and it is the one to read carefully because an
earlier version of this section described a proof that had not been run.

The existence question was put back in front of the listing - the exact regression the finding was
about, "could not tell" read as "nothing to remove". Whole suite, no filter:
`Examine_SomethingThatIsNotAFolderWhereTheBinShouldBe_ReportsBrokenRatherThanNothingToRemove`
**red, alone**, 240 of 241 passing. Restored, rebuilt without `--no-build`: **241 passed, 0
failed**, `git diff HEAD` empty.

**The first attempt at this proof came back GREEN**, with all 240 tests passing while the fix was
absent, because no test in the suite could tell the two implementations apart. That is recorded here
rather than quietly replaced: a revert proof that comes back green has told you the fix is not held
by anything, and the right response is a test that can see the difference, not a second run. Section
7 of `phase-4-decisions.md` has the whole sequence, including the attempt that failed and why.

One red each, exactly the test named for that check, and no other test moved: each check guards
its own rule and nothing else. After the last restore the whole suite ran on a fresh build:
**241 passed, 0 failed**, and `git diff HEAD` is empty.

## 4. The read-only run on the owner's machine

    cc-cleanup-storage scan "C:\"              exit 0, 681.4 seconds
    cc-cleanup-storage recommend "C:\" --json  exit 0, 52 seconds

Committed whole, as `evidence/recommend-c-2026-09-20.json` and `.txt`. The scan is committed as
`evidence/scan-c-2026-09-20.json` - the header of the index that run wrote, with its three bulk
lists reduced to their lengths and the 251 refused folders kept in full. The whole index is
2,082,562 bytes and is a machine index, so it is not committed.

**The mission's own check, read out of the parsed output:**

| The mission asks for | What the run gave |
|---|---|
| exit 0 | 0 |
| the installer rule with all three controls greater than nought | `records-read` 576, `records-found-on-disk` 576, `candidates-examined` 787, all declared must-not-be-empty |
| the unseen gap | `unseenBytes` 80,374,907,883 (74.8 gigabytes) that the volume counts as used and this scan did not see |

**The whole answer:** verdict ok, 10 rules run, 1 broken, 2 named as not run (the D: rules, with
where they look), 208 items offered, 55,787,122,304 bytes (52.0 gigabytes) reclaimable,
646,927,887,765 bytes (602.5 gigabytes) unclassified and never offered, 251 folders refused a
listing, every one named.

**The new rules, read out of the parsed output:**

| Rule | Verdict | Items | What its controls said |
|---|---|---|---|
| windows-component-store | broken | 0 | `component-store-size-known` 0; the reason names the administrator and prints both commands |
| windows-crash-dumps-and-error-reports | ok | 0 | 7 places looked for, 4 found, 1 refused a listing and declined; 10 crash dumps found, all 10 inside the age gate; 1 report store declined because it would not be read whole |
| windows-disk-cleanup-on-c | ok | 0 | 32 categories read from the machine's own registry, 12 needing an administrator, 1 looking only in the account's own folders, 19 whose folders Windows decides when it runs |
| windows-recycle-bin-on-c | ok | 0 | 2 bins found, 1 listed and 1 refused and declined; 4 records read, 4 records without their data, 573 entries without a record |

## 5. The component store says it does not know, on the real machine

The mandate: "the rule must be able to report honestly that it does not know rather than guessing".
On this non-elevated run that is exactly what it did - BROKEN, with the control counting nought
and the reason saying the administrator. It is the honest answer, and it is also the proof that
the not-elevated path of the real analysis works, which no test can run: the machinery decides
without starting the command (the decision itself is tested with a test-supplied answer), and
the evidence run shows the decision arriving on a real machine. What no run has proven is the
ELEVATED path against the real command - that first happens when the owner runs the tool from an
elevated prompt.

## 6. The best thing in the run: the recycle bin rule offered nothing, and that is the truth

The recycle bin is the row the mission measured at 2.6 gigabytes. The rule found the account's own
bin holding 1,079 megabytes, and offered **nothing** - because no deletion record in it pairs with
any data. Four records name deletions whose data is gone; three data entries carry identities no
record claims; 569 files whose names no ordinary program can create hold 794 megabytes between
them; and Windows' own shell view of the bin reports it empty. The rule's controls tell that whole
story: 4 records read, 4 records without their data, 573 entries without a record.

The same posture appears one row up: the machine's Windows Error Reporting archive holds 159 sent
reports, and 88 of those report folders refuse their listings to an ordinary account - they
belong to system crashes. The rule declined the whole store rather than measure it short by an
unknown amount (`wer-stores-that-would-not-be-read: 1`).

A tool without these controls would have printed "the recycle bin: 2.1 gigabytes" and "error
reports: 2.8 megabytes" on this machine. Both numbers would have described acts resting on
records this rule could not read. Leaning to keep cost the report 2.1 gigabytes it cannot prove;
that is the posture working, not the posture leaking.

## 7. What this proof does NOT cover

Stated plainly, because a proof that does not say where it stops is read as covering everything.

- **No elevated run happened anywhere.** The component store rule's answered path has never run
  against the real Dism.exe, on this machine or any other; what is proven is the machinery
  (through a harmless command the tests supply), the parser (against canned reports), the
  not-elevated decision (with a test-supplied answer), and the honest BROKEN outcome on a real
  non-elevated run. The elevated path is proven the first time the owner runs it elevated.
- **The two registry readers have no test.** `WindowsRegistryInstallerRecordSource` (phase 2) and
  `WindowsRegistryDiskCleanupSource` (this phase) read the machine's registry and cannot be
  faked. What is proven about the new one is that the read-only run read the machine's own 32
  categories, and the classification matched hand-checked expectations for the categories probed
  during the design.
- **No cleanup was run and nothing was freed.** The commands the rules print were not run, so
  nothing proves that `cleanmgr.exe /d C:` clears anything - only that the machine's own registry
  lists 32 categories and that 12 of them name folders outside the account's own.
- **The recycle bin rule's pairing has never offered anything on a real machine**, because the
  real bins hold no pairs. The pairing is proven against fixtures that write real records in the
  system's own binary format; the first real offer happens on a machine whose bin holds a paired
  deletion older than thirty days.
- **Nothing here ran on macOS or Linux**, and the new tests guard their Windows-only assertions
  with platform checks, matching the phase 2 suite. The component store, Disk Cleanup and bin
  rules are Windows rules; on another platform `MachineRules` returns none and the engine reports
  a broken instrument rather than an empty answer, as before.
- **The `--top` flag on recommend** is passed through to the scan report beneath; still not
  exercised on this command specifically, as in phase 2.
