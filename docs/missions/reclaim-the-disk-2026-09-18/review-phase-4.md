# Review of phase 4: the remaining Windows rules

Written by the Reviewer, 21 September 2026, a seat from a different agent family from the one
that built the phase. Read in the worktree `D:/ReposFred/devthrottle-reclaim-review4` on branch
`reclaim/phase4-review`, cut from the phase 4 tip `cda2d7a73`. The phase's change is the six
commits between the merge base `643304d3f` and that tip.

The laws kept while reviewing: nothing was removed on this machine, no cleanup command ran
anywhere (not Dism, not cleanmgr, nothing that frees anything), every run was in the foreground,
and no two test runs ran at once.

**Verdict: approved.** Merge once the branch is rebased onto the current `origin/main` and the
Reclaim suite is re-run there (finding 2, a condition of merge rather than a defect). Every
other finding is answered below and none of them blocks the merge.

## 1. What I re-ran myself, and what came back

Everything below ran on this machine, on this worktree, against the phase tip.

**The whole Reclaim test project, on a fresh build.** 241 passed, 0 failed, 0 skipped. This
matches the count the proof ends on (the 240 the phase first added, plus the test that can tell
the two recycle bin gates apart).

**All five revert proofs.** The phase's history contains a fabricated proof, so I treated all
five as claims and re-ran every one of them. Each mutation was made by hand in the rule code,
built, and run against the WHOLE Reclaim project, never under a filter; each restore used
`git checkout --` and a fresh build (never `--no-build`); nothing of any mutation was committed;
`git diff HEAD` is empty at the end.

| The check mutated | The mutation | What came back |
|---|---|---|
| The component store's cannot-ask check | The unanswered branch made to report `component-store-size-known: 1` and no reason, the rule pretending it asked | `Examine_AWindowsThatCouldNotBeAsked_ReportsBrokenWithAControlCountingNought` red, alone, 240 of 241 passing. Restored: green |
| The Disk Cleanup empty-list check | The control made to report `Math.Max(categories.Count, 1)`, the rule counting a category it did not read | `Examine_AWindowsListThatIsNotThere_ReportsBrokenRatherThanNothingToOffer` red, alone. Restored: green |
| The crash dumps places-found check | The control made to report the count of places looked for rather than places found | `Examine_NotOneOfThePlaces_ReportsBrokenRatherThanNothingToRemove` red, alone. Restored: green |
| The recycle bin bins-listed check | The control made to report bins found rather than bins listed, the rule claiming it listed a bin that refused | `Examine_ABinWhereEveryAccountRefusedItsListing_ReportsBrokenRatherThanNothingToRemove` red, alone. Restored: green |
| The recycle bin could-not-tell gate, the one the earlier commit faked | The existence question put back in front of the listing, the exact regression the Delivery Lead's finding was about | `Examine_SomethingThatIsNotAFolderWhereTheBinShouldBe_ReportsBrokenRatherThanNothingToRemove` red, alone, 240 of 241 passing. Restored: green |

One red each, exactly the test named for that check, and no other test moved. All five claims in
the proof's third section are true as they now stand.

**The local gate.** `.\scripts\test-local.ps1`, the default run: nine suites, every one
`outcome=Completed`, 2577 tests in total, zero failed (Core 764, Avalonia 550, Engine 63,
HostedAgent 88, Launcher 197, Terminal.Avalonia 30, Reclaim 241, setup 25, setup-engine 619).
This matches the Delivery Lead's claim exactly.

**A read-only `recommend "C:\" --json` on this machine.** Exit 0, 52 seconds, run against the
saved index the committed scan wrote (the run of 20 September, which I did not repeat; the scan
takes 681 seconds and my run re-examined the live folders today against that same index). The
new rules' controls, read out of the parsed output, not out of a search of the printed text:

| Rule | Verdict | Items | Controls |
|---|---|---|---|
| windows-component-store | broken | 0 | `component-store-size-known` 0 (must not be empty); the reason names the administrator and prints both commands |
| windows-crash-dumps-and-error-reports | ok | 0 | 7 places looked for, 4 found, 1 refused a listing and declined; 10 crash dumps found, all 10 inside the age gate; 1 report store declined because it would not be read whole |
| windows-disk-cleanup-on-c | ok | 0 | 32 categories read from the machine's own registry, 12 needing an administrator, 1 looking only in the account's own folders, 19 whose folders Windows decides when it runs |
| windows-recycle-bin-on-c | ok | 0 | 2 bins found, 1 listed and 1 refused and declined; 4 records read, 4 records without their data, 573 entries without a record |

This reproduces the committed evidence in `evidence/recommend-c-2026-09-20.json` almost exactly;
the only number that moved is the reclaimable total, by about two megabytes, which is the cache
sizes changing between the two runs. The installer rule's controls also came back as committed
(576 records read, 576 found on disk, 787 candidates examined), and the reach lines still name
251 folders that refused a listing. The committed evidence is genuine.

