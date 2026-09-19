# Phase 2 review: rules and recommendations

Written by the Reviewer, 19 September 2026, from a different agent family than the seat that built
the phase. The phase under review is branch `reclaim/phase2-rules-and-recommendations` at commit
`eef8fee04`, three commits past `origin/main` at `59ae695a3`. The author built under the owner's
override recorded in `handover-delivery-lead-2.md`; this review is the mechanism that override does
not touch.

**Verdict: not approved as it stands.** One finding must be answered first - the fold can still be
failed open by a rule written later, which is the one thing this phase exists to make impossible.
Four smaller corrections to the record are listed with it. Everything else the mandate and the
mission ask of this phase was verified independently and held, including every revert proof, the
read-only run on the real machine, and the absence of removal code. If the finding is fixed and the
record corrected, the phase can merge on a fix round; I will re-read only the delta.

A finding here is not a suspicion. Each one below says what breaks and why it must change, and the
first one was demonstrated by running the code, not by reading it.

## Scope

**What I read.** The whole phase: `git diff origin/main...HEAD`, all 37 files. The mission document
(section 5 twice, as instructed), the phase 2 mandate, the decisions document and the proof document,
all read before the code and none trusted as evidence. The rule contract
(`IReclaimRule`, `RuleFold`, `RuleFinding`, `ProofKind`, `RuleControl`, `RuleSelection`,
`RecommendationReport`), the three Windows rules and their record source, the command line tool's
`Runner`, `CommandLine`, `JsonShapes` and `MachineRules` changes, the help page and the tool
registration, and every test file the phase adds.

**What I ran, all on my own build.**

- `dotnet test src\CcDirector.Reclaim.Tests` on a build the run made itself: **191 passed, 0 failed,
  0 skipped**, matching the proof document.
- All four revert proofs from the proof document, each by breaking the fix by hand, rebuilding with
  a full build, seeing the named tests red, restoring with `git checkout --`, rebuilding with a full
  build again - never `--no-build` on a restore - and seeing them green. The working tree was clean
  at the end and the final suite run was 191 green.
- The mission's own check on the real machine, read-only: `cc-cleanup-storage recommend C:\ --json`
  against the saved scan the author's run left on this machine. **Exit code 0. Parsed
  machine-readable output: verdict ok, the installer rule's three controls 576, 576 and 787, every
  one declared as one that must not be empty and every one greater than zero, and the unseen-gap
  line present.** The numbers match the committed evidence file for file. Nothing was removed,
  moved or changed on the machine by this review.
- The local gate `.\scripts\test-local.ps1`. Every suite the phase touches passed, including
  `CcDirector.Reclaim.Tests` at 191. One test failed in `cc-director-setup-engine.Tests`
  (`ProcessRunnerTests.Run_ProcessExceedingTimeout_IsKilledAndReturnsTimeoutFailure`), a
  process-timeout test in a project the phase does not touch - the diff contains no setup engine
  file. It failed once under the load of the parallel gate run and passed twice afterwards, once
  alone and once with its whole suite at 614 green. It is a timing flake, not a phase defect, and
  the phase cannot have caused it.

**What I could not reach.**

- Nothing ran on macOS or Linux. The proof document says the same, and the engine turns a platform
  with no rules into a broken report rather than an answer of nothing to remove; that path has a
  test but has never run on the other platforms.
- `WindowsRegistryInstallerRecordSource` has no test, which the proof admits. Its reading on this
  machine (576 referenced packages, 362 product records, 216 patch records) corroborates the
  design's independent script, but its behaviour against a damaged registry is unproven.
