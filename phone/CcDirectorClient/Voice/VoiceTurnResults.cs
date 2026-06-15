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
/// <see cref="AudioReady"/> / <see cref="AudioLength"/> (issue #407) tell the client the
/// reply audio is available from the dedicated audio endpoint (and how long it is) without
/// the poll carrying the bytes; <see cref="AudioBase64"/> is the back-compat inline path and
/// is empty/null in the slim poll.
/// </summary>
public sealed record VoiceTurnPollResult(
    string Stage,
    string? Transcript,
    string? Summary,
    string? AudioBase64,
    string? Error,
    bool AudioReady = false,
    int AudioLength = 0);

/// <summary>
/// One slice of reply audio fetched from the dedicated audio endpoint (issue #407).
/// <paramref name="Bytes"/> is the body returned for the requested byte offset; when the
/// server answered a Range request with 206 Partial Content, <paramref name="IsPartial"/>
/// is true and <paramref name="TotalLength"/> carries the full audio size parsed from the
/// Content-Range header, so the caller knows when it has received the whole clip. On a 200
/// (full body, no Range) <paramref name="IsPartial"/> is false and <paramref name="TotalLength"/>
/// equals the bytes returned. The resumable fetch loop appends slices until it has TotalLength
/// bytes, re-requesting only the missing tail after a dropped download.
/// </summary>
public sealed record VoiceTurnAudioChunk(byte[] Bytes, long TotalLength, bool IsPartial);

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
    /// Parse one poll response body ({ turn_id, stage, transcript, summary, audioReady,
    /// audioLength, audioBase64, message, expires_at }). A missing stage maps to "unknown" so
    /// the caller's poll loop keeps going rather than crashing on a partial body. The slim poll
    /// (issue #407) carries audioReady/audioLength and a null audioBase64; an older Gateway that
    /// still sends inline audioBase64 (no audioReady field) is handled too - AudioReady falls back
    /// to "audioBase64 is non-empty" so the caller can still tell whether a reply has audio.
    /// </summary>
    public static VoiceTurnPollResult ParsePoll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var audioBase64 = ReadString(root, "audioBase64");
        var audioReady = ReadBool(root, "audioReady") ?? !string.IsNullOrEmpty(audioBase64);
        var audioLength = ReadInt(root, "audioLength") ?? (audioBase64?.Length ?? 0);

        return new VoiceTurnPollResult(
            ReadString(root, "stage") ?? "unknown",
            ReadString(root, "transcript"),
            ReadString(root, "summary"),
            audioBase64,
            ReadString(root, "message"),
            audioReady,
            audioLength);
    }

    private static bool? ReadBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    private static int? ReadInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;

    private static string? ReadString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
