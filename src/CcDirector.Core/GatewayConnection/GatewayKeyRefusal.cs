using System.Text.Json;

namespace CcDirector.Core.GatewayConnection;

/// <summary>Why the Gateway revoked this Director's key.</summary>
public enum GatewayKeyRefusalKind
{
    /// <summary>The key was revoked because its person was removed from the team the Director works for. Said ONLY
    /// when the Gateway's answer names that reason (<c>reason: team_member_removed</c>).</summary>
    RemovedFromTeam,

    /// <summary>The key was revoked for any other reason, or by a Gateway too old to say why.</summary>
    KeyRevoked,
}

/// <summary>
/// The Gateway revoked this Director's key, so the Director has stopped trying to connect - a revoked key is refused
/// on every dial, and retrying it as if it were a network problem only leaves the person looking at "Connecting..." for
/// ever (devthrottle_internal#2311, live proof F3). This says what happened and what to do, in the words the status
/// box and the connection panel show.
///
/// The Gateway's answer is read, never guessed: "removed from the team" is said only when the answer carries the
/// <see cref="TeamRemovalReason"/>, so a key revoked for any other reason is never reported as a removal.
/// </summary>
/// <param name="Kind">What the Gateway's answer said.</param>
/// <param name="TeamName">The team the refused key was set up for, or null when that is not known for certain.</param>
public sealed record GatewayKeyRefusal(GatewayKeyRefusalKind Kind, string? TeamName)
{
    /// <summary>The <c>error</c> of the Gateway's 401 for a revoked key.</summary>
    public const string RevokedError = "device credential revoked";

    /// <summary>The <c>code</c> of the Gateway's 401 for a revoked key.</summary>
    public const string RevokedCode = "device_credential_revoked";

    /// <summary>The reason the Gateway adds to that 401 when the key's person was removed from the team.</summary>
    public const string TeamRemovalReason = "team_member_removed";

    /// <summary>
    /// Read a 401's body, and say what the Gateway revoked - or null when the body is not the Gateway's revoke answer
    /// in full. Only that answer stops the Director (reviews RM-F1 and RM-F4): a 401 can come from a proxy, a load
    /// balancer or another service in front of the Gateway, and stopping on one of those would strand a healthy
    /// Director until it is set up again. So the body must carry BOTH <c>error: device credential revoked</c> and
    /// <c>code: device_credential_revoked</c>, exactly as the Gateway writes it; with <c>reason: team_member_removed</c>
    /// it is a removal from the team, with any other reason or none (a Gateway from before the reason) a plain revoke.
    /// Anything else - no body, an HTML page, a code with no error, the generic <c>missing or invalid token</c> any hop
    /// can send - is null, and the caller retries exactly as it does for a network problem.
    /// </summary>
    /// <param name="body">The 401's body, or null when none was read.</param>
    /// <param name="teamName">The team the refused key was set up for, or null.</param>
    public static GatewayKeyRefusal? FromUnauthorizedBody(string? body, string? teamName)
    {
        var answer = ReadAnswer(body);
        if (answer is null)
            return null;
        var (error, code, reason) = answer.Value;
        if (!string.Equals(error, RevokedError, StringComparison.Ordinal)
            || !string.Equals(code, RevokedCode, StringComparison.Ordinal))
            return null;

        var kind = string.Equals(reason, TeamRemovalReason, StringComparison.Ordinal)
            ? GatewayKeyRefusalKind.RemovedFromTeam
            : GatewayKeyRefusalKind.KeyRevoked;
        return new GatewayKeyRefusal(kind, string.IsNullOrWhiteSpace(teamName) ? null : teamName.Trim());
    }

    /// <summary>The few words the status box shows where it showed "Connecting...".</summary>
    public string ChipText => Kind switch
    {
        GatewayKeyRefusalKind.RemovedFromTeam => TeamName is null ? "Removed from the team" : $"Removed from {TeamName}",
        GatewayKeyRefusalKind.KeyRevoked => "Key revoked",
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
            // Not JSON: a proxy's page, say. Not the Gateway's answer, so it is no evidence of a revoked key.
            return null;
        }
    }

    private static string? StringProperty(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
