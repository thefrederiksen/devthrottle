using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Receipts;

namespace CcDirector.Gateway.ApiModeEvents;

/// <summary>
/// The bounded, in-process store of each session's recent API-mode events, and the subscriptions on it (mission #4070,
/// phase 2). Read through <see cref="IApiModeEventFeed"/>; written only by <see cref="ApiModeEventIngest"/>, which hands
/// it each accepted batch through <see cref="Apply"/>. One lock per session makes the generation decision, the durable
/// write the ingest asks for, the append and the delivery to subscribers one step, so two batches for one session can
/// never interleave and a subscriber never sees a gap or a reordering.
/// </summary>
public sealed class ApiModeEventBuffer : IApiModeEventFeed
{
    /// <summary>The most events held per session; past it the oldest are let go.</summary>
    public const int MaxEventsPerSession = 2_000;

    /// <summary>The most characters of event JSON held per session; past it the oldest events are let go. A tool's
    /// output can be large, so the count alone does not bound memory.</summary>
    public const long MaxCharactersPerSession = 2_000_000;

    /// <summary>The most sessions held; past it the least recently written session with no subscriber is let go.</summary>
    public const int MaxSessions = 1_000;

    private readonly object _mapGate = new();
    private readonly Dictionary<(string Tenant, string SessionId), SessionState> _sessions = new();
    private long _writeCounter;
    private Action<TenantId, ApiModeHeldEvent>[] _all = Array.Empty<Action<TenantId, ApiModeHeldEvent>>();

    // ----- reading -----

    /// <inheritdoc />
    public ApiModeEventReplay Read(TenantId tenant, string sessionId, ApiModeEventCursor? after = null)
    {
        var key = Key(tenant, sessionId);
        SessionState? state;
        lock (_mapGate) _sessions.TryGetValue(key, out state);
        if (state is null)
            return new ApiModeEventReplay(null, Array.Empty<ApiModeHeldEvent>(), Complete: after is null);
        lock (state.Gate)
            return ReplayOf(state, after);
    }

    /// <inheritdoc />
    public ApiModeEventSubscription Subscribe(TenantId tenant, string sessionId, ApiModeEventCursor? after, Action<ApiModeHeldEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var key = Key(tenant, sessionId);
        while (true)
        {
            var state = GetOrCreate(key);
            lock (state.Gate)
            {
                if (state.Evicted) continue;
                var replay = ReplayOf(state, after);
                foreach (var held in replay.Events)
                    Deliver(onEvent, held, sessionId);
                state.Subscribers.Add(onEvent);
                FileLog.Write($"[ApiModeEventBuffer] Subscribe session={sessionId} tenant={tenant.ToLogString()}: replayed {replay.Events.Count} event(s), complete={replay.Complete}; {state.Subscribers.Count} subscriber(s)");
                return new ApiModeEventSubscription(replay, () =>
                {
                    lock (state.Gate) state.Subscribers.Remove(onEvent);
                    FileLog.Write($"[ApiModeEventBuffer] Unsubscribe session={sessionId} tenant={tenant.ToLogString()}");
                });
            }
        }
    }

    /// <inheritdoc />
    public IDisposable SubscribeAll(Action<TenantId, ApiModeHeldEvent> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        lock (_mapGate) _all = _all.Append(onEvent).ToArray();
        FileLog.Write("[ApiModeEventBuffer] SubscribeAll: a whole-fleet subscriber was added");
        return new ApiModeEventSubscription(new ApiModeEventReplay(null, Array.Empty<ApiModeHeldEvent>(), Complete: true), () =>
        {
            lock (_mapGate) _all = _all.Where(h => h != onEvent).ToArray();
            FileLog.Write("[ApiModeEventBuffer] SubscribeAll: a whole-fleet subscriber was removed");
        });
    }

    // ----- writing (the ingest only) -----

    /// <summary>One event of a batch, with the length of its JSON (what it costs to hold).</summary>
    internal readonly record struct Incoming(SessionEvent Event, int Characters);

