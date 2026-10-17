# Answers to the review of pull request 4118

The review is `review-4118.md`. It found no defect in the watch's logic, made two findings, and listed nine
observations. Every point is answered below, in the review's order. The branch was rebased onto origin/main
9d0539b29 first, with no conflicts.

## Finding 1 - the gate did not finish green on this commit

**Settled by the Tech Lead,** who is running the gate on the head. The rebase and the changes below are a new
head, so the run must be on that commit; its results go into `launcher-crash-restart.md` when the Tech Lead sends
them.

## Finding 2 - the proof repeats the Windows-only "second copy exits 0" claim

**Agreed, and corrected.** The section "What this changes for the owner's setup in pull request #4117" now says
what a second copy does on each platform:

- **Windows:** it brings the first copy's window forward and exits with 0.
- **Everywhere else:** it exits with 1 after the "already running" notice, because `TryRaiseExistingWindow` is
  Windows-only.

On macOS an owner's launch agent would then loop every 30 seconds, and the section now says that this is why
removing an owner setup there is not optional. It also says #4117 has been cut down to Linux, where the unit's
start condition skips any start while a Director is running.

## Observations

1. **A Director that dies before its journal exists is not restarted, and the table said every crash is.**
   Corrected. The table has a row for it ("nothing: read as a deliberate end"), and "What it does not see" names
   both cases: before registration, and before the journal.
2. **A restarted Director that never comes up ends the watch silently.** Agreed that a line would help, and not done
   here. It needs a second piece of state - "started at T, not yet seen" - and a rule for when to give up waiting,
   which is more than a wording fix. The restart limit is not affected. I suggest it as follow-up work if the
   Tech Lead wants it.
3. **`MarkClean` failing silently is the one path from a quit to a restart.** Agreed. It is now written in the
   proof as a known limit, under "What it does not see". Changing `MarkClean` is in the Director, outside this pull
   request.
4. **Two sign-in sessions of one account each run a launcher and a watch.** Agreed. That machine already runs two
   Directors on one instance before this change, so it is not this pull request's to fix.
5. **A ledger record that outlives a stop that did not end the process.** **Fixed.** `DirectorStopLedger.Forget`
   takes the record back, and the supervisor calls it in two places:
   - where it refuses to kill a process not certified as the installed image;
   - where the kill fails. `KillProcess` now answers whether the process is gone.

   The new test `Forget_AStopThatDidNotEndTheProcess_DoesNotExcuseALaterCrash` proves a taken-back record no longer
   excuses a later crash: that crash is restarted. With `Forget` made to do nothing, the test fails. The two call
   sites are read, not tested: reaching them needs a live process that ignores its shutdown signal, which on Linux
   and macOS means a real 20-second wait, and the test standard forbids that.
6. **A record consumed only when the watch sees the gap can sit for the hour.** Agreed, and harmless, as the review
   says: records are keyed by Director id and process id, so no later crash of another process is excused.
7. **"The instance picker's hand-over" does not go through the shutdown routine.** Corrected. The proof now says it
   skips the shutdown but happens before the journal exists, so the outcome is the same.
8. **Logging.** Nothing to answer. The watch logs each change of state and each decision, and nothing on a quiet
   look.
9. **No real name in the diff.** Checked again after these changes: nothing.

## The macOS point the review made in this change's favour

The review noted that this change starts the Director through `/usr/bin/open`, which runs the bundle's real entry
point and so gives the Director the search path it needs. That is exactly what the owner's launch agent in #4117 got
wrong. The proof now says so, under `DirectorStopLedger`.

## Tests

15 tests in `DirectorCrashWatchTests.cs`: the 14 the review read, plus the `Forget` test. All pass on the rebased
branch (`dotnet test` of the launcher tests filtered to `DirectorCrashWatchTests`). The gate is the Tech Lead's run,
under finding 1.
