using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The words the Director shows for a session's request to talk to another session (issue #3851) - the same words the
/// Cockpit shows (<c>packages/client-core/src/fleet/messageLinksClient.ts</c>), so the owner reads one sentence on
/// either screen. Pure, so it is tested without an Avalonia app.
/// </summary>
public static class LinkAskWords
{
    /// <summary>What the session asked for: "one message", "one message and a reply", "as much as they need". A
    /// request that named no amount asked for what the Gateway gives by default, one message and a reply.</summary>
    public static string Amount(string? requestedAmount) => (requestedAmount ?? "").Trim().ToLowerInvariant() switch
    {
        "once" => "one message",
        "ongoing" => "as much as they need",
        _ => "one message and a reply",
    };

    /// <summary>
    /// The session asked for, as "Name (eb502e6b)": the Gateway's name for it first, because the session is often on
    /// another machine; this Director's own name for it next; the short id alone when neither knows a name.
    /// </summary>
    public static string Target(FleetMessageLinkRequestDto request, Func<string, string?> localName)
    {
        var id = request.TargetSessionId ?? "";
        var shortId = id.Length > 8 ? id[..8] : id;
        var name = !string.IsNullOrWhiteSpace(request.TargetSessionName) ? request.TargetSessionName : localName(id);
        return string.IsNullOrWhiteSpace(name) ? shortId : $"{name.Trim()} ({shortId})";
    }

    /// <summary>The rail row's line: "wants to talk to Cube Coordinator (eb502e6b)". Empty when nothing waits.</summary>
    public static string Line(IReadOnlyList<FleetMessageLinkRequestDto> mine, Func<string, string?> localName)
        => mine.Count == 0 ? "" : "wants to talk to " + string.Join(", ", mine.Select(r => Target(r, localName)));

    /// <summary>The header chip: "Wants to talk to Cube Coordinator (eb502e6b)", or "Wants to talk to 2 sessions".</summary>
    public static string Chip(IReadOnlyList<FleetMessageLinkRequestDto> mine, Func<string, string?> localName)
        => mine.Count switch
        {
            0 => "",
            1 => "Wants to talk to " + Target(mine[0], localName),
            _ => $"Wants to talk to {mine.Count} sessions",
        };
}
