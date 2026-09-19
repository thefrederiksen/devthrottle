# Phase 3 plan: removal, with a holding folder

Written by the Tech Lead for phase 3, 19 September 2026, before any code was written. The mandate for
this phase is `mandate-phase-3.md` and the mission document's section 5, "Removal is a move, and it is
late" and "The refusal list". Where this plan and the mandate disagree, the mandate wins.

**The law that binds this phase above every other: nothing on the owner's machine is ever removed by
this mission.** Not once, not as a demonstration, not with the apply flag on a folder that looks safe.
Removal is proven only on fixture trees the tests build and destroy. The first real removal is the
owner's, from the finished tool, after the report.

This plan is written against phase 2 as it stands on the branch `reclaim/phase2-rules-and-recommendations`.
Phase 2 merges to main before this phase builds, and this worktree is then brought onto that main. Every
name below that already exists (`RuleFold`, `ReclaimCandidate`, `FixtureTree`, the command line reader)
is read from that phase; everything else is new and named here for the first time.

## 1. What exists, and what this phase adds

Phase 2 ends with: rules that examine one known place each and answer with controls and candidates;
`RuleFold`, which decides in one place whether a rule's answer can be believed; `RuleSelection`, which
splits the machine's rules into the ones that look inside the folder asked about and the ones that do
not; a command line tool with `scan`, `report` and `recommend`; and a test suite of 193 tests in which
no test points at anything on the real machine.

Phase 3 adds four things and touches nothing that exists:

1. `src/CcDirector.Reclaim/Removal/` - the holding folder, its record, and the refusal gate. Nothing
   platform specific in the engine.
2. The `reclaim` command: dry run by default, `--apply` the only way anything moves.
3. The `holding` command with `list`, `restore` and `purge`.
4. The machinery that runs an owner's own cleanup command for a rule whose proof is that the owner has
   one, proven with a harmless command a test supplies. Nothing in a test ever runs a real package
   manager's cleanup command against this machine.

## 2. The command surface

```
cc-cleanup-storage reclaim "<folder>" [--rule <id>] [--apply] [--json]
cc-cleanup-storage holding list "<folder-or-volume>" [--json]
cc-cleanup-storage holding restore <entry-id> --holding-root "<folder>" [--json]
cc-cleanup-storage holding purge --holding-root "<folder>" [--days N] [--apply] [--json]
```

- `reclaim` selects and runs the rules exactly as `recommend` does, folds them through `RuleFold`, and
  then puts every candidate through the refusal gate. `--rule <id>` narrows the run to one rule; a rule
  id that is not among the rules that look inside the folder asked about is an error naming the rules
  that do, never a quiet empty answer.
- Dry run is the default. Without `--apply` the command reports exactly what it would move, having run
  every refusal check for real, and moves nothing. There is no environment variable, no configuration
  file, and no second way to make a dry run move something.
- `--apply` is the only way anything moves or any owner command runs.
- Every command takes `--json`, and every flag applies to the machine-readable output too, as the AXI
  standard requires.
- Purging is its own explicit command and is never part of a removal. `holding purge` is itself a dry run
  by default and needs `--apply` to purge anything.

## 3. What a reclaim run does, in order

1. Select the rules for the folder asked about, through the same `RuleSelection` the recommend command
   uses, and name the ones left out.
2. Run the selected rules and fold each answer through `RuleFold`. A broken rule offers nothing here,
   exactly as it does in a recommendation; there is no second fold and no second meaning for broken.
3. Measure the volume's free space and the candidate bytes, before anything happens.
4. Put every candidate through the refusal gate (section 5). The gate runs in the dry run exactly as it
   runs in an apply: the dry run is not a simulation, and the two can never disagree about what is
   eligible.
5. Dry run: report what would move, what is refused with which refusal, the measured bytes, and the
   plain sentence that no space is freed until holding is purged. Move nothing.
6. Apply: for each eligible item, immediately before its own move, run the refusal gate again (section
   5, "the moment of the move"). Then either move it into holding (proof kinds: a record the system
   keeps, we made it) or run the owner's own cleanup command (proof kind: the owner's own command). An
   item whose proof is the owner's own command is never held, because that command does not offer to
   put anything back - the rule's "what is lost" already says so.
