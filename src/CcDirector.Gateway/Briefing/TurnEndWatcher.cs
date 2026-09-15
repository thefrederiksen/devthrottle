using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Briefing;

/// <summary>One observed turn boundary: the session, the id of the Director that owns it (Gateway
/// Cleanup mission, Phase 2: the owning Director is now carried as its DirectorId, not a dialable control
/// URL, so the voice-refresh path reaches it through the tunnel), and the tenant that owns it. Hosted
/// Multi-Tenancy (MTR-10 Gap C): the tenant is RESOLVED before the transition decision and carried here, so
/// the voice refresh runs in the right partition with no second resolution. <paramref name="IsNewTurn"/> is
/// true only for a live Working -> Waiting boundary (a genuinely new turn the user is now waiting on); it is
/// false when a session is FIRST seen already waiting (a startup catch-up of a turn that ended earlier).
/// Voice generation uses it to show the yellow "wingman reading" hold only for a new turn and stay quiet on
/// a catch-up refresh (issue #1322). <paramref name="PreviousActivityState"/> is the state this session was
/// remembered in immediately before the boundary, or null when it had never been seen - the same fact
/// <paramref name="IsNewTurn"/> is derived from, carried in full for the turn log, which records what the
/// detector SAW rather than only what it concluded. Nothing in the product branches on it.
///
/// <paramref name="ObservedAtUtc"/> IS THE JOIN KEY, and it is REQUIRED rather than defaulted. It is the
/// moment the DETECTOR saw this boundary, stamped once here where the signal is created, and it is what
/// lets everything produced from one stop - the turn-log record and the Wingman's verdict row - be matched
/// to each other by (account, session, observed moment). Every consumer runs at its own pace and stamps
/// its own completion time, so without one shared moment the only way to pair them is to look for the
/// nearest timestamp, which is a guess that gets worse the slower a consumer is. It has no default for the
/// same reason: a caller that forgot it would stamp the epoch and match nothing, and a join key that
/// silently matches nothing looks exactly like a stop that produced no record.</summary>
public sealed record TurnEndSignal(
    string SessionId,
    string DirectorId,
    TenantId Tenant,
    DateTime ObservedAtUtc,
    bool IsNewTurn = false,
    string? PreviousActivityState = null);

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

    /// <summary>
    /// Test seam (issue #549): when false, <see cref="Start"/> registers the watcher but does
    /// NOT run the Director-polling sweep (startup catch-up + reconcile timer). The push-fed
    /// <see cref="Observe"/> path is unaffected. The Gateway.Tests assembly turns this off in its
    /// module initializer so a test-spun host never polls its fake Directors and disturbs
    /// request-count assertions - the same isolation the retired CC_TURNBRIEFS=0 flag used to give.
    /// Production never touches it (default true). Mirrors the TailscaleServeSelfProvisioner.Enabled
    /// test seam precedent.
    /// </summary>
    public static bool SweepEnabled = true;

    private readonly PushedSessionStore? _pushedSessions;
    private readonly TimeSpan _streamStale;
    private readonly Action<TurnEndSignal> _onTurnEnd;
    // (tenant, sessionId, directorId): the owning tenant is resolved BEFORE the transition decision and passed
    // in (Hosted Multi-Tenancy voice-serving, MTR-10 Gap C), so the handler clears the stale voice cache in the
    // right partition; the director id is carried for the tunnel reach.
    private readonly Action<TenantId, string, string> _onSessionWorking;
    private readonly TimeSpan _interval;
    // MTR-10 Gap C: keyed by (tenant, sessionId), never the bare session id. Two accounts can run sessions with
    // the SAME id; a bare key let one tenant's last-seen state suppress - or fabricate - the other tenant's
    // Working -> Waiting transition (and so its voice refresh / stale-cache clear). The owning tenant is
    // resolved before Observe and scopes the transition memory here. Self-host uses the one TenantId.Local key.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), string> _lastActivity = new();
    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    /// <param name="pushedSessions">Gateway Cleanup mission, Phase 2: non-null under stream mode. When set,
    /// the catch-up / reconcile sweep reads each stream-connected Director's pushed session snapshot from the
    /// push store instead of HTTP-pulling it, so the watcher no longer dials the Director. A Director that
    /// never pushes (stream mode off / file-discovered legacy) is still pulled over HTTP, byte-identical.</param>
    /// <param name="streamStale">Freshness window for the push store read; defaults to the roster's window.</param>
    public TurnEndWatcher(
        Action<TurnEndSignal> onTurnEnd,
        Action<TenantId, string, string> onSessionWorking,
        TimeSpan? reconcileInterval = null,
        PushedSessionStore? pushedSessions = null,
        TimeSpan? streamStale = null)
    {
        _pushedSessions = pushedSessions;
        _streamStale = streamStale ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
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
        if (!SweepEnabled)
        {
            FileLog.Write("[TurnEndWatcher] Start: sweep disabled (test seam); push-fed Observe path stays active");
            return;
        }
        FileLog.Write($"[TurnEndWatcher] Start: reconcile={_interval.TotalSeconds:F0}s for non-pushing Directors");
        _timer = new Timer(_ => PollSafe(sweepAll: false), null, _interval, _interval);
        _ = Task.Run(() => PollSafe(sweepAll: true));
    }

    /// <summary>
    /// Feed one observation (doorbell ping, heartbeat snapshot entry, or reconcile sweep
    /// row) into the tracker. Idempotent for repeated identical states - a heartbeat
    /// replaying a state the doorbell already delivered changes nothing.
    ///
    /// MTR-10 Gap C: the OWNING <paramref name="tenant"/> is resolved by the caller BEFORE this call and
    /// scopes the transition memory, so a session id shared across two accounts can never suppress or
    /// fabricate the other account's Working -> Waiting boundary. On self-host the caller passes
    /// <see cref="TenantId.Local"/> and the behaviour is unchanged.
    /// </summary>
    public void Observe(TenantId tenant, string sessionId, string activityState, string directorId)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(activityState)) return;

        var key = (tenant, sessionId);
        var hadPrev = _lastActivity.TryGetValue(key, out var prev);
        if (hadPrev && prev == activityState) return; // no transition, nothing to do
        _lastActivity[key] = activityState;

        if (IsWorking(activityState))
        {
            _onSessionWorking(tenant, sessionId, directorId);
            return;
        }

        if (IsTurnEnd(hadPrev ? prev : null, activityState))
        {
            // A live boundary (previous state was Working) is a genuinely new turn; a first sighting
            // of an already-waiting session (no previous state) is a catch-up of an earlier turn.
            var isNewTurn = hadPrev && prev == "Working";
            // The observed moment is stamped HERE, at the boundary decision, and not by any consumer: this
            // is the only place that knows when the turn end was seen, and every consumer that stamps its
            // own clock stamps a later, different moment.
            _onTurnEnd(new TurnEndSignal(
                sessionId, directorId, tenant, DateTime.UtcNow, isNewTurn, hadPrev ? prev : null));
        }
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

    internal Task SweepAsync(bool sweepAll)
    {
        // Post-cut: the catch-up / reconcile sweep reads each stream-connected Director's pushed session
        // snapshot straight from the push store - there is no HTTP pull. A Director that never pushes is not
        // connected to the tunnel and simply does not appear here. The signal carries the DirectorId.
        _ = sweepAll;
        if (_pushedSessions is not null)
        {
            // Hosted Multi-Tenancy (session-serving), MTR-10 Gap C: reconcile ONE pass PER TENANT, each row fed
            // with its OWNING tenant so the transition memory is partitioned. The push store's KnownTenants is
            // exactly the set of tenants with a tunnel-bound Director - the only fleets a push-store reconcile
            // could act on. Self-host has one tenant (Local) and runs the single pass unchanged; on hosted each
            // tenant's snapshot is read in its own partition and never reaches across.
            foreach (var tenant in _pushedSessions.KnownTenants())
            {
                if (!tenant.IsValid) continue;
                foreach (var (directorId, session) in _pushedSessions.SnapshotFresh(tenant, _streamStale))
                {
                    if (_disposed) return Task.CompletedTask;
                    Observe(tenant, session.SessionId, session.ActivityState, directorId);
                }
            }
        }
        return Task.CompletedTask;
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
