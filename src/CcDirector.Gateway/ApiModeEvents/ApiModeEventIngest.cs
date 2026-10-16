using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.ApiModeEvents;

/// <summary>
/// THE GATEWAY'S INGEST OF API-MODE SESSION EVENTS (mission #4070, phase 2). The hub's <c>PushApiModeEvents</c> hands
/// it each <see cref="ApiModeEventBatch"/> with the tenant and Director the connection is bound to (never anything the
/// batch claims); the hub has already applied the session-ownership rule. Here:
///
///  1. The batch is checked whole before anything is stored (<see cref="Parse"/>): a batch that disagrees with itself
///     is a Director defect, refused with the reason, and nothing is written.
///  2. The buffer decides, under the session's lock, which events are new: an older generation is refused, a later
///     one replaces the session's, and inside one generation only sequences above the highest held are new - so a
///     duplicate or late batch stores nothing and cannot move a session backwards.
///  3. The new events feed the durable receipt store (inside the tenant's scope), then the buffer appends them and
///     hands them to its subscribers.
///
/// The receipts are idempotent on their own (each row keeps the position of the last event applied), so a batch the
/// Gateway half-stored before an error is safe to send again.
/// </summary>
public sealed class ApiModeEventIngest
{
    private readonly ApiModeEventBuffer _buffer;
    private readonly ApiModeReceiptStore _receipts;
    private readonly HostedTenantBoundary _boundary;

