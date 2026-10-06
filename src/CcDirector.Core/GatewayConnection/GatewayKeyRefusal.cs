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

    /// <summary>The Gateway does not know the key at all: its own "missing or invalid token" answer.</summary>
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

    /// <summary>The <c>error</c> of the Gateway's own 401 for a key it does not know (it carries no code).</summary>
    public const string UnknownKeyError = "missing or invalid token";

    /// <summary>
    /// Read a 401's body, and say what the GATEWAY refused - or null when the body is not one of the Gateway's own
    /// credential answers. Only positive evidence counts (review RM-F1): a 401 can come from a proxy or an intermediary
    /// in front of the Gateway, and stopping a Director on one of those would strand it until it is set up again.
    /// <list type="bullet">
    /// <item><c>code: device_credential_revoked</c> with <c>reason: team_member_removed</c>: removed from the team.</item>
    /// <item>That code with any other reason, or none (a Gateway from before the reason was sent): a plain revoke.</item>
    /// <item><c>error: missing or invalid token</c> and no code: a key the Gateway does not know.</item>
    /// <item>Anything else - no body, an HTML page, other JSON: null, and the caller retries as before.</item>
    /// </list>
    /// </summary>
    /// <param name="body">The 401's body, or null when none was read.</param>
    /// <param name="teamName">The team this Director recorded, or null.</param>
    public static GatewayKeyRefusal? FromUnauthorizedBody(string? body, string? teamName)
    {
        var answer = ReadAnswer(body);
        if (answer is null)
            return null;
        var (error, code, reason) = answer.Value;

        GatewayKeyRefusalKind kind;
        if (string.Equals(code, RevokedCode, StringComparison.Ordinal))
            kind = string.Equals(reason, TeamRemovalReason, StringComparison.Ordinal)
                ? GatewayKeyRefusalKind.RemovedFromTeam
                : GatewayKeyRefusalKind.KeyRevoked;
        else if (code is null && string.Equals(error, UnknownKeyError, StringComparison.Ordinal))
            kind = GatewayKeyRefusalKind.KeyNotAccepted;
        else
            return null;

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

    // The error, code and reason of a JSON object body, or null when the body is not a JSON object at all.
    private static (string? Error, string? Code, string? Reason)? ReadAnswer(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return (StringProperty(doc.RootElement, "error"), StringProperty(doc.RootElement, "code"),
                StringProperty(doc.RootElement, "reason"));
        }
        catch (JsonException)
        {
            // Not JSON: a proxy's page, say. Not the Gateway's answer, so it is no evidence of a refused key.
            return null;
        }
    }

    private static string? StringProperty(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
