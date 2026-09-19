# Phase 1 mandate: scan and report

From the Delivery Lead, 18 September 2026. You are the Developer for phase 1 of the Reclaim the
Disk mission. There is no Tech Lead on this phase; you report to the Delivery Lead.

## Read these first, in this order

1. `docs/missions/reclaim-the-disk-2026-09-18/mission.md` in this worktree. It is the mandate above
   this one. Where it and anything else disagree, it wins.
2. `cc-devthrottle skill get devthrottle-method` - the section "If you are a Developer" binds you.
3. `docs/CodingStyle.md` and `CLAUDE.md` in this worktree.

## Your worktree

`D:\ReposFred\devthrottle-reclaim-phase1`, branch `reclaim/phase1-scan-and-report`, cut from
origin/main. It is yours alone. Do not work in `D:\ReposFred\devthrottle` - that shared checkout is
routinely more than a hundred commits behind, and reading it has already produced two reported
"facts" that were fiction. To read shipped code, use `git show origin/main:<path>`.

## What you build

Three things, and nothing beyond them.

### 1. `src/CcDirector.Reclaim` - the engine library

Platform-neutral. No user interface. Nothing Windows-specific in it: the Windows rules are a
separate project that arrives in phase 2.

- **The scanner.** An ordinary recursive directory walk. Speed is explicitly not a constraint - the
  owner said "fast doesn't really matter because we can do this in the background, offline, and
  slowly". Do NOT read the file table, do not write a fast path. Measured on this machine a plain
  walk manages 20,000 to 60,000 entries a second, and that is accepted.
- **Links and junctions are never followed, and are counted.** A reparse point is recorded as what
  it is and the walk stops there.
- **A folder that refuses access is counted and named**, never swallowed. 244 folders refused
  access during the design measurement.
- **Cloud placeholder files are counted separately**, because they occupy no disk. On Windows these
  carry the offline attribute; treat an unreadable placeholder as a placeholder, not as an error.
- **The saved index.** The scan's result is written somewhere a later process reads without
  rescanning. The Launcher will host the background scan in phase 5 and the Director will render the
  saved result - so the index format is the contract between them. Version it from the first commit.
- **The report.** The engine produces the finished sentences. Critical rule 7 in `CLAUDE.md` binds
  here: the screens do not decide what anything means, so no caller ever re-derives a verdict or a
  label from the numbers. Every report states bytes seen against bytes the volume says are used and
  shows the difference as a number, with the folders that refused access beside it. That is the
  unseen-gap line, and it is required output, not a diagnostic.
- **An empty or zero result is a broken instrument until proven otherwise.** A scan that saw nothing
  reports BROKEN, never "nothing here".

### 2. `tools/cc-cleanup-storage` - a thin command line tool over the engine

- C#, registered in `tools/registry.json` as type `dotnet`. `cc-click` is the working model of an
  entry; copy its shape.
- Phase 1 implements exactly two commands: `scan` and `report`. Leave the others out entirely -
  a stub that says "not implemented yet" is worse than an unknown command, because it teaches an
  agent the command exists.
- Every command has `--json`, and it follows the AXI standard - `docs/axi-standard.md` in this
  worktree is the checklist. The rules most often broken: `--json` keeps its shape, every filter
  applies to `--json` too, an unknown flag fails rather than being ignored, nothing is cut short in
  list output, and an empty result says `count: 0` rather than printing nothing.
- It works on Windows, macOS and Linux.

### 3. `src/CcDirector.Reclaim.Tests` - the tests, added to the default gate

Add the project to the project list in `scripts\test-local.ps1` so it runs in the default run, not
in `-Parked`. The suite must stay inside the two-minute budget the gate holds.

## The proof you owe

The mission's check, run by you and passing, plus a fixture tree. Build the fixture tree from the
tests - never point a test at anything on the real machine - and have it report EXACT expected
numbers, not a range and not "greater than zero". It must contain, at minimum:

- a link or junction that the scan does not follow and does count;
- a folder the scan cannot read, which it counts and names;
- enough real bytes that the unseen-gap line has both sides to print.

`tools/cc-worktrees` on main is the house model for this whole mission: 42 tests, nearly all named
for something the tool declines to do. Read it before you write your first test.

Test names are `MethodName_Scenario_ExpectedResult`, per `CLAUDE.md`.

## Rules that will bite you if you skip them

- **No fallback programming.** If something can fail, fix the root cause or fail with a clear error
  and the exact command to fix it. Never "try X, fall back to Y". This is law 1 of the method and
  rule 3 of `CLAUDE.md`, and it is the rule this mission is most likely to break, because a scanner
  meets unreadable things constantly. An unreadable thing is REPORTED, not skipped quietly.
- **Log entry, exit and errors on every public method**, in the `FileLog.Write` form `CLAUDE.md`
  rule 2 sets.
- **Try-catch at entry points only** - event handlers and lifecycle methods. Not in helpers, not in
  service methods.
- **ASCII only.** No Unicode, no emoji, no arrows, no check marks, anywhere - code, comments,
  console output, logs, documents, commit messages.
- **Plain English, no abbreviations**, in every line of output and every document. "pull request",
  not "PR".
- **Never sign the code.** No co-authored-by trailer, no "Generated with" line, no mention of any
  assistant or vendor in a commit, a pull request, an issue or a comment. Check your text before
  every commit and every `gh` call.

## What phase 1 must NOT contain

No rule contract, no classifier, no `recommend`, and above all **no removal code of any kind** - no
delete, no move, no holding folder, not even unreachable. Phase 1 cannot delete anything because the
code to do it does not exist yet, and that is what makes it safe to merge quickly. Phase 2 adds
rules; phase 3 adds removal with a Tech Lead watching.

Nothing in this phase touches the owner's machine except by reading it.

## How it ends

1. Local gate green: `.\scripts\test-local.ps1`, plus `dotnet test src\CcDirector.Reclaim.Tests`
   showing your tests by name.
2. Commit the proof beside the code - the fixture tree's expected numbers and the gate's result -
   and commit this mandate file with it. Push the branch and open a pull request against `main`
   that says what it does and what it deliberately leaves out. Reference issue #3120.
3. **Report to the Delivery Lead** with `cc-devthrottle message send 5ae63da8 "..."`, one line and
   no newlines, saying it is pushed and the pull request is open, with the number.
4. **Do not merge, and do not arrange your own review.** The Delivery Lead sends your code to a
   Reviewer running a different agent, reads nothing itself, and merges. If the Reviewer raises
   findings they come back to you, and you answer every one - accepted, or declined with the reason.

If something inside this mandate is genuinely undecidable, ask the Delivery Lead with
`cc-devthrottle message send 5ae63da8 --reply-wanted "..."` and carry on with everything that does
not depend on the answer. Never guess, and never stop and wait.
