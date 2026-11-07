# Issue #4748: Schedule emails "Schedule failed" when the Director is merely slow

## Root Cause

When a scheduled run's start outcome was not known (the Director didn't answer within 30 seconds), the run was recorded with `Result = StartUnknown` and `ResultUtc = firedUtc` (set immediately).

The `ScheduleFailureNotifier` scans the run history every 30 seconds and considers any run with `Result = StartUnknown` or `Result = Problem` as a failure. Because `ResultUtc` was set immediately for `StartUnknown` runs, the notifier would send a false "Schedule failed" email right away, even though the session might start minutes later and complete successfully.

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

### 3. ScheduleFailureNotifierTests.cs: Regression test
Added test `AStartUnknownRun_IsNotToldImmediately_BecauseTheDirectorMayBeSlowButTheSessionMayStillStart` that verifies:
- A `StartUnknown` run does NOT send a failure notification immediately
- This proves slow Directories won't trigger false alarms

## What Was NOT Changed

- The 30-minute window logic (MinimumNameWindow) - still in place to look for the session
- The failure notifier hourly rate limit - still prevents notification spam
- The "heal by name" logic that matches sessions to `StartUnknown` runs

## Test Verification

- Regression test added to `ScheduleFailureNotifierTests.cs` that fails on today's code and passes with the fix
- Test verifies that `StartUnknown` runs do not trigger immediate failure notifications
- All existing 17 ScheduleFailureNotifier tests still pass

## Behavior Change

**Before**: A slow Director triggers a false "Schedule failed" email within 30 seconds

**After**: A slow Director does NOT trigger a failure email until the 30-minute window closes and no session appears

If a run genuinely fails (no session appeared by window close), it's then reported as a failure.

## Not Proven

- End-to-end behavior with real slow Director responses
- Whether the UI shows "may not have started" correctly for `StartUnknown` runs still waiting for the window
- Whether owners receive a "recovered" message if an alarm was sent and the run then completes
