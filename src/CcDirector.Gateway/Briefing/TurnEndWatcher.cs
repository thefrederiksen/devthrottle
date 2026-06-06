using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Briefing;

/// <summary>One observed turn boundary: the session and the Director that owns it.</summary>
public sealed record TurnEndSignal(string SessionId, string DirectorEndpoint);

/// <summary>
/// The Gateway brief agent's v1 trigger (issue #185): a fleet poll that observes each
/// session's ActivityState across consecutive sweeps and fires on the turn boundary
/// (Working -> WaitingForInput/Idle). PULL, not push - zero Director changes; the push
/// doorbell (issue #186) replaces this class when it lands, which is why the transition
/// logic is one small pure function.
///
/// Re-entering Working fires the second callback so the brief agent can watch-cancel:
/// the user replied, the decision is already made, briefing it is wasted tokens.
///
/// A session FIRST seen already waiting also fires a turn end: at Gateway boot the brief
/// agent skips it if the store already covers that turn, and backfills the brief if not.
/// </summary>
public sealed class TurnEndWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

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
        TimeSpan? interval = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _onTurnEnd = onTurnEnd ?? throw new ArgumentNullException(nameof(onTurnEnd));
        _onSessionWorking = onSessionWorking ?? throw new ArgumentNullException(nameof(onSessionWorking));
        _interval = interval ?? DefaultInterval;
    }

    public void Start()
    {
        FileLog.Write($"[TurnEndWatcher] Start: interval={_interval.TotalSeconds:F1}s");
        _timer = new Timer(_ => PollSafe(), null, _interval, _interval);
    }

    // Timer callbacks must never overlap (a slow Director sweep would stack) and never throw.
    private void PollSafe()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        _ = PollOnceAsync().ContinueWith(t =>
        {
            if (t.Exception is not null)
                FileLog.Write($"[TurnEndWatcher] poll EXCEPTION: {t.Exception.GetBaseException().Message}");
            Interlocked.Exchange(ref _polling, 0);
        });
    }

    internal async Task PollOnceAsync()
    {
        var observed = new List<(string Sid, string Activity, string Endpoint)>();
        foreach (var d in _registry.ListDirectors())
        {
            if (_disposed) return;
            if (!_registry.ShouldProbe(d.DirectorId)) continue; // circuit breaker open
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
                observed.Add((s.SessionId, s.ActivityState, ep));
        }

        foreach (var (sid, activity, endpoint) in observed)
        {
            var hadPrev = _lastActivity.TryGetValue(sid, out var prev);
            _lastActivity[sid] = activity;

            if (IsWorking(activity))
            {
                if (!hadPrev || !IsWorking(prev!))
                    _onSessionWorking(sid);
                continue;
            }

            if (IsTurnEnd(hadPrev ? prev : null, activity))
                _onTurnEnd(new TurnEndSignal(sid, endpoint));
        }
    }

    /// <summary>
    /// The boundary decision, pure for tests: fire when a session leaves Working into a
    /// waiting state, or when it is FIRST observed already waiting (Gateway boot backfill -
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
