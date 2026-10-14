# Answers - review of pull request 3998, the definition store and its migrations

The Developer's answer to each point in `review-store.md` beside this file. Both findings are accepted and fixed, and
so is the note. The Tech Lead placed finding 1 in this pull request, in `DefinitionPaths`, so the store and the check
share one rule. Each fix has a test that fails without it.

## 1. A path the rules accept can still be one no export can write - ACCEPTED, FIXED

`DefinitionPaths.ShapeProblem`, which both the store and the whole-definition check ask, now also refuses:

- a part that is a name Windows keeps for a device, in any case and with any extension, whether it is a file or a
  folder on the way (`files/nul`, `files/CON.txt`, `files/lpt1/notes.txt`, `checks/com1.py`, `skills/prn/SKILL.md`).
  The list is `FactoryNames.Reserved`, which notes already used, now shared through `FactoryNames.IsWindowsDeviceName`
  so notes and definitions read one list. Windows reads the part before the first dot, so `con.txt` is refused too;
  a note name cannot hold a dot, so this changes nothing for notes;
- a part ending in a dot or a space (`files/report.`, `files/report `, `files/drafts./a.md`);
- any of the characters `< > : " | ? *` (`DefinitionPaths.CharactersWindowsRefuses`). Control characters were already
  refused.

A new rule, `DefinitionPaths.Clashes`, refuses two paths no machine could hold side by side:

- two names that differ only in the case of their letters, compared at every folder on the way and not only at the
  file name (`files/Brief.md` with `files/brief.md`, and `files/Drafts/a.md` with `files/drafts/b.md`);
- a name that is a file in one path and a folder in another (`files/notes` with `files/notes/a.md`). The review did
  not name this case, but it does the same harm: an export cannot write both, so the round trip of phase 3 fails.

The check reports a clash on the later of the two paths (in ordinal order), at line 0, and compares only paths that
passed the per-path rules, so a path already refused is reported once, for its own reason. The store refuses the save
with the same sentence.

Tests, through the store (`FactoryDefinitionStoreTests`): 16 new cases in
`A_path_that_breaks_a_rule_is_REFUSED_and_says_which_rule` (every device-name shape, every trailing dot or space shape,
each of the seven characters), `TWO_PATHS_NO_MACHINE_CAN_HOLD_SIDE_BY_SIDE_ARE_REFUSED` (four pairs), and
`Paths_that_differ_in_more_than_case_are_ACCEPTED_side_by_side`. Through the check (`FactoryDefinitionCheckTests`):
13 new cases in `Check_BadPath_SaysWhy`, and `Check_TwoPathsNoMachineCanHoldSideBySide_IsAnErrorOnTheLaterPath`
(three pairs).

## 2. The one guard that makes "set once" true across two replicas is covered by no test - ACCEPTED, FIXED

The guarded update is now its own function, `FactoryDefinitionStore.MarkPublished`, and `Publish` calls it.
`THE_PUBLISH_MARK_IS_SET_ONCE_even_by_a_writer_that_read_the_row_before_it_was_published` publishes version 1 through
the store, then runs that update against version 1 again as a second replica's would, and asserts it marks nothing
and the first publisher and time stay recorded; on version 2, still a draft, the same update marks it.

The review's first suggestion (mark the row back to draft, then publish again) would not have tested the guard: with
the row a draft again, the guard lets the update through, so the test passes with or without it. Testing the update
on its own is the review's second suggestion.

Proof it is the guard that the test holds: with `&& v.State == FactoryDefinitionStates.Draft` removed from
`MarkPublished`, the store tests ran 99 passed and 1 failed, the failure being exactly this test. The line was then
restored from the commit.

The brief store has the same gap. It is not touched here; it is a candidate for the same test.

## 3. Note: non-ASCII literals in the tests - ACCEPTED, FIXED

The three lines now write the e with an accent and the euro sign as C# escapes. The emoji was already an escape. The
test file is ASCII only. The copy of the review beside this file writes the same characters as escapes too, so the
folder stays ASCII only.

## The check, after the fixes

From the worktree, rebased onto origin/main:

- `.\scripts\test-local.ps1`: see the pull request and the message to the Tech Lead for the run on the final commit.
- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory"`: 1,311 passed, 0 failed.
