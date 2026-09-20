using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Avalonia.SmartRestart;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>A clock a test moves by hand, so no test sleeps.</summary>
internal sealed class FakeShutdownClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public FakeShutdownClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now()
    {
        lock (_gate)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
            _now += by;
    }
}

/// <summary>
/// A shutdown a test steps by hand: move a session to a state, add one, and count what the screen asked
/// for. Safe to step from any thread, because the real engine reports from its own.
/// </summary>
internal sealed class FakeShutdownProgressSource : IShutdownProgressSource
{
    private readonly object _gate = new();
    private readonly List<ShutdownProgressSession> _sessions = new();

    public FakeShutdownProgressSource(
        ShutdownProgressKind kind,
        TimeSpan timeAllowed,
        DateTimeOffset startedAt,
        params string[] sessionNames)
    {
        Kind = kind;
        TimeAllowed = timeAllowed;
        StartedAt = startedAt;
        foreach (var name in sessionNames)
            _sessions.Add(new ShutdownProgressSession(IdFor(name), name, ShutdownProgressState.Asked));
    }

    public ShutdownProgressKind Kind { get; }

    public TimeSpan TimeAllowed { get; }

    public DateTimeOffset StartedAt { get; }

    public event EventHandler? Changed;

    public int ShutDownNowRequests { get; private set; }

    public int CancelRequests { get; private set; }

    /// <summary>When set, either request throws this instead of being counted.</summary>
    public Exception? RequestFailure { get; set; }

    public IReadOnlyList<ShutdownProgressSession> Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.ToList();
        }
    }

    public static string IdFor(string name) => "id-" + name;

    public void MoveTo(string name, ShutdownProgressState state, string? reason = null)
    {
        lock (_gate)
        {
            var at = _sessions.FindIndex(s => s.Name == name);
            if (at < 0)
                throw new InvalidOperationException($"The fake has no session named {name}");
            _sessions[at] = _sessions[at] with { State = state, Reason = reason };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(string name, ShutdownProgressState state)
    {
        lock (_gate)
            _sessions.Add(new ShutdownProgressSession(IdFor(name), name, state));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestShutDownNow()
    {
        if (RequestFailure is not null)
            throw RequestFailure;
        ShutDownNowRequests++;
    }

    public void RequestCancelAndKeepWorking()
    {
        if (RequestFailure is not null)
            throw RequestFailure;
        CancelRequests++;
    }
}
