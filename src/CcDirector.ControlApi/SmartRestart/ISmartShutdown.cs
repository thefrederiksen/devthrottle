using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// The smart shutdown engine, as a screen sees it. This is the ONE interface between the engine and the
/// way down screens (mission document "Smart Director Restart", phase 1 interface, sections 1 and 2): the
/// names and shapes here are a contract another phase builds against, so they change only by agreement.
///
/// The engine never closes the application and never shows a window. It reports; the screen decides what
/// to do with the report.
/// </summary>
public interface ISmartShutdown
{
    /// <summary>
    /// Asked when the dialog opens. It changes nothing. It may take a second or two, because it asks the
    /// Gateway, so the dialog opens at once and fills this in when it arrives.
    /// </summary>
    /// <param name="purpose">What the owner asked for: close, or restart.</param>
    /// <param name="ct">Cancels the question, never a shutdown.</param>
    Task<SmartShutdownAvailability> CheckAsync(SmartShutdownPurpose purpose, CancellationToken ct);

    /// <summary>
    /// Start a smart shutdown. Returns at once, before anything is asked of any session.
    /// </summary>
    /// <param name="request">What was asked for, and the time allowed.</param>
    /// <exception cref="InvalidOperationException">A run is already under way on this Director; the
    /// message says so.</exception>
    ISmartShutdownRun Start(SmartShutdownRequest request);

    /// <summary>
    /// Shut down and ignore all sessions: write the record first, then end every session. It works with
    /// the Gateway unreachable too - then <see cref="IgnoreAllResult.RecordWritten"/> is false,
    /// <see cref="IgnoreAllResult.RecordRefusal"/> says why, and the sessions are still ended, because
    /// the owner chose to discard them.
    /// </summary>
    /// <param name="request">What was asked for. The time allowed is carried for the record only.</param>
    /// <param name="ct">Cancels the wait on the Gateway.</param>
    Task<IgnoreAllResult> ShutDownIgnoringAllAsync(SmartShutdownRequest request, CancellationToken ct);

    /// <summary>
    /// The operating system is shutting down. This is NOT a run: no dialog and no progress screen. It
    /// writes the record (names, repositories, conversation ids) and returns; it ends nothing itself.
    /// </summary>
    /// <param name="ct">Cancels the wait on the Gateway.</param>
    Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct);
}

/// <summary>What the owner asked the Director to do once it is empty.</summary>
public enum SmartShutdownPurpose
{
    /// <summary>Close the Director and leave it closed (the X on the window).</summary>
    Close,

    /// <summary>Restart the Director through its launcher (File, Smart Restart).</summary>
    Restart,
}

/// <summary>
/// One request for a smart shutdown.
///
/// <see cref="TimeAllowed"/> is exactly one of <see cref="SmartShutdownTimes.Allowed"/>. Anything else
/// is refused where the request is built, and again on any copy made with a different time, so an engine
/// handed a request never has to wonder whether two thirds of it is a sensible moment.
/// </summary>
/// <param name="Purpose">Close, or restart.</param>
/// <param name="TimeAllowed">How long the sessions get in all. One of the five allowed values.</param>
/// <param name="Reason">Optional, the owner's words; written into the record.</param>
public sealed record SmartShutdownRequest(SmartShutdownPurpose Purpose, TimeSpan TimeAllowed, string? Reason)
{
    private readonly TimeSpan _timeAllowed = SmartShutdownTimes.RequireAllowed(TimeAllowed);

    /// <summary>How long the sessions get in all. One of <see cref="SmartShutdownTimes.Allowed"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the allowed times.</exception>
    public TimeSpan TimeAllowed
    {
        get => _timeAllowed;
        init => _timeAllowed = SmartShutdownTimes.RequireAllowed(value);
    }
}

/// <summary>The times the owner may allow a smart shutdown: five, ten, fifteen, thirty and sixty minutes.</summary>
public static class SmartShutdownTimes
{
    /// <summary>The allowed times, shortest first: 5, 10, 15, 30 and 60 minutes.</summary>
    public static readonly IReadOnlyList<TimeSpan> Allowed = new[]
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60),
    };

    /// <summary>The time allowed when the owner does not choose: 10 minutes.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Hand the value back when it is one of <see cref="Allowed"/>, and refuse it otherwise with a
    /// message that names what was sent and what may be sent.
    /// </summary>
    /// <param name="timeAllowed">The candidate time.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the allowed times.</exception>
    public static TimeSpan RequireAllowed(TimeSpan timeAllowed)
    {
        if (Allowed.Contains(timeAllowed)) return timeAllowed;

        var message = RefusalFor(timeAllowed);
        FileLog.Write($"[SmartShutdownTimes] RequireAllowed FAILED: {message}");
        throw new ArgumentOutOfRangeException(nameof(timeAllowed), timeAllowed, message);
    }

    /// <summary>
    /// Why this time is not allowed, in plain words, naming what was asked for and what may be asked for.
    ///
    /// It is separate from <see cref="RequireAllowed"/> because a caller that can refuse BEFORE it builds a
    /// request wants the sentence without the exception - the command line door answers a bad time as a
    /// refusal a person reads, not as a stack trace - and the words must be the same sentence in both cases
    /// rather than a second one written next to the first.
    /// </summary>
    /// <param name="timeAllowed">The candidate time.</param>
    public static string RefusalFor(TimeSpan timeAllowed)
    {
        var allowed = string.Join(", ", Allowed.Select(t => ((int)t.TotalMinutes).ToString()));
        return $"The time allowed for a smart shutdown must be one of {allowed} minutes; " +
               $"{timeAllowed.TotalMinutes:0.###} minutes was asked for.";
    }
}

/// <summary>
/// What this Director can do right now, with the reason for anything it cannot. A Director that cannot do
/// a smart shutdown says why here; it never hands back nothing.
/// </summary>
/// <param name="CanSmartShutdown">False when the Gateway cannot be reached: the record must live off the
/// machine.</param>
/// <param name="SmartShutdownRefusal">Why not, in plain words. Null when it can.</param>
/// <param name="CanRestart">False when this Director is not the one its launcher would restart (a
/// development slot).</param>
/// <param name="RestartRefusal">Why not, in plain words. Null when it can.</param>
public sealed record SmartShutdownAvailability(
    bool CanSmartShutdown,
    string? SmartShutdownRefusal,
    bool CanRestart,
    string? RestartRefusal);

/// <summary>What came of a shut down that ignored all sessions, or of a record written for an operating
/// system shutdown.</summary>
/// <param name="RecordWritten">The record reached the Gateway.</param>
/// <param name="WorkspaceId">The record on the Gateway. Null when it was not written.</param>
/// <param name="RecordRefusal">Why the record was not written, in plain words. Null when it was.</param>
/// <param name="SessionsEnded">How many sessions this call ended. Zero for a record-only call.</param>
/// <param name="Detail">Anything the owner must be told that the other fields cannot say, in plain words:
/// a session that was ended although it is in no record because it appeared after the record was written,
/// and a session that would not end. Null when there is nothing to say.</param>
public sealed record IgnoreAllResult(
    bool RecordWritten,
    string? WorkspaceId,
    string? RecordRefusal,
    int SessionsEnded,
    string? Detail = null);
