# Review of pull request 4232: backup machines for a definition trigger's check

Mail Desk, phase 5 (issue #4213). Branch `mail-desk-p5-backup-machines` at commit `c3674c96a`, into
`mail-desk-phase-3`. Reviewed against `docs/qa/mail-desk/phase-5/CONTRACT.md` on the base branch and the
pull request's own `docs/qa/mail-desk/phase-5/README.md`. Written 18 October 2026 on devlinux. The reviewer read
and ran; nothing here was fixed by the reviewer, and nothing the reviewer wrote while gathering evidence is
committed.

Two findings. The first means the feature does not work on a real backup machine as shipped; the second is a gap
in what a report from a Director that lost its claim may do. Everything else the Tech Lead asked to be looked at
hardest was looked at and held up; section 3 says what and how.

## 1. A backup machine's Director is refused the check's code, so the backup never runs the check

**Severity: the feature fails at its first real failover.** Must change before this ships.

**What breaks.** A Director on a backup machine is handed the trigger after the lapse (the store does this
correctly), and then goes to download the check it was handed. The download route refuses it:

- `src/CcDirector.Gateway/Api/FactoryCheckEndpoints.cs`, lines 76 to 85: the route answers the `checks/` and
  `files/` of a version only when some seat of that version has `MachineOrDefault` equal to the asking Director's
  machine. Otherwise it returns 404 with the code `no_seat_on_machine` and the sentence "version N of the
  definition of F has no seat on M, so Director 'D' is not given its checks". A backup machine is, by the
  contract's own definition, a machine the seat does not run on, so it has no seat in the version.
- `src/CcDirector.ControlApi/Triggers/DefinitionCheckRunner.cs`, lines 153 to 154 and 278 to 281: the Director
  turns that refusal into the check's start error, "version N of the checks of F could not be downloaded: HTTP
  404: ...", and reports a failed check.
- The Gateway records that as a failed run. The trigger goes RED on the backup, and no mail is checked for as long
  as the seat's machine is away, which is the one moment the backup exists for.

So the claim moves to the backup, the backup fails every interval, and the trigger's own machine, which is away,
cannot take it back. The outcome is worse than having no backup: with no backup the trigger reads as silent for
want of a Director; with one it reads as a check that fails every ten minutes.

**Evidence.** Reproduced through the real route, in the existing `FactoryCheckEndpointsTests` harness (the seat
`outreach` on `SOREN_SOUTH`, with `backupMachines: [LAPTOP_2]` added to its interval start, saved and published as
version 1; the Director `dir-elsewhere` is registered on `LAPTOP_2`):

```
GET /directors/dir-elsewhere/factory-checks/devthrottle/1
status=404 body={"code":"no_seat_on_machine","error":"version 1 of the definition of devthrottle has no seat on
LAPTOP_2, so Director 'dir-elsewhere' is not given its checks"}
```

The reviewer's scratch test was reverted; the file is unchanged on the branch. The existing test
`Get_ADirectorWhoseMachineHasNoSeatOfTheFactory_IsRefused` in the same file asserts today's rule, and would need
to be joined by one that admits a backup.

**Why the pull request's tests did not see it.** Every "Director" in `BackupMachinesClaimTests` is a name calling
`TriggerService.AssignmentsFor` and `ReportCheckAsync`; none of them downloads the check. The README's section 6
says a backup Director's own behaviour "is assumed because an assignment carries no machine at all". The
assignment carries no machine, but the download route reads the Director's machine from its registration, exactly
as the trigger routes do, and gates on it. The assumption was checked against the wrong route.

**What must change.** This is Gateway-only, so "no Director change" still holds: the download route must admit a
Director whose machine is named as a backup by any start of any seat in that version, not only a seat's own
machine. The route already reads the published version's definition to build `seatsHere`, so the backups are in
hand. The contract's words, "the backups are where its CHECK runs while the seat's machine is away", are exactly
what this route must honour. A test through the route, for a backup machine with no seat, belongs beside the
refusal test.

## 2. A report from a Director whose machine is no longer on the list is recorded, and it takes the claim

**Severity: bounded, but it is the case the Tech Lead named.** Should change; the Developer decides how.

**What breaks.** The report route now passes the reporting Director's machine, but `ReportCheckAsync` refuses a
report only for `HeldByAnotherDirector` (`TriggerService.cs`, line 350) and never asks whether the reporting
Director's machine is on the trigger's list. `RenewClaim` (`TriggerStore.cs`, lines 462 to 476) then makes that
Director the holder, with its machine. And `HeldFromLaterMachine` (`TriggerStore.cs`, line 319) treats a holder on
no list as "not later", so the trigger's own machine cannot take the claim back; only the lapse frees it.

The order of events that does it, which the contract's section 1 row "Publish" describes without this case:

1. The seat's machine is away; the backup holds the claim and runs the check every interval.
2. The owner republishes with the backup removed. `WriteDefinitionTrigger` releases the claim, correctly (the
   test `Publish_ARepublishRemovingTheHoldersMachine_ReleasesTheClaim` proves this).
3. The backup's Director still has the trigger from its last poll (`DirectorTriggerRunner` polls every 30 seconds
   and runs a check for up to its timeout), so a check in flight at the publish reports after it.
