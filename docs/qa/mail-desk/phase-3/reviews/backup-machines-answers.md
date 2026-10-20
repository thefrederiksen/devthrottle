# Answers to the review of pull request 4232: backup machines

The Developer's answer to each finding in [backup-machines-review.md](backup-machines-review.md). Written 18 October
2026 on devlinux. Both findings are accepted and fixed in this pull request; neither fix touches the Director or the
migration.

## Finding 1: a backup machine's Director is refused the check's code - ACCEPTED, fixed

The reviewer is right, and the README's claim that a backup Director needs nothing new was checked against the wrong
route: the assignment carries no machine, but the download route reads the Director's registered machine and gates on
it.

**The fix** (`src/CcDirector.Gateway/Api/FactoryCheckEndpoints.cs`): the route gives a version's `checks/` and
`files/` to a Director whose machine is a seat's machine, as before, **or is named in `backupMachines` by a start with
a check of any seat in that version**. Any other machine is refused exactly as before, with the same code and the same
sentence, so the existing test `Get_ADirectorWhoseMachineHasNoSeatOfTheFactory_IsRefused` is unchanged and green.

**The tests**, in the new `src/CcDirector.Gateway.UnitTests/Factory/Triggers/BackupMachinesThroughTheRoutesTests.cs`.
They go through the real Map functions in memory, over a definition saved and published by the real publish, which
writes the trigger:

- `Get_FactoryChecks_ADirectorOnABackupMachineWithNoSeat_IsGivenTheChecks`: 200, with `checks/new_mail.py`.
- `Get_FactoryChecks_AMachineThatIsNeitherASeatsNorABackup_IsStillRefused`: 404 `no_seat_on_machine`.
- `Handover_ThroughTheRoutes_TheBackupPullsDownloadsAndReports_ThenTheSeatsMachineTakesItBack`: end to end, as the
  Tech Lead asked, through every route a Director calls, in the order it calls them.
  1. The seat's machine pulls its triggers and reports.
  2. Before the lapse, the backup's pull is empty.
  3. After the lapse, the backup pulls the trigger with the cursor, downloads the version and check path it was handed,
     and its report is recorded.
  4. The seat's machine pulls again and gets the trigger back at once, with the backup's cursor.
  5. The backup's next report is 409 `held_by_another_director`, nothing is recorded, and its next pull is empty.

**It fails before the fix.** With the route put back as it was at `c3674c96a`, the download test and the end-to-end
handover test both go red (the handover fails at "the backup could not download its check: 404").

**Other routes that ask "does the seat run on this machine".** I searched the Gateway for every comparison of a
seat's machine (`MachineOrDefault`) or a trigger's machine with a caller's machine:

- **The check download** is the only route that gates on a seat's machine. It is fixed.
- **The kit route** (`FactoryKitEndpoints`) does not gate on the machine.
- **Due checks** (`DueCheckService`) gate on the schedule's machine. They belong to timed starts, and the definition
  check refuses `backupMachines` on a timed start, so a backup is never involved.
- **Secrets** are not served by the Gateway to a check. The Director reads them from cc-secrets on its own machine,
  and whether cc-secrets lets the check use them is that machine's local policy. The publish already asks every
  backup about the check's secrets and words a notice on the backup's line.
- **The seat start** builds its session on the reporting Director's machine (the Tech Lead's ruling, tested).
  `FactorySeatStart`'s own machine is used only for a person's start, never for a trigger's.

## Finding 2: a report from a machine a republish removed is recorded and takes the claim - ACCEPTED, fixed

**The fix** (`src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs`, `src/CcDirector.Gateway/Api/TriggerEndpoints.cs`):

- `ReportCheckAsync` refuses a report whose machine is not on the trigger's list (its own machine and its backups).
  The new refusal is `MachineNotOnTheList`, answered 409 `machine_not_on_the_list`, with the sentence "trigger 'X' runs
  on <the list in words>, not on <machine>, so this report from Director D was not recorded".
- The check is made under the per-trigger lock, before the claim check, so nothing is recorded, no state is kept and
  no claim is taken.
- A caller that does not say its machine keeps the path it always had. Only in-process callers do that; the one
  production route always says it.
- **No Director change.** The Director already treats a 409 to a report the way it treats `held_by_another_director`.

**The tests**, in the same new file:

- `Post_Checks_AReportFromABackupARepublishRemoved_IsRefused_AndTheSeatsMachineIsHandedItAtOnce` reproduces the
  reviewer's sequence through the real publish and the real routes:
  1. The backup holds the claim.
  2. A republish drops the backup.
  3. The backup's report arrives late. It is refused with the sentence above, writes no run, and leaves the claim free.
  4. The seat's machine's pull hands it the trigger at once, not after the lapse.
- `Post_Checks_AReportFromAMachineOnNoList_IsRefused_EvenWithTheClaimFree` is the report half of contract test 3:
  "a machine on no list is never handed the trigger".

**It fails before the fix.** With the refusal switched off, both tests go red.

This is a change for command triggers too: a report about a command trigger from a Director on another machine is now
refused. That is the same promise, since such a Director is never handed one. Every existing test still passes
unchanged (numbers below).

## Numbers after the fixes (devlinux)

| Run | Result |
|---|---|
| Backup tests (claim, definition, routes, SQLite migration) | 30 passed, 0 failed |
| Whole Gateway.UnitTests, in three parts | 14,492: 14,487 passed, 2 skipped (`GatewayStatsStoreDisposeReleasesTheFileTests`, not mine), 3 failed. The 3 are the same three that fail unchanged on origin/main on Linux (`VoiceUnheardCountTests`, `VoiceAutoOffLineTests`, `HeldDeliveryDriverTests`; see the README). |
| `scripts/test-database.ps1` | not run again: the migration did not change since the green run (79 of 79, none skipped) |
| `cc-devthrottle` tests | not run again: the tool did not change |
