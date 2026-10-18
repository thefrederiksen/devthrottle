# Review of pull request 4118 - the launcher starts a crashed Director again (Track B, phase 6)

Reviewer: a separate review session, a different agent family from the Developer, opened by the Track B Tech Lead (session 111). Read only. Nothing in the worktree was edited, built or committed.

## Scope

**What I read.** The whole diff of 4118 against its base 6c783e3d4 (origin/main has since moved to 0702c88ce; the branch is one commit on the older base, and nothing it touches was changed by the two commits since): the new `DirectorCrashWatch.cs` (the watch and the `DirectorStopLedger`), the ten-line change to `DirectorSupervisor.cs`, the wiring in `LauncherCore.cs`, `LauncherTrayController.cs` and the launcher's `Program.cs`, the 14 tests in `DirectorCrashWatchTests.cs`, the proof `launcher-crash-restart.md` and the one-line capabilities document change. Against the claims, on the base commit: `DirectorInstanceLocator` (resolve, certification of the installed image, the alive check, the journal directory it reads), `DirectorCrashJournal` (where it is written, `MarkClean`, `DetectAndClaim`), `App.axaml.cs` (where the journal is created and deleted, the fatal start-up path), `Program.cs` of the Director (every early exit), `MainWindow.OnClosing` and the Smart Shutdown coordinator, `DirectorUpdateOwner` (the swap lock's scope), `BinarySwapLock`, `InstanceRegistration`, the launcher's own single-instance mutex, and the macOS packaging script.

**What I ran.** Nothing. A reviewer never builds into the worktree, and the launcher tests need a build. Every test claim is checked by reading the test. I swept the diff for real names, machine names, addresses, email domains and user paths.

**What I could not reach.** A real crash on a real installed Director (the Developer did not either, and says so). launchd and macOS behaviour in general. Whether the gate is green: the Developer's run was not.

## Verdict

**No defect found in the watch's logic that must change before merge.** Within the scope above, a quit is never restarted, a crash is, a stop the launcher made is excused, the give-up holds, and the swap lock is honoured. Two findings for the Tech Lead's judgment: the gate did not finish green on this commit, and the proof repeats the Windows-only "second copy exits 0" claim already found wrong in the review of 4117. Then a set of observations about edges the proof does not mention, none of which I can turn into a concrete failure worth blocking on.

## The questions the Tech Lead asked, answered from the code

### Crash versus quit, decided by the crash journal

The rule is: process gone, and `<instance home>/config/director/crash-journal/<id>.json` still on disk, equals crash. I checked both halves.

**The path is the one the Director writes.** The watch builds it by hand (`DirectorCrashWatch.CrashJournalPath`), and so does the locator's `ReadSessionCount` (`DirectorInstanceLocator.cs:592`) that the update path already relies on in production; the Director writes to `CcStorage.ToolConfig("director")/crash-journal/<id>.json`, which is the same folder under the instance home it runs with. Consistent with existing, exercised code.

**Every deliberate end deletes it, with the exceptions below.** `App.OnShutdown` calls `CrashJournal.MarkClean()` (`App.axaml.cs:1106`) before `Environment.Exit(0)` (line 1131), and `OnShutdown` is reached from the window close (`MainWindow.axaml.cs:7133`, which is also where the Smart Shutdown flow lets the close carry on), from the shutdown signal (`App.axaml.cs:769`), and from the programmatic shutdown path. The registration is deleted in the same routine, before the journal, which is why the watch's "registration gone, process still here" reading waits (`StillExiting`) rather than deciding - and that order is correct: a clean shutdown always deletes the journal before the process can be gone, so a journal found after the process is gone was never going to be deleted.

The journal cases the Tech Lead named, one by one:

