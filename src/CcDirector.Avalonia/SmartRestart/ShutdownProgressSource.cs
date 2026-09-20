using System;
using System.Collections.Generic;

namespace CcDirector.Avalonia.SmartRestart;

/// <summary>Which of the two shutdowns the progress screen is showing.</summary>
public enum ShutdownProgressKind
{
    /// <summary>The smart shutdown: sessions are asked to hand over and are given time to do it.</summary>
    Smart,

    /// <summary>Shut down and ignore all sessions: everything is ended at once, with no time allowed.</summary>
    IgnoreAll,
}

/// <summary>Where one session stands in a shutdown that is under way.</summary>
public enum ShutdownProgressState
{
    /// <summary>The session has been asked to hand over and has not started writing.</summary>
    Asked,

    /// <summary>The session is writing its handover.</summary>
    Writing,

    /// <summary>The handover is written. The session is still open.</summary>
    HandedOver,

    /// <summary>The session is closed.</summary>
    ShutDown,

    /// <summary>The session was still mid-turn at the two-thirds point and was told to hand over now.</summary>
    Interrupted,

    /// <summary>The session was still open when the time ran out and was ended.</summary>
    EndedAtLimit,

    /// <summary>The session could not take the request at all. The reason says why.</summary>
    CouldNotBeAsked,
}

/// <summary>One session as the shutdown reports it. A plain value: the screen never changes it.</summary>
/// <param name="Id">What identifies the session across reports. Two reports with one id are one row.</param>
/// <param name="Name">The name the owner knows the session by.</param>
/// <param name="State">Where the session stands.</param>
/// <param name="Reason">Why the session could not be asked. Required for that state, ignored otherwise.</param>
public sealed record ShutdownProgressSession(
    string Id,
    string Name,
    ShutdownProgressState State,
    string? Reason = null);

/// <summary>
/// The least the progress screen needs from a shutdown that is under way. The engine is being built
/// beside this screen and will publish its own interface; a later task adapts one to the other, so this
/// stays small and plain on purpose.
/// </summary>
public interface IShutdownProgressSource
{
    /// <summary>The smart shutdown or the ignore-all one. Does not change while the screen is up.</summary>
    ShutdownProgressKind Kind { get; }

    /// <summary>The time the sessions were given in all. Not read for the ignore-all kind.</summary>
    TimeSpan TimeAllowed { get; }

    /// <summary>When the time allowed started running. Not read for the ignore-all kind.</summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Every session in the shutdown with its state right now, in the order to draw them. A snapshot:
    /// the caller may keep it, and it may be read from any thread.
    /// </summary>
    IReadOnlyList<ShutdownProgressSession> Sessions { get; }

    /// <summary>Something in <see cref="Sessions"/> changed. May be raised from any thread.</summary>
    event EventHandler? Changed;

    /// <summary>The owner asked to stop waiting and end everything now. Must return at once.</summary>
    void RequestShutDownNow();

    /// <summary>The owner asked to call the shutdown off and get the sessions back. Must return at once.</summary>
    void RequestCancelAndKeepWorking();
}
