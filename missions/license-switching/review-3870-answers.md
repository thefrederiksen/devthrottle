# Pull request 3870: answers to the review

The review is `review-3870-retire.md` beside this file.

## Finding: the storage migration still copies the token-bearing accounts file - ACCEPTED, fixed

`accounts.json` is removed from all three storage migrations:

- `CcStorageMigration.MigrateDirectorConfig`, which runs at start-up.
- `scripts/migrate-storage.py`.
- `tools/cc_storage/migrate.py`.

They copy nothing and delete nothing: the file stays where it is, unread and uncopied. Each migration has a comment
in its place saying why: the file holds sign-in tokens, and a copy would be a second holder of them (contract 1.1).

`RetiredAccountsFileIsLeftAloneTests` now forbids the copy too. It scans the Director's C# sources and all three
migrations. No line of code in any of them may name the file; a comment may. It proves it read what it scans, so a
scan that read nothing cannot pass:

- **The Director's sources:** by their count.
- **Each migration:** by finding in it the file every migration still carries forward, `root-directories.json`.

Revert proof: with the three old lines put back, the test fails and names all three (`CcStorageMigration.cs:79`,
`migrate-storage.py:166`, `migrate.py:58`). With them removed it passes.

## Condition 1, partly evidenced

Agreed, and it is as the pull request says:

- **What is shown:** the evidence shows the Director's own copy is present and reachable on both machines.
- **Where the own-copy path lives:** the code that makes the Director run only that copy, `ResolveOwnCopy`, is in
  pull request 3868, not this one.
- **When the commands work:** the `license` commands work there from the next release that carries them.

## The red Core.Tests case

`WorktreeInventoryIntegrationTests.CancelledWhileListingWorktrees_PropagatesCancellation_NotAFailure` is outside this
change. Nothing here touches worktrees or that test's temporary folders, and it failed because another process held
its temporary folder.

## Runs

Rebased on main first.

- Core.UnitTests: 1,518 passed, 0 failed.
- Avalonia.Tests: 1,036 passed, 0 failed.
- Core.Tests, filter `Storage`: 79 passed.
- Both Python migrations compile.
