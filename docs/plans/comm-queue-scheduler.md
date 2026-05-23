# Comm-Queue Scheduler (in-process, leader-elected)

## Status: PLANNED

Owner: Soren. Designed 2026-05-22 after the LinkedIn connection-request runner (`scripts/linkedin-connect-from-queue.py`) shipped and we needed a way to fire it automatically without standing up a separate Windows service.

## Problem

Approved items in the comm-queue (`%LOCALAPPDATA%/cc-director/config/comm-queue/communications.db`) sit indefinitely unless a human invokes a runner. We want them to send automatically on a schedule. Constraints:

- **No extra install footprint.** No `nssm`, no Administrator step, no new Windows service. Everything must live inside the cc-director Avalonia app the user already runs daily.
- **Session-bound.** If the user is not logged in to the machine, nothing should send. Running as the logged-in user (not SYSTEM) achieves this naturally - no extra logic needed.
- **Concurrent-safe.** The user routinely runs more than one `cc-director` Avalonia instance simultaneously. Only one instance can run the scheduler at any moment, and ownership must hand off automatically when the current owner exits.
- **Machine-local.** Vault and comm-queue are per-machine. No cross-machine coordination needed or wanted.

## Why not the existing `scheduler/` Python service

The repo already has a `cc_director` Python service at `scheduler/cc_director/` deployed via `nssm` (see `scheduler/deploy.bat`). It supports cron jobs and has a SQLite comm-queue dispatcher. **We are not using it** because:

- Requires a separate install step (Administrator + `nssm install`).
- Runs as SYSTEM, which would attempt sends even when the user is signed out.
- Adds a second always-on process to maintain.

This document supersedes that path for the comm-queue use case. The Python service may still be useful for tasks that genuinely need to run when the user is away; comm-queue sends are not one of those tasks.

## Design

### Component layout

New namespace `CcDirector.Core/Scheduler/` (placed in `CcDirector.Core` so both `CcDirector.Avalonia` and any future headless/test harnesses can reuse it).

| File | Purpose |
|---|---|
| `SchedulerService.cs` | Singleton service. Starts on app startup, stops on app shutdown. Holds the leader-election primitive and the tick loop. |
| `MutexLeaderElection.cs` | Wraps a Windows named mutex (`Global\cc-director-scheduler`). Exposes `TryAcquire()`, `Release()`, `IsHeld`, plus a 30s background re-acquisition poll for followers. |
| `CommQueueScheduler.cs` | The tick body. Reads `communications.db` for items that match a registered runner; invokes the runner via `Process.Start`. |
| `IRunnerRegistration.cs` | Interface so we can register runners (linkedin-connect, linkedin-dm, gmail-send, etc.) without scattering knowledge across the service. |

### Leader election

Named mutex `Global\cc-director-scheduler`.

- **Acquire path (startup):** `Mutex.TryOpenExisting(name, out m)` -> if exists, this instance is follower. If not, `new Mutex(initiallyOwned: true, name)` -> this instance is leader.
- **Failover path (every 30s on followers):** `Mutex.TryOpenExisting`. If the existing mutex disappeared (leader process exited), promote: `new Mutex(initiallyOwned: true, name)`.
- **Release path:** on clean app shutdown, `mutex.ReleaseMutex(); mutex.Dispose()`. Windows automatically releases a kernel mutex when the holding process dies, even on crash - no stale-lock cleanup code needed.
- **Logging:** leader state changes (`"now scheduler leader"`, `"stepping down"`) log to `%LOCALAPPDATA%/cc-director/logs/cc-director.log`. Include instance PID for debuggability when multiple windows are open.

### Tick loop (leader only)