4. That report is recorded (its run row, its last outcome, and on nothing-to-do its state as the memory), and the
   ex-backup becomes the holder on a machine that is on no list.
5. The seat's machine comes back and asks for its assignments: it is handed nothing until the lapse, two
   intervals and a minute. The ex-backup is handed nothing either (its machine is on no list), so nobody checks.

**Evidence.** Reproduced with the store and service on a real database file and an injected clock (the republish
simulated by the exact row changes `WriteDefinitionTrigger` makes: the list emptied and the claim cleared):

```
dir-b (machine-b, no longer on the list) reports after the publish:
  REFUSAL=None  holder=dir-b on 'machine-b'
dir-a (machine-a, the trigger's own) asks for its assignments:
  handed now=0   handed just before the lapse=0   handed after the lapse=1
```

The scratch test was deleted; nothing of it is on the branch.

**How much it costs.** One lapse of no check on the seat's machine, which for a ten-minute interval is twenty-one
minutes, once per such republish, and the ex-backup's last cursor kept as the memory, which is correct data from a
check that did run. The same shape has existed since triggers could be moved between machines; what is new is
that the Gateway now knows the reporting machine on every production report and could refuse. The contract's
test 3, "a machine on no list is never handed the trigger, held or not", is met for assignments; a report is the
other half of that promise.

**What could change.** Refuse a report whose machine is not on the trigger's list (the route always says the
machine; a null machine from an older in-process caller keeps today's path), with its own refusal code and
sentence, as `CannotRunDefinitionChecks` has. Whether to do it in this pull request or in phase 3 task F is the
builder's call.

## 3. Looked at hardest, as asked, and held up

- **Two Directors on the same machine.** `ClaimForDirector` computes a position for each machine; two Directors on
  one machine stand at one position, so `HeldFromLaterMachine` is false between them and only the lapse hands over,
  exactly as before. Test 5 (`ClaimForDirector_TwoDirectorsOnTheSameMachineOfATriggerWithBackups_HandOverOnlyAtTheLapse`)
  proves it on a trigger with backups, and the 883 existing trigger and definition tests ran green with no test file
  edited (confirmed by the diff: no existing test file is touched).
- **A claim held by two Directors, or by none for longer than the lapse.** The claim is one column written under
  the store's one lock; `ClaimForDirector` and every record go through it. "Held by none for longer than the lapse"
  cannot happen from these paths; "held by a Director that will never run it for a lapse" can, and that is
  finding 2. Also considered: a poll under the store lock landing between a report's refusal check (under the
  per-trigger gate) and its record. The run row is then written but the claim is not taken back, because
  `RenewClaim` declines when another Director holds it live; that guard is new and the late-start-result test
  proves it. The window is microseconds and has the same shape between two Directors on one machine today.
- **A report from the Director that lost the claim.** Refused with `HeldByAnotherDirector`, nothing recorded, and
  its state not kept (hand-back test; the state is kept only after a recorded nothing-to-do). The gap is the
  Director that lost the claim by republish, finding 2.