| Journal left by | What the code does | Right? |
|---|---|---|
| **A force-kill by the launcher** | `StopAsync` records (id, pid) in the ledger BEFORE the signal (`DirectorSupervisor.cs:394-396`); the watch consumes the record on the first look after the process is gone and answers `StoppedByLauncher`. | Yes. Tested with the real supervisor. |
| **An update** | The launcher's update owner stops the Director through the same `StopAsync` (ledger), swaps and starts under the swap lock. The Director-side `--apply-update` relauncher and `TryApplyStagedUpdateAtStartup` run BEFORE the journal exists (`Program.cs:234` returns 0; the journal is created in `InitializeServices`, `App.axaml.cs:936`), so there is no journal to leave. | Yes. |
| **Smart Restart** | Ends through the window close, which calls `OnShutdown` (`MainWindow.axaml.cs:7133`) and deletes the journal. A restart asked of the launcher goes through `RestartAsync`, which records the ledger. | Yes. |
| **A Director that never wrote one** (journal creation failed, `App.axaml.cs:945`; or died between registering and line 936) | The watch reads "no journal" as `EndedOnPurpose` and starts nothing. A crash of such a Director is NOT restarted. | The safe direction, but the proof's table says a crash is always restarted; it is not for this Director. See observation 1. |
| **A journal from before this launcher started** (a crash during sign-out, a launcher restart) | A fresh watch has `_watched` null, so it never acts on a journal it did not see written. `NothingToWatch`. | Yes - this is what keeps a sign-in from restarting whatever died last session. |
| **A fatal start-up error AFTER the journal is created** (`desktop.Shutdown(1)`, `App.axaml.cs:992`) | Programmatic `Shutdown` does not run `OnShutdown`, so the journal stays. If the watch saw the Director registered in the few hundred milliseconds before it died, it restarts it, up to five times, each showing the start-up error notice. | Acceptable: the limit stops it, and the notice is the right thing to show. Observation 2. |

