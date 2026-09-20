# Review of phase 3: removal, with a holding folder

Written by the Reviewer seated for phase 3, 19 September 2026, on the branch `reclaim/phase3-review`,
at the phase 3 tip `3726cd607`. The Reviewer runs a different agent family from every seat that
built the phase. The brief is `review-brief-phase-3.md`; the mandate is `mandate-phase-3.md` and
mission section 5; the phase's own record is `phase-3-proof.md`, `phase-3-decisions.md`,
`phase-3-plan.md` and `phase-3-rulings.md`.

## Verdict

**Approved. No blocking findings.**

The refusals are real, not comments. Seven of the thirteen revert proofs were re-run by this seat,
by hand, one at a time, against the whole Reclaim test project with no filter, and every one
reproduced the proof's count and test names exactly. The proof document is an honest record: where
it says something was not run, the thing was not run, and where it says a red was name only, the red
was name only - this seat saw both kinds with its own eyes.

Three observations are recorded below for the answers document. None of them proves a harm that
must change before merge.

## Scope: what this review did and did not reach

Read in full: `RefusalGate.cs`, `ReclaimRunner.cs`, `HoldingStore.cs`, `HoldingRecord` usage,
`CanonicalPath.cs`, `OwnerCommandRunner.cs`, `FolderMeasures.cs`, `VolumeReader.cs`,
`RefusalCheck.cs`, `CcStorage.cs` (the protected paths and their guard), the tool's `Runner.cs` and
`CommandLine.cs`, `MachineRules.cs`, `WindowsRuleSet.cs`, the whole of `ReclaimRefusalTests.cs`,
`ReclaimCommandWiringTests.cs`, `FixtureTree.cs`, `CcStorageProtectedPathsTests.cs`, and the parts of
`RunnerTests.cs` and `HoldingStoreTests.cs` the proof's claims rest on. Read alongside:
`mandate-phase-3.md`, `mission.md` section 5, the plan, the rulings, the decisions and the proof.

Ran, on this machine, in this worktree: the whole Reclaim test project, unfiltered, more than
 twenty times (the baseline, seven mutated runs, seven restore runs, and the probes below); the
protected paths test class three times; one throwaway probe file that was deleted before this
review was committed.

Did not reach, and this approval does not cover:

- **The parked gate** (`.\scripts\test-local.ps1 -Parked`). The Delivery Lead is running it now and
  the machine cannot take two. This phase changes `CcDirector.Core` (`CcStorage`, `KeyVault`), so the
  parked suites genuinely apply, and the phase is not landed until that run is green.
- **The five protected paths tests inside the whole of `Core.Tests`.** This seat ran the one class,
  filtered by name, as the Tech Lead did; the rest of that parked suite belongs to the gate above.
- **macOS and Linux.** Nothing here ran there; see the judgement below.
- **The real Windows rule set with the apply flag against anything it could match.** As the plan
  says, that is the owner's first run, after the report.

## What this seat re-ran, and what each run said

Every mutation was made by deleting the check or replacing the line, never by switching a condition
off; the build was checked for warnings each time (there were none); every run was the whole
project with no filter; every restore was `git checkout --`, a full rebuild, and the whole project
again, never `--no-build`; no mutation was committed; `git status` was read after every restore and
the tree was clean each time. The baseline, before anything was touched: 261 passed.

| Mutation | What was taken out | Red tests, this seat's run | What the run said |
|---|---|---|---|
| 1 | the gate's protected-path check | 2 of 261 | the engine test said the item under a stand-in for a protected path was eligible; the command wiring test said an item under the config folder must never be eligible |
| 4 | the link-or-junction check | 1 of 261 | name only: `Expected: LinkOrJunction, Actual: NotCanonical` - the item through a junction was still refused, by refusal 10 |
| 7 | the empty-controls check | 1 of 261 | name only: `Expected: EmptyControls, Actual: ChangedSinceRecommendation` - the item of a broken rule was still refused, by refusal 8 |
| 9 | the apply-flag branch of the runner, so a dry run takes the move path | 5 of 261 | the dry run moved the item; the whole flow test went red at the dry run; the owner's own command ran in a dry run; the standard-tree apply found nothing to move; the dry run and the apply disagreed |
| 11 | the second check at the move, replaced with the report pass's answer for the same item | 2 of 261 | an item written to after the report pass moved on the stale answer; an item inside an item that had just moved was not checked against the disk as it then was |
| 12 | the command's protected paths, handed to the gate as an empty list | 1 of 261 | an item under the config folder was eligible through the real command |
| 13 | the command's user folders, handed to the gate as an empty list | 1 of 261 | an item under the OneDrive folder was eligible through the real command |

