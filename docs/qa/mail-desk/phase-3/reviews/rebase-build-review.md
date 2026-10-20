# Review: pull request 4236 - repair of what the rebase of the Mail Desk phase 3 branch broke

Reviewed head: `0be24bb54` (branch `mail-desk-p3-rebase-build`, into `mail-desk-phase-3`), confirmed against the pull request's own head before reading.

## Findings

None. Nothing in the change was found to break, and nothing outside what the rebase broke was changed. Each item below says what was checked and what showed it.

1. **Each Designer file's model equals its snapshot.** I extracted the body of `BuildTargetModel` from
   `20261018182424_AddTriggerBackupMachines.Designer.cs` (SQLite) and from `20261018182501_AddTriggerBackupMachines.Designer.cs`
   (PostgreSQL), and the body of `BuildModel` from each project's `GatewayDbContextModelSnapshot.cs`, and compared them
   character for character. Both pairs are identical (226,471 and 241,980 characters). Neither snapshot is touched by the
   pull request.
2. **Nothing else changed in the Designer files.** The diff of the two Designer files removes zero lines. What it adds is
   the five Mail Desk entities (`mail_desk_channels`, `mail_desk_decisions`, `mail_desk_list_changes`, `mail_desk_routed`,
   `mail_desk_rules`) in each, with the schema `gateway` on PostgreSQL and none on SQLite, matching each provider's other tables.
3. **No migration's Up or Down changed.** The pull request touches no `*_AddTriggerBackupMachines.cs` or `*_AddMailDesk.cs`
   file; its seven files are the two Designer files and five test files.
4. **The migration order is right.** On both providers the directory sorts `AddMailDesk` (20261018153106 / 20261018153213)
   before `AddTriggerBackupMachines` (20261018182424 / 20261018182501), and both are newest. The `AddMailDesk` Designer
   files carry no `BackupMachinesJson` (zero mentions), so phase 1's model does not claim phase 5's column; the
   `AddTriggerBackupMachines` Designer does. The two migration tests now name `AddMailDesk` as the migration before theirs,
   which matches that order.
5. **`mailDeskOn: _ => false` is the right value for the three phase 5 test constructions.** In the Gateway the predicate is
   read in exactly two places on these paths - `TriggerService.cs:349` and `DueCheckService.cs:185` - and in both it is
   consulted only when `MailDeskStart.Is(...)` is true for the trigger or job. The backup-machine tests do not build Mail Desk
   triggers, so the value cannot change their outcome; `false` also matches the production default (Mail Desk off).
6. **The new route test asserts what it claims.** `CheckReportRoute_MachineNotOnTheList_AnswersMailDeskOffThenMachineNotOnTheList`
   reports from a machine on no list, first with Mail Desk off (expects 409 with `error: mail_desk_off`, a `detail`, and no
   `code`), then on (expects 409 with `code: machine_not_on_the_list` and no `detail`), and checks no run, no state row and
   no start. It uses only in-memory parts and an injected clock; the only address used is `email@devthrottle.com`.

## Scope

- **Read:** the full diff of the pull request against `origin/mail-desk-phase-3` (two commits, seven files); both
  model snapshots; the migration directories of both providers; every use of `mailDeskOn` in `src/CcDirector.Gateway`.
- **Ran:** a character-for-character comparison of each Designer's model against its snapshot; searches for
  `BackupMachines` in the four Designer files. I built nothing and ran no tests: the Tech Lead's runs
  (`scripts/test-database.ps1` 80 of 80 with none skipped, and 1,238 filtered unit tests) were not repeated, as the brief allowed.
- **Could not reach / did not check:** whether the snapshots themselves equal the model the current entity configuration
  would produce (that needs `dotnet ef migrations add` or the model-snapshot tests, which belong to the test runs above);
  whether the order of the two refusals in the report route is the intended design rather than simply what the test pins;
  anything in the rebase itself outside these seven files.
