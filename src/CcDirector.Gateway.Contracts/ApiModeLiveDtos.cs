namespace CcDirector.Gateway.Contracts;

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
    /// <summary>The Gateway cannot hand over every event after the client's cursor: the buffer let them go, or this
    /// Gateway process never held them (it restarted since).</summary>
    public const string MissedEvents = "missed_events";

    /// <summary>The Director restarted (a new generation): the sequence started again, and the client's cursor names
    /// a generation the Gateway no longer holds.</summary>
    public const string NewGeneration = "new_generation";

    /// <summary>The client read the stream more slowly than the session produced events, so the Gateway stopped
    /// sending to it rather than hold up the session's other readers. Reconnect, and rebuild.</summary>
    public const string FellBehind = "fell_behind";
}

/// <summary>
/// <c>GET /sessions/{sid}/live/items</c>: the same items the stream sends (<see cref="ApiModeLiveItem"/>, ruled by the
/// Gateway), for a client that polls.
/// </summary>
public sealed class ApiModeLivePageDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Set when the client must rebuild before using <see cref="Items"/>; null when the items continue exactly
    /// from the cursor it sent.</summary>
    public ApiModeLiveResetDto? Reset { get; set; }

    /// <summary>The items ruled from the events after the cursor, in the order a client applies them. After a reset
    /// they start with the stored receipts.</summary>
    public List<ApiModeLiveItem> Items { get; set; } = new();

    /// <summary>The cursor to send as <c>after</c> on the next poll; null means send none.</summary>
    public string? Next { get; set; }
}
