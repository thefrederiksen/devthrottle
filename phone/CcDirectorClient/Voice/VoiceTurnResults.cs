using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// 202 Accepted response from the Gateway's POST /sessions/{sid}/voice-turn/submit:
/// the <paramref name="TurnId"/> the phone polls with and when the cached result
/// expires on the Gateway (10-minute TTL).
/// </summary>
public sealed record VoiceTurnSubmitResult(string TurnId, DateTimeOffset? ExpiresAt);

/// <summary>
/// One poll response from the Gateway's GET /sessions/{sid}/voice-turn/{turnId}.
/// <see cref="Stage"/> is one of: submitted, transcribing, transcript, waiting,
/// thinking, summarizing, reply (terminal), error (terminal) - see
/// docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md.
/// </summary>
public sealed record VoiceTurnPollResult(
    string Stage,
    string? Transcript,
    string? Summary,
    string? AudioBase64,
    string? Error);

/// <summary>
/// Parsers for the Gateway's async voice-turn responses. Kept free of MAUI/Android
/// dependencies so the wire-shape handling (snake_case turn_id/expires_at on submit,
/// camelCase fields on poll) is unit tested off-device.
/// </summary>
public static class VoiceTurnResults
{
    /// <summary>
    /// Parse the submit response body. The Gateway emits snake_case
    /// (<c>turn_id</c>, <c>expires_at</c>). Throws when the body carries no turn id,
    /// so a malformed 202 surfaces immediately instead of producing a poll loop
    /// against an empty id.
    /// </summary>
    public static VoiceTurnSubmitResult ParseSubmit(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var turnId = ReadString(root, "turn_id");
        if (string.IsNullOrWhiteSpace(turnId))
            throw new InvalidOperationException("voice-turn/submit returned no turn id");

        DateTimeOffset? expiresAt = null;
        var expiresRaw = ReadString(root, "expires_at");
        if (!string.IsNullOrWhiteSpace(expiresRaw) && DateTimeOffset.TryParse(expiresRaw, out var parsed))
            expiresAt = parsed;

        return new VoiceTurnSubmitResult(turnId!, expiresAt);
    }

    /// <summary>
    /// Parse one poll response body ({ turn_id, stage, transcript, summary,
    /// audioBase64, message, expires_at }). A missing stage maps to "unknown" so the
    /// caller's poll loop keeps going rather than crashing on a partial body.
    /// </summary>
    public static VoiceTurnPollResult ParsePoll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new VoiceTurnPollResult(
            ReadString(root, "stage") ?? "unknown",
            ReadString(root, "transcript"),
            ReadString(root, "summary"),
            ReadString(root, "audioBase64"),
            ReadString(root, "message"));
    }

    private static string? ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
