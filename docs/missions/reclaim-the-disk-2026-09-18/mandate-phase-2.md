# Phase 2 mandate: rules and recommendations

From the Delivery Lead, 19 September 2026. This is the mandate for phase 2 of the Reclaim the Disk
mission. There is no Tech Lead on this phase.

**The Delivery Lead built this phase itself**, under the owner's explicit override recorded in
`handover-delivery-lead-2.md`: it may review code and implement improvements, its clear job being to
finish the mission. That override replaces the method's rule that a Delivery Lead never builds and
never reads diffs, it lives in this mission's record, and it dies with this mission. The review
mechanism the override does NOT touch still stands: this phase is read by a Reviewer running a
different agent, sent by the Delivery Lead, before it merges.

## Read these first, in this order

1. `docs/missions/reclaim-the-disk-2026-09-18/mission.md` in this worktree. It is the mandate above
   this one. Where it and anything else disagree, it wins. Read section 5 twice: "The one idea",
   "Every rule fails closed", and "First Windows rules, in order of measured value".
2. `docs/missions/reclaim-the-disk-2026-09-18/mandate-phase-1.md` and
   `review-phase-1-answers.md` - phase 1 is merged and is the house shape you extend.
3. `cc-devthrottle skill get devthrottle-method` - the section "If you are a Developer" binds you.
4. `docs/CodingStyle.md`, `docs/axi-standard.md` and `CLAUDE.md` in this worktree.

## Your worktree

Yours alone, cut from origin/main after phase 1 merged. Do not work in `D:\ReposFred\devthrottle` -
that shared checkout runs far behind and reading it has already produced reported "facts" that were
fiction. To read shipped code use `git show origin/main:<path>`.

## What you build

### 1. The rule contract, in `src/CcDirector.Reclaim`

Platform-neutral. A rule answers one question: what on this disk can be proven disposable, and by
which of exactly three proofs. **There are three proof kinds and a rule may hold no other.** The
engine implements them; a rule chooses among them and cannot invent a fourth.

1. **A record says nothing needs it.** The system keeps its own record of what it still needs and
   the item is not in it.
2. **The owner has its own cleanup command.** The tool that made the data ships a command that
   clears it, and that command is what runs - we never delete inside it ourselves.
3. **We made it, by an exact name, and it is old and closed.** A folder matching a name
   DevThrottle's own code creates, older than the rule's age gate, with no file in it open.

Everything matched by no rule is reported as **unclassified** and is never offered for removal. This
is an allow-list: the tool enumerates what to remove, never what to skip.

Every rule carries, as data a caller can read: its name, the proof kind, whether it needs an
administrator, what it removes, why that is safe, **what is lost**, and **how to get it back** - and
where the answer is genuinely unknown it says unknown rather than guessing. "Probably cache" is not
a policy and must not appear.

### 2. Every rule reports its controls, and fails closed

A rule that compares two lists reports three numbers with its answer: how many records it read, how
many of those exist on disk, and how many candidates it examined. **If either side of the comparison
is empty the rule reports BROKEN, never "nothing to remove".** An empty or zero result is a broken
instrument until proven otherwise. The measurement script in
`docs/missions/reclaim-the-disk-2026-09-18/evidence/` shows the shape; read it.

This is the rule most likely to be got wrong, because the failing case and the clean case produce
the same empty list, and only one of them is safe to act on.

### 3. `src/CcDirector.Reclaim.Windows` - the Windows rules

A sibling project, not a rewrite of the engine. macOS and Linux arrive later as further sibling rule
sets. Three rules, in this order of measured value:

- **Orphaned Windows installer packages** - proof kind: a record. Windows records, for every
  installed product and patch, the cached package it needs to repair or uninstall it; a package
  nothing points at is an orphan. Measured on this machine on 18 September: 211 files, 27.9 gigabytes
  orphaned out of 58.8. **Age gate 30 days** - one of the 211 had been written two days earlier.
  Removal needs an administrator; reading the records does not. The rule never raises itself: it says
  an administrator is needed and prints the exact command for the owner to run.
