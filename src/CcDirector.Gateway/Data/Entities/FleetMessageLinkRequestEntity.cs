namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One REQUEST FOR A MESSAGE LINK (issue #3548): a session that was refused a message to a session it is not related to
/// asked the owner to let the two talk. One row per request, in the <c>fleet_message_link_requests</c> table, so a request
/// waits through a Gateway restart and every answer stays answerable afterwards.
///
/// A request asks; it never allows anything by itself. Only the owner's answer does, and an "allow" sets up an ordinary
/// <see cref="FleetMessageLinkEntity"/> through the same checks as a link the owner set up unasked.
///
/// Written by the asking session (<c>FleetMessageLinkRequestEndpoints</c>), by the owner's answer - from the owner's own
/// device or a raised session - and by the Gateway when either session has ended.
/// </summary>
public sealed class FleetMessageLinkRequestEntity : GatewayMintedKeyEntity
{
    /// <summary>The request's id, minted by the Gateway: 32 lower-case hex characters.</summary>
    public string RequestId { get; set; } = "";

    /// <summary>The session that asked, in the canonical lower-case form every roster row carries. It would be the
    /// link's sender.</summary>
    public string RequesterSessionId { get; set; } = "";

    /// <summary>The session it asked to talk to. It would be the link's recipient.</summary>
    public string TargetSessionId { get; set; } = "";

    /// <summary>Why the session asked, in its own words, shown to the owner as written.</summary>
    public string Reason { get; set; } = "";

    /// <summary><c>pending</c>, <c>allowed</c>, <c>declined</c> or <c>ended</c>
    /// (<c>Messaging.FleetMessageLinkRequestStatuses</c>).</summary>
    public string Status { get; set; } = "";

    /// <summary>When the session asked (UTC).</summary>
    public DateTime AskedAtUtc { get; set; }

    /// <summary>Who answered - the owner's device, or the raised session that answered for him - or the Gateway when a
    /// session ended; null while it waits.</summary>
    public string? AnsweredBy { get; set; }

    /// <summary>When it was answered or ended (UTC), or null.</summary>
    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>For an allowed request, how much the owner allowed (<c>Messaging.FleetMessageLinkAmounts</c>).</summary>
    public string? Amount { get; set; }

    /// <summary>For an allowed request, the link it set up.</summary>
    public string? LinkId { get; set; }
}