Every count and every test name matches the proof's table. The kinds match too: the proof calls 1,
9, 11, 12 and 13 held and 4 and 7 name only, and that is what the runs above show.

One lesson from a mistake of this seat's own, worth recording because it strengthens the suite:
the first attempt at mutation 11 reset the counter inside the loop, so every item was given the
first item's report answer - a harsher mutation than the proof's. It caught a THIRD test:
`Reclaim_ADryRunAndAnApplyOnTheSameTree_AgreeAboutWhatIsEligible` went red as well, because the
second item moved on an answer that was never its own. The proof's own mutation is the right one to
record (each item given its own stale answer, two red), and the harsher variant shows the suite
watches the move-time check from more directions than the two named tests.

After the mutation 9 run - the one mutation that makes dry runs move - neither `C:\cc-reclaim-holding`
nor `D:\cc-reclaim-holding` existed, checked by hand as the proof says it was.

## Refusal 1, checked by running

This is the refusal that protects the owner's credentials, and the Delivery Lead overruled the
plan's original design for it. The ruling asked for three things, and all three hold.

1. **The staleness guard actually fails when a path member is added and left unclassified.** This
   seat added a probe member to `CcStorage` returning a path under the storage root, ran the
   protected paths test class, and the guard went red naming the probe member in its message. With
   the probe removed the class returned to five green. The guard enumerates every public static
   path member of `CcStorage` (parameterless, returning a string), so the parameterized composers
   (`ToolConfig`, `ConnectionProfile`) cannot name a place the guard has not judged, because they
   compose from members it has.
2. **Every protected path is refused with a fixture stand-in under a rule that matched it, and the
   refusal is named.** The engine test builds one stand-in for EVERY entry of
   `CcStorage.ProtectedPaths()` - not a fixed list - each carrying the resolver's own sentence for
   what it holds, each under a rule that matched an item inside it, and asserts the fired check is
   refusal 1 and that the reason carries both the path and the sentence. Add a ninth protected path
   tomorrow and this test refuses it with no change anywhere in the reclaim code. The command wiring
   test drives the real command, with the storage root pointed into the fixture, and asserts the
   refusal is named with the config folder in the reason. This seat's mutation 1 run is the proof
   both directions: with the gate's check deleted, both went red with the item eligible.
3. **The enumeration covers the four the ruling names and more.** `CcStorage.ProtectedPaths()`
   carries eight entries: the vault, the secrets store, the config folder, the account key vault
   file, both account credential blobs, the automation browser root and the browser connections
   folder. Every entry composes from the live member at the moment of the call, so the environment
   overrides those members honour are honoured, and a test pins that the entry equals what the
   member itself resolves to - a restated path could not pass it. A separate test pins that the
   four the ruling names are present. `KeyVault` now resolves its default through
   `CcStorage.KeyVaultFile()`, so the file and its protected entry cannot drift apart.

## Judgements on the four things the Tech Lead said are not proven

**Refusals 4 and 7 go red on the name only, because refusals 10 and 8 still refuse the same item.
Not blocking.** This seat re-ran both mutations and saw exactly that: with refusal 4 deleted the
junction item was still refused, by refusal 10; with refusal 7 deleted the broken rule's item was
still refused, by refusal 8. The owner does not lose data when either check is missing - what is
lost is the true name of the reason, and the true name is precisely what those two checks still
add. A test that goes red on the name when the refusal is deleted IS the proof a naming check owes.
The proof does not hide this; it explains the mechanism for each, and the decisions document
correctly declined to reorder the checks to make the names come out differently - what the owner is
told would change, and that is a design decision, not a proof task. Refusal 5 was also name only
and CAN stand alone - a rule that keeps offering an item inside its own age gate - and it got its
second test, which this seat's mutation 5 reading of the gate confirms is the only defence in that
shape. Nothing to fix.

