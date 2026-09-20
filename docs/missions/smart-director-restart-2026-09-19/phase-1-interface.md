# Phase 1 interface - what the screens of phase 2 call

Settled by the Tech Lead of phase 1 on 19 September 2026, read against `origin/main` at `7e040db5b`.
This is the ONE interface between the engine (phase 1) and the way down screens (phase 2). Phase 2
builds against it with a fake of its own until the real types land; the real types will carry exactly
the names and shapes below. If phase 2 needs something that is not here, it asks the Delivery Lead,
who asks phase 1 - it never reaches into the engine's files.

Everything lives in the project `src/CcDirector.ControlApi`, namespace
`CcDirector.ControlApi.SmartRestart`. The Avalonia project already references that project.

## 1. How a screen gets the engine

    ISmartShutdown ControlApiHost.CreateSmartShutdown()

Never null. A Director that cannot do a smart shutdown says why through `CheckAsync` below; it does
not hand back nothing.

## 2. The engine

    public interface ISmartShutdown
    {
        Task<SmartShutdownAvailability> CheckAsync(SmartShutdownPurpose purpose, CancellationToken ct);
        ISmartShutdownRun Start(SmartShutdownRequest request);
        Task<IgnoreAllResult> ShutDownIgnoringAllAsync(SmartShutdownRequest request, CancellationToken ct);
        Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct);   // see section 4
    }

    public enum SmartShutdownPurpose { Close, Restart }

    public sealed record SmartShutdownRequest(
        SmartShutdownPurpose Purpose,
        TimeSpan TimeAllowed,      // exactly one of SmartShutdownTimes.Allowed; anything else throws
        string? Reason);           // optional, the owner's words; written into the record

    public static class SmartShutdownTimes
    {
        public static readonly IReadOnlyList<TimeSpan> Allowed;   // 5, 10, 15, 30, 60 minutes
        public static readonly TimeSpan Default;                  // 10 minutes
    }

    public sealed record SmartShutdownAvailability(
        bool CanSmartShutdown, string? SmartShutdownRefusal,   // false when the Gateway cannot be reached:
                                                               // the record must live off the machine
        bool CanRestart, string? RestartRefusal);              // false when this Director is not the one
                                                               // its launcher would restart (a dev slot)

- `CheckAsync` is asked when the dialog opens. It changes nothing. It may take a second or two (it
  asks the Gateway), so the dialog opens at once and fills this in when it arrives.
- The dialog's own review (question boxes open, working against waiting) is NOT here. It is phase 2's,
  read from the session manager and `PendingInteraction`, as the phase 2 mandate says.
- `Start` returns at once, before anything is asked of any session. It throws
  `InvalidOperationException` with the reason when a run is already under way on this Director.
- `ShutDownIgnoringAllAsync` writes the record first, then ends every session. It works with the
  Gateway unreachable too (section 7 of the mission document): then `RecordWritten` is false and
  `RecordRefusal` says why, and the sessions are still ended, because the owner chose to discard them.

    public sealed record IgnoreAllResult(
        bool RecordWritten, string? WorkspaceId, string? RecordRefusal, int SessionsEnded);

## 3. One run - what the progress screen subscribes to

    public interface ISmartShutdownRun
    {
        SmartShutdownSnapshot Current { get; }
        event Action<SmartShutdownSnapshot>? Changed;
        Task<SmartShutdownResult> Completion { get; }
        void ShutDownNow();
        void CancelAndKeepWorking();
    }

- `Changed` is raised on every change of any session's state or of the phase, and at least once per
  poll (ten seconds). It is raised on an ENGINE thread: the screen dispatches to the UI thread itself.
  Each snapshot is complete and immutable; the screen replaces what it shows, it never merges.
  The handler must return at once. It dispatches to the user interface thread ASYNCHRONOUSLY (a
  post, never a synchronous invoke). A handler that blocks stalls the run, the two thirds stage, the
  limit and the "Shut down now" button, and holds the one-run gate so that no later smart shutdown
  can start.
- `Current` is valid from the moment `Start` returns (phase `Starting`, no rows yet is possible).
- The time left is the screen's own clock against `LimitUtc`; the engine does not tick every second.
- `ShutDownNow` and `CancelAndKeepWorking` return at once and never throw. Each is honoured only while
  the snapshot says it may be (`CanShutDownNow`, `CanCancel`); otherwise it is ignored and logged. The
  effect arrives as snapshots.