- **Package caches: npm, pip, uv, NuGet, Gradle** - proof kind: the owner's command. About 16
  gigabytes here. The rule names the command that clears each one. **It does not run it in this
  phase** - phase 2 holds no removal code of any kind. What is lost is a re-download, and the rule
  says how long that takes to the extent it can be known, and otherwise says it is unknown.
- **DevThrottle test scratch folders in Temp** - proof kind: we made it. The suites leak scratch
  folders under several name patterns; 90,479 of them existed before the hand cleanup. **Age gate 7
  days**, and no file in the folder may be open.

### 4. `recommend`, a third command on `tools/cc-cleanup-storage`

It names, sizes and explains. It never moves and never deletes. Every recommendation prints the
rule, what it removes, the proof that it is safe, what is lost, how to get it back, and the rule's
controls. `--json` carries every one of those as its own field, keeps its shape, and every filter
applies to it too. The help page and the command line reader stay one statement, as phase 1 left
them: a flag the reader takes is a flag the page names, and the reverse.

`recommend` reads a saved scan the way `report` does. It does not walk the disk itself unless asked
to, and if it cannot find a saved scan it says so and prints the `scan` command - it does not
silently scan.

## The proof you owe

1. The mission's check, run by you and passing: `.\scripts\test-local.ps1` green with
   `CcDirector.Reclaim.Tests` in the default list, inside the budget.
2. **The BROKEN case, on a fixture.** A rule given an empty record set reports BROKEN, and there is a
   test that fails if it reports "nothing to remove" instead. Prove it red by removing the check.
3. **Fixture trees, built by the tests, with exact expected numbers** - never a range, never "greater
   than zero", and never a test pointed at anything on the real machine.
4. **A read-only run on the owner's machine**: `cc-cleanup-storage recommend C:\ --json` must exit 0,
   must show the installer rule with all three controls greater than zero, and must show the
   unseen-gap line. **Exit code and parsed JSON decide, never a search of the printed text.** Commit
   the parsed evidence, not a screenshot of a terminal.

## What phase 2 must NOT contain

**No removal code of any kind** - no delete, no move, no holding folder, not even unreachable, not
even behind a flag that is never passed. Phase 2 cannot delete anything because the code to do it
does not exist, and that is what makes it safe to merge quickly. Phase 3 adds removal with a Tech
Lead watching and the Reviewer reading it before it merges.

**Nothing on the owner's machine is removed by this mission at all**, in any phase. Removal is proven
on fixture trees the tests build and destroy. The first real removal is the owner's, from the
finished tool, after the report.

Nothing is imported from BleachBit or Winapp2. Both reason from path patterns and a path pattern from
somebody else's product is not a proof. They were reading material during the design and that is all.

## Rules that will bite you if you skip them

- **No fallback programming.** Fix the root cause or fail with a clear error and the exact command to
  fix it. Never "try X, fall back to Y". A rule that cannot read its record set says so; it does not
  fall back to guessing from paths.
- **Log entry, exit and errors on every public method**, in the `FileLog.Write` form.
- **Try-catch at entry points only.**
- **ASCII only**, everywhere - code, comments, output, logs, documents, commit messages.
- **Plain English, no abbreviations**: "pull request", not the short form.
- **Never sign anything.** No co-authored-by trailer, no "Generated with" line, no mention of any
  assistant, model or vendor in a commit, pull request, issue, comment, document or code comment.
  Check your text before every commit and every `gh` call.
- **Test names are `MethodName_Scenario_ExpectedResult`.** `tools/cc-worktrees` on main is the house
  model: 42 tests, nearly all named for something the tool declines to do.

## How it ends

1. Local gate green, and `dotnet test src\CcDirector.Reclaim.Tests` showing your tests by name.
2. Commit the proof beside the code, and commit this mandate file with it. Push and open a pull
   request against `main` saying what it does and what it deliberately leaves out. Reference issue
   #3120.
3. A Reviewer running a different agent family reads it before it merges, sent by the Delivery Lead.
   Every finding is answered in `review-phase-2-answers.md`, accepted or declined with the reason.
4. Merge on local green plus that review, then the worktree is removed and the branch deleted.
