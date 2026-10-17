# Phase 6, track B - the launcher starts a crashed Director again

## Why

On 16 October 2026 a Director died and stayed down until the owner noticed, and while it was down its machine
could not be switched to another license. The Director restart proof (`director-restart.md`, pull request
#4117) found that the product installs nothing that brings a crashed Director back. It gave the owner a
restart setup of their own for each platform. That setup has one cost it cannot avoid: it watches the one
process it started, so the first update or Smart Restart ends its watch.

The Tech Lead ruled that the product owns the launcher, so the launcher should close the gap on Windows and
macOS. Three conditions came with the ruling: a deliberate exit stays quit, a crash loop stops and says so, and
every step is logged. On Linux no launcher is installed, so the owner's systemd unit stays the answer there.

## What changed

**`DirectorCrashWatch` (new, in the launcher).** Every 15 seconds it reads the machine the way the supervisor
already does (`DirectorInstanceLocator`). It acts as follows:

- **An installed Director is running:** the watch follows that one. It follows the instance, not a process:
  after an update or Smart Restart, the Director that took over is the one watched.
- **The watched Director is gone:** the watch decides between a crash and a deliberate end, as described in the
  next section. A crash leads to starting the installed Director again, through the supervisor's own `Start`.
- **The registration is gone but the process is still exiting:** it waits for the next look.
- **The machine cannot be read to one answer** (several claimants, or a registration that will not read): it
  does nothing.
- **A development build holds the instance:** it is not watched, because it is not the launcher's to bring
  back.
- **Crash loops:** at most 5 restarts in 10 minutes. After that, one log line says the watch is giving up and
  why, and nothing more is started until a Director is seen running again (a person started one by hand).
- **Updates:** the start runs under the machine-wide swap lock, without waiting for it. If an update or a
  launcher swap holds the lock, the restart is held, not dropped, and runs on the next look.

**`DirectorStopLedger` (new).** The supervisor records a Director BEFORE stopping it. The watch consumes that
record, so a stop the launcher made itself is never read as a crash. This matters because a stop can end in a
kill, and a kill leaves the crash journal behind, exactly like a crash.

A stop that does NOT end the process takes its record back (`Forget`): either the kill is refused, because the
process is not certified as the installed image, or the kill fails. Otherwise a Director still running would carry
an excuse for an hour, and a real crash in that hour would be read as the launcher's own stop. That was review
observation 5.

**The Director it starts has the search path the agents need.** On macOS the supervisor starts the Director with
`/usr/bin/open` on the application, which runs the bundle's real entry point (`Contents/MacOS/launch`). That entry
point puts the folders the agents are installed in on the search path. The owner's launch agent in the first
version of #4117 pointed past it, and the review of #4117 found that defect.

**Started by the installed launcher only.** `LauncherCore.RunDirectorCrashWatchAsync` is started beside the
update loop, in the tray and in headless mode, only when the launcher runs with `--managed`. That is the
installed launcher, the same rule the update loop uses.

`docs/architecture/CC_DIRECTOR_CAPABILITIES.md` says this in one line.

## How a crash is told from a deliberate exit

**Not by the exit code.** That was the owner setup's rule, and it cannot work here. On macOS the launcher does
not start the Director as its own child: `/usr/bin/open` hands it to launchd, and a process cannot read the
exit code of one it did not start.

**By the crash journal, which works the same on every platform.** A running Director keeps
`<instance home>/config/director/crash-journal/<id>.json`, and its clean shutdown deletes it
(`DirectorCrashJournal.MarkClean`, called from `App.OnShutdown`). Every deliberate end of a running Director goes
through that shutdown:

- a person quitting it;
- its shutdown signal;
- the launcher stopping it cleanly for an update or Smart Restart.

(The instance picker's hand-over does not go through that shutdown, but it happens before the journal exists, so
there is nothing for it to leave.)

So **a Director whose process is gone while its journal is still on disk did not end on purpose.** The journal
is read only after the process has left, because the shutdown deletes it before the process exits.

| How the Director ended | Journal | Launcher's record | What the watch does |
|---|---|---|---|
| Quit by a person, its shutdown signal | deleted | - | nothing |
| Stopped cleanly by the launcher (update, Smart Restart, tray Stop) | deleted | recorded | nothing |
| Stopped by the launcher with a kill after the timeout | left | recorded | nothing |
| Handed over to another Director (update, rollback) | deleted | - | watches the new one |
| Crashed, killed by Task Manager, killed for memory | left | - | starts it again, within the limit |
| Crashed before its journal was written (the first moments after it registers, or a journal that could not be created) | none | - | nothing: read as a deliberate end |

**What it does not see.** Two kinds of crash are not restarted:

- **A Director that dies before it has registered** was never seen running.
- **A Director that dies before its crash journal exists** looks like a deliberate end. That is the moment
  between registering and creating the journal, or a journal whose creation failed.

Both are the safe direction: nothing is started that was not known to have crashed.

**The one way a quit could be read as a crash.** The clean shutdown deletes the journal, and if that delete fails
(`DirectorCrashJournal.MarkClean` logs the failure and carries on), a Director the person quit would be started
again. I know of no case where the delete fails on a healthy machine. Changing `MarkClean` is outside this pull
request, so it is a known limit, written down here.

## Tests

`src/CcDirector.Launcher.Tests/DirectorCrashWatchTests.cs`, 16 tests (15 facts and a two-case theory). Each one
in a sentence:

- **A crash:** a Director that dies with its journal on disk is started again, once.
- **A quit:** a Director that deleted its journal is not started again, on that look or any later one.
- **A launcher stop that ends in a kill:** not started again, even though the journal is left. This test uses
  the real supervisor and the watch's public constructor, so an attempted start would throw.
- **A shutdown in progress:** while the registration is gone but the process has not left, the watch decides
  nothing.
- **A crash loop:**
  - five restarts, then a give-up, and no sixth start;
  - a Director started by hand turns crash restarts back on.
- **Spread-out crashes:** restarts older than ten minutes do not count toward the limit.
- **An update holding the swap lock:** the restart is held off and runs on the next look. This is proven on the
  REAL lock, so the production path is shown to take it.
- **A lock held over several looks:** every look holds the restart off and none counts toward the limit; the first
  look after it is free starts the Director, once.
- **After a handover:** the watch follows the Director that took over, and restarts it if it crashes.
- **A development build:** a build that is not the installed image is not watched.
- **A machine that cannot be read** (several claimants, or one unreadable claim): nothing is started.
- **A start that throws:** it is tried again, and each try counts toward the limit.
- **A Director already running again when the start runs:** reported as such, not as a restart.
- **The ledger:** the supervisor records its stop before stopping, and the record answers only once.
- **A stop taken back:** a record the supervisor takes back with `Forget` (refused or failed kill) no longer excuses
  a later crash: that crash is restarted. With `Forget` made to do nothing, this test fails.
  The two supervisor call sites of `Forget` were read, not tested: reaching them needs a live process that ignores
  its shutdown signal, which on Linux and macOS means a real 20-second wait.

None of them starts a real Director, waits on a real timer or depends on anything else on this machine. The
machine reading and the start are scripted, the clock is injected, and the crash journal is a real file in a
throwaway folder. Every test but one uses a stand-in for the swap lock that the test holds or frees, so no
outcome depends on when a real mutex is let go; the one real-lock test holds a test-only lock name. The supervisor tests name a process id
(`int.MaxValue`) that cannot exist.

**The tests can fail.** Each break below was made to the code alone, with the tests unchanged:

| Break | Tests that failed |
|---|---|
| The supervisor does not record its stop | `StopAsync_RecordsTheDirectorInTheLedgerBeforeStoppingIt`, `CheckOnceAsync_LauncherStoppedItWithAKill_IsNotStartedAgain` |
| Every disappearance is read as a crash | `CheckOnceAsync_DirectorQuitOnPurpose_IsNotStartedAgain`, `CheckOnceAsync_RegistrationGoneButProcessStillExiting_DecidesNothingUntilItHasLeft` |
| No restart limit | `CheckOnceAsync_CrashLoop_GivesUpAfterTheLimitUntilADirectorRunsAgain`, `CheckOnceAsync_StartThrows_IsTriedAgainAndCountsTowardTheLimit` |

Restored, all 14 passed. With the `Forget` test and the held-lock test added after review, all 16 pass.

**Under load.** At 39d82be42 the Tech Lead's gate failed `CheckOnceAsync_CrashLoop_GivesUpAfterTheLimitUntilADirectorRunsAgain`
(expected `Restarted`, got `RestartHeldOff`). It passed alone. The cause is worked out from the code, not observed:

- `RestartHeldOff` is returned only when the named mutex is owned at that instant.
- In that test the only owner of the name is the watch's own previous look.
- `BinarySwapLock` hands back its answer before its thread releases the mutex, so a look that follows at once can
  find the earlier hold.
- It did not reproduce: the old code passed 15 runs in a row with every core busy.

The tests now use a stand-in lock instead (`DirectorCrashWatch.RunUnderSwapLock`, a seam that production fills with
`BinarySwapLock`). In production, looks are 15 seconds apart, which makes that moment irrelevant.

The whole Launcher suite (230 tests) then passed **5 of 5 with every processor core busy**, under 24 processes the
test run started to load them, and stopped after. That was checked to be the new binary: it lists 16 crash-watch
tests. Before that, it passed 5 of 5 under the machine's own 96% load from other work.

**Not proven here.** The watch was not run against a real installed Director on a real machine; the brief
forbids changing a live machine's state. It is proven by the tests above and by the code paths they drive.

## What this changes for the owner's setup in pull request #4117

- **Windows and macOS:** with this change, no owner setup is needed, and none should be added. The launcher does
  the same job and survives every handover. #4117 has been cut down to Linux for that reason.
- **An owner setup added anyway would fight it, worse on macOS than on Windows.** After a crash, both would start a
  Director, and the second copy is stopped by the single-instance guard. What that copy does next depends on the
  platform:
  - **Windows:** it brings the first copy's window forward and exits with 0, so an owner's watcher stands down.
  - **Everywhere else:** it cannot bring the window forward, because `TryRaiseExistingWindow` is Windows-only. It
    shows "Director is already running" and exits with 1. An owner's launch agent with `KeepAlive` then starts it
    again every 30 seconds for ever, with a dialog each time.

  That is why removing an owner setup on macOS is not optional. This sentence was wrong in the first version of
  this proof (review finding 2).
- **Linux:** the systemd unit in #4117 stays, because no launcher is installed there. Its start condition skips any
  start while a Director is running, so it cannot loop in this way.