7. Measure the volume's free space and the candidate bytes again, after everything happened, and report
   both numbers as measurements. The report says plainly that a move to holding frees no space, and
   that space is freed only when holding is purged.

A rule that needs an administrator is never raised to one. On a machine where the process is not
elevated, the move is attempted, the file system refuses it, the item is kept, and the refusal names
the reason and the finding's own line that said an administrator was needed. This tool never elevates
itself and never prints a command that elevates it.

## 4. The holding folder and its record

### Where holding lives

One holding root per volume, at `<volume-root>\cc-reclaim-holding`. Removal is a move on the same
volume: a move across volumes is a copy and a delete, which is not what this is. Before any move, the
gate proves the item and the holding root sit on the same volume by asking the volume reader for both
roots; a mismatch is a broken configuration, the item is refused, and the reason says so. A holding
root that cannot be created or written is a refusal for every item that would go into it, never a
fallback location somewhere else.

The engine takes the holding root as a parameter and holds no default of its own. The command line
computes the per-volume default. This is what lets every test keep its holding inside a fixture tree
the test built, so no test ever creates a folder at the root of a real volume.

### The shape of one entry

```
<holding-root>\2026-09-19-3f2a1b9c\
    record.json
    <the moved item, at its own name>
```

The entry folder name is the date of the move and eight hexadecimal characters, so a person can read
the date in a directory listing and no two entries ever share a folder.

### The record

`record.json` holds, for one moved item:

| Field | What it is |
|---|---|
| `original-path` | The absolute path the item came from, in canonical form |
| `name` | The item's own name |
| `bytes` | The bytes measured at the moment of the move |
| `last-written-utc` | The newest write inside it, measured at the moment of the move |
| `rule` | The identifier of the rule that proved it disposable |
| `moved-at-utc` | When the move happened |
| `purge-not-before-utc` | The moved moment plus the holding period, 30 days by default |
| `state` | `held` or `moving` (see below) |

The record is written in the same breath as the move, not as a separate earlier step. The order is:
create the entry folder, write `record.json` with state `moving`, move the item, rewrite the record
with state `held`. A crash between the record and the move leaves an entry whose record says `moving`:

- `holding list` names it as an incomplete entry, in its own list, never silently counted among the
  held and never silently dropped.
- `holding purge` refuses incomplete entries, always. They are not purgeable until they are resolved.
- `holding restore` puts back whatever the entry holds, if the original parent is free; if the original
  path is still occupied by the item itself (the move never happened), restore reports that the item
  is already home and clears the entry. Restore never overwrites anything.

This is the destructive-sweeps rule taken literally: a record that cannot say what happened is a
refusal, not an assumption.

### The three holding commands

- `holding list "<folder-or-volume>"` - every entry in that volume's holding root: entry id, original
  path, bytes, when it moved, when it becomes purgeable, state. Incomplete entries are listed
  separately and named. A holding root that does not exist is `count: 0`, not an error: an empty
  holding is an honest answer. A holding root that exists but cannot be read is an error naming why,
  never an empty list.
- `holding restore <entry-id> --holding-root "<folder>"` - moves one item back to its original path.
  It refuses when the original parent no longer exists, or when something now stands at the original
  path; the reason says so, nothing is overwritten, and the entry stays. Restoring is never a dry run:
  it moves only within holding, back to where the record says the item came from, and it is refused by
  any check it cannot answer.
- `holding purge --holding-root "<folder>" [--days N] [--apply]` - dry run by default; reports exactly
  which entries have passed their holding period. With `--apply`, it removes the entry folders whose
  `purge-not-before-utc` has passed, re-reading each record immediately before removing it: a record
  that cannot be read, or an entry folder that cannot be listed, is kept and named, never assumed. The
  default holding period is 30 days; `--days N` changes it for this call only.

Restore and purge both answer a question with the record; neither re-runs any rule. A restored item is
the item that was moved, at the path it came from, and its record is removed with the entry.

## 5. The ten refusals, checked by one component