- Interval: default 5 minutes (`SchedulerOptions.TickInterval`). Configurable via `appsettings.json` or env var.
- On each tick:
  1. Open `communications.db` read-only.
  2. For each registered runner, query for items matching the runner's filter (e.g. linkedin-connect runner filters by `platform='linkedin' AND type='message' AND tags LIKE '%connection-request%' AND status='approved'`).
  3. If matching items exist, invoke the runner's command (e.g. `python D:\ReposFred\cc-director\scripts\linkedin-connect-from-queue.py`) via `Process.Start` with stdout/stderr captured to log.
  4. The runner itself does the per-item marking-as-posted via `cc-comm-queue mark-posted`. Scheduler does not touch DB state directly.
- **Jitter:** before invoking, sleep a random 0-60 minutes if the runner declares `RespectHumanCadence = true`. The first scheduled fire of the day for LinkedIn should not be deterministic at 08:00:00.000 - LinkedIn pattern-matches that.
- **Per-runner cooldown:** keep an in-memory `Dictionary<string, DateTime> LastFiredAt` keyed by runner name. Do not refire a runner within 30 minutes of its last fire. Protects against tick storms during recovery.

### Runner registration

Each runner registers itself in `App.axaml.cs` startup:

```csharp
schedulerService.RegisterRunner(new RunnerRegistration {
    Name = "linkedin-connect",
    QueueFilter = "platform='linkedin' AND type='message' AND tags LIKE '%connection-request%' AND status='approved'",
    Command = "python",
    Args = new[] { @"D:\ReposFred\cc-director\scripts\linkedin-connect-from-queue.py" },
    Schedule = Cron.Daily("08:00"),       // simple cron-ish helper
    RespectHumanCadence = true,           // 0-60min jitter
    MinIntervalBetweenFires = TimeSpan.FromHours(1),
});
```

Cron-ish parsing is intentionally minimal: daily-at-time, weekdays-only-at-time, every-N-minutes. Anything more complex, write code.

### Integration points

- **Startup:** `App.axaml.cs` constructs `SchedulerService` as a singleton, calls `Start()`. Pass it `IConfiguration` for the tick interval + runner list.
- **Shutdown:** `App.axaml.cs` `Exit` event calls `schedulerService.Stop()`, which releases the mutex if held and stops the timer.
- **Multiple instances:** each Avalonia process constructs its own `SchedulerService`. They race for the mutex on startup. Loser polls every 30s for failover.

## Acceptance criteria

1. Two Avalonia instances launched concurrently: exactly one logs `"now scheduler leader"`. The other logs `"scheduler follower, polling for leadership"`.
2. Kill the leader: within 60s the follower logs `"now scheduler leader"`.
3. Approved LinkedIn connection-request items in `communications.db` are sent on the next tick after their scheduled time, with 0-60 min jitter, and marked `posted` by the runner.
4. No `nssm`, no `sc create`, no Administrator step required to deploy. A fresh user clone-and-runs cc-director and gets the scheduler.
5. When neither Avalonia instance is running, nothing sends. (Verified by observing the queue across an overnight gap with the app closed.)

## File list (estimate)

- `src/CcDirector.Core/Scheduler/SchedulerService.cs` (~150 lines)
- `src/CcDirector.Core/Scheduler/MutexLeaderElection.cs` (~80 lines)
- `src/CcDirector.Core/Scheduler/CommQueueScheduler.cs` (~120 lines)
- `src/CcDirector.Core/Scheduler/RunnerRegistration.cs` (~30 lines)
- `src/CcDirector.Core/Scheduler/Cron.cs` (~80 lines for the minimal helper)
- `src/CcDirector.Core.Tests/Scheduler/MutexLeaderElectionTests.cs` (~100 lines - spawn two test processes, assert one wins)
- Edits to `src/CcDirector.Avalonia/App.axaml.cs` (~30 lines)

Total: ~600 lines including tests.

## Open questions

- Where do runner registrations live long-term? Inline in `App.axaml.cs` is fine for now, but eventually probably a config file.
- Should followers also tail the leader's log so the user can see scheduled-job output regardless of which window has focus? Probably yes, post-MVP.
- Test approach for the mutex election. Manual two-process verification is enough for v1; automated test in `CcDirector.Core.Tests` is a stretch.
