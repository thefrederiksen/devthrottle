# Two live Directors on one local engine database never run an occurrence at the same time (at-least-once after a crash)

Proof for devthrottle_internal#2311, live proof finding F6. Branch `teams/2311-engine-shared-db`, cut from
origin/main e8f673ab9. Updated for review rounds 1 (EN-F1, EN-F2) and 2 (EN-F3, EN-F4, EN-F5), see the
last sections. Scope: a database file on this machine. A file shared over a network folder is not covered.

## The contract (Tech Lead ruling, 6 October 2026)

- Two LIVE Directors on one `engine.db` never run the same due occurrence, and never overlap one.
- A Director never fails, releases or overwrites another live Director's run.
- **The exception, stated plainly:** after an ambiguous end - a Director killed, or the machine losing
  power - a job may run again, as it does today with one Director. That is at-least-once, never
  concurrently: the rerun waits until the earlier command process is proven gone.
- **The second exception, stated plainly (round 3):** an overlap is possible ONLY for a command whose
  identity was lost (its process id or start time was never recorded) and that is still running past
  its own timeout plus five minutes. Such a run is released at that deadline, never earlier.
- **No job is held forever:** a run whose command cannot be proven gone ends at its deadline (the timeout
  stored on the run plus five minutes) at the latest. A RECORDED command that the operating system shows
  still running keeps its claim for as long as it runs (see the round 3 note on this reading).
- A file shared across machines over a network folder is out of scope.
- Every decision about an open run is made from the database, never from memory that can be lost.

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

## Review round 2: an unrecorded command, and a command that exits late (EN-F3, EN-F4)

**EN-F3 - recording the started command could fail outside the kill protection.** The command started,
the write that records it threw, and the run was then completed with `next_run` moved while the
unrecorded command kept running. Now `ProcessJob` reads the start time and calls the record inside the
same protection as a cancellation: on any failure it kills the process tree, confirms the exit, and
throws `CommandNotRecordedException` saying whether it stopped. The executor ends the run exactly as a
confirmed cancellation (without moving `next_run`) only when it stopped; otherwise the claim stays open
and the command is watched from memory (the executor learns the process id before the write, so it
knows it even when the record failed). The executor's catch-all for other failures also watches a
started command that is not proven gone instead of completing over it.

**EN-F4 - a killed command that exited after the ten-second window held its job forever.** The claim
was kept (correctly) but nothing ever looked again while the Director lived. Now every run whose
killed command was not seen to exit - a timeout, a cancellation, a failed record, a failure while it
ran - is kept in the executor's watch list, and every scheduler tick calls `ReleaseConfirmedStops`:
once the command (process id plus start time) is proven gone, the run ends and the claim is released
exactly as a confirmed cancellation, so the occurrence runs again. A command still running, or one that
cannot be decided, keeps the claim.

**EN-F5 - the short forms overstated the promise.** The heading above and the pull request title now
carry the scope (a local database) and the exception (at-least-once after a crash).

Tests:
- `ProcessJobTests.ExecuteAsync_RecordingTheStartedCommandFails_TheCommandIsKilled` - red on the reviewed
  head a2dc91d4d ("the command whose record failed was left running", `red-review2-on-a2dc91d4d.txt`).
- `UnconfirmedStopTests.RecordingTheCommandFails_TheCommandIsKilled_TheRunEndsWithoutMovingTheSchedule_AndNobodyStartsItWhileItLived`
  - a real looping command; the record write is made to throw; a second Director tries to claim the
  whole time the execution runs and never can; afterwards the command is gone, the run ended, `next_run`
  is still due.
- `UnconfirmedStopTests.KilledCommandExitsAfterTheConfirmationWindow_TheClaimIsReleasedOnALaterTick_AndTheNextOccurrenceRuns`
  - a real Scheduler; the first execution reports a kill not confirmed while a real process keeps
  running for about three seconds. While it lives: one execution, the run open, the job unclaimable.
  After it exits: a tick ends the run and the occurrence runs again.
