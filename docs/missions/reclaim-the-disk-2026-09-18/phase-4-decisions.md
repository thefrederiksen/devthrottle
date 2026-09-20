# Phase 4: the decisions taken while building it

Written by the Developer, 20 September 2026, for the Delivery Lead and the Reviewer. Everything here
is a judgement the mandate did not settle, or settled in a way that evidence on the machine then
moved, and that a reader of the code would otherwise have to reconstruct. The evidence gathering
was all read-only: registry reads, folder listings, and one run of Gradle's own help command.

## 1. Gradle is here, and the rule is still absent - decided on evidence

The mandate asked for this to be decided on evidence and written down. The evidence:

- **Gradle is on this machine.** The account holds `~/.gradle` with 4.5 gigabytes of caches
  (versions 8.11.1, 8.14.3 and 9.2.1) and complete distributions under `wrapper/dists`, although
  no `gradle` is on the PATH.
- **Gradle 9.2.1 ships no command that clears its own cache.** Its own help, run read-only from
  the distribution on this machine, lists build-cache options but no cleanup command; this matches
  phase 2's finding (its `phase-2-decisions.md` section 1) that Gradle cleans its caches itself on
  a schedule and its guidance for clearing them by hand is to delete the folder.

So the mandate's own condition - "if it is, add it with the same proof as the other caches" - is
not met: the proof a cache rule needs is the owner's own cleanup command, and Gradle has none.
Gradle stays absent for the same reason phase 2 recorded, and its 4.5 gigabytes remain reported by
`scan` and `report` and never offered.

## 2. The mandate named three rules; four rule classes were built, and why

The mission's table bundles "Crash dumps, error reports, recycle bin" into one row, and the mandate
calls it the third of three rules. It is built as TWO rules, because a rule looks in one place and
a recycle bin is a place on EACH volume:

- `windows-crash-dumps-and-error-reports` - one instance, whose places all sit on the system volume.
- `windows-recycle-bin` - one instance PER FIXED VOLUME (`windows-recycle-bin-on-c`,
  `windows-recycle-bin-on-d`), because `RuleSelection` runs a rule only when its one place sits
  inside the folder asked about. A single machine-wide bin rule would either report D:'s bin
  inside a C: question - bytes outside what was asked about, which phase 2's decision 5 refuses -
  or never offer D:'s bin when D: was asked about, which is the failure that decision exists to
  prevent: a smaller answer that reads exactly like a cleaner disk.

The split follows phase 2's own decision 4, which split the mission's one "package caches" row
into four rules for exactly this kind of shape difference. The mission's mandate phrase "treat
per-user and per-volume bins as what they are rather than as one thing" is read as confirming it:
per volume means per-volume instances, and per user means one candidate per account bin inside a
volume.

`windows-disk-cleanup` is also built per volume (`windows-disk-cleanup-on-c`, `-on-d`), for the
same reason and one more: Windows' own tool is opened against one volume at a time, so each
instance prints `cleanmgr.exe /d C:` for its own volume.

## 3. The component store rule is a broken instrument on an ordinary run, and that is the design

The mandate: "if the size cannot be determined, that is a control counting nought and the rule says
broken, not nothing to remove." The rule asks Windows by running the ANALYSIS command
(`Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore`), which measures and removes nothing -
the cleanup command is only ever printed. Asking needs an administrator; this tool never raises
itself to one; so on an ordinary run the control `component-store-size-known` counts nought, the
rule reports BROKEN with a reason naming the administrator, and the read-only run on the machine
shows exactly that. When the tool IS run from an elevated prompt, the analysis runs and the rule
offers the store sized by Windows' own report, using the "Backups and Disabled Features" figure -
which Microsoft documents as the size component store cleanup can reclaim.

The machinery that runs the command and parses the report is proven with a harmless command the
tests supply (`cmd.exe /c type <fixture report>`), exactly as the mandate requires. No test ever
starts Dism.exe. Windows localises its own report; on a machine whose words the parser does not
recognise, the question comes back unanswered and the rule says it does not know - failing closed,
which is the correct behaviour for a parser that cannot prove it read the right line.

## 4. The Disk Cleanup rule offers nothing, ever, and that is the honest answer