    public ApiModeEventIngest(ApiModeEventBuffer buffer, ApiModeReceiptStore receipts, HostedTenantBoundary boundary)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _receipts = receipts ?? throw new ArgumentNullException(nameof(receipts));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
    }

    /// <summary>
    /// Store one batch from <paramref name="directorId"/> in <paramref name="tenant"/>. Answers what the Gateway now
    /// holds for the session, or null when the batch is REFUSED for good (an older generation); throws
    /// <see cref="ArgumentException"/> for a malformed batch, which the hub logs and answers null.
    /// </summary>
    public ApiModeEventWatermark? Accept(TenantId tenant, string directorId, ApiModeEventBatch batch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directorId);
        var events = Parse(batch);
        var first = events.Count > 0 ? events[0].Event.Sequence : -1;
        var last = events.Count > 0 ? events[^1].Event.Sequence : -1;

        var result = _buffer.Apply(tenant, batch.SessionId, batch.Generation, batch.GenerationStartedUtc, events, fresh =>
        {
            var position = (long sequence) => new ApiModeEventPosition(batch.Generation, batch.GenerationStartedUtc, sequence);
            using (_boundary.EnterScope(tenant))
            {
                foreach (var e in fresh)
                    FeedReceipts(directorId, e, position(e.Sequence));
            }
        });

        if (result.Refused)
        {
            FileLog.Write($"[ApiModeEventIngest] REFUSED director={directorId} session={batch.SessionId} generation={batch.Generation} sequences {first}..{last}: an older generation than the session's ({result.Generation})");
            return null;
        }
        FileLog.Write($"[ApiModeEventIngest] director={directorId} session={batch.SessionId} generation={batch.Generation}: stored {result.Stored} of {events.Count} event(s) (sequences {first}..{last}); highest held {result.HighestSequence}");
        return new ApiModeEventWatermark { SessionId = batch.SessionId, Generation = result.Generation, Sequence = result.HighestSequence };
    }

    /// <summary>The receipt-bearing events go to the receipt store; the rest are the buffer's only.</summary>
    private void FeedReceipts(string directorId, SessionEvent e, ApiModeEventPosition position)
    {
        var at = e.At.UtcDateTime;
        switch (e)
        {
            case PromptQueued q:
                _receipts.RecordQueued(directorId, q.SessionId, q.PromptId, Word(q.Reason), position, at);
                break;
            case PromptDelivered d:
                _receipts.RecordDelivered(directorId, d.SessionId, d.PromptId, Word(d.Disposition), d.Acknowledgement, position, at);
                break;
            case PromptRefused r:
                _receipts.RecordRefused(directorId, r.SessionId, r.PromptId, r.Reason, Word(r.RefusedBy), position, at);
                break;
            case TurnStarted s:
                _receipts.RecordTurnStarted(directorId, s.SessionId, s.TurnId, s.PromptIds, position);
                break;
            case TurnEnded t:
                _receipts.RecordTurnEnded(directorId, t.SessionId, t.TurnId, Word(t.Outcome), t.Reason, position, at);
                break;
        }
    }

    /// <summary>The event model's own word for an enumeration value, as the events write it on the wire.</summary>
    internal static string Word<TEnum>(TEnum value) where TEnum : struct, Enum
        => JsonSerializer.Serialize(value, SessionEventJson.Options).Trim('"');

    /// <summary>
    /// Check a batch whole and read its events. Refuses (<see cref="ArgumentException"/>) a batch with no session, no
    /// generation or no start time, too many events, an event that is not a session event, an event of another session,
    /// sequences that do not rise strictly, a negative sequence, a turn that ended other than done with no reason, or an
    /// id or text longer than the receipt store keeps - so nothing reaches the store that it would refuse half-way.
    /// </summary>
    internal static IReadOnlyList<ApiModeEventBuffer.Incoming> Parse(ApiModeEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (string.IsNullOrWhiteSpace(batch.SessionId)) throw new ArgumentException("the batch names no session", nameof(batch));
        if (batch.SessionId.Length > ApiModeReceiptStore.MaxIdLength) throw new ArgumentException($"the session id is longer than {ApiModeReceiptStore.MaxIdLength} characters", nameof(batch));
        if (string.IsNullOrWhiteSpace(batch.Generation)) throw new ArgumentException("the batch names no generation", nameof(batch));
        if (batch.Generation.Length > ApiModeEventBatch.MaxGenerationLength) throw new ArgumentException($"the generation is longer than {ApiModeEventBatch.MaxGenerationLength} characters", nameof(batch));
        if (batch.GenerationStartedUtc == default) throw new ArgumentException("GenerationStartedUtc is not set", nameof(batch));
        if (batch.Events is null) throw new ArgumentException("Events is null", nameof(batch));
        if (batch.Events.Count > ApiModeEventBatch.MaxEvents) throw new ArgumentException($"a batch may carry at most {ApiModeEventBatch.MaxEvents} events; this one carries {batch.Events.Count}", nameof(batch));

        var parsed = new List<ApiModeEventBuffer.Incoming>(batch.Events.Count);
        long previous = -1;
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var json = batch.Events[i] ?? throw new ArgumentException($"event {i} is null", nameof(batch));
            SessionEvent e;
            try
            {
                e = JsonSerializer.Deserialize<SessionEvent>(json, SessionEventJson.Options)
                    ?? throw new ArgumentException($"event {i} is null", nameof(batch));
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"event {i} is not a session event: {ex.Message}", nameof(batch), ex);
            }
            if (!string.Equals(e.SessionId, batch.SessionId, StringComparison.Ordinal))
                throw new ArgumentException($"event {i} ({e.GetType().Name}) belongs to session '{e.SessionId}', not the batch's '{batch.SessionId}'", nameof(batch));
            if (e.Sequence < 0)
                throw new ArgumentException($"event {i} has a negative sequence ({e.Sequence})", nameof(batch));
            if (i > 0 && e.Sequence <= previous)
                throw new ArgumentException($"event {i} has sequence {e.Sequence}, not above the previous {previous}; sequences rise strictly inside a batch", nameof(batch));
            CheckFields(i, e);
            previous = e.Sequence;
            parsed.Add(new ApiModeEventBuffer.Incoming(e, json.Length));
        }
        return parsed;
    }

    private static void CheckFields(int i, SessionEvent e)
    {
        switch (e)
        {
            case PromptQueued q: Id(i, q.PromptId, "promptId"); break;
            case PromptDelivered d: Id(i, d.PromptId, "promptId"); Text(i, d.Acknowledgement, "acknowledgement"); break;
            case PromptRefused r: Id(i, r.PromptId, "promptId"); Text(i, r.Reason, "reason"); break;
            case TurnStarted s:
                Id(i, s.TurnId, "turnId");
                if (s.PromptIds is null) throw new ArgumentException($"event {i} (TurnStarted) has no promptIds", "batch");
                foreach (var p in s.PromptIds) Id(i, p, "promptIds");
                break;
            case TurnEnded t:
                Id(i, t.TurnId, "turnId");
                if (t.Outcome != TurnOutcome.Done && string.IsNullOrWhiteSpace(t.Reason))
                    throw new ArgumentException($"event {i} (TurnEnded {ApiModeEventIngest.Word(t.Outcome)}) carries no reason; every outcome but done must say why", "batch");
                if (t.Reason is not null) Text(i, t.Reason, "reason");
                break;
            case SessionTurnEvent other: Id(i, other.TurnId, "turnId"); break;
        }
    }

    private static void Id(int i, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"event {i} has an empty {name}", "batch");
        if (value.Length > ApiModeReceiptStore.MaxIdLength) throw new ArgumentException($"event {i} has a {name} longer than {ApiModeReceiptStore.MaxIdLength} characters", "batch");
    }

    private static void Text(int i, string? value, string name)
    {
        if (value is null) throw new ArgumentException($"event {i} has no {name}", "batch");
        if (value.Length > ApiModeReceiptStore.MaxVerbatimLength) throw new ArgumentException($"event {i} has a {name} of {value.Length} characters; at most {ApiModeReceiptStore.MaxVerbatimLength} are stored, and a verbatim field is never cut", "batch");
    }
}
