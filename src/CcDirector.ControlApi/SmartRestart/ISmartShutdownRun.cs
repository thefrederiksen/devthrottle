namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// One smart shutdown under way - what the progress screen subscribes to (phase 1 interface, section 3).
///
/// The screen decides LAYOUT only. What a state is called and what the count says come from the engine
/// (<see cref="SmartShutdownSessionProgress.StateLabel"/>, <see cref="SmartShutdownSnapshot.PhaseLabel"/>,
/// <see cref="SmartShutdownSnapshot.CountLabel"/>), so a new state is one edit in the engine and no new
/// branch in a window. A screen may colour by a state; it never words by it.
/// </summary>
public interface ISmartShutdownRun
{
    /// <summary>The run as it stands. Valid from the moment <see cref="ISmartShutdown.Start"/> returns;
    /// phase <see cref="SmartShutdownPhase.Starting"/> with no rows yet is possible.</summary>
    SmartShutdownSnapshot Current { get; }

    /// <summary>
    /// Raised on every change of any session's state or of the phase, and at least once per poll (ten
    /// seconds). It is raised on an ENGINE thread: the screen dispatches to the user interface thread
    /// itself. Each snapshot is complete and immutable; the screen replaces what it shows, never merges.
    /// </summary>
    event Action<SmartShutdownSnapshot>? Changed;

    /// <summary>Completes with the result. It never faults for an expected end.</summary>
    Task<SmartShutdownResult> Completion { get; }

    /// <summary>Jump to the limit. Returns at once and never throws. Honoured only while the snapshot
    /// says <see cref="SmartShutdownSnapshot.CanShutDownNow"/>; otherwise ignored and logged. The effect
    /// arrives as snapshots.</summary>
    void ShutDownNow();

    /// <summary>Stop closing sessions and bring back the ones already closed. Returns at once and never
    /// throws. Honoured only while the snapshot says <see cref="SmartShutdownSnapshot.CanCancel"/>;
    /// otherwise ignored and logged. The effect arrives as snapshots.</summary>
    void CancelAndKeepWorking();
}

/// <summary>The whole run at one moment. Complete and immutable.</summary>
/// <param name="Phase">Where the run is.</param>
/// <param name="PhaseLabel">Plain words, shown as is.</param>
/// <param name="Sessions">Leads first, each lead followed by the sessions under it.</param>
/// <param name="Total">How many sessions the run covers.</param>
/// <param name="Gone">How many are verified absent from this Director.</param>
/// <param name="CountLabel">"4 of 9 shut down" - shown as is.</param>
/// <param name="StartedUtc">When the run started.</param>
/// <param name="InterruptAtUtc">Two thirds of the time allowed.</param>
/// <param name="LimitUtc">The limit. The time left is the screen's own clock against this; the engine
/// does not tick every second.</param>
/// <param name="CanShutDownNow">Whether "Shut down now" would be honoured.</param>
/// <param name="CanCancel">Whether "Cancel and keep working" would be honoured.</param>
/// <param name="WorkspaceId">The record on the Gateway, once it exists.</param>
/// <param name="Note">The most recent thing worth saying, in plain words.</param>
public sealed record SmartShutdownSnapshot(
    SmartShutdownPhase Phase,
    string PhaseLabel,
    IReadOnlyList<SmartShutdownSessionProgress> Sessions,
    int Total,
    int Gone,
    string CountLabel,
    DateTime StartedUtc,
    DateTime InterruptAtUtc,
    DateTime LimitUtc,
    bool CanShutDownNow,
    bool CanCancel,
    string? WorkspaceId,
    string? Note);

/// <summary>Where a smart shutdown is.</summary>
public enum SmartShutdownPhase
{
    /// <summary>Writing the record; nothing asked yet.</summary>
    Starting,

    /// <summary>The requests are going out.</summary>
    Asking,

    /// <summary>Waiting for handovers, closing finished sessions as they land.</summary>
    Collecting,

    /// <summary>Two thirds reached: sessions still mid-turn are interrupted and asked again.</summary>
    Interrupting,

    /// <summary>The limit, or "Shut down now": every session still present is ended.</summary>
    EndingAtLimit,

    /// <summary>"Cancel and keep working": telling open sessions, bringing closed ones back.</summary>
    Cancelling,

    /// <summary>Purpose restart only: emptied, asking the launcher.</summary>
    Restarting,

    /// <summary>Over. <see cref="ISmartShutdownRun.Completion"/> carries the result.</summary>
    Finished,
}

/// <summary>One session's row on the progress screen.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Name">Its name.</param>
/// <param name="Mission">Its mission, when it has one.</param>
/// <param name="Role">Its role, when it has one.</param>
/// <param name="OwnerSessionId">The lead it sits under. Null for a lead or a standalone session.</param>
/// <param name="State">How far it has got.</param>
/// <param name="StateLabel">Plain words, shown as is.</param>
/// <param name="Detail">Why, when there is a why (the delivery refusal, for one).</param>
public sealed record SmartShutdownSessionProgress(
    string SessionId,
    string Name,
    string? Mission,
    string? Role,
    string? OwnerSessionId,
    SmartShutdownSessionState State,
    string StateLabel,
    string? Detail);

/// <summary>How far one session has got through a smart shutdown.</summary>
public enum SmartShutdownSessionState
{
    /// <summary>In the record, not asked yet (a session under a lead is asked BY its lead).</summary>
    Pending,

    /// <summary>The request landed.</summary>
    Asked,

    /// <summary>The request could not land (a wedged session); the detail carries the reason.</summary>
    NotDelivered,

    /// <summary>A handover file exists and is not finished.</summary>
    Writing,

    /// <summary>The handover was read and accepted, or its lead's handover covers it.</summary>
    HandedOver,

    /// <summary>Interrupted at two thirds and asked to hand over now.</summary>
    Interrupted,

    /// <summary>Gone, after handing over. Terminal.</summary>
    ShutDown,

    /// <summary>Ended by the engine at the limit, conversation id recorded. Terminal.</summary>
    EndedAtLimit,

    /// <summary>After a cancel: still open, told the restart is off. Terminal.</summary>
    KeptRunning,

    /// <summary>After a cancel: was closed, a new session reads its handover. Terminal.</summary>
    BroughtBack,
}

/// <summary>How a smart shutdown ended.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="WorkspaceId">The record on the Gateway, when one was written.</param>
/// <param name="Detail">Plain words, shown as is.</param>
/// <param name="Final">The last snapshot.</param>
public sealed record SmartShutdownResult(
    SmartShutdownOutcome Outcome,
    string? WorkspaceId,
    string Detail,
    SmartShutdownSnapshot Final);

/// <summary>What happened to a smart shutdown.</summary>
public enum SmartShutdownOutcome
{
    /// <summary>Purpose close: the Director is empty; the CALLER now closes the application.</summary>
    Emptied,

    /// <summary>Purpose restart: the launcher accepted; this process is about to be stopped.</summary>
    RestartAccepted,

    /// <summary>Purpose restart: emptied, but the launcher would not restart it; the detail says why.
    /// The record stands and is offered on the next start.</summary>
    RestartRefused,

    /// <summary>The Director holds the same missions it started with; the record is marked cancelled.</summary>
    Cancelled,

    /// <summary>Never started (Gateway unreachable, a run already under way); nothing touched.</summary>
    Refused,

    /// <summary>Stopped on an error; the detail says what, the record says how far it got.</summary>
    Failed,
}
