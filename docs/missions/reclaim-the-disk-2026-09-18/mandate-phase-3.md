# Phase 3 mandate: removal, with a holding folder

From the Delivery Lead, 19 September 2026. **This is the dangerous phase.** Phases 1 and 2 could not
delete anything because the code did not exist. This phase writes that code.

A Tech Lead is seated for this phase, as the mission requires, and **the Reviewer reads this phase
BEFORE it merges, not after.**

## Read these first, in this order

1. `docs/missions/reclaim-the-disk-2026-09-18/mission.md`, all of it, twice. Section 5's "Removal is a
   move, and it is late" and "The refusal list" are the mandate for this phase and this document only
   expands them.
2. `mandate-phase-2.md`, `phase-2-decisions.md` and `phase-2-proof.md` - the house shape you extend.
3. `cc-devthrottle skill get devthrottle-method`, and the skill `destructive-sweeps-lean-to-keep`.
4. `tools/cc-worktrees` on main. **42 tests, nearly all named for something the tool declines to do.**
   This is the house model for the whole mission and it is the model for this phase above all.
5. `docs/CodingStyle.md`, `docs/axi-standard.md` and `CLAUDE.md`.

## The law that binds this phase above every other

**Nothing on the owner's machine is ever removed by this mission.** Not once, not as a demonstration,
not with `--apply` on a folder that looks safe. Removal is proven on fixture trees the tests build and
destroy. The method's law 18 forbids a seat to destroy on its own, and the owner granted building, not
deleting. The first real removal is his, from the finished tool, after the report.

If you find yourself about to run the built tool with `--apply` against any path that is not a fixture
tree the tests just built, stop and ask.

## What you build

### 1. Removal is a MOVE, and it is late

- Removal moves an item into a **holding folder on the same volume** and records where it came from.
  A move across volumes is a copy and a delete, which is not what this is.
- **Space is not freed until holding is purged, and the report says so plainly** rather than claiming
  the space early. A caller who removes 27 gigabytes and sees no free space is owed that sentence
  before the fact, not after.
- `holding list`, `holding restore` and `holding purge` manage it. Default holding period 30 days.
  **Purging is its own explicit command** and is never part of a removal.
- **The check that an item is still disposable is made again at the moment of the move**, not only at
  the moment of the recommendation. The rule re-examines, the controls are re-read, and an item that
  has changed since it was recommended is refused.
- Items cleared by an owner's own cleanup command are **not held**, because that command does not
  offer to put anything back. The rule's "what is lost" already says so.

### 2. `reclaim`, and the flag

- `cc-cleanup-storage reclaim "<folder>" [--rule <id>] [--apply] [--json]`
- **Dry run is the default.** Without `--apply` it reports exactly what it WOULD move, having run
  every refusal check for real, and moves nothing. The dry run is not a simulation: every check that
  would refuse an item refuses it in the dry run too, so the dry run and the real run can never
  disagree about what is eligible.
- `--apply` is the only way anything moves. There is no environment variable, no configuration file,
  and no second way.
- It reports **measured bytes before and after**, never an estimate presented as a fact.

### 3. The refusal list - ten refusals, each a numbered test

The tool refuses, **even when a rule matched**:

1. anything under the credential vault, the secrets store, or any path the storage resolver in
   `CcDirector.Core` calls credentials or vault. **The list is READ FROM THE RESOLVER, never typed.**
   A typed list goes stale the day somebody adds a vault path, and the staleness is invisible.
2. anything inside a git working tree. Worktrees belong to `cc-worktrees`, which already proves whether
   work has landed; this tool reports them and points there.
3. anything under the user's Documents, Pictures, Videos, Desktop or OneDrive folders.
4. anything reached through a link or junction.
5. anything younger than its rule's age gate.
6. anything with an open file in it.
7. anything when the rule's controls are empty - a broken rule offers nothing, and nothing it offered
   before it knew it was broken may be acted on.
8. anything when the item changed between recommendation and removal.
9. any removal without the explicit apply flag.
10. any path that is not canonical after resolution.

**Every refusal is a numbered test, named for what it declines, and every refusal test is proven to
fail when the refusal is taken out.** That is the proof this phase owes and it is not optional: a
refusal with a green test that stays green when the refusal is deleted is not a refusal, it is a
comment. Follow the commit order in `phase-2-proof.md` section 6 - commit first, then mutate, so a
restore cannot eat the work.

### 4. Refusals are enumerated, never inferred

The refusal list is checked for every item, by one component, and the result says which refusal fired
and why. A refusal that is skipped because an earlier one already fired is still reported as not
reached, so nobody can later read an unchecked refusal as a passed one.

**Lean to keep.** Where a check cannot answer - a path that will not resolve, a file that will not
open, a folder that will not be listed - the answer is refuse. Every one of those is a test.

## The proof you owe

1. The mission's check, run and passing: `.\scripts\test-local.ps1` green with
   `CcDirector.Reclaim.Tests` in the default list.
2. `dotnet test src\CcDirector.Reclaim.Tests` **showing every numbered refusal test by name**. The
   mission's section 7 asks for this by name; paste the list.
3. **Every refusal test proven red with its refusal removed.** Ten of them, individually, each with
   the test name and the failure. Not "the suite went red" - which test, and what it said.
4. The whole flow on a fixture tree: recommend, dry run, apply, `holding list`, `holding restore`,
   `holding purge`, with **measured bytes before and after at each step**, and the tree checked
   afterwards to prove exactly the right items moved and nothing else did.
5. **The failure cases, not one success run.** A QA report that shows only the happy path is not a QA
   report.

## What phase 3 must NOT contain

- No removal anywhere on the owner's machine, at any point, for any reason.
- Nothing that raises itself to administrator. A rule that needs one says so and prints the command.
- Nothing that runs a real package manager's cleanup command during a test. The machinery that runs an
  owner's own command is proven with a harmless command the test supplies; pointing a test at
  `npm cache clean` would clear the real cache on this machine.
- No unattended removal, no scheduled removal, no removal from any surface but this command.

## How it ends

Local gate green, a Reviewer from a different agent family reads it **before** merge, every finding
answered in `review-phase-3-answers.md` accepted or declined with the reason, then merge on local
green plus that review.
