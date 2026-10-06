using System.Text.Json;

namespace CcDirector.Core.GatewayConnection;

/// <summary>Why the Gateway refused this Director's key with a 401.</summary>
public enum GatewayKeyRefusalKind
{
    /// <summary>The key was revoked because its person was removed from the team the Director works for. Said ONLY
    /// when the Gateway's answer names that reason (<c>reason: team_member_removed</c>).</summary>
    RemovedFromTeam,

    /// <summary>The key was revoked for any other reason, or by a Gateway too old to say why.</summary>
    KeyRevoked,

    /// <summary>The Gateway does not know the key at all ("missing or invalid token"), or gave no reason it can read.</summary>
    KeyNotAccepted,
}

/// <summary>
/// The Gateway refused this Director's key with a 401, so the Director has stopped trying to connect - a key that
/// is refused once is refused for good, and retrying it as if it were a network problem only leaves the person
/// looking at "Connecting..." for ever (devthrottle_internal#2311, live proof F3). This says what happened and what
/// to do, in the words the status box and the connection panel show.
///
/// The Gateway's answer is read, never guessed: "removed from the team" is said only when the answer carries the
/// <see cref="TeamRemovalReason"/>, so a key revoked for any other reason is never reported as a removal.
/// </summary>
/// <param name="Kind">What the Gateway's answer said.</param>
/// <param name="TeamName">The team this Director recorded it works for, or null when none is recorded.</param>
public sealed record GatewayKeyRefusal(GatewayKeyRefusalKind Kind, string? TeamName)
{
    /// <summary>The code the Gateway puts on a 401 for a revoked key.</summary>
    public const string RevokedCode = "device_credential_revoked";

    /// <summary>The reason the Gateway adds to that 401 when the key's person was removed from the team.</summary>
    public const string TeamRemovalReason = "team_member_removed";

    /// <summary>
    /// Read a 401's body. <c>code: device_credential_revoked</c> with <c>reason: team_member_removed</c> is a removal
    /// from the team; that code with any other reason, or none (a Gateway from before the reason was sent), is a plain
    /// revoke; anything else - "missing or invalid token", an empty or unreadable body - is a key the Gateway does not
    /// accept.
    /// </summary>
    /// <param name="body">The 401's body, or null when none was read.</param>
    /// <param name="teamName">The team this Director recorded, or null.</param>
    public static GatewayKeyRefusal FromUnauthorizedBody(string? body, string? teamName)
    {
        var (code, reason) = ReadCodeAndReason(body);
        var kind = !string.Equals(code, RevokedCode, StringComparison.Ordinal)
            ? GatewayKeyRefusalKind.KeyNotAccepted
            : string.Equals(reason, TeamRemovalReason, StringComparison.Ordinal)
                ? GatewayKeyRefusalKind.RemovedFromTeam
                : GatewayKeyRefusalKind.KeyRevoked;
        return new GatewayKeyRefusal(kind, string.IsNullOrWhiteSpace(teamName) ? null : teamName.Trim());
    }

    /// <summary>The few words the status box shows where it showed "Connecting...".</summary>
    public string ChipText => Kind switch
    {
        GatewayKeyRefusalKind.RemovedFromTeam => TeamName is null ? "Removed from the team" : $"Removed from {TeamName}",
        GatewayKeyRefusalKind.KeyRevoked => "Key revoked",
        GatewayKeyRefusalKind.KeyNotAccepted => "Key not accepted",
        _ => throw new InvalidOperationException($"Unknown key refusal {Kind}"),
    };

    /// <summary>What happened and what the person can do, in full.</summary>
    public string Summary => Kind switch
    {
        GatewayKeyRefusalKind.RemovedFromTeam =>
            $"This Director was removed from {TeamName ?? "its team"}: you are no longer a member of that team, so the " +
            "Gateway revoked this Director's key and it has stopped trying to connect. " + WhatToDo,
        GatewayKeyRefusalKind.KeyRevoked =>
            "The Gateway revoked this Director's key, so it has stopped trying to connect. " + WhatToDo,
        GatewayKeyRefusalKind.KeyNotAccepted =>
            "The Gateway does not accept this Director's key, so it has stopped trying to connect. " + WhatToDo,
        _ => throw new InvalidOperationException($"Unknown key refusal {Kind}"),
    };

    // A revoked key cannot be moved to another team - the move acts on a working key - so the way back is to set the
    // Director up again, which issues a new key for whichever team (or the personal account) the person chooses.
    private const string WhatToDo =
        "To use it again, set it up again: click the Gateway status, sign in, and choose another team or set it up for yourself.";

    private static (string? Code, string? Reason) ReadCodeAndReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (StringProperty(doc.RootElement, "code"), StringProperty(doc.RootElement, "reason"));
        }
        catch (JsonException)
        {
            // Not JSON: a proxy's page, say. It names no code, so it is read as a key not accepted - exactly what a
            // body with no code is.
            return (null, null);
        }
    }

    private static string? StringProperty(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
