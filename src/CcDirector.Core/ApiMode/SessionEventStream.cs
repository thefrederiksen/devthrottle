using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.Core.ApiMode;

/// <summary>
/// Where an API-mode session's events go, in the order they happened (API mode mission, issue #4070). The Director owns
/// it; the upstream message to the Gateway (phase 2, <c>DirectorUpStreamMessages</c>) is filled from it once that
/// message exists.
///
/// <see cref="Publish"/> is called from the session's one event loop while it holds the session's lock, so an
/// implementation must return at once: hand the batch on, never wait on the network.
/// </summary>
public interface ISessionEventSink
{
    void Publish(SessionEventBatch batch);
}

/// <summary>
/// One or more events of one session, with the generation they belong to, in the shape of the turn push
/// (<c>TurnPushBatch</c>): <see cref="Generation"/> is an opaque identity, never a number to compare, and generations are
/// ordered by <see cref="GenerationStartedUtc"/>. <see cref="SessionEvent.Sequence"/> rises within a generation; a new
/// generation starts whenever the Director starts a session's event stream afresh (a Director restart), so a reader
/// de-duplicates on (generation, sequence).
/// </summary>
public sealed record SessionEventBatch(
    string SessionId,
    string Generation,
    DateTime GenerationStartedUtc,
    IReadOnlyList<SessionEvent> Events);

/// <summary>
/// One session's event stream inside one Director process: its numbering (the sequence, the turn ids and the command
/// ids), its generation and where its events go. One instance per DevThrottle session for the life of the Director
/// process, handed to every agent process of that session, so taking the wheel and handing back keep the same
/// generation and keep counting. A Director restart makes a new one, and so a new generation.
///
/// Not thread-safe, like <see cref="SessionEventNumbering"/>: the session's host uses it from one place at a time.
/// </summary>
public sealed class SessionEventStream
{
    private readonly ISessionEventSink _sink;

    /// <param name="sessionId">The DevThrottle session.</param>
    /// <param name="sink">Where the events go.</param>
    /// <param name="clock">The clock the generation's start is read from.</param>
    public SessionEventStream(string sessionId, ISessionEventSink sink, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        SessionId = sessionId;
        _sink = sink;
        // A new identity for every stream: what makes it a new generation is that it is new, and its start orders it.
        Generation = Guid.NewGuid().ToString();
        GenerationStartedUtc = clock.GetUtcNow().UtcDateTime;
        FileLog.Write($"[SessionEventStream] Created: session={sessionId}, generation={Generation}, started={GenerationStartedUtc:O}");
    }

    public string SessionId { get; }

    /// <summary>The generation's identity: opaque, new for every stream.</summary>
    public string Generation { get; }

    /// <summary>When this generation started (UTC), from the injected clock. Orders generations.</summary>
    public DateTime GenerationStartedUtc { get; }

    /// <summary>The numbers this session hands out, shared by every agent process of it.</summary>
    public SessionEventNumbering Numbering { get; } = new();

    /// <summary>
    /// A new Director prompt id, for a prompt that has no Gateway delivery id: a new GUID every time, never a counter.
    /// Receipts are kept by tenant, session and prompt id, and a counter would start again at 1 after a Director
    /// restart and overwrite an earlier receipt (the Delivery Lead's revised ruling, 16 October 2026).
    /// </summary>
    public string NextPromptId() => Guid.NewGuid().ToString();

    /// <summary>Hand <paramref name="events"/> to the sink as one batch. An empty list sends nothing.</summary>
    public void Publish(IReadOnlyList<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return;
        _sink.Publish(new SessionEventBatch(SessionId, Generation, GenerationStartedUtc, events));
    }
}

/// <summary>
/// The sink the Director uses until the upstream message to the Gateway exists (phase 2): every event is written to the
/// Director's log as its JSON, so an API-mode session's events are on record and readable. It sends nothing anywhere.
/// </summary>
public sealed class LoggingSessionEventSink : ISessionEventSink
{
    public void Publish(SessionEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var e in batch.Events)
        {
            FileLog.Write($"[SessionEvent] session={batch.SessionId}, generation={batch.Generation}: " +
                          System.Text.Json.JsonSerializer.Serialize(e, SessionEventJson.Options));
        }
    }
}