One component checks the refusal list: `RefusalGate`, in `src/CcDirector.Reclaim/Removal/`. Nothing
else in the engine, the tool, or any later screen decides whether an item may be removed. The gate is
the single fold, for the same reason `RuleFold` is: a check that each caller remembered to run is a
check the next caller forgets, and the forgetting is invisible.

The gate runs twice for every item that is ever moved: once in the report pass (which is the whole of
a dry run), and once more immediately before that item's own move in an apply run. Between one item's
move and the next item's gate run, the disk has changed; the next item is checked against the disk as
it is at that moment, not as it was when the run started.

### Enumerated, never inferred

For every item the gate returns the outcome of all ten checks, in the mandate's numbered order:

| Outcome | Meaning |
|---|---|
| `passed` | The check ran and did not fire. |
| `refused` | The check ran and fired; this is the refusal, with its reason. |
| `not-reached` | An earlier check fired, so this one was not run. |

The first check that fires stops the gate, and every check after it is reported `not-reached` - so
nobody can later read an unchecked refusal as a passed one. The text output names the fired refusal
and its reason; the machine-readable output carries all ten outcomes for every item, so an agent can
see that a refusal was reached, refused, or never reached. Where a check cannot answer - a path that
will not resolve, a folder that will not be listed, a file that will not open - the answer is refuse,
and the reason says the check could not answer. Every one of those is a test.

The ten checks, in the order they run, and how each answers:

1. **Under a path the storage resolver names as credentials or vault.** The gate reflects over the
   public static methods of `CcStorage` in `CcDirector.Core` and takes every one whose name contains
   "vault", "credential" or "secret", calls it, and treats the returned path and everything under it
   as refused. The list is READ FROM THE RESOLVER, never typed: the day somebody adds a vault method to
   `CcStorage`, every item under it is refused by this check with no change here. The reflection
   reader is itself tested against a stub class the test declares, with a method the test adds, to
   prove the list grows without being touched. The resolver's methods honour the environment variables
   they already read, because the gate calls them at the moment of the check, not at startup.
2. **Inside a git working tree.** The gate walks every ancestor of the item looking for an entry named
   `.git` - a folder, or the file a linked worktree leaves. An item inside a git working tree is refused,
   and the reason points at `cc-worktrees`, which already proves whether work has landed: this tool
   reports and never rules on a working tree.
3. **Under the user's own folders: Documents, Pictures, Videos, Desktop, OneDrive.** Read from the
   system's own resolver, `Environment.GetFolderPath`, which follows the folder redirection a machine
   may have, plus the `OneDrive` environment variable for the OneDrive folder. The absence of the
   OneDrive variable means no OneDrive folder is configured, not that the check is off. Compared in
   canonical form, without regard to letter case on the platforms whose file systems do not
   distinguish it.
4. **Reached through a link or junction.** The item itself, and every ancestor between the folder the
   run was asked about and the item, is asked whether it is a link (`LinkTarget`). One link anywhere
   on the way down is a refusal, because the item stands somewhere other than where the path says.
5. **Younger than its rule's age gate.** Re-measured at the moment of the check, the same way the rule
   measured it: the newest write anywhere inside, not the item's own stamp. The gate asks the owning
   rule to examine again (below), and an item that no longer passes the age gate is refused even
   though it passed when it was recommended.
6. **An open file inside.** Every file inside is asked, the way phase 2's scratch rule asks: held
   exclusively for an instant and given straight back. One file that will not be held is a refusal.
7. **The rule's controls are empty.** A broken rule offers nothing, and nothing it offered before it
   knew it was broken may be acted on. The gate asks the owning rule to examine again, immediately
   before the item's move, and re-folds the fresh answer through `RuleFold`: a fresh answer that is
   broken refuses every one of that rule's items, and the fresh answer's controls are what count, not
   the ones the run started with.
8. **Changed between recommendation and removal.** The gate compares the item as the run recommended
   it (path, bytes, newest write) with the item as the fresh examination just found it. Any difference
   - one byte, one write - is a refusal: the recommendation was about an item that no longer exists.
   The item vanishing entirely between the two moments is the same refusal.
