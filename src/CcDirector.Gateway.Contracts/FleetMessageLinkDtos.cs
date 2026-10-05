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

/// <summary>
/// <c>POST /fleet/link-requests</c> (issue #3548): the calling session asks the owner to let it talk to a session it may
/// not message. Only a session asks, and only for itself.
/// </summary>
public sealed class FleetMessageLinkRequestAskRequest
{
    /// <summary>The session it wants to talk to.</summary>
    public string TargetSessionId { get; set; } = "";

    /// <summary>Why, in a sentence the owner reads as written. Required; at most 500 characters are kept.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>One request for a message link, as the owner and the asking session read it.</summary>
public sealed class FleetMessageLinkRequestDto
{
    public string RequestId { get; set; } = "";

    public string RequesterSessionId { get; set; } = "";

    public string TargetSessionId { get; set; } = "";

    public string Reason { get; set; } = "";

    /// <summary><c>pending</c>, <c>allowed</c>, <c>declined</c> or <c>ended</c>.</summary>
    public string Status { get; set; } = "";

    public DateTime AskedAtUtc { get; set; }

    public string? AnsweredBy { get; set; }

    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>For an allowed request, how much the owner allowed.</summary>
    public string? Amount { get; set; }

    /// <summary>For an allowed request, the link it set up.</summary>
    public string? LinkId { get; set; }
}

/// <summary>The answer to <c>POST /fleet/link-requests</c>.</summary>
public sealed class FleetMessageLinkRequestAskResponse
{
    public FleetMessageLinkRequestDto Request { get; set; } = new();

    /// <summary>False when the same request was already waiting; nothing new was asked.</summary>
    public bool Created { get; set; }

    /// <summary>What the asking session is told, in words.</summary>
    public string Note { get; set; } = "";
}

/// <summary>The answer to <c>GET /fleet/link-requests</c>: every waiting request, then every request answered in the
/// last <see cref="AnsweredWithinDays"/> days.</summary>
public sealed class FleetMessageLinkRequestListResponse
{
    public List<FleetMessageLinkRequestDto> Requests { get; set; } = new();

    public int AnsweredWithinDays { get; set; }
}

/// <summary>
/// <c>POST /fleet/link-requests/{id}/answer</c>: the owner's answer. Either an <see cref="Amount"/>, which allows the
/// request and sets up a link from the asking session to the one it asked for, or <see cref="Decline"/>.
/// </summary>
public sealed class FleetMessageLinkRequestAnswerRequest
{
    /// <summary><c>once</c>, <c>once-with-reply</c> or <c>ongoing</c> to allow; null with <see cref="Decline"/>.</summary>
    public string? Amount { get; set; }

    /// <summary>True to say no.</summary>
    public bool Decline { get; set; }
}

/// <summary>The answer to <c>POST /fleet/link-requests/{id}/answer</c>.</summary>
public sealed class FleetMessageLinkRequestAnswerResponse
{
    public FleetMessageLinkRequestDto Request { get; set; } = new();

    /// <summary>The link the answer set up, when it allowed the request.</summary>
    public FleetMessageLinkDto? Link { get; set; }
}