- `Completion` never faults for an expected end. It completes with the result below.

    public sealed record SmartShutdownSnapshot(
        SmartShutdownPhase Phase,
        string PhaseLabel,                 // plain words, shown as is
        IReadOnlyList<SmartShutdownSessionProgress> Sessions,   // leads first, each lead followed by
                                                                // the sessions under it
        int Total,
        int Gone,                          // verified absent from this Director
        string CountLabel,                 // "4 of 9 shut down" - shown as is
        DateTime StartedUtc,
        DateTime InterruptAtUtc,           // two thirds of the time allowed
        DateTime LimitUtc,
        bool CanShutDownNow,
        bool CanCancel,
        string? WorkspaceId,               // the record on the Gateway, once it exists
        string? Note);                     // the most recent thing worth saying, plain words

    public enum SmartShutdownPhase
    {
        Starting,        // writing the record; nothing asked yet
        Asking,          // the requests are going out
        Collecting,      // waiting for handovers, closing finished sessions as they land
        Interrupting,    // two thirds reached: sessions still mid-turn are interrupted and asked again
        EndingAtLimit,   // the limit, or Shut down now: every session still present is ended
        Cancelling,      // Cancel and keep working: telling open sessions, bringing closed ones back
        Restarting,      // Purpose Restart only: emptied, asking the launcher
        Finished
    }

    public sealed record SmartShutdownSessionProgress(
        string SessionId,
        string Name,
        string? Mission,
        string? Role,
        string? OwnerSessionId,            // the lead it sits under, null for a lead or a standalone
        SmartShutdownSessionState State,
        string StateLabel,                 // plain words, shown as is
        string? Detail);                   // why, when there is a why (the delivery refusal, for one)

    public enum SmartShutdownSessionState
    {
        Pending,        // in the record, not asked yet (a session under a lead is asked BY its lead)
        Asked,          // the request landed
        NotDelivered,   // the request could not land (a wedged session); Detail carries the reason
        Writing,        // a handover file exists and is not finished
        HandedOver,     // the handover was read and accepted, or its lead's handover covers it
        Interrupted,    // interrupted at two thirds and asked to hand over now
        ShutDown,       // gone, after handing over                                   (terminal)
        EndedAtLimit,   // ended by the engine at the limit, conversation id recorded  (terminal)
        KeptRunning,    // after a cancel: still open, told the restart is off         (terminal)
        BroughtBack     // after a cancel: was closed, a new session reads its handover (terminal)
    }

The screen decides LAYOUT only. What a state is called and what the count says come from the engine
(`StateLabel`, `PhaseLabel`, `CountLabel`), so a new state is one edit in the engine and no new branch
in a window. A screen may colour by `State`; it never words by it.

    public sealed record SmartShutdownResult(
        SmartShutdownOutcome Outcome,
        string? WorkspaceId,
        string Detail,                     // plain words, shown as is
        SmartShutdownSnapshot Final);

    public enum SmartShutdownOutcome
    {
        Emptied,          // Purpose Close: the Director is empty; the CALLER now closes the application
        RestartAccepted,  // Purpose Restart: the launcher accepted; this process is about to be stopped
        RestartRefused,   // Purpose Restart: emptied, but the launcher would not restart it; Detail says
                          // why. The record stands and is offered on the next start
        Cancelled,        // the Director holds the same missions it started with; record marked cancelled
        Refused,          // never started (Gateway unreachable, a run already under way); nothing touched
        Failed            // stopped on an error; Detail says what, the record says how far it got
    }

The engine never closes the application and never shows a window. On `Emptied` the screen closes the
application. On `RestartAccepted` the launcher stops the process; the screen does nothing.

## 4. What phase 2 may rely on

- A call on this interface never blocks the UI thread for longer than it takes to return.
- A fake needs: a list of rows, a way to push a snapshot, and the two buttons flipping `CanShutDownNow`
  and `CanCancel` to false once used. Nothing else in a screen should depend on the engine.
- The operating system shutting down (mission 10.5) is NOT a run: no dialog and no progress screen.
  Phase 1 exposes it as `Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct)` on
  `ISmartShutdown`: it writes the record (names, repositories, conversation ids) and returns; it ends
  nothing itself.

## 5. What this interface does not cover, and who owns it

- The record's new marks (a session ended at the limit, a record that was cancelled, a record that came
  from a smart shutdown or from an ignore-all) are phase 1's, in `CcDirector.Gateway.Contracts`. Phase 3
  reads them; phase 2 does not.
- The Gateway refuses a drain state it does not know (`WorkspaceValidation`). So the new marks need a
  Gateway carrying them before a real Director can save them. The isolated rig has its own Gateway;
  the real run of phase 5 needs a Gateway deploy first. Reported to the Delivery Lead.
