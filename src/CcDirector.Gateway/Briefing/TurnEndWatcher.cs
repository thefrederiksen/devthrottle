using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Briefing;

/// <summary>One observed turn boundary: the session and the Director that owns it.</summary>
public sealed record TurnEndSignal(string SessionId, string DirectorEndpoint);

/// <summary>
/// The brief agent's turn-boundary tracker (issues #185/#186). PUSH-fed since #186:
/// Directors ring the doorbell on every state change (announce THAT, never WHAT) and
/// snapshot every session's state in their 15s heartbeat - both feed <see cref="Observe"/>,
/// which fires the turn-end callback on the Working -> WaitingForInput/Idle boundary and
/// the watch-cancel callback when a session re-enters Working. Lost pings are harmless:
/// the heartbeat snapshot replays the same observation within 15s.
///
/// Two pulls remain, both at heartbeat cadence or rarer:
///   - a one-time catch-up sweep of EVERY Director at Gateway startup, so sessions
///     already waiting get briefed immediately;
///   - a 15s reconcile poll of ONLY the Directors that have never pushed (file-discovered
///     locals without gateway.url, old builds) - their sessions would otherwise never
///     signal. The moment a Director rings the doorbell or heartbeats a state snapshot,
///     the registry marks it state-reporting and the poll skips it.
/// </summary>
public sealed class TurnEndWatcher : IDisposable
{
    /// <summary>Reconcile cadence for non-pushing Directors (matches the heartbeat).</summary>
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly DirectorRegistry _registry;
    private readonly DirectorEndpointClient _client;
    private readonly Action<TurnEndSignal> _onTurnEnd;
    private readonly Action<string> _onSessionWorking;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, string> _lastActivity = new();
    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    public TurnEndWatcher(
        DirectorRegistry registry,
        DirectorEndpointClient client,
        Action<TurnEndSignal> onTurnEnd,
        Action<string> onSessionWorking,
        TimeSpan? reconcileInterval = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _onTurnEnd = onTurnEnd ?? throw new ArgumentNullException(nameof(onTurnEnd));
        _onSessionWorking = onSessionWorking ?? throw new ArgumentNullException(nameof(onSessionWorking));
        _interval = reconcileInterval ?? ReconcileInterval;
    }

    /// <summary>
    /// First tick immediately (the startup catch-up: sweep EVERY Director so sessions
    /// already waiting get briefed now), then the reconcile poll for non-pushing
    /// Directors every <see cref="ReconcileInterval"/>.
    /// </summary>
    public void Start()
    {
        FileLog.Write($"[TurnEndWatcher] Start: reconcile={_interval.TotalSeconds:F0}s for non-pushing Directors");
        _timer = new Timer(_ => PollSafe(sweepAll: false), null, _interval, _interval);
        _ = Task.Run(() => PollSafe(sweepAll: true));
    }

    /// <summary>
    /// Feed one observation (doorbell ping, heartbeat snapshot entry, or reconcile sweep
    /// row) into the tracker. Idempotent for repeated identical states - a heartbeat
    /// replaying a state the doorbell already delivered changes nothing.
    /// </summary>
    public void Observe(string sessionId, string activityState, string directorEndpoint)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(activityState)) return;

        var hadPrev = _lastActivity.TryGetValue(sessionId, out var prev);
        if (hadPrev && prev == activityState) return; // no transition, nothing to do
        _lastActivity[sessionId] = activityState;

        if (IsWorking(activityState))
        {
            _onSessionWorking(sessionId);
            return;
        }

        if (IsTurnEnd(hadPrev ? prev : null, activityState))
            _onTurnEnd(new TurnEndSignal(sessionId, directorEndpoint));
    }

    // Timer callbacks must never overlap (a slow Director would stack) and never throw.
    private void PollSafe(bool sweepAll)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        _ = SweepAsync(sweepAll).ContinueWith(t =>
        {
            if (t.Exception is not null)
                FileLog.Write($"[TurnEndWatcher] sweep EXCEPTION: {t.Exception.GetBaseException().Message}");
            Interlocked.Exchange(ref _polling, 0);
        });
    }

    internal async Task SweepAsync(bool sweepAll)
    {
        foreach (var d in _registry.ListDirectors())
        {
            if (_disposed) return;
            if (!sweepAll && _registry.IsStateReporting(d.DirectorId)) continue; // it pushes
            if (!_registry.ShouldProbe(d.DirectorId)) continue;                  // circuit open
            var ep = d.ControlEndpoint;
            if (string.IsNullOrEmpty(ep)) continue;

            var (sessions, error) = await _client.ListSessionsWithStatusAsync(ep);
            if (sessions is null)
            {
                _registry.RecordUnreachable(d.DirectorId, error ?? "unreachable");
                continue;
            }
            _registry.RecordReachable(d.DirectorId);
            foreach (var s in sessions)
                Observe(s.SessionId, s.ActivityState, ep);
        }
    }

    /// <summary>
    /// The boundary decision, pure for tests: fire when a session leaves Working into a
    /// waiting state, or when it is FIRST observed already waiting (startup catch-up -
    /// the brief agent skips already-briefed turns, so this never double-briefs).
    /// </summary>
    internal static bool IsTurnEnd(string? previousActivity, string currentActivity)
    {
        if (currentActivity is not ("WaitingForInput" or "Idle")) return false;
        if (previousActivity is null) return true;          // first sighting, already waiting
        return previousActivity == "Working";               // the live boundary
    }

    private static bool IsWorking(string activity) => activity == "Working";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }
}
