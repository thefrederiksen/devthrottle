# Two live Directors on one engine.db: one run per occurrence, never overlapping

Proof for devthrottle_internal#2311, live proof finding F6. Branch `teams/2311-engine-shared-db`, cut from
origin/main e8f673ab9. Updated for review round 1 (findings EN-F1 and EN-F2), see the last sections.

## The contract (Tech Lead ruling, 6 October 2026)

- Two LIVE Directors on one `engine.db` never run the same due occurrence, and never overlap one.
- A Director never fails, releases or overwrites another live Director's run.
- **The exception, stated plainly:** after an ambiguous end - a Director killed, or the machine losing
  power - a job may run again, as it does today with one Director. That is at-least-once, never
  concurrently: the rerun waits until the earlier command process is proven gone.
- A file shared across machines over a network folder is out of scope.

## What was wrong (code at e8f673ab9)

When two Directors open the same `engine.db`:

- `Scheduler.cs:96-102` reads the due jobs and skips one only if THIS process is running it
  (`_runningJobs` is per process). `EngineDatabase.cs:253-262` (`GetDueJobs`) is a plain read, nothing is
  claimed. `JobExecutor.cs:26` then inserts a run unconditionally, and `JobExecutor.cs:52` moves
  `next_run` only after the job has finished. So both Directors see the job due and both run it.
- `EngineDatabase.cs:351-361` (`CleanupOrphanedRuns`, called at every start from `Scheduler.cs:36`) sets
  every run with no end time to failed, including a run another live Director has in progress.

## What changed

1. **A claim.** `EngineDatabase.TryClaimRun` checks and starts a run inside one `BEGIN IMMEDIATE`
   transaction: the job must be enabled, due, and have NO unfinished run. The unfinished run is the
   claim. `EngineDatabase.CompleteRun` records the result and moves `next_run` in one transaction, so
   there is no moment where the job looks due with nothing holding it. The scheduler raises
   `JobStarted` only after winning the claim. Connections also set `busy_timeout` so a Director waiting
   for the write lock backs off in SQLite's own short steps.
2. **A run owner.** Each run records the Director that started it: the Director home
   (`CC_DIRECTOR_ROOT`), the machine, the process id and the process start time. An id alone is not
   proof, because Windows reuses ids; id plus start time names one process.
3. **Cleanup fails only what is provably not running.** A run with no end time is failed when either:
   - its owner is on this machine and the operating system shows that process is gone (no process
     with that id, or the process with that id started at a different time). This is how this
     Director's own runs from a previous launch are failed.
   - it has been open longer than its job's timeout plus five minutes. Every executor kills a job at its
     timeout, so such a run is not in progress anywhere.

   Everything else is kept: a running owner, an owner on another machine, a process the probe may not
   inspect, and a run recorded before owners existed (until it ages past the timeout). A run of this
   very process is never failed by this process. Cleanup now also runs every scheduler tick, not only at
   start, so a run left by a Director that died does not hold its job's claim until someone restarts.
4. **Forward migration.** Opening a database adds `owner_director`, `owner_machine`, `owner_pid` and
   `owner_process_started` to `runs` when missing, under a write lock so two Directors opening one old
   file at once do not both add them. Old rows keep a null owner.

## Red on today's code

`red-on-e8f673ab9.txt`: the two tests in `SharedEngineDatabaseTests.cs` use only the API that existed at
e8f673ab9 and were run against that code before any source change.

- `TwoSchedulers_OneFile_SameDueJob_RunsExactlyOncePerRound`: "round 1: expected 1 runs in total, found 2".
- `SecondDirectorStarting_LeavesTheFirstDirectorsRunInProgressOpen`: the first Director's live run was
  marked failed.

## Revert proof

`revert-proof.txt`: removing only the "no unfinished run" condition from `TryClaimRun` turns four tests
red, among them the 300-round race ("round 1: 2 Directors claimed the same occurrence") and the
two-scheduler test. The source was restored afterwards and the gate below ran on a fresh build.

## The tests

- `EngineDatabaseSharedFileTests.TryClaimRun_TwoDirectorsOneFile_ManyRounds_ExactlyOneClaimPerOccurrence`:
  two `EngineDatabase` instances on one file, released together by a barrier, 300 rounds. Exactly one
  claims each round; the loser also cannot claim while the winner holds it; 300 runs in total.
