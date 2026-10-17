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
- the launcher stopping it cleanly for an update or Smart Restart;
- the instance picker's hand-over.

So **a Director whose process is gone while its journal is still on disk did not end on purpose.** The journal
is read only after the process has left, because the shutdown deletes it before the process exits.

| How the Director ended | Journal | Launcher's record | What the watch does |
|---|---|---|---|
| Quit by a person, its shutdown signal | deleted | - | nothing |
| Stopped cleanly by the launcher (update, Smart Restart, tray Stop) | deleted | recorded | nothing |
| Stopped by the launcher with a kill after the timeout | left | recorded | nothing |
| Handed over to another Director (update, rollback) | deleted | - | watches the new one |
| Crashed, killed by Task Manager, killed for memory | left | - | starts it again, within the limit |

**What it does not see.** A Director that dies before it has registered was never seen running, so it is not
restarted. That is the safe direction: nothing is started that was not known to be running.

## Tests

`src/CcDirector.Launcher.Tests/DirectorCrashWatchTests.cs`, 14 tests (13 facts and a two-case theory). Each one
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
- **An update holding the swap lock:** the restart is held off and runs on the next look.
- **After a handover:** the watch follows the Director that took over, and restarts it if it crashes.
- **A development build:** a build that is not the installed image is not watched.
- **A machine that cannot be read** (several claimants, or one unreadable claim): nothing is started.
- **A start that throws:** it is tried again, and each try counts toward the limit.
- **A Director already running again when the start runs:** reported as such, not as a restart.
- **The ledger:** the supervisor records its stop before stopping, and the record answers only once.

None of them starts a real Director, waits on a real timer or depends on anything else on this machine. The
machine reading and the start are scripted, the clock is injected, and the crash journal is a real file in a
throwaway folder. The swap-lock test holds a test-only lock name. The supervisor tests name a process id
(`int.MaxValue`) that cannot exist.

**The tests can fail.** Each break below was made to the code alone, with the tests unchanged:

| Break | Tests that failed |
|---|---|
| The supervisor does not record its stop | `StopAsync_RecordsTheDirectorInTheLedgerBeforeStoppingIt`, `CheckOnceAsync_LauncherStoppedItWithAKill_IsNotStartedAgain` |
| Every disappearance is read as a crash | `CheckOnceAsync_DirectorQuitOnPurpose_IsNotStartedAgain`, `CheckOnceAsync_RegistrationGoneButProcessStillExiting_DecidesNothingUntilItHasLeft` |
| No restart limit | `CheckOnceAsync_CrashLoop_GivesUpAfterTheLimitUntilADirectorRunsAgain`, `CheckOnceAsync_StartThrows_IsTriedAgainAndCountsTowardTheLimit` |

Restored, all 14 pass.

**Not proven here.** The watch was not run against a real installed Director on a real machine; the brief
forbids changing a live machine's state. It is proven by the tests above and by the code paths they drive.

## What this changes for the owner's setup in pull request #4117

- **Windows and macOS:** once this merges, the owner's own setup is not needed and should not be added. The
  launcher does the same job and survives every handover.
- **If the owner had already added one:** both would start a Director after a crash. The second copy finds the
  first through the single-instance guard, shows its window and exits with 0. The result is one Director, never
  two, but the owner's setup should be removed.
- **Linux:** the systemd unit stays, because no launcher is installed there.

I will trim the Windows and macOS sections of `director-restart.md` when I answer the review of #4117, so the
document under review does not move now.