**The one way a quit can be misread.** `MarkClean` swallows its own failure (`DirectorCrashJournal.cs:315-327`): a journal that cannot be deleted at that instant (a scanner's hold, a full disk) is logged and left, the Director exits 0, and the watch restarts a Director the person quit. I cannot name a case where the delete fails on a healthy machine, so this is an observation (3), not a finding.

### Two launchers, or a launcher plus a person starting it

**Two installed launchers in one sign-in session cannot run:** the launcher takes a single-instance mutex keyed on its root (`Program.cs:16-17, 113-116`), and only a `--managed` launcher runs the watch. A development launcher never starts the installed Director. A launcher self-update stops the old launcher and the new one starts with an empty watch.

**Two sign-in sessions of one account on one machine** (a console session and a remote one) each run a launcher: the launcher's mutex has no `Global\` prefix, so it is per session, and the Director's own guard is `Local\` too (`SingleInstanceGuard.cs:30`). Two watches would then both see a crash and both start; only the swap lock (`Global\`, `BinarySwapLock.cs:48`) is machine-wide, and it is released between the two starts, so it does not serialise them. That is a machine already running two Directors on one instance before this change, so it is not this pull request's to fix. Observation 4.

**A person starting it after a crash.** The supervisor's `Start` resolves the machine first and skips when a Director holds the instance (`DirectorSupervisor.cs:258-263`), and the watch reports `AlreadyRunningAgain` - tested. The race is the person's Director being between launch and registration when the watch's start fires (a window of seconds against a 15-second poll): then two copies start, and the Director's guard ends the second - with exit 0 on Windows when the first one's window can be raised, otherwise exit 1 with the "already running" notice (the review of 4117 has the detail). The watch counts that as one restart. Rare, bounded, one dialog at worst.

### The 5-in-10-minutes give-up

`RestartAfterCrashAsync` drops restarts older than the window, refuses at five, logs one GIVING UP line, and sets `_gaveUp`; a Director seen running again clears it and the list (`Watch`). A start that throws is counted first (`_restarts.Add(now)` before `_startDirector()`), so a Director that cannot be started at all also ends in a give-up; a start held off by the swap lock is NOT counted, which is right because nothing started. Three tests cover it (the loop, the window, the throwing start), and the mutation note says the limit's removal failed two of them. Correct.

One edge the proof does not say: after a give-up, a person starting ONE Director that then crashes at once re-arms a full five more restarts (the list is cleared on `Watching`). That is the stated design, and I think it is the right one.

### The swap lock

`RunExclusivelyAsync` with no `waitFor` tries the `Global\` mutex once and returns `whenBusy` at once (`BinarySwapLock.cs:105-118`); the watch keeps `_pendingCrash` on that answer and tries on the next look. The update owner holds the same lock across claim, stop, swap, start and the health wait (`DirectorUpdateOwner.cs:320-359`), so during an update the watch cannot start the old build, and after a rollback the restored build is simply seen running. A crash DURING an update's health wait is held off until the owner releases the lock, by which time the owner has either confirmed or rolled back and a Director is running - the pending crash is then cleared by `Watch`. Correct, and tested with a held mutex on its own thread.

### Does anything restart a Director on a machine where the person quit it?

**No**, by the code paths above: the quit deletes the journal before the process exits, and a fresh launcher never acts on a journal it did not see. The two residual ways are the `MarkClean` failure (observation 3) and a quit made while a Director the watch had already decided to restart was still starting - which is not a quit of a running Director, and the watch's start skips when one holds the instance.

### macOS

The watch works there: the locator certifies the bundle's process by prefix (`DirectorInstanceLocator.cs:527-530`: the executable under `~/Applications/Director.app/`), the supervisor starts through `/usr/bin/open`, which honours the bundle's real entry point `launch` and so gives the Director the PATH it needs - the very thing the owner's launch agent in 4117 gets wrong. This is a point in favour of 4118 over the owner's setup, and worth a sentence in the proof.

## Findings

### Finding 1 - the gate did not finish green on this commit

The Developer reports `test-local.ps1` on cc70ad3c8 stopped `CcDirector.Avalonia.Tests` at the two-minute budget because the machine was at 100% from other work, the script refusing a re-run of an over-budget run, and a hand `dotnet test` of that suite passing 1,063 in 64 seconds. The launcher suite (228, the one this change touches) completed. I believe the explanation, and the change does not touch the Director. But the rule is that the gate goes green, the over-budget verdict is a verdict of the script, and a two-minute re-run on a quiet machine is cheap. The Tech Lead may accept the hand run or ask for one clean gate run before merge.

### Finding 2 - the proof repeats the Windows-only "second copy exits 0" claim

Under "What this changes for the owner's setup", the proof says that if the owner had added their own setup as well, "the second copy finds the first through the single-instance guard, shows its window and exits with 0". `TryRaiseExistingWindow` returns false off Windows (Director `Program.cs:382-388`), so on macOS the second copy exits 1 with a notice and the owner's launch agent then restarts it every 30 seconds for ever (the review of 4117, finding 2). The conclusion the proof draws - remove the owner's setup on Windows and macOS once this merges - is right; the sentence justifying it is wrong on macOS and should say so, because it is the reason the removal is not optional there.

## Observations (no action required)

1. **A Director that dies before it has a journal is not restarted**, and the proof's table says a crash always is. The watch's class comment says it correctly ("a Director that dies before it has registered was never seen running"); the gap is the few hundred milliseconds between registration and journal creation, and a journal whose creation threw. Safe direction; one sentence in the proof's table.
2. **A restarted Director that never comes up ends the watch silently.** After `Restarted`, both `_watched` and `_pendingCrash` are null; if the new process dies before registering, the next look is `NothingToWatch` and the log's last word is "started the Director again". A line saying "the Director started N seconds ago has not registered" would help the owner reading the log after a bad night. The restart limit is not affected, since nothing is seen crashing.
3. **`MarkClean` failing silently is the one path from quit to restart.** Pre-existing code; the watch makes its failure matter more than before. A retry, or treating "journal present but `LastUpdatedUtc` older than the registration's start" as not-a-crash, would close it; neither is needed for merge.
4. **Two sign-in sessions of one account** each run a launcher and a watch (per-session mutexes), on a machine that would already be running two Directors before this change. Not this pull request's.
5. **A ledger entry that outlives a stop that did not end the process.** `Record` runs before the signal; if the signal is not delivered and the kill is refused (an uncertified image) or fails, the Director keeps running with a one-hour excuse in the ledger, and a genuine crash in that hour is read as `StoppedByLauncher`. The pre-conditions are rare (the supervisor always has an installed path to certify against), so I note it; a `Forget` on the refused-kill paths would remove it.
6. **A ledger entry is consumed only when the watch sees the gap.** An update that stops and restarts within one 15-second poll leaves its entry until the hour's retention. Harmless: entries are keyed by (id, pid), so no later crash is excused by it.
7. **The proof lists "the instance picker's hand-over" as going through the shutdown routine.** It does not (`desktop.Shutdown()`, `App.axaml.cs:107`, is the programmatic path that skips `OnShutdown`), but it runs before any journal exists, so the outcome is the same. A wording fix.
8. **Logging.** The watch logs on every change of state and every decision, and nothing on a quiet look; `RunAsync` logs start and stop. That meets the Tech Lead's "every step is logged" without a line every 15 seconds.
9. **No real name, machine name, address or user path** in the diff.

## Tests, read not run

All 14 exist and assert what the proof's sentences say. The scripted machine reading, the counted start, the injected clock and the real journal file in a throwaway folder make them independent of the machine; the swap-lock test holds a test-named mutex on its own thread, which is the right way to hold a mutex across an `await`. The two tests built on the real `DirectorSupervisor` use `int.MaxValue` as a pid that cannot exist, so the stop sees the process gone without touching anything. The mutation table names six failures across three breaks, which is consistent with what each test asserts.