- `SharedEngineDatabaseTests.TwoSchedulers_OneFile_SameDueJob_RunsExactlyOncePerRound`: two real
  `Scheduler`s, one-second ticks, real `echo` jobs, four rounds, one run per round.
- `CleanupOrphanedRuns_SecondDirectorStarting_FailsItsOwnStaleRunButNotTheFirstsLiveRun`: Director A has
  a run in progress; Director B's previous launch died with a run open; B starts. B's stale run is failed
  ("Interrupted by shutdown"), A's run is untouched, B can then claim its job and still cannot claim A's.
- `CleanupOrphanedRuns_UndecidableOwner_KeptUntilPastTimeoutPlusGrace`,
  `CleanupOrphanedRuns_ThisProcessesOwnOpenRun_IsKept`,
  `ProcessOwnerLiveness_CurrentProcess_IsRunning_AndAReusedIdIsGone` (the real probe: this process is
  running, the same id with another start time is gone, another machine is undecidable).
- `Open_DatabaseMadeByThePreviousEngine_MigratesForwardAndKeepsItsRows`: the fixture
  `src/CcDirector.Engine.Tests/Fixtures/engine-e8f673ab9.db` was WRITTEN BY THE ENGINE AT e8f673ab9 (a
  throwaway test in a scratch worktree at that commit, since removed): one job, one finished run, one
  open run. Opening it adds the four columns, keeps both rows, keeps the old open run while it could
  still be running (and it holds the job), fails it once past the timeout, and the job is claimable again.
- `Open_TwoDirectorsMigrateOneOldFileAtOnce_BothSucceed`.
- Two existing tests that asserted the old behaviour (fail every open run) now set up a run whose owner
  is gone: `EngineDatabaseTests.CleanupOrphanedRuns_MarksOpenRunsOfAGoneOwnerAsFailed`,
  `SchedulerTests.Start_CleansUpOrphanedRuns`.

## The gate

`test-local-default.txt`: `.\scripts\test-local.ps1` (default), every suite `outcome=Completed`,
`CcDirector.Engine.Tests` 80 of 80. The script names three parked suites as a coverage gap because a
project file changed. Their only link to the engine is a project reference (no test uses an engine type -
their `CronRunRecord` is the Gateway's own), and all three were built and compile. They were not run.

## What this proof does NOT cover

- The two "Directors" in the tests are two connections in one test process, not two processes. SQLite's
  locking is per file handle, so the same locks apply, but no test starts two operating-system processes.
  No Director was started, by instruction.
- A Director still running the OLD engine on the same file does not claim, so it can still run a job a
  new Director is running. Once every Director on the machine is upgraded this is closed.
- A run owned by a Director on ANOTHER machine (a shared engine.db on a synced folder) is never judged by
  process; it is failed only after its timeout plus five minutes.

## Review round 1: the claim covers the command (EN-F1) and is judged fairly (EN-F2)

The section "What changed" above describes the first version. Review round 1 found two holes, and
these rules now replace the cleanup rule given there.

**EN-F1 - a claim owned only the Director, not its command.** A cancelled command was left running
(`ProcessJob` killed only on timeout) while its run ended with `next_run` still due, and cleanup released
a dead Director's claim without asking whether its command had survived. Now:
- `ProcessJob` kills the command's whole process tree on ANY cancellation and waits up to ten seconds to
  see it exit, then throws `JobCancelledException` saying whether it did. A timeout does the same wait.
- The run records the command process (`child_pid`, `child_process_started`) the moment it starts.
- A cancellation (or timeout) that is not seen to exit leaves the claim OPEN. Only a proven stop ends the
  run, and then `next_run` stays due so the occurrence runs again - after, never alongside.
- Cleanup releases a gone Director's claim only when its command process is proven gone too (same
  id-plus-start-time proof), or it never recorded one. A surviving command keeps the claim until it ends.

**EN-F2 - a live owner could be aged out by a mutable timeout, and a late completion overwrote the
verdict.** Now:
- The run stores the timeout it was claimed with (`run_timeout_seconds`); the executor uses that value
  and cleanup judges by it, never by the job's current, editable timeout.
- A positively running owner is NEVER aged out by another Director. Age-out applies only when the owner
  cannot be decided (another machine, an uninspectable process, a run from before owners existed).
