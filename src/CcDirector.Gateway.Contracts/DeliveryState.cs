using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What became of one delivery - one recording's words sent to one session, named by its delivery id
/// (<see cref="PromptRequest.DeliveryId"/>). The Director is the one process that knows whether a delivery
/// happened, so it keeps this per delivery id and answers with it (Voice Delivery mission, phase 1).
///
/// On the wire it is the lower-case words <c>unknown</c>, <c>delivering</c>, <c>delivered</c>,
/// <c>not-delivered</c> and <c>unconfirmed</c>, NEVER the enum's number: the numbers are positional, and a client or a stored
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

    /// <summary>The words provably did not reach the session - the send threw, was refused before typing, or the
    /// session ended first; the reason says why. A retry of this id IS typed - it is a real retry of a failed send.</summary>
    [JsonStringEnumMemberName(DeliveryStates.NotDelivered)]
    NotDelivered,

    /// <summary>
    /// The words left the composer and the Director's watch of the agent's records ended WITHOUT PROOF EITHER WAY
    /// (issue #3484): the limit ran out with no records to watch, the records watch failed, the records could not be
    /// read at all, or they never showed the words. The agent may hold them, so a retry of this id is refused - typing
    /// it again could deliver it twice - and the Gateway answers it "could not confirm it arrived", with no "Send
    /// anyway". Added after the other four, so a Gateway older than it cannot read the word: the Director sends it
    /// only to a Gateway that says it reads it (<see cref="DeliveryStateRequest.ReadsUnconfirmed"/>,
    /// <see cref="PromptRequest.ReadsUnconfirmed"/>).
    /// </summary>
    [JsonStringEnumMemberName(DeliveryStates.Unconfirmed)]
    Unconfirmed,
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
    public const string Unconfirmed = "unconfirmed";

    /// <summary>The wire word for <paramref name="state"/>.</summary>
    public static string Format(DeliveryState state) => state switch
    {
        DeliveryState.Unknown => Unknown,
        DeliveryState.Delivering => Delivering,
        DeliveryState.Delivered => Delivered,
        DeliveryState.NotDelivered => NotDelivered,
        DeliveryState.Unconfirmed => Unconfirmed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a delivery state."),
    };

    /// <summary>Reads a wire word back. A word that is not one of the five is refused rather than guessed:
    /// reading a corrupt or newer word as <see cref="DeliveryState.Unknown"/> would let a retry type again.</summary>
    public static bool TryParse(string? text, out DeliveryState state)
    {
        switch (text)
        {
            case Unknown: state = DeliveryState.Unknown; return true;
            case Delivering: state = DeliveryState.Delivering; return true;
            case Delivered: state = DeliveryState.Delivered; return true;
            case NotDelivered: state = DeliveryState.NotDelivered; return true;
            case Unconfirmed: state = DeliveryState.Unconfirmed; return true;
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

    /// <summary>
    /// True when the Gateway asking can read <see cref="DeliveryState.Unconfirmed"/> (issue #3484). A Gateway older
    /// than that word does not send this field, and its reader throws on a word it does not know - so a Director never
    /// answers such a Gateway <c>unconfirmed</c>. It answers a failure instead, which that Gateway reads as "no answer"
    /// and rules "could not confirm it arrived" past its own age limit, with no "Send anyway": the same verdict, reached
    /// the older way. Never a retry that could double the words.
    /// </summary>
    public bool ReadsUnconfirmed { get; set; }
}

/// <summary>The Director's answer to <see cref="DeliveryStateRequest"/>.</summary>
public sealed class DeliveryStateResponse
{
    /// <summary>The delivery id asked about.</summary>
    public string DeliveryId { get; set; } = "";

    /// <summary>What became of it. <see cref="DeliveryState.Unknown"/> only when the Director never saw it: a Director
    /// that cannot read its record answers with a failure instead, never with this.</summary>
    public DeliveryState State { get; set; }

    /// <summary>Why it was not delivered, for <see cref="DeliveryState.NotDelivered"/>, or why it could not be confirmed,
    /// for <see cref="DeliveryState.Unconfirmed"/>; null otherwise.</summary>
    public string? Reason { get; set; }

    /// <summary>When the Director wrote that state, in UTC; null for <see cref="DeliveryState.Unknown"/>.</summary>
    public DateTime? At { get; set; }
}