    /// <summary>What <see cref="Apply"/> decided.</summary>
    internal sealed record ApplyResult(bool Refused, string Generation, long HighestSequence, int Stored);

    /// <summary>
    /// Decide and store one batch for one session, under the session's lock. The batch's generation is compared with
    /// the session's (<see cref="ApiModeEventPosition"/>): an OLDER generation is refused and nothing happens; a LATER
    /// one replaces the session's and counts its sequence afresh. The events above the highest sequence held are the
    /// new ones: <paramref name="persist"/> is called with them first (the durable receipts), and only when it returns
    /// are they appended, bounded, and handed to the subscribers. If it throws, nothing in memory changes and the
    /// exception reaches the caller, so the same batch can be sent again.
    /// </summary>
    internal ApplyResult Apply(TenantId tenant, string sessionId, string generation, DateTime generationStartedUtc,
        IReadOnlyList<Incoming> events, Action<IReadOnlyList<SessionEvent>> persist)
    {
        var key = Key(tenant, sessionId);
        var started = ApiModeEventPosition.StartPrecision(generationStartedUtc);
        while (true)
        {
            var state = GetOrCreate(key);
            lock (state.Gate)
            {
                if (state.Evicted) continue;

                var switches = false;
                if (state.Generation is not null && !string.Equals(state.Generation, generation, StringComparison.Ordinal))
                {
                    var batchPosition = new ApiModeEventPosition(generation, started, 0);
                    if (!batchPosition.IsLaterGenerationThan(state.Generation, state.GenerationStartedUtc))
                    {
                        FileLog.Write($"[ApiModeEventBuffer] Apply session={sessionId} tenant={tenant.ToLogString()}: REFUSED a batch of generation={generation} (started {started:O}); the session is on the later generation={state.Generation} (started {state.GenerationStartedUtc:O})");
                        return new ApplyResult(true, state.Generation, state.HighestSequence, 0);
                    }
                    switches = true;
                }

                var floor = switches || state.Generation is null ? -1 : state.HighestSequence;
                var fresh = events.Where(e => e.Event.Sequence > floor).ToList();
                if (fresh.Count > 0)
                    persist(fresh.Select(e => e.Event).ToList());

                if (switches)
                {
                    FileLog.Write($"[ApiModeEventBuffer] Apply session={sessionId} tenant={tenant.ToLogString()}: moved from generation={state.Generation} to generation={generation}; {state.Events.Count} held event(s) of the earlier generation let go");
                    state.Events.Clear();
                    state.Characters = 0;
                    state.EvictedThrough = -1;
                    state.HighestSequence = -1;
                }
                if (state.Generation is null || switches)
                {
                    state.Generation = generation;
                    state.GenerationStartedUtc = started;
                }

                var all = _all;
                foreach (var incoming in fresh)
                {
                    var held = new ApiModeHeldEvent(generation, incoming.Event);
                    state.Events.AddLast((held, incoming.Characters));
                    state.Characters += incoming.Characters;
                    state.HighestSequence = incoming.Event.Sequence;
                    Trim(state);
                    foreach (var subscriber in state.Subscribers.ToArray())
                        Deliver(subscriber, held, sessionId);
                    foreach (var subscriber in all)
                        DeliverAll(subscriber, tenant, held, sessionId);
                }
                state.LastWrite = Interlocked.Increment(ref _writeCounter);
                return new ApplyResult(false, generation, state.HighestSequence, fresh.Count);
            }
        }
    }

    /// <summary>How many sessions are held, for the tests.</summary>
    internal int SessionCount { get { lock (_mapGate) return _sessions.Count; } }

    // ----- internals -----

    private sealed class SessionState
    {
        public readonly object Gate = new();
        public string? Generation;
        public DateTime GenerationStartedUtc;
        public long HighestSequence = -1;
        /// <summary>The highest sequence of the current generation let go by the bound; -1 when none was.</summary>
        public long EvictedThrough = -1;
        public readonly LinkedList<(ApiModeHeldEvent Held, int Characters)> Events = new();
        public long Characters;
        public readonly List<Action<ApiModeHeldEvent>> Subscribers = new();
        public long LastWrite;
        /// <summary>Set, under both locks, when the session is let go: a holder of a stale reference re-fetches.</summary>
        public bool Evicted;
    }

