# Two Directors on one engine.db never run a job twice

Proof for devthrottle_internal#2311, live proof finding F6. Branch `teams/2311-engine-shared-db`, cut from
origin/main e8f673ab9.

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
