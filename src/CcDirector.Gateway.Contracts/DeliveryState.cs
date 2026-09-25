using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What became of one delivery - one recording's words sent to one session, named by its delivery id
/// (<see cref="PromptRequest.DeliveryId"/>). The Director is the one process that knows whether a delivery
/// happened, so it keeps this per delivery id and answers with it (Voice Delivery mission, phase 1).
///
/// On the wire it is the lower-case words <c>unknown</c>, <c>delivering</c>, <c>delivered</c> and
/// <c>not-delivered</c>, NEVER the enum's number: the numbers are positional, and a client or a stored
/// record reading a digit would have to hold its own copy of the ordering. <see cref="DeliveryStates"/>
/// holds the same words for code that handles the string form.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryState>))]
public enum DeliveryState
{
    /// <summary>The Director has no record of this delivery id for this session: it was never seen.
    /// This is NOT "the Director cannot say" - a Director that cannot read its record answers with a
    /// failure, and one older than the question answers "unknown verb". Only a never-seen id is this.</summary>
    [JsonStringEnumMemberName(DeliveryStates.Unknown)]
    Unknown,

    /// <summary>Typing has started and has not finished - or the Director stopped while it was typing, so
    /// the words may be in. A retry of this id is refused: typing it again could deliver it twice.</summary>
    [JsonStringEnumMemberName(DeliveryStates.Delivering)]
    Delivering,

    /// <summary>The send completed: the words reached the session. A retry of this id is refused.</summary>
    [JsonStringEnumMemberName(DeliveryStates.Delivered)]
    Delivered,

    /// <summary>The send threw, so the words did not reach the session; the reason says why. A retry of this
    /// id IS typed - it is a real retry of a failed send.</summary>
    [JsonStringEnumMemberName(DeliveryStates.NotDelivered)]
    NotDelivered,
}

/// <summary>
/// The wire words of <see cref="DeliveryState"/>, and the one place that turns one into the other. Use these
/// instead of bare literals wherever a delivery state travels as a string.
/// </summary>
public static class DeliveryStates
{
    public const string Unknown = "unknown";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
    public const string NotDelivered = "not-delivered";

    /// <summary>The wire word for <paramref name="state"/>.</summary>
    public static string Format(DeliveryState state) => state switch
    {
        DeliveryState.Unknown => Unknown,
        DeliveryState.Delivering => Delivering,
        DeliveryState.Delivered => Delivered,
        DeliveryState.NotDelivered => NotDelivered,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a delivery state."),
    };

    /// <summary>Reads a wire word back. A word that is not one of the four is refused rather than guessed:
    /// reading a corrupt or newer word as <see cref="DeliveryState.Unknown"/> would let a retry type again.</summary>
    public static bool TryParse(string? text, out DeliveryState state)
    {
        switch (text)
        {
            case Unknown: state = DeliveryState.Unknown; return true;
            case Delivering: state = DeliveryState.Delivering; return true;
            case Delivered: state = DeliveryState.Delivered; return true;
            case NotDelivered: state = DeliveryState.NotDelivered; return true;
            default: state = default; return false;
        }
    }
}

/// <summary>
/// Payload of the Director's <c>delivery-state</c> verb - "what became of delivery id X?" - asked of the session
/// named by the command's session id.
/// </summary>
public sealed class DeliveryStateRequest
{
    /// <summary>The verb's name on the tunnel.</summary>
    public const string Verb = "delivery-state";

    /// <summary>The delivery id asked about: the recording's upload id (<see cref="PromptRequest.DeliveryId"/>).</summary>
    public string DeliveryId { get; set; } = "";
}

/// <summary>The Director's answer to <see cref="DeliveryStateRequest"/>.</summary>
public sealed class DeliveryStateResponse
{
    /// <summary>The delivery id asked about.</summary>
    public string DeliveryId { get; set; } = "";

    /// <summary>What became of it. <see cref="DeliveryState.Unknown"/> only when the Director never saw it: a Director
    /// that cannot read its record answers with a failure instead, never with this.</summary>
    public DeliveryState State { get; set; }

    /// <summary>Why it was not delivered, for <see cref="DeliveryState.NotDelivered"/>; null otherwise.</summary>
    public string? Reason { get; set; }

    /// <summary>When the Director wrote that state, in UTC; null for <see cref="DeliveryState.Unknown"/>.</summary>
    public DateTime? At { get; set; }
}
