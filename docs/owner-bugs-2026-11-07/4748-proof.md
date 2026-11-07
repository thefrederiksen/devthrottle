# Issue #4748: Schedule emails "Schedule failed" when the Director is merely slow

## Root Cause

When a scheduled run's start outcome was not known (the Director didn't answer within 30 seconds), the run was recorded with `Result = StartUnknown` and `ResultUtc = firedUtc` (set immediately).

The `ScheduleFailureNotifier` scans the run history every 30 seconds and considers any run with `Result = StartUnknown` or `Result = Problem` as a failure. Because `ResultUtc` was set immediately for `StartUnknown` runs, the notifier would send a false "Schedule failed" email right away, even though the session might start minutes later and complete successfully.

Additionally, there was no recovery notification mechanism: if a failure alarm was sent and the run then recovered (a subsequent run succeeded), the owner was never told the issue resolved.

## Solution

Changed the code to NOT set `ResultUtc` for `StartUnknown` runs when they're first recorded. Set it to `null` instead.

- **File**: `src/CcDirector.Gateway/Running/CronEngine.cs` line 319
- **Change**: Set `record.ResultUtc = null` for `StartUnknown` runs instead of `firedUtc`

This way:
1. When a run has `StartUnknown`, it's not yet considered a failure (because `FailedSince` requires `ResultUtc != null`)
2. The `ScheduleFailureNotifier` won't send an immediate false alarm
3. When the window closes (30 minutes later):
   - If the session appeared, `AdoptStartUnknown` changes the run to `Result = Pending` with `ResultUtc = null` - it's no longer a failure
   - If the session didn't appear, `LapseStartUnknown` changes the run to `Result = Problem` with `ResultUtc = now` - now it's a real failure

## Changes Made

### 1. CronEngine.cs: Don't set ResultUtc for StartUnknown runs
```csharp
if (startUnknown)
{
    record.Result = CronRunResults.StartUnknown;
    record.ResultReason = StartUnknownReason(error);
    record.ResultUtc = null;  // Changed from: record.ResultUtc = firedUtc;
}
```

### 2. ScheduleFailureWords.cs: Handle lapsed StartUnknown runs
Added logic to recognize when a `Problem` run is actually a lapsed `StartUnknown` (the reason format indicates it):
```csharp
// A start whose outcome was not known lapses when its window closes
if (reason.Contains("whether it started a session was not known", StringComparison.Ordinal) 
    && reason.Contains("when its window closed", StringComparison.Ordinal))
    return StartUnknown(reason, machine);
```

### 3. ScheduleFailureNotifierTests.cs: Regression and recovery tests
Added two tests:
- `AStartUnknownRun_IsNotToldImmediately_BecauseTheDirectorMayBeSlowButTheSessionMayStillStart`: verifies that a `StartUnknown` run does NOT send a failure notification immediately (proves slow Directories won't trigger false alarms)
- `ARunThatWasMarkedFailed_ThenRecovers_NotifiesTheOwnerOfRecovery`: verifies that when a previously-failed schedule has a successful run, a recovery notification is sent

### 4. ScheduleFailureNotifier.cs: Recovery notification mechanism
Added logic to track which schedules have had failure notifications and detect recoveries:
- Tracks failed jobs in `_jobsWithFailureNotified` set
- Calls new `ScanRecoveriesAsync()` method after processing failures
- Sends recovery notifications with `Succeeded = true` and `Reason = "The schedule has recovered."`

### 5. CronRunHistoryStore.cs: Success run lookup
Added `SuccessRunsSince(DateTime, string jobId)` method to find successful runs for a given schedule after a specific time

## What Was NOT Changed

- The 30-minute window logic (MinimumNameWindow) - still in place to look for the session
- The failure notifier hourly rate limit - still prevents notification spam
- The "heal by name" logic that matches sessions to `StartUnknown` runs

## Test Verification

Two regression tests, each seen red on the code without the fix and green with it:

- `ASlowDirectorIsNotAFailedScheduleTests.AFireWhoseDirectorDidNotAnswerInTime_TellsTheOwnerNothing_WhileTheSessionMayStillStart`
  drives a fire through the real `CronEngine` (a starter that answers "outcome not known", as `DirectorCronSessionStarter`
  does when the Director is slow) and then the real `ScheduleFailureNotifier`. Red: `CronEngine.cs` replaced with the
  origin/main version, the scan sent 1 notice (`Expected: 0, Actual: 1`). Green with the fix.
  The earlier test `AStartUnknownRun_IsNotToldImmediately...` builds its record by hand with `ResultUtc = null`, so it
  passes on main too and proves nothing about the engine; it is kept only as a notifier-level check.
- `ScheduleFailureNotifierTests.ARecoveryNoticeThatFails_FailsTheScan_AndTheNextScanTellsTheOwner`. Red: the notifier
  from the previous commit swallowed the failure (`Assert.Throws() Failure: No exception was thrown`). Green now.

The gate: `.\scripts	est-local.ps1 -Parked` at commit 63f0da343 (rebased on origin/main 40c1e1257), every suite
outcome=Completed, 0 failed: Core.UnitTests 3639, Avalonia.Tests 1272, Engine.Tests 85, HostedAgent.Tests 85,
Launcher.Tests 234, Terminal.Avalonia.Tests 111, Reclaim.Tests 307, DeliveryQualification.Tests 151, RuleHook.Tests 70,
cc-director-setup.Tests 43, cc-director-setup-engine.Tests 1023 of 1029 (6 skipped), cc-director-setup-cli.Tests 78,
Gateway.Tests 426 + 482 + 439 of 442 (3 skipped) + 489, Core.Tests 4643 of 4661 (18 skipped), Gateway.UnitTests 17483.
The run had to put TEMP on drive D because drive C had 4.4 GB free.

## Behavior Change

**Before**: A slow Director triggers a false "Schedule failed" email within 30 seconds

**After**: A slow Director does NOT trigger a failure email until the 30-minute window closes and no session appears

If a run genuinely fails (no session appeared by window close), it's then reported as a failure.

## Swallowed failures removed (takeover, 7 November 2026)

The previous commit answered a review by adding eleven catch blocks inside helper methods - `Claim`, push, each
recovery channel, the recovery loop, three around `SaveChanges`, and two in `Dispose`. Every one is removed: they hid
failures (repository rules 3 and 4). A failure in a claim, a push or a recovery notice now fails the scan, the scan
loop's per-tick handler (the true entry point) logs it, and the next scan tries again. A recovery is forgotten only
once the owner has been told, or the schedule no longer exists. `Dispose` caught `TimeoutException`, which `Task.Wait`
with a timeout never throws, and disposed the scan gate while a scan that outlived the five-second wait could still
release it; both are gone. The two per-channel catches in the failure path (fleet notice, Your inbox) were on main
before this pull request and are unchanged.

## Not Proven

- End-to-end behavior with real slow Director responses
- Whether the UI shows "may not have started" correctly for `StartUnknown` runs still waiting for the window
- A recovery notice whose fleet notice succeeded but whose push or inbox write failed is sent again on the fleet channel at the next scan (the owner may hear twice on that channel)
- The recovery set is held in memory: a Gateway restart between the failure notice and the recovery forgets it, and no recovery is told
- Recovery notification delivery through all channels (fleet, push, inbox message display)
- Whether recovery notifications reach owners in real-world scenarios