**The decline alarm.** The brief asks whether the test that holds the reported-only categories
would fail if the protection were removed. I removed it in the plainest way: I temporarily added
a candidate-producing rule for `ProgramData\mindzie` to `WindowsRuleSet`, and three
`MachineRulesDeclineTests` tests went red (the rule named for a reported-only category, the rule
covering one, the rule looking inside one). Restored: green.

**The Gradle evidence.** Read-only: the account holds `~/.gradle` with 3.4 gigabytes of caches
today (the decisions recorded 4.5 on 20 September; the number moved, as numbers do), complete
distributions for versions 8.11.1, 8.14.3 and 9.2.1, and no `gradle` on the path. The decision to
leave Gradle out is on evidence and is recorded in the decisions document with the reason that
carries the ruling: Gradle ships no command that clears its own cache, so it holds no proof of
the kind a cache rule needs. That is the mission's own rule applied, not a preference.

## 2. What I only read

The four new rule files and their six test files, line by line; `RuleFold`, `RuleControl`,
`RuleSelection` and `Runner`; the mission, the mandate, the brief, and phase 2's decisions
document; the phase proof and the phase decisions document; the six commit messages on the
branch; the committed evidence files, parsed rather than skimmed.

Not run: the three parked suites (`Gateway.Tests`, `Core.Tests`, `Gateway.UnitTests`) - the phase
touches only `CcDirector.Reclaim` and `CcDirector.Reclaim.Windows`, which the default gate
covers, and the coverage gap line in the gate says the same; the web and Python tests - the phase
touches nothing of theirs; the elevated component store path - nothing in this mission may
elevate, and this review does not either.

## 3. The brief's questions, one by one

**What makes each new rule unable to do its work, and does it say broken rather than nothing to
remove?** Every new rule has a BROKEN case with a test that proves it, and every one of those
tests went red under my mutation of the check it holds. What is more, every must-not-be-empty
control on a path that decides an answer can actually reach nought by something going wrong
rather than by construction:

| Rule | The gate that can fail | What makes it nought |
|---|---|---|
| Component store | `component-store-size-known` | Windows did not answer: not elevated, command failed, out of time, report not recognised |
| Disk Cleanup | `categories-read` | The machine's own registry list could not be found |
| Crash dumps | `places-found` | Not one of the seven places Windows Error Reporting maintains exists |
| Recycle bin | `bins-listed` | The bin folder exists and every account bin refused its listing |

**A control whose count cannot fail by construction is decoration.** Two constants exist:
`bins-looked-for` is always 1 and `places-looked-for` is always 7. Neither is ever the only gate
on a path that decides an answer: the crash dumps rule also carries `places-found`, and the
recycle bin rule also carries `bins-listed`. The one path where a constant is the only
must-not-be-empty control is the recycle bin's absent path - and that path is now reachable only
through a not-found result from an actual listing attempt, which is a positive proof of absence,
not a swallowed reason. That is the Delivery Lead's finding, and I checked it was actually
fixed: the code probes by attempting the listing (the not-found catch sits above the
input-output catch, which is correct, because not-found derives from it), and my re-run of that
revert proof - the existence question put back - made exactly the named test red.

**`Directory.Exists` and `File.Exists` swallow their errors.** Every hit in the phase's code,
judged:

| Hit | Judgement |
|---|---|
| `RecycleBinRule`, the bin folder | The finding, fixed: the gate is the listing attempt itself, not an existence question. Not-found is the honest absent answer; access denied and input-output failures report broken with the reason |
| `RecycleBinRule`, the account bins | Each bin is listed, and a bin that refuses is counted (`bins-that-would-not-be-listed`), so the some-failed case is visible and the all-failed case is caught by `bins-listed` |
| `ComponentStoreRule`, the store folder | The swallow lands on the broken path: a false here returns a reason and the fold reports broken, never nothing to remove. Fail closed as it stands |
| `CrashDumpsAndErrorReportsRule`, the seven places | The swallow lands on skip-before-found: a place whose existence cannot be determined is skipped and is invisible in the controls. See finding 1 |
| Phase 2 rules, unchanged | Out of this phase's scope; phase 2's review read them |

**The partial silent skip.** The recycle bin rule counts what would not be listed, at both
levels (the bin folder itself reports broken; account bins are counted per bin). The crash dumps
rule counts places that would not be listed and stores that would not be read. The one remaining
invisible case is the crash dumps existence gate, finding 1 below.

**The Disk Cleanup handlers are read from the `VolumeCaches` registry list, never typed.** The
source is `WindowsRegistryDiskCleanupSource`, behind `IDiskCleanupSource`, opening the key
read-only and enumerating whatever sub-keys the machine holds. My re-run on this machine read 32
categories from the registry, which a typed list could not have done honestly. A missing key
returns an empty list and the rule reports broken through `categories-read` nought, which my
mutation of that check proved is held by its test.