**One existing test calls the real command with the apply flag, safe only by rule selection. Not
blocking - the test cannot move a real file on this machine.** The test is
`RunnerTests.Run_EveryAnswerThisToolCanGive_IsPlainAscii`, and the question the brief asks is
whether it could ever move a real file here. It asks the real command to reclaim a fixture tree
root: a folder created moments earlier under the temporary folder, with a fresh unique name,
containing only files the test itself wrote. The command runs a rule only when the rule's own folder
is INSIDE the folder asked about, and no rule's folder can be inside a folder that did not exist
moments before and holds only test content - every real rule looks at a fixed real place (the
Windows installer folder, the package caches, the temporary folder itself, which is a parent of
the fixture, not inside it). So the run always ends broken - "no rule on this machine looks inside
the folder that was asked about" - before any item, any move, or any holding root exists, and a
sibling test proves exactly that broken answer. For this test to move a real file, a rule would
have to be added whose folder is computed to sit inside a freshly created fixture folder, which
would be a defect of that rule and would be caught by its own tests pointing at the real machine.
This seat also checked the one environment interaction that could be imagined against it: the two
wiring tests redirect the temporary folder, and even with that redirection active while the test
built its tree, the scratch rule's folder would be a parent of the fixture root, not inside it -
still no rule selected.

The residual that is real, and that the proof already states plainly: the test passes no holding
root, so if that impossible selection ever happened, holding would be created at the root of the
real volume holding fixture files - a breach of test isolation, not a loss of owner data. The
plan's sentence that the real rule set "is never constructed with the apply flag in any test" is
not literally true, and the Developer said so in the proof rather than hiding it. The observation
for the answers document: give this test a holding root inside its own fixture (the request already
has the field), or give `reclaim` a `--holding-root` flag when the command surface next changes.
One line, worth doing, not worth blocking on.

**Nothing ran on macOS or Linux. Not blocking.** First, the reach: off Windows the machine rule set
is empty (`MachineRules.ForThisMachine` returns an empty list), and the engine turns an empty rule
list into a broken answer, so no removal is reachable on those platforms today with or without the
apply flag - the exposure this gap creates is zero now and arrives with the sibling rule sets the
mission plans for later. Second, the real risk the proof names: on those platforms path resolution
does not follow links, so refusal 4 would be the ONLY defence against an item reached through a
link, and on Windows its red is name only. That is a fact the mandates for those rule sets must
carry, and the proof and the Tech Lead both already say so. It cannot be proven here and now, and
it blocks nothing that merges here.

**The parked gate was not run by the Developer, the Tech Lead, or this seat.** It is the Delivery
Lead's to run, it is running now, and this approval does not stand in for it.

## The other suspicions the brief names

- **Enumerated, never inferred.** `not-reached` really appears and is never collapsed into
  `passed`: the gate assembles all ten outcomes for every item, the text answer names the fired
  refusal with its reason, and the machine-readable answer carries all ten with `not-reached` as
  its own word. Tests pin both surfaces, and the proof's mutation 2 run (reproduced by the
  Developer and the Tech Lead, read against the gate by this seat) shows the enumeration test goes
  red when a check is deleted. The holding root on another volume is its own configuration
  refusal with every check reported not reached, and it has a test.
- **The dry run is not a simulation.** The report pass runs the gate for every candidate in a dry
  run and in an apply alike, with identical options except the apply flag, which alters only the
  ninth check's recorded outcome, never eligibility. This seat tried to make the two disagree the
  only way left - by deleting the apply branch itself (mutation 9) - and the agreement test went
  red the moment the dry run moved. Short of deleting the branch, there is no input that makes them
  disagree about what is eligible.
