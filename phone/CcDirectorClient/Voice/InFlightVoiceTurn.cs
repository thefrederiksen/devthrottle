using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirectorClient.Voice;

/// <summary>
/// The one voice turn currently in flight: the <see cref="SessionId"/> it was submitted
/// to, the <see cref="TurnId"/> the Gateway handed back to poll with, and when it was
/// <see cref="SubmittedAt"/> (issue #406). Persisted the instant submit returns so a reply
/// the Gateway has already cached (~10-minute TTL) is not lost when the app is killed,
/// backgrounded past the foreground service, or crashes mid-turn. MAUI-free by design so
/// the JSON round-trip and the TTL check are unit tested off-device.
/// </summary>
public sealed record InFlightVoiceTurn(string SessionId, string TurnId, DateTimeOffset SubmittedAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Compact, stable shape: this is our own persisted blob, never a wire contract.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// True while this turn is still inside the Gateway's cached-result window: the time
    /// since <see cref="SubmittedAt"/> is at or under <paramref name="ttl"/>. A turn past the
    /// TTL is a guaranteed 404 on the Gateway and must be discarded rather than polled.
    /// </summary>
    public bool IsWithinTtl(DateTimeOffset now, TimeSpan ttl) => now - SubmittedAt <= ttl;

    /// <summary>Serialize to the JSON blob stored under the single in-flight-turn key.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parse a persisted blob back into a turn, or null when the blob is absent, empty, or
    /// malformed (a corrupt blob is treated as "nothing in flight", not a crash). A blob that
    /// parses but is missing a session id or turn id is also rejected as unusable.
    /// </summary>
    public static InFlightVoiceTurn? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        InFlightVoiceTurn? turn;
        try
        {
            turn = JsonSerializer.Deserialize<InFlightVoiceTurn>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // A corrupt persisted blob means "no usable in-flight turn" - the caller clears it.
            return null;
        }
        if (turn is null
            || string.IsNullOrWhiteSpace(turn.SessionId)
            || string.IsNullOrWhiteSpace(turn.TurnId))
            return null;
        return turn;
    }
}