    private static (string, string) Key(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid) throw new ArgumentException("the tenant is not valid", nameof(tenant));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return (tenant.Value, sessionId);
    }

    private SessionState GetOrCreate((string Tenant, string SessionId) key)
    {
        lock (_mapGate)
        {
            if (_sessions.TryGetValue(key, out var state)) return state;
            state = new SessionState { LastWrite = Interlocked.Increment(ref _writeCounter) };
            _sessions[key] = state;
            EvictOverCap(keep: key);
            return state;
        }
    }

    /// <summary>Let go of the least recently written sessions with no subscriber until the cap holds. Called under the
    /// map lock; takes each victim's own lock so no writer is mid-append on it.</summary>
    private void EvictOverCap((string Tenant, string SessionId) keep)
    {
        if (_sessions.Count <= MaxSessions) return;
        foreach (var (key, state) in _sessions.Where(kv => kv.Key != keep).OrderBy(kv => kv.Value.LastWrite).ToList())
        {
            if (_sessions.Count <= MaxSessions) return;
            if (!Monitor.TryEnter(state.Gate)) continue;   // a writer holds it right now: it is not the least recent
            try
            {
                if (state.Subscribers.Count > 0) continue;
                state.Evicted = true;
                _sessions.Remove(key);
                FileLog.Write($"[ApiModeEventBuffer] let go of session={key.SessionId} ({state.Events.Count} event(s)): over {MaxSessions} sessions held");
            }
            finally { Monitor.Exit(state.Gate); }
        }
    }

    private static void Trim(SessionState state)
    {
        while (state.Events.Count > MaxEventsPerSession
               || (state.Characters > MaxCharactersPerSession && state.Events.Count > 1))
        {
            var oldest = state.Events.First!.Value;
            state.Events.RemoveFirst();
            state.Characters -= oldest.Characters;
            state.EvictedThrough = oldest.Held.Event.Sequence;
        }
    }

    private static ApiModeEventReplay ReplayOf(SessionState state, ApiModeEventCursor? after)
    {
        if (state.Generation is null)
            return new ApiModeEventReplay(null, Array.Empty<ApiModeHeldEvent>(), Complete: after is null);

        if (after is { } cursor && string.Equals(cursor.Generation, state.Generation, StringComparison.Ordinal))
        {
            var events = state.Events.Where(e => e.Held.Event.Sequence > cursor.Sequence).Select(e => e.Held).ToList();
            return new ApiModeEventReplay(state.Generation, events, Complete: cursor.Sequence >= state.EvictedThrough);
        }

        // No cursor, or a cursor of another generation: everything held. Complete only when nothing of this generation
        // was let go AND the reader is not carrying on from a generation it can no longer see the end of.
        var whole = state.Events.Select(e => e.Held).ToList();
        return new ApiModeEventReplay(state.Generation, whole, Complete: after is null && state.EvictedThrough < 0);
    }

    /// <summary>An external subscriber's handler is an entry point: its failure is logged, never allowed to stop the
    /// other subscribers or the ingest that is delivering.</summary>
    private static void Deliver(Action<ApiModeHeldEvent> handler, ApiModeHeldEvent held, string sessionId)
    {
        try { handler(held); }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeEventBuffer] a subscriber of session={sessionId} FAILED on {held.Event.GetType().Name} {held.Cursor}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DeliverAll(Action<TenantId, ApiModeHeldEvent> handler, TenantId tenant, ApiModeHeldEvent held, string sessionId)
    {
        try { handler(tenant, held); }
        catch (Exception ex)
        {
            FileLog.Write($"[ApiModeEventBuffer] a whole-fleet subscriber FAILED on session={sessionId} {held.Event.GetType().Name} {held.Cursor}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