- **The gate runs again immediately before each move.** Found in the apply loop, proven by this
  seat's mutation 11 run: two tests went red, and a harsher variant of the same mutation was caught
  by a third.
- **Space is not freed until holding is purged, and the report says so plainly.** The sentence is
  in the reclaim answer before any numbers ("a move to holding frees no space... space is freed
  only when holding is purged"), the purge answer carries its own ("purging is the only step that
  frees space"), and the flow test asserts the first sentence while measuring that the whole tree
  GREW by exactly the record's bytes across the apply. The volume's free space is reported as a
  measurement and asserted never, which the decisions document reasons correctly.
- **Purge is its own command and never part of a removal.** `ReclaimRunner` never constructs a
  purge; the reclaim answer offers the purge command as text and nothing else; purge is a dry run
  by default and needs its own apply flag. Nothing calls it implicitly anywhere in the engine or
  the tool.
- **Lean to keep.** Every cannot-answer shape this seat looked for refuses: an ancestor folder
  that will not be listed is a git-check refusal saying the check could not answer; an entry that
  will not open is a link-check refusal saying the same; a folder inside the item that will not be
  listed is answered as in use; a path that will not resolve is refusal 10 with checks 1 to 9
  reported not reached; a holding root that cannot be named is a configuration refusal; a move the
  file system refuses keeps the item and leaves no entry; a restore never overwrites; a purge
  re-reads each record immediately before removing the entry and keeps what it cannot answer. Each
  shape has a test in the suite that passed in this seat's runs.
- **Nothing raises itself to administrator.** No elevation verb, no shell execution, anywhere in
  the engine or the tool. The only process the tool ever starts is the owner's own cleanup command,
  run verbatim, in the rule's own folder.
- **No real cleanup command is ever run by a test.** The real commands (`npm cache clean --force`
  and its kin) appear only as strings in `WindowsRuleSet`, which no test constructs with the apply
  flag against anything it could match; the owner-command machinery is proven with `cmd.exe`
  writing a marker file inside the fixture, a command that fails, and a program that does not
  exist.
- **The two command wiring tests are safe and their isolation claim holds.** Both assert the
  parsed request is a dry run before running it, point the storage root and the temporary folder
  into their own fixture, refuse to go on unless the resolver's answer is inside their own tree
  (so the real vault carried by this machine's environment can never be pointed at), and assert
  afterwards that the default holding root at the volume root exists exactly as it did before.
  Their collection changes process-wide environment variables, and this seat probed the claim that
  it "never runs beside another test" with a throwaway pair of probe collections: a collection
  marked to disable parallelization ran strictly after every other collection finished, in every
  run, on this runner. The claim holds; it rests on the runner's behaviour rather than on the
  attribute's documented meaning alone, which is worth a sentence in the answers document but is
  nothing to fix.

## Observations for the answers document (none blocking)

1. `RunnerTests.Run_EveryAnswerThisToolCanGive_IsPlainAscii` constructs the real rule set with the
   apply flag. It cannot move a real file on this machine (judged above), but it would put holding
   at the root of the real volume if a rule were ever selected inside its fixture. Give it a
   holding root inside its own fixture, or add a `--holding-root` flag to `reclaim` when the command
   surface next changes.
2. The wiring tests' class comment says the collection "never runs beside another test". True on
   this runner, verified by probe; the mechanism is the runner's scheduling of collections that
   disable parallelization, worth stating in the comment so the next reader knows what carries the
   guarantee.
3. Refusal 4 stands alone on platforms whose path resolution does not follow links, and nothing
   here proves it there. The mandates for the sibling rule sets must carry that requirement; the
   proof already records it, and it should not be lost when the proof document is condensed for the
   mission record.

## Laws kept during this review

Nothing was removed from this machine, at any point. Every move this seat caused happened inside a
fixture tree a test built and destroyed; the built tool was never run by this seat at all, with or
without the apply flag. No mutation was committed. No background work was started. The parked gate
was not touched. The tree at the end of this review is the phase 3 tip with exactly one file added:
this one.