9. **Removal without the explicit apply flag.** This one is a property of the run, not of an item, and
   the gate checks it once per run, first: without `--apply` the run is a dry run, the gate reports
   refusal 9 as fired at the run level, and no item's other checks are skipped - they all still ran
   for real, and their outcomes are reported so the dry run and an apply can never disagree about what
   is eligible. With `--apply` it is reported passed, once, in the same enumeration.
10. **Not canonical after resolution.** The gate resolves the item's path to its final form - on
    Windows the operating system's own final-path answer, which expands the short names (`DOCUME~1`)
    that `Path.GetFullPath` leaves alone; on the other platforms `Path.GetFullPath` is the whole
    answer. If the final form differs from the path the rule reported - a short name, a trailing dot,
    a different separator - the item is refused: the rule examined one path and the gate was about to
    act on another. A path that will not resolve at all is refused here with the reason why, and the
    checks before it are reported `not-reached`, because they could not be run against a path that
    does not exist. The link check (4) runs on the path as spelled before any resolution, so
    resolution can never hide a junction it should have caught.

The fresh examination in checks 5, 7 and 8 means the owning rule runs twice for every item that moves:
once when the run builds its recommendations, and once immediately before that item's own move.
Speed is not a constraint of this mission, and per-item re-examination is what makes "the check that
an item is still disposable is made again at the moment of the move" true rather than asserted.

## 6. What reclaim never does

- Never removes anything on the owner's machine. Not once, not as a demonstration, not with `--apply`
  on a folder that looks safe. The apply flag is exercised only on fixture trees the tests build and
  destroy.
- Never raises itself to administrator, and never prints a command that elevates it. A rule that needs
  an administrator says so in its finding.
- Never runs a real package manager's cleanup command in a test. The machinery that runs an owner's
  own command is proven with a harmless command the test supplies.
- Never removes unattended, on a schedule, or from any surface but this one command.
- Never holds an item cleared by the owner's own cleanup command, never restores such an item, and
  never promises a way back for one. The rule's "what is lost" already said there is none.

## 7. The fixture trees the tests build

Every fixture tree is built by the test that uses it, inside the test's own temporary folder, and
destroyed by it. No test points at anything on the real machine - not the registry, not the real
package caches, not the real temporary folder, not the real vault paths, not a real volume root. The
holding root a test uses is inside the fixture tree, because the engine takes it as a parameter.

The suite extends `FixtureTree` with the builders the refusal tests need. Each builder proves what it
built before it returns, the way the existing ones do.

1. **The standard reclaim tree.** A scratch folder with one of the known names, aged past the gate by
   the same method the phase 2 tests use (the moment the age gates are judged against is given to the
   rule, so a tree built now is judged as if four hundred days had passed); a second folder with a
   name nobody here made; a third that is young. Reclaim on this tree offers exactly the first, and
   the tree is checked afterwards item by item: the offered folder is in holding, everything else is
   where it was, with the same bytes.
2. **The refusal trees.** One small tree per refusal, each built to trigger exactly its own refusal
   and nothing else:
   - a folder under a path the protected-paths substitution names (the test builds its own protected
     list over the fixture tree; the reflection reader has its own test against a stub class);
   - a folder inside a git working tree (the fixture creates an entry named `.git`, which is exactly
     what the check examines; no git installation is needed);
   - a folder under the fixture's own stand-ins for the user's folders (the check takes them as a
     parameter for the same reason the protected paths are a parameter: the real Documents folder is
     on the real machine and no test points at it);
   - a junction that stands where a folder would, pointing at a folder full of bytes the rule
     matched - the item is refused and the target is untouched;
   - a folder aged past the gate at recommendation time, judged against a moment that puts it inside
     the gate at move time;
   - a folder in which the test holds one file open for the length of the check;
   - a run whose rule is given a record source that answers broken at the moment of the move;
   - a candidate the test writes to between the recommendation and the gate run;
   - a run with no `--apply`;
   - a folder the test refers to by its Windows short name, built on Windows only, and the fixture
     throws and names the reason on a volume or platform that does not keep short names, the way the
     cloud placeholder fixture does.
