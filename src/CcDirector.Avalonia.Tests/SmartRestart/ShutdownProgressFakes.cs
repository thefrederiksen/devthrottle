using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.ControlApi.SmartRestart;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>A clock a test sets by hand, so no test sleeps.</summary>
internal sealed class FakeShutdownClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public FakeShutdownClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
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
/// The real run interface, stepped by hand (phase 1 interface, section 4): a list of rows, a way to push
/// a snapshot, a way to complete the run, and the two buttons flipping CanShutDownNow and CanCancel to
/// false once used. Every word in a snapshot is written by the TEST, the way the engine writes it in
/// life, so a test can tell the engine's words from anything the screen made up.
///
/// Safe to push from any thread, because the real engine raises from its own.
/// </summary>
internal sealed class FakeSmartShutdownRun : ISmartShutdownRun
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<SmartShutdownResult> _completion = new();
    private Action<SmartShutdownSnapshot>? _changed;
    private SmartShutdownSnapshot _current;

    public FakeSmartShutdownRun(SmartShutdownSnapshot first) => _current = first;

    public SmartShutdownSnapshot Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event Action<SmartShutdownSnapshot>? Changed
    {
        add
        {
            lock (_gate)
                _changed += value;
        }
        remove
        {
            lock (_gate)
                _changed -= value;
        }
    }

    public Task<SmartShutdownResult> Completion => _completion.Task;

    /// <summary>How many handlers hold the change event right now.</summary>
    public int Subscribers
    {
        get
        {
            lock (_gate)
                return _changed?.GetInvocationList().Length ?? 0;
        }
    }

    public int ShutDownNowCalls { get; private set; }

    public int CancelCalls { get; private set; }

    /// <summary>When true, a used button flips nothing and pushes nothing: the engine has not answered yet.</summary>
    public bool StaysSilentWhenPressed { get; set; }

    public void ShutDownNow()
    {
        ShutDownNowCalls++;
        AnswerAPress();
    }

    public void CancelAndKeepWorking()
    {
        CancelCalls++;
        AnswerAPress();
    }

    /// <summary>Replaces the whole run, as the engine does, and raises the change on the calling thread.</summary>
    public void Push(SmartShutdownSnapshot snapshot)
    {
        Action<SmartShutdownSnapshot>? handlers;
        lock (_gate)
        {
            _current = snapshot;
            handlers = _changed;
        }

        handlers?.Invoke(snapshot);
    }

    /// <summary>Ends the run. The last snapshot is the one it stands at, unless another is given.</summary>
    public SmartShutdownResult Complete(SmartShutdownOutcome outcome, string detail, SmartShutdownSnapshot? final = null)
    {
        var result = new SmartShutdownResult(outcome, Current.WorkspaceId, detail, final ?? Current);
        _completion.SetResult(result);
        return result;
    }

    private void AnswerAPress()
    {
        if (StaysSilentWhenPressed)
            return;
        Push(Current with { CanShutDownNow = false, CanCancel = false });
    }
}

/// <summary>Snapshots and rows in the engine's shape, with the words a test chooses.</summary>
internal static class Shutdown
{
    public static readonly DateTime StartedUtc = new(2026, 9, 19, 22, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime LimitUtc = StartedUtc.AddMinutes(10);

    public static SmartShutdownSessionProgress Row(
        string name,
        SmartShutdownSessionState state,
        string stateLabel,
        string? detail = null,
        string? under = null) =>
        new("id-" + name, name, Mission: null, Role: null,
            OwnerSessionId: under is null ? null : "id-" + under,
            state, stateLabel, detail);

    public static SmartShutdownSnapshot Snapshot(
        SmartShutdownPhase phase,
        string phaseLabel,
        string countLabel,
        IEnumerable<SmartShutdownSessionProgress> rows,
        bool canShutDownNow = true,
        bool canCancel = true,
        string? note = null)
    {
        var sessions = rows.ToList();
        var gone = sessions.Count(r =>
            r.State is SmartShutdownSessionState.ShutDown or SmartShutdownSessionState.EndedAtLimit);
        return new SmartShutdownSnapshot(
            phase, phaseLabel, sessions, sessions.Count, gone, countLabel,
            StartedUtc, StartedUtc.AddSeconds(400), LimitUtc,
            canShutDownNow, canCancel, WorkspaceId: "workspace-1", note);
    }
}
