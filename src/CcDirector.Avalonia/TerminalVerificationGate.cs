using System;
using System.Diagnostics;

namespace CcDirector.Avalonia;

/// <summary>
/// The rate gate in front of the terminal verification check. The check joins the whole scrollback
/// into one string on the screen thread and is requested on every scroll change, so at most one run
/// is admitted per interval. A request inside the interval is answered with how long to wait, so the
/// caller can run one trailing check when the interval ends instead of dropping the request: a session
/// whose output stops right after its start-up burst must still be verified.
/// </summary>
public sealed class TerminalVerificationGate
{
    /// <summary>The shortest trailing wait a caller should schedule; anything shorter is timer noise.</summary>
    public static readonly TimeSpan MinimumTrailingWait = TimeSpan.FromMilliseconds(50);

    private readonly TimeSpan _interval;
    private long _lastAdmittedTimestamp;

    public TerminalVerificationGate(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "The gate interval must be positive.");
        _interval = interval;
    }

    /// <summary>
    /// Asks to run now. Returns <see cref="TimeSpan.Zero"/> when the run is admitted (and records it),
    /// otherwise the time left until the interval ends, which is when a trailing run should happen.
    /// <paramref name="nowTimestamp"/> is a <see cref="Stopwatch.GetTimestamp"/> value.
    /// </summary>
    public TimeSpan Admit(long nowTimestamp)
    {
        if (_lastAdmittedTimestamp != 0)
        {
            var sinceLast = Stopwatch.GetElapsedTime(_lastAdmittedTimestamp, nowTimestamp);
            if (sinceLast < _interval)
                return _interval - sinceLast;
        }
        _lastAdmittedTimestamp = nowTimestamp;
        return TimeSpan.Zero;
    }
}