3. **The holding tree.** Reclaim with the apply flag on the standard tree, holding inside the fixture;
   then `holding list` naming the entry with its original path and bytes; then `holding restore`
   returning the item, checked byte for byte; then a purge dry run naming the entry as not yet
   purgeable; then a purge with the apply flag after the holding period (the period is a parameter,
   so the test does not wait thirty days); then the entry gone and the restored item still in place.
   Every step measures the volume's free space and the item's bytes before and after, and the tree is
   checked after each step.
4. **The owner-command tree.** A package cache rule the test constructs with a harmless command the
   test supplies - a command that writes one marker file inside the fixture tree. The dry run reports
   the command and runs nothing. The apply run runs the command, reports the measured bytes before
   and after, holds nothing, and the tree is checked afterwards. The real Windows rule set is never
   constructed with the apply flag in any test; the rules a test runs with the apply flag are built
   by the test with commands the test supplies.
5. **The incomplete-entry tree.** A record left in state `moving` (the test writes it directly), named
   by `holding list`, refused by `holding purge`, and resolved by `holding restore` in both shapes:
   the move happened (the item returns) and the move never happened (the item is already home).

## 8. The numbered refusal tests

Ten refusal tests, each named for what it declines, in the house style of `tools/cc-worktrees`:

1. `Reclaim_AnItemUnderAVaultPathTheResolverNames_IsRefusedEvenWhenARuleMatched`
2. `Reclaim_AnItemInsideAGitWorkingTree_IsRefusedAndPointsAtCcWorktrees`
3. `Reclaim_AnItemUnderTheUsersOwnFolders_IsRefused`
4. `Reclaim_AnItemReachedThroughALinkOrJunction_IsRefused`
5. `Reclaim_AnItemYoungerThanItsRulesAgeGate_IsRefusedAgainAtTheMomentOfTheMove`
6. `Reclaim_AnItemWithAFileOpenInIt_IsRefused`
7. `Reclaim_AnItemOfARuleWhoseControlsAreEmptyAtTheMove_IsRefused`
8. `Reclaim_AnItemThatChangedSinceItWasRecommended_IsRefused`
9. `Reclaim_WithoutTheApplyFlag_MovesNothingEvenWhenEveryOtherCheckPassed`
10. `Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused`

Beside them: one test per refusal for the "cannot answer" shape (an unresolvable path, an unlistable
folder, a file that will not open), one test that the machine-readable output carries all ten outcomes
for a refused item with the not-reached ones marked, one test that a dry run and an apply on the same
tree agree about what is eligible, and the flow tests of section 7.

## 9. The revert proofs, and the commit order that makes them safe

Every refusal test is proven red with its refusal taken out - a refusal with a green test that stays
green when the refusal is deleted is not a refusal, it is a comment. Ten mutations, one per refusal.
The method is phase 2's, followed exactly:

1. **Commit the work first**, so a restore cannot eat it. The refusal code and its tests land in the
   branch before any mutation happens.
2. Delete the refusal by hand (delete the check, or return the not-fired answer - never `if (false)`,
   which turns into a build failure under warnings-as-errors and proves nothing).
3. Rebuild, and run the **whole suite unfiltered** - the tests nobody thought to name are the ones
   that say something nobody knew. Record which tests went red and what each said; the expected
   number is recorded before the run, and a wrong number is investigated, not explained.
4. Restore with `git checkout --`, rebuild, and confirm green. `git diff HEAD` empty at the end.

The revert proofs are recorded in the phase proof document with the test names and the failure text
of each, as the mandate requires: not "the suite went red", but which test, and what it said.

## 10. The proof this phase owes

1. `.\scripts\test-local.ps1` green, with `CcDirector.Reclaim.Tests` in the default list (phase 1 put
   it there).
2. `dotnet test src\CcDirector.Reclaim.Tests` showing every numbered refusal test by name; the list is
   pasted into the proof document.
3. The ten revert proofs of section 9, individually, each with the test name and the failure.
4. The whole flow on a fixture tree - recommend, dry run, apply, `holding list`, `holding restore`,
   `holding purge` - with measured bytes before and after at each step, and the tree checked afterwards
   to prove exactly the right items moved and nothing else did.