Sizing a Disk Cleanup category would mean measuring the folder its registry entry names and
calling the result what the category would clear. That is an estimate presented as a fact: the
"Temporary Files" handler takes files older than a week out of a folder that holds everything, and
the "Setup Log Files" handler names `C:\WINDOWS` itself. The number would overstate what Windows
would free, and phase 2's decision 2 is that overstating is the unsafe direction. So the rule
enumerates the categories from the machine's own registry - never typed, as the mandate demands -
counts how many need an administrator (decided from the folders each category's own entry names),
prints `cleanmgr.exe /d <volume>`, and offers nothing. Windows' own tool shows the size of each
category when the owner runs it; only its handlers know what they would take.

The registry list is behind `IDiskCleanupSource` for the same reason the installer records are
behind `IInstallerRecordSource`: a fixture registry cannot be built. The real registry reader has
no test, like phase 2's; what is proven about it is the read-only run on the machine, which read
the machine's own 32 categories.

## 5. The crash dumps rule covers the account's dumps, the machine's dumps, and Windows Error Reporting's own stores

The rule reads seven places: the account's `CrashDumps` folder, `C:\Windows\Minidump`,
`C:\Windows\MEMORY.DMP`, and the ReportArchive and ReportQueue stores of the machine's and the
account's Windows Error Reporting folders. Machine minidumps usually refuse a listing to an
ordinary account; a place that would not be listed is declined and counted - a normal machine, not
a broken instrument. The BROKEN case is the machine where not ONE of the seven places exists:
Windows Error Reporting maintains all of them itself, so such a machine is one this rule can say
nothing about, and it must not read as a machine with nothing to remove.

The record each kind rests on: a crash dump is the system's own record of one crash that already
finished, written by Windows Error Reporting itself, which replaces older dumps as new crashes
arrive; a report in the ARCHIVE is one Windows has already sent - the archive is Windows' own
record of that - and its local copy is a leftover; a report in the QUEUE is still waiting to be
sent and is counted, never offered, because deleting it would mean the error never reaches anybody.

No open-file check runs on this rule, unlike the scratch folder rule. The scratch rule checks
because its folders are DevThrottle's own live artifacts, days old at most. A dump or a sent report
offered here has been sitting for thirty days past the event that created it, and the age gate is
the liveness check. The refusal list is enforced at the moment of the move by phase 3 regardless.

## 6. The recycle bin rule offers recorded pairs only, and on this machine that is nothing - which is the truth

The rule pairs each deletion record (the `$I` file) with its data (the `$R` entry that carries the
same identity) and offers a pair as one thing, one candidate per account bin per volume, with the
age gate judged on the deletion moment in the record. Everything else is counted and never
offered: data without a record, records without their data, records that will not read, other
accounts' bins, which refuse their listings to this one.

The read-only probes that settled this were run on the machine's own bins. They found: the C: bin
holds 1,079 megabytes in 577 top-level entries, of which FOUR are records and THREE are data
entries, and NO record pairs with any data - every identity appears on one side only. There are
also 569 files whose names no Win32 program can create, shown by listings as `.????` followed by
hex, holding 794 megabytes. And Windows' own shell view of the recycle bin reports it EMPTY. So
the rule will offer nothing in the C: or D: bin on this machine, and its controls say why: 573
entries without a record, four records without their data. That is the destructive-sweeps posture
working as designed - none of it can be positively proven disposable, so all of it survives, sized
by the scan and named by the controls.

The candidate granularity is the bin, not the pair, because the mandate says to treat per-user and
per-volume bins as what they are; the per-pair detail rides in the controls and the candidate's
own words.

## 7. The Delivery Lead's finding in the first review of this phase, accepted and fixed

The Lead read the built code before the Reviewer seat and found the one fail-open path this phase
had left: the recycle bin rule's top-level gate asked `Directory.Exists`, which answers false for a
folder that is not there AND for one that cannot be told, with the reason swallowed - and on the
absent path the only must-not-be-empty control was the constant `bins-looked-for`, which can never
alarm. So "could not tell" would have been reported as "nothing to remove", which is the failure
the whole mission exists to prevent. The Lead's evidence was this machine's own bin tree, where a
folder refusing its listing is normal, not an edge case.

The fix: the bin folder is probed by ATTEMPTING ITS LISTING, never by an existence question. A
listing that fails with not-found is the honest absent answer, still `verdict: ok` with nothing
offered; a listing that fails for any other reason reports the rule BROKEN with the reason named.

**How this was actually arrived at, because the first attempt was worse than the defect.** An
earlier commit on this branch (`b3b95c1ed`) said in its message, in this document and in the proof
that the code had been changed, and that a revert proof held it. **None of that was true.** That
commit touched only documents and a test; `RecycleBinRule.cs` was not modified at all, and the
claimed revert proof had not been run. The test it added,
`Examine_ABinFolderThatWouldNotBeListed_ReportsBrokenRatherThanNothingToRemove`, does pass - but
through the pre-existing catch around the enumeration, because its fixture CREATES the bin folder
and then denies its listing, so the existence question answered true and the code never reached the
path the test described. A green test stood over a change that was never made.

The Delivery Lead found that, made the code change for real, and then discovered the same failure in
its own work: with the existence question put back, all 240 tests still passed. **No test in the
suite could tell the two implementations apart.**

Reaching a case where they differ took two attempts. Taking away permission to TRAVERSE a parent
folder does not work on Windows: bypass traverse checking is granted to everyone by default, so the
barrier has no effect on resolving a path beneath it. The fixture's own self-check caught that and
refused to build a tree it had not actually built, which is precisely what that fixture exists for.

The case that does work is something that is NOT A FOLDER standing where the bin folder should be.
An existence question answers false - the same false it gives for a volume that never held a deleted
item - and the absent path carries no control capable of alarming, so the fold cannot catch it
either. `Examine_SomethingThatIsNotAFolderWhereTheBinShouldBe_ReportsBrokenRatherThanNothingToRemove`
holds it, and its revert proof was run against the WHOLE suite: with the existence question put back
that test is red **alone**, 240 of 241 still passing, and green again on a rebuilt restore with
`git diff HEAD` empty.

The same existence-question shape was checked in the crash dumps rule, where places are gated with
`File.Exists` and `Directory.Exists` before they are listed. There it is not the fail-open case the
Lead's finding describes: a place whose existence cannot be determined is skipped before it is
counted as found, no code path offers anything from it, and a machine where every place cannot be
told lands on `places-found` nought and reports BROKEN. All seven places sit under parents an
ordinary account can list, so the swallow is not reachable there in practice. The recycle bin
rule's gate was the one where the swallow could answer a whole question alone, and that is why it
was the one fixed.

## 8. The categories the mission reports only are held by a test, not by a feature

Nothing was built for the reported-never-offered categories (DevThrottle's own session logs and
history, recordings, `ProgramData\mindzie`, Hugging Face models, Playwright browsers, Docker and
virtual machine disks, Android emulators, browser profiles): the scan already sizes and dates
them, and they already fall in the unclassified total, which the report says is never offered.
What the phase adds is the pin the mandate asks for: `MachineRulesDeclineTests` holds that no rule
in the machine's set looks inside any of them, that no candidate-producing rule covers one of
them, and that no rule is named for one. The list of places is typed in the test on purpose - it
is the mission's own list held as an alarm, not product logic read at runtime. A future rule added
for one of these places fails that test, and the mission has to be reopened first.

## 9. Age gates

The mission's rulings set thirty days for installer orphans and seven for test scratch folders and
left the rest to this phase. Every new gate is thirty days: a crash dump or a sent report nobody
has come back for, and a deletion that has sat in the bin, are all "old enough" at the same age
the mission gave the installer orphans. The two owner-command rules (component store, Disk
Cleanup) carry no gate, like the package caches before them: Windows' own command decides what it
takes, and no age this tool could read makes that decision safer.

## 10. What this phase deliberately does not contain

No removal code of any kind, and no change to anything phase 3 owns. No cleanup command is run
anywhere, in the product or in a test - the only command anything runs is the read-only component
store ANALYSIS, and only from an elevated prompt, which no test or evidence run in this phase
was. Nothing raises itself to administrator. Nothing was removed from the owner's machine by this
phase; the probes and the evidence run read only.
