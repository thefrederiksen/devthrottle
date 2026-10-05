namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One MESSAGE LINK (issue #3548): the owner - or the Fleet Manager for him - has let two sessions that are not owner
/// and worker talk to each other. One row per link, in the <c>fleet_message_links</c> table, so a link survives a
/// Gateway restart and every link ever set up stays answerable afterwards.
///
/// A link names exactly two sessions and how much talking it allows (<see cref="Amount"/>, one of
/// <c>Messaging.FleetMessageLinkAmounts</c>). It is never a wildcard, and it never narrows the default rule: it only
/// adds to it.
///
/// Written only by the owner's own device and by a raised session (the routes in <c>FleetMessageLinkEndpoints</c>),
/// by a message that uses up a one-time link (<c>FleetMessageStore.TryEnqueue</c>, in the same save as the message),
/// and by a session ending.
/// </summary>
public sealed class FleetMessageLinkEntity : GatewayMintedKeyEntity
{
    /// <summary>The link's id, minted by the Gateway: 32 lower-case hex characters.</summary>
    public string LinkId { get; set; } = "";

    /// <summary>The session that may send, in the canonical lower-case form every roster row carries.</summary>
    public string SenderSessionId { get; set; } = "";

    /// <summary>The session it may send to. On an <c>ongoing</c> link it may send back too.</summary>
    public string RecipientSessionId { get; set; } = "";

    /// <summary><c>once</c>, <c>once-with-reply</c> or <c>ongoing</c>.</summary>
    public string Amount { get; set; } = "";

    /// <summary><c>live</c>, <c>used</c>, <c>removed</c> or <c>ended</c> (<c>Messaging.FleetMessageLinkStatuses</c>).</summary>
    public string Status { get; set; } = "";

    /// <summary>Who set it up: the owner's device, or the raised session that did it for him.</summary>
    public string SetUpBy { get; set; } = "";

    /// <summary>When it was set up (UTC).</summary>
    public DateTime SetUpAtUtc { get; set; }

    /// <summary>For a one-time link, the message that used it up; null until then.</summary>
    public string? UsedMessageId { get; set; }

    /// <summary>When it was used up (UTC), or null.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Who ended it - the owner's device, a raised session, or the Gateway when a session ended - or null
    /// while it is live or once it was used.</summary>
    public string? EndedBy { get; set; }

    /// <summary>When it was removed or ended (UTC), or null.</summary>
    public DateTime? EndedAtUtc { get; set; }
}