- These two use a test seam added in this round (the executor's job factory and record hook), so they
  cannot compile on the reviewed head. Their red is shown by reverting the fix instead
  (`revert-proof-review2.txt`): without the per-tick release the late-exit test fails; without the kill
  in the record failure path, the executor test and the `ProcessJob` test both fail. Source restored
  from the commit afterwards.

Engine tests: 92 of 92. Default gate (`test-local-default.txt`): every suite Completed. Its first attempt
was stopped because `CcDirector.Core.UnitTests` passed the two-minute ceiling
(`test-local-default-first-attempt-over-budget.txt`); that suite is untouched by this change and ran in
67 seconds in the round-0 gate, and the immediate rerun was green. The trigger runner's 9 tests pass
again (`trigger-runner-tests.txt`).

**Superseded by round 3.** The in-memory watch list described in this section was removed in round 3,
because it could be lost (EN-F6, EN-F7); the rule below replaces it.

## Review round 3: every decision about an open run comes from the database (EN-F6, EN-F7)

**EN-F6 - a dead Director's unrecorded command lost its claim at once.** The command's identity lived
only in the dead Director's memory, so another Director saw "owner gone, no command" and released it
while the command could still run. **EN-F7 - the only revisit could be lost while the owner lived.** An
engine restarted in the same process got an empty watch list, and its own runs were never re-examined;
a command whose start time could not be read was never watched at all.

**The rule now (Tech Lead ruling, round 3).** The watch list is gone. The executor keeps in memory ONLY
which jobs it is claiming or executing right now. Every tick, `CleanupOrphanedRuns(now, jobInFlightHere)`
reads every open run from the database and decides:

| Run | Decision |
|---|---|
| Owned by this process, job being executed here | kept |
| Owned by this process, NOT being executed here (a killed command not seen to exit, or a run left by an engine restarted in this process) | judged by its command, below |
| Another owner, probed RUNNING | kept - never aged out |
| Another owner, probed GONE | judged by its command, below |
| Owner undecidable, or none recorded | ended at its deadline |

Judged by its command, from the row:

| Command on the row | Decision |
|---|---|
| Recorded, probed GONE | ended at once |
| Recorded, probed RUNNING | kept while it runs |
| Recorded, undecidable | ended at its deadline |
| Not recorded, but the starting mark is set (identity lost) | ended at its deadline, never earlier |
| Not recorded and no starting mark (it never started) | ended at once |

The deadline is the run's started time plus the timeout STORED on the run plus five minutes. Ending a run
here never moves `next_run`, so the occurrence runs again. The **starting mark** (`command_starting_at`,
a new column, added by the same forward migration) is written just BEFORE the command process starts.
That is what separates "this run never started a command" (released at once, as before - so a Director
that dies between claiming and starting does not hold the job for its whole timeout) from "a command may
be running and we lost who it is" (held to the deadline). If the mark cannot be written, the command is
not started.

**A reading I took, for the Tech Lead.** The ruling says both "a command that cannot be proven gone is
released at its deadline" and "every open run ends by its deadline plus the margin at the latest". A
RECORDED command that the operating system positively shows still running past its deadline is not
"cannot be proven gone" - it is proven alive - and the round 1 ruling says a surviving child keeps the
claim. I kept it held while it runs, so the only overlap remains the lost-identity case the ruling
names. If the intent was a hard ceiling for that case too, it is a one-line change in `DecideByCommand`.

Tests (`DatabaseDecidesOpenRunsTests.cs`):
- `EngineRestartedInALiveProcess_UnconfirmedStop_IsReleasedWhenTheCommandExits` - a first engine's
  command is reported killed but not seen to exit (a real process that runs about three more seconds);
  a second database, executor and scheduler are started in the same live process. While the command
  lives the run stays open and nothing runs; after it exits a tick releases it and the job runs again.
- `RecordFailed_OwnerThenGone_NoSecondStartBeforeTheDeadlinePlusMargin_AStartAfterIt` and
  `StartTimeUnreadable_OwnerThenGone_...` - the record write fails (or the start time is unreadable) and
  the kill is not confirmed; the owner is then gone. Another Director cannot end or claim it now, nor
  five seconds before the deadline; five seconds after, it ends it and can claim.
- `StartTimeUnreadable_OwnerStillLive_TheOwnerItselfReleasesItAtTheDeadlinePlusMargin` - the same, judged
  by the live owner itself, which is not executing it any more.
- `OwnerGone_CommandNeverStarted_IsReleasedAtOnce` - no starting mark: released immediately.

Red on the reviewed head b369273b4 (`red-review3-on-b369273b4.txt`): the restart test ("the restarted
engine never released the claim after the command exited") and both owner-gone deadline tests fail. The
never-started test passes there too, as it should - it guards behaviour kept from before. The
owner-still-live test calls the new cleanup overload, so it was left out of that run (noted in the file).

Engine tests: 97 of 97; the round 2 and round 3 timing tests passed three runs in a row. Default gate
(`test-local-default.txt`): every suite Completed, first attempt. Trigger runner: 9 of 9.

**What round 3 does NOT cover.** The overlap named in the contract: a command whose identity was lost
and that is still running past its deadline. A Director whose clock is badly wrong judges deadlines by it.

## Review round 4: a recorded command is asked first, and nothing is released without proof (EN-F8, EN-F9)

**EN-F8 - an undecidable owner skipped the recorded command.** When the owning Director could not be
inspected, cleanup went straight to the deadline and released a command the operating system could still
see running. Now a recorded command is asked FIRST, whatever the owner's state (this replaces the order of
the round 3 tables; their rows still apply when no recorded command decides):

| Recorded command | Decision |
|---|---|
| Probed RUNNING | kept, whoever the owner is |
| Probed GONE | ended at once - unless the owner is another LIVE Director, which records its own result |
| Undecidable, or none recorded | the owner rules of round 3 decide (deadline at most) |

**A reading I took, for the Tech Lead.** The ruling says "proven Gone releases it" for every owner state.
For an owner that is another live Director I keep the run instead: its command has just exited and that
Director is about to record the result. Releasing it would end a live Director's run and turn its real
result into a late one, against the contract line "A Director never fails, releases or overwrites another
live Director's run". That Director's own tick releases the run if it is no longer executing it.

**EN-F9 - a missing starting mark was taken as proof.**
1. *Stale read.* Cleanup read the rows, decided, and ended a run with only "still open" as the condition.
   Now the end is conditional on EXACTLY the state it was decided from: the same starting mark, the same
   recorded command and the same owner, compared with SQLite's null-safe `IS`. If a mark was written or a
   command recorded in between, nothing changes and the run is decided again next tick.
2. *The executor checks its own writes.* Writing the starting mark already refused to start the command
   when it changed no row. Recording the command now does the same: zero rows means the run was ended
   under it, so it throws inside the kill protection and the command's process tree is killed and its exit
   confirmed.
3. *Migration.* When the starting mark column is added, every run still open is marked as possibly started
   (the mark is set to its start time). Such a run is held to its deadline unless a recorded command proves
   it gone - never released as "never started".

Tests (`NoReleaseWithoutProofTests.cs`):
- `OwnerUndecidable_RecordedCommandRunningPastTheDeadline_IsKept` - a real running process recorded as the
  command, the owner undecidable, cleanup an hour past the deadline: kept, and the job cannot be claimed.
- `CleanupReadsBeforeTheStartingMark_TheExecutorMarksInBetween_NoReleaseAndNoOverlap` - a test hook
  (`AfterOpenRunsRead`) writes the starting mark between cleanup's read and its end, as the old executor of
  a restarted engine would: nothing is ended, and another Director cannot claim the job.
- `ChildRecordAffectsZeroRows_TheCommandIsKilled` - a real 30-second command; the run is ended under the
  executor just before the record lands: the command is gone within seconds, and the other release stands.
- `MigratedOpenOwnerBearingRunWithoutAMark_IsReleasedOnlyAtItsDeadline` - an owner-bearing open run in a
  database whose `runs` table has no starting mark column, opened and migrated, its owner gone: not ended
  now nor five seconds before the deadline; ended five seconds after.

Red on the reviewed head 7d14dbaa6 (`red-review4-on-7d14dbaa6.txt`): the undecidable-owner, zero-row and
migration tests all fail there ("the command kept running after its run was ended under it" for the
second). The stale-read test needs the new hook, so it was left out of that run; its red is in
`revert-proof-review4.txt` - removing only the starting-mark condition from the conditional end turns it,
and only it, red.

Engine tests: 101 of 101; the round 3 and 4 tests passed three runs in a row. Default gate
(`test-local-default.txt`): every suite Completed, first attempt. Trigger runner: 9 of 9.

**What round 4 does NOT cover.** When recording the command finds the run already ended and the kill is
not confirmed within ten seconds, the claim is already gone and the command may still run; that is the
lost-identity overlap named in the contract. The migration test builds its old database by dropping the
new column from a current one, not from a binary written by 7d14dbaa6.

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
