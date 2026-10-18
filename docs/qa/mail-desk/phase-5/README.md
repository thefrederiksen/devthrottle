# Mail Desk, phase 5: backup machines - the proof

What was built against [CONTRACT.md](CONTRACT.md), how it is proved, how to run the proof, the numbers, and what is
**not** proven. Written 18 October 2026 on devlinux, branch `mail-desk-p5-backup-machines`.

## 1. What it does, in plain words

A definition start that checks on an interval may now name backup machines:

```yaml
starts:
  - every: 10m
    check: checks/new_mail.py
    prompt: "New mail: {count}. {context}"
    backupMachines: [machine-b]
```

The seat's own machine runs the check first. If its Director stops asking for its work for two intervals and a minute
(the claim lapse, unchanged), the next machine on the list takes the check over. The memory the check keeps (its
cursor) is on the Gateway, so the backup carries on from where the first machine stopped. As soon as the first
machine asks again, it takes the check back, even though the backup's claim is still live; the backup's next report
is refused with `HeldByAnotherDirector` and records nothing, and its next request for work hands it nothing.

**Where the session starts.** When a backup's check counts work, the seat's session starts **on the backup** - the
machine whose Director ran the check, and the one machine known to be up at that moment. The Tech Lead accepted this
on 18 October 2026, and it matches mission section 5.7 ("on the machine that ran the check"). When the seat's own
machine ran the check, nothing changes. **So a backup needs what the seat needs, not only what the check needs:** a
seat with a repository will fail to start on a backup that does not have that repository registered, and the
failure is recorded as a failed start that names the machine. The publish asks a backup only what the check needs
(its Python or uv, and the check's secrets), as the contract says - not the seat's repository.

Where the trigger's machine is shown, the Gateway now also sends the words for the whole list, for example
`machine-a, then machine-b if machine-a is away`, and `cc-devthrottle trigger list` and `trigger show` print them.

## 2. The changes

| Where | What |
|---|---|
| `Data/Entities/TriggerEntity.cs`, `GatewayDbContext.cs` | Two columns: `BackupMachinesJson` (a JSON array, `[]` by default) and `ClaimedByMachine` (the holder's machine, empty when nobody holds it). |
| `Data/Migrations/20261018182424_AddTriggerBackupMachines` and `Migrations.Postgres/Migrations/20261018182501_AddTriggerBackupMachines` | The migration pair. Both also fill `ClaimedByMachine` with the trigger's own machine for every claim held before the migration - the only machine a claim was ever held on until now. |
| `Factory/Triggers/TriggerMachines.cs` (new) | The trigger's machines in order, a machine's place in that list, and the words for it. One place. |
| `Factory/Triggers/TriggerStore.cs` | `ClaimForDirector`: a Director is handed a trigger when its machine is anywhere on the list, and takes a live claim only from a machine later on the list. Renewing a claim on a recorded result no longer takes it back from a Director that holds it live (a backup's slow start ending after the hand-back). |
| `Factory/Triggers/TriggerService.cs`, `Api/TriggerEndpoints.cs` | The report route passes the reporting Director's machine; it becomes the claim's machine and where the session starts. The trigger's answer carries `backupMachines` and `machinesText`. "No Director runs definition checks" is said only when no machine on the list has one. |
| `Factory/Definition/Schemas/seat.schema.json`, `FactoryDefinition.cs`, `FactoryDefinitionCheck.cs` | The `backupMachines` field and its refusals (section 3, test 6). Each backup gets the same notices the seat's machine gets for the check: its Python or uv, and the check's secrets. |
| `FactoryMachineQuestions.cs` | The publish asks every backup for the check's secrets and its Python or uv. |
| `FactoryPublishPlan.cs`, `FactoryPublishApply.cs`, `FactoryDefinitionChanges.cs` | The publish writes the list onto the trigger, says it in the step ("..., on SOREN_SOUTH, then machine-b if SOREN_SOUTH is away."), shows a changed list as a detail, and releases the claim only when the holder's machine is no longer on the list. |
| `tools/cc-devthrottle/src/trigger_ops.py` | `trigger list` and `trigger show` print the Gateway's words. |
| `docs/factories/definition-format.md` | The field, and its refusals. |

No Director code changed.

## 3. The tests, and what each proves

All with an injected clock and a real database file; no Director runs, no port is opened, no network.

`src/CcDirector.Gateway.UnitTests/Factory/Triggers/BackupMachinesClaimTests.cs` (10 tests) - contract tests 1 to 5
and 9:

| Test | Contract | What it proves |
|---|---|---|
| `ClaimForDirector_FirstMachineGoesQuiet_BackupIsHandedNothingBeforeTheLapse_ThenTakesOverWithTheMemory` | 1 | At exactly the lapse the backup is handed nothing; one second later it is handed the trigger with the first machine's cursor, its report is recorded, and its own cursor is then what it is handed. |
| `ClaimForDirector_FirstMachineAsksAgainWhileTheBackupHoldsALiveClaim_TakesItBack_AndTheBackupIsRefused` | 2 | The first machine takes a live claim back with the backup's cursor; the backup's report is refused and no run row is written; the backup's next poll is empty. |
| `RecordStartResult_TheBackupsStartEndsAfterTheFirstMachineTookItsCheckBack_TheClaimStaysWithTheFirst` | 2 | A backup's session start that ends after the hand-back writes its row but does not take the claim back. |
| `ReportCheckAsync_ABackupsCheckCountsWork_TheSessionStartsOnTheBackup_NamingItsDirector` | Tech Lead ruling | The session starts on the backup machine, naming the backup's Director. |
| `ReportCheckAsync_TheTriggersOwnMachineCountsWork_TheSessionStartsThere_AsBefore` | Tech Lead ruling | Unchanged when the seat's machine ran the check. |
| `ClaimForDirector_AMachineOnNoList_IsNeverHandedTheTrigger_HeldOrNot` | 3 | Free, held and lapsed: a machine on no list is handed nothing and changes no claim. |
| `ClaimForDirector_ThreeMachines_SecondTakesOver_ThenTheThird_AndTheSecondTakesItBackFromTheThird` | 4 | The whole three-machine sequence. |
| `ClaimForDirector_TwoDirectorsOnTheSameMachineOfATriggerWithBackups_HandOverOnlyAtTheLapse` | 5 | Two Directors on one machine still hand over only at the lapse, even on a trigger with backups. |
| `ClaimForDirector_MachineNamesDifferingOnlyInCase_AreTheSameMachine` | - | `MACHINE-B` is `machine-b`, as everywhere a trigger's machine is compared. |
| `ToDto_ATriggerWithAndWithoutBackups_NamesEveryMachineInOrder` | 9 | The words with none, one and two backups. |

Contract test 5 also rests on the existing trigger tests staying green **unchanged**: no existing test file was
edited. The existing calls that do not pass a machine keep today's behaviour exactly.

`src/CcDirector.Gateway.UnitTests/Factory/Definition/BackupMachinesDefinitionTests.cs` (13 tests) - contract tests 6,
7 and 9:

- The check accepts a list on an interval start with a check and keeps its order; it refuses the seat's own machine
  (ignoring case), a machine named twice (ignoring case), a list on a cron start, a list on a start with no check, a
  blank machine name and an empty list - each on the `backupMachines` line.
- A publish where a backup has no Python says so on that backup's line.
- The machine questions ask every backup for the check's secrets and its Python, and not for the seat's repository.
- The publish writes the list onto the trigger and names it in the step; a republish removing the holder's machine
  releases the claim; one keeping it keeps the claim; one dropping every backup releases nothing held on the seat's
  own machine.
- A changed list (including a change of order) is a changed start.

`src/CcDirector.Gateway.UnitTests/Data/AddTriggerBackupMachinesMigrationTests.cs` (1 test, SQLite) and
`src/CcDirector.Gateway.DatabaseTests/Data/AddTriggerBackupMachinesPostgresTests.cs` (1 test, PostgreSQL) - contract
test 8: each migration applies right after the one before it, keeps the stored triggers, gives none of them backups,
gives a held claim the trigger's machine and an unclaimed one none, leaves no model change pending, and its Down
removes both columns and keeps the rows. The PostgreSQL one also drives the real `TriggerStore` on PostgreSQL through
a handover and a hand-back.

`tools/cc-devthrottle/tests/test_trigger_ops.py`:
`test_list_and_show_print_where_the_check_runs_as_the_gateway_worded_it`.

**Contract test 10 is left for task F.** It needs the Mail Desk service's per-mailbox problem (phase 3 section 6),
which does not exist on this branch's base.

### Each test can fail

Every key promise was broken on purpose once, the backup tests run, and the code put back (`git checkout` of the one
file). Each break turned these red:

| Break | Red |
|---|---|
| An earlier machine never takes a live claim from a later one | hand-back, three machines, case, late start result (4) |
| The backups ignored: only the trigger's own machine is on the list | handover, hand-back, three machines, case, late start result, session on the backup, both publish claim tests (8) |
| A late start result takes the claim back | late start result (1) |
| The session always starts on the trigger's own machine | session on the backup (1) |
| A republish never releases a claim | republish removing the holder (1) |
| The seat's own machine accepted as a backup | the seat's machine refusal (1) |
| The machine questions skip the backups | the questions test, and all four publish tests (the check refuses to word a notice for a machine that was not asked) (5) |
| The migration's claim backfill removed | the SQLite migration test (1) |

## 4. How to run it

On devlinux (Linux), from the worktree:

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_ROOT=$HOME/.dotnet LOCALAPPDATA=$HOME/.local/share DOTNET_USE_POLLING_FILE_WATCHER=1
dotnet build src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true
dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~BackupMachines|FullyQualifiedName~AddTriggerBackupMachines"
pwsh -NoProfile -File scripts/test-database.ps1        # needs Docker
```

On Linux, `scripts/test-database.ps1` calls `powershell` by name to remove its throwaway database, which does not
exist there; this run put a two-line `powershell` script that runs `pwsh` on the `PATH`, outside the repository. On
Windows it runs as it is.

The command-line tool's tests:

```bash
cd tools/cc-devthrottle
uv run --no-project --with pytest --with typer --with click --with rich --with requests --with ../cc_storage --with ../cc_shared python -m pytest -q tests
```

## 5. The numbers (18 October 2026, devlinux)

| Run | Result |
|---|---|
| Backup tests (claim, definition, SQLite migration) | 25 passed, 0 failed |
| `Factory` tests of Gateway.UnitTests, before the new tests were added | 2,606: one failed - the format document did not name `backupMachines`; fixed by documenting it |
| `scripts/test-database.ps1`, the whole project, no filter | GREEN: 79 executed, 79 passed, 0 failed, **0 skipped** - including `AddTriggerBackupMachinesPostgresTests` |
| `cc-devthrottle` tool tests, the whole suite | 4,832 passed, 2 skipped (neither in the trigger tests) |
| The whole Gateway.UnitTests project, in three parts (one run outlasts a ten-minute tool call) | 14,487: 14,482 passed, 2 skipped (`GatewayStatsStoreDisposeReleasesTheFileTests`, not mine), 3 failed - `VoiceUnheardCountTests.NotePlayed_AResetWhoseSaveFailed_IsWrittenAgainByTheNextPlay`, `VoiceAutoOffLineTests.ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt`, `HeldDeliveryDriverTests.ARecordWriteThatThrowsAfterTheClaimMark_IsPressedAgainByTheDriver_ResolvedWithNoRestart`; all three fail the same way on unchanged origin/main (20491d6ef) on Linux |

## 6. What is NOT proven

- **No live failover on two real machines.** Every "Director" here is a name calling the service; that is phase 6's,
  if the owner's second machine is online.
- **A backup Director's own behaviour is not exercised.** No Director change was made: a Director runs what it is
  handed. That it does so for a trigger whose machine field names another machine is assumed because an assignment carries no
  machine at all, not tested here.
- **The seat starting on a backup is proved only as far as the start request** - which machine and which Director it
  names. That the backup can actually run the seat (its repository, its agent) is not checked at publish and not
  proven.
- **Contract test 10** (a backup with no mailbox sign-in named in the Mail Desk problem) waits for task F.
- **Rule 7 surfaces beyond the trigger list and show.** The words are on the trigger's answer and in the publish
  step. The factory page and the Cockpit were not changed: no client on this base shows a trigger's machine. The
  export's sentence "It runs every ... on <machine>" is for a command trigger, which cannot have backups, so it is
  unchanged.
- **A report from a caller that does not say its machine** (only tests and older callers inside the Gateway) leaves
  the holder's machine unknown when it takes a claim; such a claim can then only lapse, never be taken back early.
  The one production route always says the machine.