- `CompleteRun` is fenced on "still open and still mine". A revoked owner's late result is stored in
  `late_completion` and changes neither the run's verdict nor `next_run`.
- The migration adds the four new columns too, and back-fills `run_timeout_seconds` once from each
  job's timeout at migration time - the best record there is of what an old run ran with.

The cleanup rules in full, for each unfinished run:

| Owner | Decision |
|---|---|
| This process, or probed RUNNING | kept, always |
| Probed GONE, command recorded | failed only if the command is probed GONE too; otherwise kept |
| Probed GONE, no command recorded | failed |
| Undecidable, or no owner recorded | failed once open longer than its stored timeout plus five minutes |

**Red on the reviewed head.** `red-review1-on-3823c55c5.txt`: `ClaimCoversTheCommandTests.cs` uses only API
that existed at 3823c55c5 and was run there in a scratch worktree. All five fail:
- `CancelledMidRun_TheCommandStops_AndTheOtherSchedulerDoesNotStartItWhileItLives` - "the cancelled
  command kept running after its Director shut down". A real looping command, two real schedulers.
- `DirectorGoneWithItsCommandStillAlive_NoSecondStart` - the run was failed while its command ran.
- `JobEditedToAShorterTimeoutMidRun_UndecidableOwner_IsJudgedByTheTimeoutItWasClaimedWith` - aged out at
  400 seconds by the edited one-second timeout.
- `LiveOwner_IsNeverAgedOutByAnotherDirector` - a running owner's run was failed.
- `RevokedOwnersLateCompletion_ChangesNeitherTheVerdictNorTheSchedule` - the late result overwrote it.

Further tests only the fix can compile: a gone Director with one dead and one live command (only the
dead one's run is failed), a late completion is kept as late and reports not applied, an owner's own
completion applies, the migration adds all eight columns and back-fills 300 seconds, and
`ProcessJob` cancelled mid-run reports the command stopped and the real probe finds it gone.

Engine tests: 89 of 89. The new tests passed three runs in a row.

**What round 1 does NOT cover.**
- A Director that dies in the instant between starting the command and recording it leaves a command
  nobody knows about; cleanup then releases the claim once the Director is proven gone. The window is
  one database write long.
- "Proven stopped" is the command's shell process exiting after the tree kill. Its descendants were
  sent the kill but are not each waited for.
- A cancellation not proven stopped inside a Director that stays alive (an engine restarted in the same
  process) keeps the claim until that process exits; nothing in the app restarts the engine in-process.
- The trigger runner (`DirectorTriggerRunner`) also uses `ProcessJob`: a cancelled trigger check now kills
  its command instead of leaving it running. It still sees an `OperationCanceledException`. Its 9 tests
  (`DirectorTriggerRunnerTests`, in the parked `Gateway.UnitTests`) were run by filter and pass
  (`trigger-runner-tests.txt`); the rest of that parked suite was built, not run.

Gate after round 1: `test-local-default.txt`, every suite Completed, Engine 89 of 89. ControlApi and the
three parked test projects build.

## CC_VAULT_PATH - not changed here

How it resolves today: `CcStorage.Vault()` (`src/CcDirector.Core/Storage/CcStorage.cs:236-243`) returns
`CC_VAULT_PATH` when it is set, and only otherwise `<CC_DIRECTOR_ROOT>\vault`. `EngineDb()`
(`CcStorage.cs:619`) is `Vault()\engine.db`. Each Director sets `CC_DIRECTOR_ROOT` to its own instance home
at start (`Program.cs:287-289`), but `CC_VAULT_PATH` wins over it, so when it is set at the user level,
as on the owner's computer, every Director instance on the machine shares one vault and one engine.db.
The Python `cc-vault` tool reads the same variable first (`tools/cc-vault/src/config.py:59-68`).

What changing it would mean: if a Director stopped honouring `CC_VAULT_PATH` for engine.db (or for the
vault as a whole), each instance would open a fresh, empty engine.db in its own home. Scheduled jobs and
run history in the shared file would no longer be seen by any Director, so jobs would silently stop
running unless they were copied over. Moving the vault itself would also move where the owner's personal
data (vault.db, documents, transcripts) is read from, which `cc-vault` would no longer match. That is a
data move and an owner decision; this change makes the shared file safe instead, whichever path leads
two Directors to it.