- The scan run of `C:\` (exit 0, 1409 seconds) I did not repeat; see finding 2 for why its evidence
  matters anyway.
- The parked suites were not run. The phase touches no file they cover: the one registration change
  outside the Reclaim projects is the tool's description in `tools/registry.json`, and no test
  anywhere asserts on that description or on this tool's name (searched the whole tree).

## The checks, in the order the mission cares about them

### 1. A rule that cannot do its work reports broken and offers nothing

Held, and held hard. `RuleFold.BrokenReasonFor` is the single place the verdict is decided, and the
four ways the same failure arrives all land on it: the rule says it cannot run, the rule counted
nothing at all, a control the rule declared load-bearing is nought, or the rule's own answer
disowns what it collected. The consequence is folded in the same place: a broken rule's candidates
are emptied before any reader sees them, so nothing a broken rule gathered is ever a
recommendation.

The revert proofs, each reproduced by me with a full rebuild on both sides:

| Break | Red tests I saw | The proof claimed |
|---|---|---|
| `empty.Count == 0` changed to `>= 0` | 5, the same five by name | 5 |
| The no-controls branch made unreachable | 1: `Fold_ARuleThatCountedNothingAtAll_ReportsBroken` | 1 |
| `Candidates = broken ? [] : answer.Candidates` unwrapped | 3 | 2 |
| The rules-not-run naming line removed | 1: `Build_RulesLeftOutBecauseTheyLookElsewhere_AreNamedWithWhereTheyLook` | 1 |

Every restore rebuilt with a full build and ran green; the tree was clean at the end.

One gap remains, and it is finding 1 below.

### 2. No removal code of any kind

Held. I swept the three projects the mandate names - `src/CcDirector.Reclaim`,
`src/CcDirector.Reclaim.Windows`, `tools/cc-cleanup-storage` - for every delete, move, copy, create,
recycle, holding, process start, shell, elevation and registry write there is, in code and in
comments, including anything unreachable or behind a flag. The only writes anywhere in the engine
are the phase 1 index writer saving the scan itself (`ScanIndexStore`), which is what the `report`
and `recommend` commands read. No process is started by anything in these projects: the rules print
the commands the owner would run (`CommandToRun` is a string that is rendered, never executed), and
nothing raises itself to administrator - the installer rule says an administrator is needed and
prints what the owner runs. Every registry key is opened `writable: false`. The word "holding"
appears only in sentences that say it arrives in phase 3.

### 3. Exactly three proofs, and the sixteen names

Held. `ProofKind` holds exactly three values; every rule returns one of them; `RuleFinding.ProofWords`
throws for anything else, so a fourth proof cannot be printed even by accident. No rule matches by
path pattern from another product, and nothing is imported from the two public rule collections.

The sixteen scratch names were checked against `origin/main`, not against the worktree. For each of
the sixteen, every place in the repository that creates the name was found and read: **every
creation site is inside a test project.** The sweep covered all file types, not only C#, and the one
hit outside a test project is a research session transcript that mentions a name in passing and
creates nothing.

The bare name `cc-director` is deliberately absent, and the reason given is real: product code
creates a folder by that name (`BrainLog` in `CcDirector.AgentBrain` builds it under the application
data folder, and the state root the Avalonia application renders from carries the same name), so the
name fails the proof that only a test project creates it. A rule that matched it would offer a
running Director's own folder. One sentence in the decisions document overstates this, and is
finding 5.

I also checked the one name that names another agent's data, `codex-sessions-`: the product reads
real rollout files from the user profile (`.codex\sessions`), never from the temporary folder, and
the folders the tests create under this name in the temporary folder are pure fixtures. The name is
created only by `CodexRolloutLocatorTests`.

### 4. The unclassified count and the rules-not-run list

Held. Both are folded once in the engine, and neither can make a smaller answer look like a cleaner
disk:

- A rule that was not run is **named**, with the folder it looks in, in both the text answer and the
  machine-readable one, and asking about a folder no rule looks inside gives verdict broken and exit
  code 1 with every rule named underneath - never exit 0 and "nothing to remove". The revert proof
  for the naming line was reproduced red and green.
- `RuleSelection` runs only the rules whose folder is inside the folder asked about, and the prefix
  trap in the path comparison (`C:\Users\bobby` inside `C:\Users\bob`) has its own test.
- The unclassified number is a subtraction of the rules' bytes from the scan's seen bytes, and every
  rule that ran looks inside the folder that was scanned, so the subtraction means what it says.
  Where the rules measure more than the scan saw - which happens when the scan could not read
  something the rules could - the report says the number "cannot be counted here" instead of
  printing a zero that would read as a measurement, and the machine-readable answer carries
  `rulesSawMoreThanTheScan` as its own field so a reader does not have to parse the sentence.

### 5. The record's honesty

The read-only run's proof rests on the exit code and the parsed machine-readable output, as the
mission requires: I reproduced the check myself and got exit code 0 with a parsed verdict of ok,
the three installer controls greater than zero, and the unseen line, matching the committed
evidence exactly. The committed machine-readable file and the printed text file agree with each
other, and the run's numbers land on the design's hand measurement: 204 offered plus 7 held back by
the age gate is 211, the number the design's own throwaway script measured on 18 September, and 576
referenced packages matches it exactly.

Every touched file is pure ASCII (checked character by character), the new text carries no
abbreviations, and nothing names any assistant, model or vendor - no signature, no trailer, no
attribution, in code, comments, documents or commit messages.

## Findings

### Finding 1 - the fold still accepts a rule that can never report broken

`RuleFold.BrokenReasonFor` refuses a rule that counted nothing at all, with the reason that its
empty answer "cannot be distinguished from a failure and is not accepted as one". The same is true
of a rule that counts only controls it has declared may be empty - and the fold accepts that one.

**The proof.** I built a scratch test (written, run, then deleted; never committed) that folds a
rule reporting one control, `files-examined: 0`, declared may-be-empty, with no candidates. The fold
returned **verdict ok**, with the finished lines reading `verdict: ok` and `items: 0`. That is
"nothing to remove", printed by a rule that has nothing behind its answer. The test passed, which
is the harm.

**Why it must change.** A rule whose every control may be empty is a rule no count can ever alarm,
so it can never report broken, so its empty answer is always believed. That is the one failure this
whole mission exists to prevent, and the fold's own standard, written in its doc comment, is that
"one rule cannot fail closed on its own; they all do, or the next one written will not". Nothing
shipped in this phase exploits the gap - all three rules carry controls that must not be empty -
but phase 4 adds rules and phase 5 turns rules into data refreshed from the Gateway, and the fold is
the single gate a refreshed rule passes through. The mission's own law is that an empty result is a
broken instrument *until proven otherwise*; a rule with no load-bearing control proves nothing, and
the fold currently asks it nothing.

**The fix is small.** In `BrokenReasonFor`, after the no-controls check, treat a rule that reported
no control marked must-not-be-empty the same way as one that reported no controls: it has told us
nothing about whether it worked. Add the test that holds it, in the shape of
`Fold_ARuleThatCountedNothingAtAll_ReportsBroken`. All three shipped rules already satisfy the
requirement, so the change moves nothing that is on the machine today.

I recommend fixing it in this phase, while the fold is the only gate and the change is one line and
one test. I would accept a reasoned decline only if it names what else stops a refreshed rule from
declaring every control optional, and I have found nothing that does.

### Finding 2 - "Both runs are committed in evidence/" is not true

The proof document says, of the scan run and the recommend run: "Both runs are committed in
`evidence/`." Only the recommend run is. The scan run - the one that took 1409 seconds and exited
0 - has no committed evidence anywhere; the evidence folder holds the recommend run's two files and
the two design measurement scripts, and no scan output was ever committed on any branch.

The scan's success is corroborated indirectly - a broken scan is never saved, and the recommend run
read a saved one - and I reproduced the recommend check myself against that saved scan. But the
sentence says committed, and a reader of the record will take it as committed. Correct the sentence
to say what is true: the recommend run's parsed output is committed, and the scan run is
corroborated by the saved index it left and nothing else. Alternatively, commit a scan run's
evidence - but that means running one, and it is a twenty minute walk of the disk.

### Finding 3 - the third revert proof turned three tests red, not two

The proof document says the candidates unwrap made two tests red. It made three:
`Examine_RecordsThatNameOnlyFilesThatAreGone_ReportsBroken` also failed, on my run and on the
numbers (that test asserts the broken rule offers nothing, which is exactly what the unwrap
breaks). The understatement is in the safe direction - more coverage than claimed, not less - but
the record should carry the right number.

### Finding 4 - the proof's test table undercounts one file

The table says `RecommendationBuilderTests` holds 8 tests. The file holds 9. The phase total is
right (56 new tests, 135 plus 56 is the 191 the suite runs), so this is a counting slip in one row
of the table, not a missing test.

### Finding 5 - one clause in the decisions document is not backed by anything

Section 8 says the bare name `cc-director` "appears in the temporary folder". Nothing in the
repository creates a folder by that name directly under the temporary folder, and it is not present
in the temporary folder on this machine. The exclusion itself is right and necessary - product code
creates the name in the application data folder, so it fails the proof - and the rule's own doc
comment and test say it that way, correctly. Correct the clause to the reason that holds: the name
fails the proof because product code creates it too, not because it appears in the temporary
folder.

## What I checked and found clean, stated so it is not read as unchecked

- The help page and the command line reader agree on the `recommend` command: the flags the page
  names are the reader's own lists, and the test that proves it asks the reader, one flag at a time,
  rather than comparing the page with itself. The old test whose "no such command" example was
  `recommend` was correctly re-pointed, with a comment saying why.
- `recommend` never walks the disk: it reads a saved scan, and with none saved it refuses and names
  the `scan` command. Proven by a test.
- The reach lines are carried as a list on the report and spliced into both renderings, so a reworded
  line can no longer leave the recommendations silently without one - the design decision the
  document names, and the code matches it.
- The rules' honesty lines are real: what is lost says "is not known here" where it is not known,
  and how to get it back says plainly that a cleared cache comes back by re-download and that
  nothing is held for it.
- The age gates are what the mission rules: 30 days for installer packages, 7 days for scratch
  folders, none for caches, with the scratch rule judging on the newest write anywhere inside a
  folder and asking about open files only after the age gate.
- Gradle's absence is reasoned from the mission's own one idea, and the reasoning is in the code and
  the decisions document, not just in a commit message.

## What this review does not cover

The fix round, when it comes: I will read the delta against this document's findings and nothing
else needs re-reading. The removal phase (phase 3) is not reviewed here and is not begun here - the
code that would do it does not exist, which I verified by sweep rather than by taking the mandate's
word.
