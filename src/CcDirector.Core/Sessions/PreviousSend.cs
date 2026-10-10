using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// HOW THE SEND BEFORE THIS ONE ENDED, written into a refusal (the Error Logging mission, issue #3675, step 4e).
///
/// On 9 October 2026 a phone prompt was refused six times in a row because the composer still held a whole earlier
/// prompt that had been typed and never submitted - and nothing in the error log said which earlier send left it, or
/// how that send had ended. A refusal now says so: <c>previous_send=delivered|timed-out|refused|in-flight|none</c> and,
/// when that send was recorded with one, <c>previous_correlation_id=</c> the id every report of that send carries.
///
/// The answer is read from the Director's own <see cref="DeliveryRecord"/> - the one place the Director already writes
/// how each send ended - never from a second record. Every word here is written by our own code; nothing a person or a
/// model wrote is ever part of it.
///
/// The prompt verb opens a <see cref="Begin"/> scope naming its record and its own delivery id, so the current send is
/// never taken for the previous one. A refusal outside any scope (a send typed on the desktop) reads the Director's
/// shared record, where such a send has no line of its own.
/// </summary>
public static class PreviousSend
{
    public const string Delivered = "delivered";
    public const string TimedOut = "timed-out";
    public const string Refused = "refused";
    public const string InFlight = "in-flight";
    public const string None = "none";

    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    private sealed class Scope : IDisposable
    {
        public required DeliveryRecord Record;
        public required Guid SessionId;
        public required string? DeliveryId;
        public required Scope? Outer;

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = Outer;
        }
    }

    /// <summary>The word for how a send ended, from its <see cref="DeliveryRecord"/> state. A send the record holds no
    /// line for is <see cref="None"/>.</summary>
    public static string WordFor(DeliveryState state) => state switch
    {
        DeliveryState.Delivered => Delivered,
        // Unconfirmed: the words left the composer and the watch ran out without proof either way.
        DeliveryState.Unconfirmed => TimedOut,
        DeliveryState.NotDelivered => Refused,
        DeliveryState.Delivering => InFlight,
        _ => None,
    };

    /// <summary>
    /// For the send that is starting on this flow of execution: refusals logged inside the scope describe the latest
    /// OTHER send in <paramref name="record"/> for <paramref name="sessionId"/>, never <paramref name="deliveryId"/> itself.
    /// </summary>
    public static IDisposable Begin(DeliveryRecord record, Guid sessionId, string? deliveryId)
    {
        ArgumentNullException.ThrowIfNull(record);
        var scope = new Scope { Record = record, SessionId = sessionId, DeliveryId = deliveryId, Outer = CurrentScope.Value };
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// <c>previous_send=&lt;word&gt;</c>, and <c>, previous_correlation_id=&lt;id&gt;</c> when the previous send was recorded
    /// with one, for a refusal of a send to <paramref name="sessionId"/>. Never throws: it is written from inside a
    /// failure, and an exception here would replace the failure being recorded. A record that cannot be read says so.
    /// </summary>
    public static string Describe(Guid sessionId)
    {
        var scope = CurrentScope.Value;
        var inScope = scope is not null && scope.SessionId == sessionId;
        var record = inScope ? scope!.Record : DeliveryRecord.Shared;
        var except = inScope ? scope!.DeliveryId : null;
        DeliveryRecordEntry? previous;
        try
        {
            previous = record.LatestOtherThan(sessionId, except);
        }
        catch (DeliveryRecordUnreadableException ex)
        {
            FileLog.Write($"[PreviousSend] Describe: session={sessionId}: the delivery record cannot be read: {ex.FilePath}");
            return "previous_send=unknown (the delivery record cannot be read)";
        }
        if (previous is null) return $"previous_send={None}";
        DeliveryStates.TryParse(previous.State, out var state);
        var text = $"previous_send={WordFor(state)}";
        if (!string.IsNullOrWhiteSpace(previous.CorrelationId)) text += $", previous_correlation_id={previous.CorrelationId}";
        return text;
    }
}