- **The migration on existing rows and its Down.** Both migrations add the two columns with defaults, fill
  `ClaimedByMachine` with the trigger's machine for every row with a non-empty `ClaimedByDirectorId`, and leave an
  unclaimed row empty; Down drops both columns and keeps the rows. The SQLite test proves each of those and that no
  model change is pending; the PostgreSQL test proves the same and drives the real store through a handover and a
  hand-back (read, not run here; the Tech Lead ran it green). A row with a NULL `ClaimedByDirectorId`, if any
  existed, is untouched by `<> ''`, and reads as unclaimed, which is right.
- **A republish adding or removing a backup while a backup holds the claim.** Adding keeps the claim (the holder
  stays on the list); removing the holder releases it; removing another backup keeps it; dropping every backup while
  the seat's machine holds it keeps it. All four are tested. The one gap is the in-flight report after the release,
  finding 2.
- **A seat started on a backup machine that cannot run it.** The session starts on the reporting Director's
  machine, naming that Director (`PendingStart.StartMachine`; two tests). The seat start is built by
  `FactorySeatStart.Build` with the repository by name and an empty folder, so the backup must have the repository
  registered. The publish asks a backup only for the check's needs, as the contract says in so many words, and the
  README says the start then fails naming the machine. This review accepts the contract's choice and does not raise
  it; it notes only that the first time the owner learns a backup lacks the repository is at failover, when the
  seat's machine is already away. Not verified: that the spawner's failure sentence names the machine.
- **The `cc-devthrottle trigger` output.** `--json` is the Gateway's answer verbatim (the existing test
  `test_list_json_is_the_gateway_answer`), so `backupMachines` and `machinesText` pass through unchanged; the
  human lines print the whole sentence and cut nothing; an older Gateway with no `machinesText` falls back to the
  machine. The 25 tests in `tests/test_trigger_ops.py` pass.
- **The definition check and the words.** The six refusals are each on the `backupMachines` line and tested. A
  backup machine with no Director connected gets, at publish, the "could not be asked" notice from the program and
  secret notices on its line, so a misspelt backup is said; it does not get the seat machine's own "No Director on
  X is connected now" notice (`MachineNotice` is not called for a backup). That is a difference in wording only
  and is not raised as a finding. The words for none, one and two backups are tested, and the publish step and
  the trigger answer use the one `TriggerMachines.Text`.
- **"Silent for want of a Director"** is now said only when no machine on the list has a Director that runs
  definition checks, which is the right reading of the fold with backups.

## 4. Scope

**Read, in full, on the branch at `c3674c96a`:** the contract and the README; the whole diff against
`origin/mail-desk-phase-3` (28 files); `TriggerStore.cs`, `TriggerMachines.cs` and the changed parts of
`TriggerService.cs` and `TriggerEndpoints.cs`; `FactoryDefinitionCheck.CheckBackupMachines`,
`FactoryMachineQuestions`, `FactoryPublishApply.WriteDefinitionTrigger`, the `FactoryPublishPlan` changes and
`FactoryDefinitionChanges`; both migrations and both model snapshots' trigger sections; all four new test files;
the `trigger_ops.py` change and its test. Also read, because the feature depends on them though the diff does not
touch them: `FactoryCheckEndpoints.cs`, `DefinitionCheckRunner.cs` (the download and failure path),
`DirectorTriggerRunner.cs` (the poll and report loop), `FactorySeatStart.Build`'s signature, and the trigger
service wiring in `GatewayHost.cs`.

**Ran, on devlinux, from a worktree at the branch head:**

- `dotnet build src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true`: 0 errors.
- `dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~Trigger|FullyQualifiedName~FactoryDefinition|FullyQualifiedName~BackupMachines"`:
  883 passed, 0 failed, 0 skipped, 1 minute 41 seconds.
- `tests/test_trigger_ops.py` under `uv run` as the README says: 25 passed.
- Two scratch tests written to gather the evidence in findings 1 and 2, run, and then removed (`git status` clean
  before this file was added).

**Not reached:** `scripts/test-database.ps1` (no run here; the Tech Lead's green run is taken on its word, and
the PostgreSQL test was read); a real Director on a backup machine; two real machines; the Director's behaviour
when a trigger leaves its assignment list mid-check beyond what the runner's code says; whether the spawner's
failure sentence for a seat start on a backup names the machine; the Cockpit and the mobile app (the README says
no client on this base shows a trigger's machine, and a search of the three web packages for a trigger's machine
found nothing to check); contract test 10, left for task F by agreement.
