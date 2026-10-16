namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One item of an API-mode session's live relay (mission #4070, phase 2): <c>GET /sessions/{sid}/live</c> sends it as
/// the <c>data</c> of an <c>item</c> event, and <c>GET /sessions/{sid}/live/items</c> lists it.
/// </summary>
public sealed class ApiModeLiveItemDto
{
    /// <summary>Where this item stands in the session's stream, <c>&lt;generation&gt;:&lt;sequence&gt;</c>. Hand it back as
    /// <c>after</c> (or, on the stream, it is the event's <c>id</c>, which a browser sends back as <c>Last-Event-ID</c>) to
    /// get what comes after it.</summary>
    public string Cursor { get; set; } = "";

    /// <summary>The display item the Gateway ruled from the session event (CLAUDE.md rule 7): a client renders it as it
    /// is and never interprets the event behind it.</summary>
    public object? Item { get; set; }
}

/// <summary>
/// The relay's one explicit gap: the client missed events and must not append what follows to what it holds. It
/// rebuilds the session from durable state instead - the receipts (<c>GET /sessions/{sid}/receipts</c>) and the
/// stored conversation (<c>GET /sessions/{sid}/history</c>) - and then carries on with the items that follow the reset.
/// </summary>
public sealed class ApiModeLiveResetDto
{
    /// <summary>Why, as one of <see cref="ApiModeLiveResetCodes"/>, for a client or test that tells them apart.</summary>
    public string Code { get; set; } = "";

    /// <summary>Why, as a sentence to show or log as it is.</summary>
    public string Message { get; set; } = "";

    /// <summary>The routes to rebuild from, with the session id filled in.</summary>
    public List<string> RebuildFrom { get; set; } = new();
}

/// <summary>The values of <see cref="ApiModeLiveResetDto.Code"/>.</summary>
public static class ApiModeLiveResetCodes
{
    /// <summary>The Gateway no longer holds every event after the client's cursor (the buffer let the oldest go, or the
    /// Gateway restarted), or the client had no cursor and the buffer no longer holds the session from its start.</summary>
    public const string MissedEvents = "missed_events";

    /// <summary>The Director restarted (a new generation): the sequence started again, and the client's cursor names
    /// a generation the Gateway no longer holds.</summary>
    public const string NewGeneration = "new_generation";

    /// <summary>The client read the stream more slowly than the session produced events, so the Gateway stopped
    /// sending to it rather than hold up the session's other readers. Reconnect, and rebuild.</summary>
    public const string FellBehind = "fell_behind";
}

/// <summary>
/// <c>GET /sessions/{sid}/live/items</c>: the same items the stream sends, for a client that polls.
/// </summary>
public sealed class ApiModeLivePageDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Set when the client must rebuild before using <see cref="Items"/>; null when the items continue exactly
    /// from the cursor it sent.</summary>
    public ApiModeLiveResetDto? Reset { get; set; }

    /// <summary>The items after the cursor, oldest first.</summary>
    public List<ApiModeLiveItemDto> Items { get; set; } = new();

    /// <summary>The cursor to send as <c>after</c> on the next poll; null means send none.</summary>
    public string? Next { get; set; }
}
