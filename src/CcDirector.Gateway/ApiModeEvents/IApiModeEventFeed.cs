using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Gateway.ApiModeEvents;

/// <summary>
/// THE RECENT API-MODE EVENTS OF EVERY SESSION, AS OTHER GATEWAY CODE READS THEM (mission #4070, phase 2). The ingest
/// (<see cref="ApiModeEventIngest"/>) writes each accepted event here once, in order; this is the one way to read
/// them. Two readers build on it: the display fold (a session's verdict from its events) and the live relay
/// (<c>GET /sessions/{id}/live</c>, replaying after a cursor and then following).
///
/// <para><b>What is held.</b> Per (tenant, session): the events of the session's CURRENT generation only (see
/// <see cref="Contracts.ApiModeEventBatch"/> - a Director restart starts a new generation and its sequence starts
/// again), oldest first, bounded (<see cref="ApiModeEventBuffer.MaxEventsPerSession"/> events and
/// <see cref="ApiModeEventBuffer.MaxCharactersPerSession"/> characters; the oldest go first), and at most
/// <see cref="ApiModeEventBuffer.MaxSessions"/> sessions (the least recently written go first). It is held in this
/// process only: a Gateway restart starts empty. Durable facts live elsewhere - the receipts in
/// <see cref="Receipts.ApiModeReceiptStore"/>.</para>
///
/// <para><b>Tenant-scoped by construction.</b> Every call names the tenant; a session id in one tenant never reaches
/// another tenant's events or subscribers.</para>
///
/// <para><b>Delivery.</b> A handler is called synchronously, in sequence order, once per event, on the thread that
/// stored it, while that session's lock is held - so a subscriber never sees a gap, a duplicate or a reordering, and
/// its replay and its live events cannot interleave. A handler must therefore be quick (hand the event to a channel)
/// and must never call back into the feed. A handler that throws is logged and does not stop the other handlers or
/// the ingest.</para>
/// </summary>
public interface IApiModeEventFeed
{
    /// <summary>The held events of the session after <paramref name="after"/> (all held events when null), oldest
    /// first. Never null; a session with nothing held answers an empty replay.</summary>
    ApiModeEventReplay Read(TenantId tenant, string sessionId, ApiModeEventCursor? after = null);

    /// <summary>
    /// Follow one session. Atomically: the held events after <paramref name="after"/> (all held events when null) are
    /// handed to <paramref name="onEvent"/> first, inside this call, and then every event stored afterwards, until the
    /// subscription is disposed. The returned subscription records what the replay was (<see cref="ApiModeEventSubscription.Replay"/>:
    /// the same events the handler was given, its generation, and whether it was complete).
    /// </summary>
    ApiModeEventSubscription Subscribe(TenantId tenant, string sessionId, ApiModeEventCursor? after, Action<ApiModeHeldEvent> onEvent);

    /// <summary>Follow every session of every tenant from now on (no replay): for Gateway code that keeps its own per
    /// session state, such as the display fold. Dispose to stop.</summary>
    IDisposable SubscribeAll(Action<TenantId, ApiModeHeldEvent> onEvent);
}

/// <summary>A point in a session's event stream: the Director's generation and the sequence inside it. A reader keeps
/// the cursor of the last event it handled and asks for what comes after.</summary>
public readonly record struct ApiModeEventCursor(string Generation, long Sequence);

/// <summary>One stored event, with the generation it arrived in (an event's own sequence restarts each
/// generation, so the pair is its identity).</summary>
public sealed record ApiModeHeldEvent(string Generation, SessionEvent Event)
{
    /// <summary>This event's cursor: hand it back to read or follow what comes after it.</summary>
    public ApiModeEventCursor Cursor => new(Generation, Event.Sequence);
}

/// <summary>
/// The answer to a read (or the replay of a subscription).
/// </summary>
/// <param name="Generation">The generation the session is on here, or null when nothing has been stored for it since
/// this Gateway started.</param>
/// <param name="Events">The held events after the cursor, oldest first.</param>
/// <param name="Complete">True when the events answered are EVERY event this Gateway stored after the cursor. False
/// when some were already let go (the bound), or the cursor is of another generation (the Director restarted since):
/// then the reader has missed events and must rebuild from durable state - the receipts, the stored conversation -
/// rather than append.</param>
public sealed record ApiModeEventReplay(string? Generation, IReadOnlyList<ApiModeHeldEvent> Events, bool Complete);

/// <summary>A live subscription to one session. Dispose to stop; disposing twice is harmless.</summary>
public sealed class ApiModeEventSubscription : IDisposable
{
    private Action? _unsubscribe;

    internal ApiModeEventSubscription(ApiModeEventReplay replay, Action unsubscribe)
    {
        Replay = replay;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The replay handed to the handler before the live events: its events, its generation, and whether it was
    /// complete.</summary>
    public ApiModeEventReplay Replay { get; }

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
