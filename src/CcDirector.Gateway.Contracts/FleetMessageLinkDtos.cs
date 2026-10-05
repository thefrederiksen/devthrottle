namespace CcDirector.Gateway.Contracts;

/// <summary>
/// <c>POST /fleet/links</c> (issue #3548): set up a message link between two sessions that are not owner and worker.
/// </summary>
public sealed class FleetMessageLinkCreateRequest
{
    /// <summary>The session that may send.</summary>
    public string SenderSessionId { get; set; } = "";

    /// <summary>The session it may send to. On an <c>ongoing</c> link it may send back too.</summary>
    public string RecipientSessionId { get; set; } = "";

    /// <summary><c>once</c> (one message, no reply), <c>once-with-reply</c> (one message and its reply) or
    /// <c>ongoing</c> (as much as they need, both ways, until removed or a session ends).</summary>
    public string Amount { get; set; } = "";
}

/// <summary>One message link, in the words the owner reads.</summary>
public sealed class FleetMessageLinkDto
{
    public string LinkId { get; set; } = "";

    public string SenderSessionId { get; set; } = "";

    public string RecipientSessionId { get; set; } = "";

    /// <summary><c>once</c>, <c>once-with-reply</c> or <c>ongoing</c>.</summary>
    public string Amount { get; set; } = "";

    /// <summary><c>live</c>, <c>used</c>, <c>removed</c> or <c>ended</c>.</summary>
    public string Status { get; set; } = "";

    /// <summary>One finished sentence saying what the link allows and where it stands, for a client to show verbatim.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Who set it up: the owner's device, or the raised session that did it for him.</summary>
    public string SetUpBy { get; set; } = "";

    public DateTime SetUpAtUtc { get; set; }

    public string? UsedMessageId { get; set; }

    public DateTime? UsedAtUtc { get; set; }

    public string? EndedBy { get; set; }

    public DateTime? EndedAtUtc { get; set; }
}

/// <summary>The answer to <c>GET /fleet/links</c>: every live link, then every link that stopped in the last
/// <see cref="StoppedWithinDays"/> days.</summary>
public sealed class FleetMessageLinkListResponse
{
    public List<FleetMessageLinkDto> Links { get; set; } = new();

    public int StoppedWithinDays { get; set; }
}

/// <summary>The answer to <c>POST /fleet/links</c>.</summary>
public sealed class FleetMessageLinkCreateResponse
{
    /// <summary>The new link.</summary>
    public FleetMessageLinkDto Link { get; set; } = new();

    /// <summary>The live links between the same two sessions that it replaced; each is now removed.</summary>
    public List<FleetMessageLinkDto> Replaced { get; set; } = new();
}