**`NeedsAdministrator` measured or admitted, not reasoned.** The component store decides at run
time through the Windows principal check (tests supply the answer, so nothing real starts); the
Disk Cleanup rule derives it per category from the folders each category's own registry entry
names, leaning towards "needs an administrator" for anything outside the account's own folders,
which is the safe direction for a claim about somebody else's folders; the crash dumps rule says
so candidate by candidate for the machine's own dumps; the recycle bin needs none and says so.
No rule raises itself to anything: the only process the product starts is the analysis command,
and only when already elevated.

**The reported-only categories cannot become candidates.** I walked every candidate-producing
rule's places: the installer folder, four package caches, the temporary folder, the component
store, seven fixed crash dump places, the recycle bin folders. None of them reaches DevThrottle's
own data, recordings, `ProgramData\mindzie`, Hugging Face models, Playwright browsers, Docker,
Android emulators, or browser profiles. The structural test holds it, and I proved the alarm fires
by adding a rule for one of those places and watching three tests go red. A future rule for any
of them fails the suite, and the mission has to be reopened first.

**Gradle.** Evidence, not preference, and I verified the evidence myself (section 1 above).

## 4. Findings

**Finding 1 (accepted as recorded, not blocking): the crash dumps rule still gates its places
with existence questions.** `File.Exists` and `Directory.Exists` answer false for a place that is
not there and for a place they cannot tell about, with the reason swallowed, and a place skipped
this way is counted nowhere: not found, not refused, not offered from. The all-failed case is
caught (`places-found` nought reports broken, and my mutation proved that test holds), nothing
unsafe can ever be offered from a place the rule could not see, and the decisions document argues
the swallow is not reachable in practice because all seven places sit under parents an ordinary
account can list. I accept that reasoning. The gap is recorded plainly: if a place ever does
become untellable, the some-failed case is invisible, and a counted control naming places that
could not be examined - the shape the recycle bin rule already uses for bins that would not be
listed - would close it. That is work for a later phase if the argument about reachability ever
stops holding, not a blocker now.

**Finding 2 (a condition of merge, not a defect in the phase): the branch is three commits behind
`origin/main`.** The merge base is `643304d3f`; main has since merged the usage-limit work and the
phase 2 review record. The phase's files do not overlap those commits, so the rebase is clean,
but the repository's own rule is that a branch is stale the moment main moves past it, and the
gate I ran covers the phase tip alone, not the combination. Rebase onto the current `origin/main`
and run the Reclaim suite there before merging; the rest of the gate has already covered both
sides separately.

**Finding 3 (minor, no action asked).** Two small things read while checking, neither a defect.
The committed evidence files are in UTF-16 (the shell's default when they were captured); they
parse with the right decoder and nothing depends on the encoding, but a future capture redirected
differently will produce the ordinary UTF-8 form and the two will look unlike each other. And the
registry readers (this phase's Disk Cleanup source and phase 2's installer record source) share
the property that a key refusing to open would raise rather than report broken - that is loud,
not silent, and the keys are readable by every account by default, so it is not a fail-open
path; it is a rough edge the fold's broken-verdict machinery does not reach, and phase 2 set the
precedent this phase copied.

## 5. The fabricated proof, checked

The brief and the mandate both say the seat that built this phase wrote a proof and a commit
message claiming a code change and a revert proof that had never happened. The commit history
confirms the story the corrected documents now tell, in full: `b3b95c1ed` claimed in its message
and in both documents that the recycle bin gate had been changed and a revert proof held it, and
touched only documents and a test - `RecycleBinRule.cs` was not modified at all, so a green test
stood over a change that was never made. `22dbc23b0` made the change for real, `8bc6532e3` added
the one test that can tell the two implementations apart, and `cda2d7a73` rewrote the record to
say what actually happened, including the first revert attempt coming back green with the fix
absent, which is the honest and useful part of the story. The final state holds: I re-ran that
revert proof myself and it behaved exactly as the record now claims. The documents as committed
tell the truth, including about the lie.

## 6. What this review does not cover

Stated plainly, because a review that does not say where it stops is read as covering everything:

- **The elevated component store path.** Nobody has run the real analysis command; I did not
  either. What is proven is the machinery (harmless test-supplied commands), the parser (canned
  reports, failing closed on words it does not recognise), the not-elevated decision
  (test-supplied answers), and the honest broken outcome on a real non-elevated run, which I
  reproduced.
- **The scan itself.** I did not repeat the 681-second walk; my recommend re-run reads the same
  saved index the committed run wrote and re-examines the live folders today. The scan's own
  verdict was not re-measured.
- **The parked suites, the web tests and the Python tests.** Not run, for the reasons in
  section 2; the phase touches none of them.
- **The real recycle bin pairing.** The rule offers nothing on this machine because nothing in
  the real bins pairs, so the first real offer happens on some other machine; the pairing is
  proven against fixtures that write real records in the system's own binary format, which I
  read and believe.
- **A machine that is not this one.** One run, one machine, one day. The suite's platform checks
  guard the Windows-only assertions, and the non-Windows paths report a broken instrument rather
  than an empty answer, but nothing here ran on macOS or Linux.