5. The failure cases, not one success run: every refusal shown firing on a fixture tree, the
   incomplete-entry shape, the owner-command run with a harmless command, and the dry run that moves
   nothing.

**What the proof does not cover, stated plainly:** the real Windows rule set is never run with the
apply flag anywhere in this phase, so nothing proves that applying the installer rule on an elevated
machine moves real orphans - that is the owner's first run, after the report. Nothing in this phase
runs on macOS or Linux with the apply flag; the Windows final-path resolution has no implementation
elsewhere yet and the tests say so rather than pretending. `holding restore` and `holding purge` are
proven on fixture trees only, as everything is.

## 11. The order of the work

The branch splits into four pull requests, each leaving the suite green, each committed before its
revert mutations run:

1. The engine's removal pieces: the holding entry, the record, the move, and the holding commands,
   with the holding tree tests of section 7. No refusal gate yet; the move is only reachable from
   tests at this point, and every test uses a fixture tree.
2. The refusal gate and the ten numbered refusal tests, with the refusal trees. The gate is wired
   into the move path in the same pull request, so there is never a commit where the move exists
   without the gate.
3. The `reclaim` command: the command line reader, the runner, the dry run, the apply flag, the
   plain sentence about space, and the owner-command machinery with its harmless test command.
4. The proof document, written from runs that were actually made, and the review answers.

A Reviewer from a different agent family reads the phase BEFORE it merges, per the mandate. Every
finding is answered in `review-phase-3-answers.md`, accepted or declined with the reason. Merge is on
local green plus that review, per the repository's rule for landing changes.

## 12. Judgements this plan takes, for the Reviewer to look hardest at

1. **Holding lives at the root of the volume being reclaimed.** `<volume-root>\cc-reclaim-holding` is
   visible in a directory listing, which is deliberate: a folder holding thirty days of the owner's
   disk is not something to hide. The engine takes the root as a parameter; only the command line
   computes the default, so no test ever writes to a real volume root.
2. **The record is written before the move.** A crash between the record and the move leaves an
   incomplete entry that is named, never purged, and resolvable by restore - rather than an item in
   holding that nothing knows about.
3. **Refusal 9 is checked once per run, by the gate, in the same enumeration.** It is a property of the
   caller, not of an item; per-item repetition would tell the reader ten times what one line says,
   and skipping it from the enumeration would leave a nine-item refusal list wearing a ten-item name.
4. **The owning rule re-examines per item at the moment of the move.** This is the honest reading of
   "the rule re-examines, the controls are re-read". It costs a second examination per moved item,
   and speed is not a constraint of this mission.
5. **The key vault file is protected only if the resolver names it.** The mandate says the list is
   read from the resolver, never typed. The storage resolver's methods matching "vault", "credential"
   or "secret" are protected, automatically, including any added later. The account key vault file
   (`keyvault.json` under the storage root) is not named by any matching method of `CcStorage` today -
   it is assembled by `KeyVault` itself - so this plan does not add it by hand, because that is the
   typed list the mandate forbids. If the Reviewer or the Delivery Lead wants it protected, the right
   move is a method on `CcStorage` that names it, and the reflection picks it up. This is recorded
   here so the decision is visible rather than silent.
6. **The git working tree check is the presence of `.git`.** Not a git command: the check must answer
   on a machine where git is not installed, and it must refuse an item whose tree claims to be a
   repository, whatever git would say about it. The refusal points at `cc-worktrees`, which is the
   tool that knows whether work has landed.
7. **`holding restore` is never a dry run.** It moves only within holding, back to a recorded path,
  and is refused by any check it cannot answer. Adding a dry run to restore would suggest restore
   belongs in the same class of danger as purge; it does not, and pretending it does would make the
   dangerous command easier to reach for.
8. **The user-folder list is parameterised in the gate, supplied by the caller.** The command line
   supplies the real folders from the system resolver; tests supply fixture stand-ins. The mission's
   phrase "read from the resolver, never typed" is written about the vault paths, and the user folders
   have their own resolver (`Environment.GetFolderPath`), which the command line reads and the tests
   never need to touch.
