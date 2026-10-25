namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The closed set of alert kinds (the Alerts mission, section 5.2). A client switches on these and nothing else; a kind
/// it does not know is a kind it cannot answer, so a new one is added here, in its source, and in the client together.
/// </summary>
public static class AlertKinds
{
    /// <summary>A session asked for a Director restart (<see cref="DirectorRestartRequestDto"/> is the detail).</summary>
    public const string RestartRequest = "restart-request";

    /// <summary>A secret transfer waits for, or had, the owner's approval (<see cref="SecretTransferDto"/> is the detail).</summary>
    public const string SecretTransfer = "secret-transfer";

    /// <summary>A session asked to message another session (<see cref="FleetMessageLinkRequestDto"/> is the detail).</summary>
    public const string MessageRequest = "message-request";

    /// <summary>Every kind, in the order the sources are composed.</summary>
    public static readonly IReadOnlyList<string> All = new[] { RestartRequest, SecretTransfer, MessageRequest };

    /// <summary>The alert id of a source record: <c>"{kind}:{sourceId}"</c>.</summary>
    public static string IdOf(string kind, string sourceId) => kind + ":" + sourceId;
}

/// <summary>
/// ONE ALERT: something that asks the owner for an answer, or did in the last day (the Alerts mission, section 5.2).
///
/// AN ALERT IS NOT NEW DATA. It is a read-time view of a record another feature already owns - a restart request, a
/// secret transfer, a message request - folded by that feature's own source. Every sentence on it is written on the
/// Gateway and rendered verbatim; the client decides nothing but layout. Answering goes through the feature's own,
/// already-tested routes, using the record in <see cref="Detail"/>.
/// </summary>
public sealed class AlertDto
{
    /// <summary>Stable and globally unique: <c>"{kind}:{sourceId}"</c>, e.g. <c>restart-request:3f9c...</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="AlertKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>True while the owner can still act. This alone drives the red count.</summary>
    public bool NeedsAnswer { get; set; }

    /// <summary>One line, written for the owner.</summary>
    public string Title { get; set; } = "";

    /// <summary>One plain sentence: who asked and why, or the outcome.</summary>
    public string Summary { get; set; } = "";

    /// <summary>A short status word: <c>Waiting</c>, <c>Accepted</c>, <c>Declined</c>, <c>Expired</c>, <c>Delivered</c>...</summary>
    public string Status { get; set; } = "";

    /// <summary>When the record was made.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the record last changed. Ordering, the "Earlier" window and seen marking all read this.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>When a needs-answer alert stops being answerable, or null.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The owner has marked this alert seen since it last changed.</summary>
    public bool Seen { get; set; }

    /// <summary>The kind's own record, as its feature already serves it, for the detail view and its actions.</summary>
    public object? Detail { get; set; }
}

/// <summary>The answer to <c>GET /gateway/alerts</c>.</summary>
public sealed class AlertListResponse
{
    /// <summary>Needs-answer alerts first, then the rest; newest first within each.</summary>
    public List<AlertDto> Alerts { get; set; } = new();

    /// <summary>How many alerts need an answer: the bell's red count.</summary>
    public int NeedsAnswerCount { get; set; }
}

/// <summary>The body of <c>POST /gateway/alerts/seen</c>.</summary>
public sealed class AlertSeenRequest
{
    /// <summary>The alert ids to mark seen at their current version.</summary>
    public List<string>? Ids { get; set; }
}

/// <summary>The answer to <c>POST /gateway/alerts/seen</c>.</summary>
public sealed class AlertSeenResponse
{
    /// <summary>How many of the given ids named a current alert and were marked.</summary>
    public int Marked { get; set; }
}

/// <summary>The answer to <c>POST /gateway/alerts/{id}/clear</c> and <c>POST /gateway/alerts/clear</c>.</summary>
public sealed class AlertClearResponse
{
    /// <summary>How many alerts this call cleared.</summary>
    public int Cleared { get; set; }
}
