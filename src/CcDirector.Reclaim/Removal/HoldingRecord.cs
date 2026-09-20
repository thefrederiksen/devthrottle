using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Reclaim.Removal;

/// <summary>Whether the move an entry records was completed.</summary>
public enum HoldingState
{
    /// <summary>The item is in holding, and this record says so.</summary>
    Held,

    /// <summary>
    /// The record was written but the move has not been confirmed. A crash between writing the record
    /// and finishing the move leaves an entry in this state, and everything that reads holding treats
    /// it as incomplete: it is named in its own list, never purged, and resolved by restore.
    /// </summary>
    Moving
}

/// <summary>
/// The record one held item carries, written as record.json inside its entry folder. This is what
/// makes a removal reversible and answerable: where the item came from, what it was, which rule
/// proved it disposable, and when it becomes purgeable. A holding entry with no readable record is
/// an incomplete entry that is named and kept, never assumed - a record that cannot say what
/// happened is a refusal, not a guess.
/// </summary>
/// <param name="OriginalPath">The absolute path the item came from, in canonical form.</param>
/// <param name="Name">The item's own name.</param>
/// <param name="Bytes">The bytes measured at the moment of the move.</param>
/// <param name="LastWrittenUtc">The newest write inside it, measured at the moment of the move.</param>
/// <param name="Rule">The identifier of the rule that proved it disposable.</param>
/// <param name="MovedAtUtc">When the move happened.</param>
/// <param name="PurgeNotBeforeUtc">The moved moment plus the holding period.</param>
/// <param name="State">Whether the move was completed.</param>
public sealed record HoldingRecord
{
    /// <summary>The absolute path the item came from, in canonical form.</summary>
    [JsonPropertyName("original-path")]
    public required string OriginalPath { get; init; }

    /// <summary>The item's own name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The bytes measured at the moment of the move.</summary>
    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }

    /// <summary>The newest write inside it, measured at the moment of the move.</summary>
    [JsonPropertyName("last-written-utc")]
    public required DateTimeOffset LastWrittenUtc { get; init; }

    /// <summary>The identifier of the rule that proved it disposable.</summary>
    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    /// <summary>When the move happened.</summary>
    [JsonPropertyName("moved-at-utc")]
    public required DateTimeOffset MovedAtUtc { get; init; }

    /// <summary>The moved moment plus the holding period; before this the entry is not purgeable.</summary>
    [JsonPropertyName("purge-not-before-utc")]
    public required DateTimeOffset PurgeNotBeforeUtc { get; init; }

    /// <summary>Whether the move was completed.</summary>
    [JsonPropertyName("state")]
    [JsonConverter(typeof(HoldingStateConverter))]
    public required HoldingState State { get; init; }

    /// <summary>How record.json is written and read. One shape, in one place, for every reader.</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>The file name the record is written to, inside the entry folder.</summary>
    public const string RecordFileName = "record.json";

    /// <summary>The state as record.json writes it.</summary>
    public static string StateWords(HoldingState state) => state switch
    {
        HoldingState.Held => "held",
        HoldingState.Moving => "moving",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "There is no such holding state.")
    };

    /// <summary>The state as record.json reads it. A state it does not know is an unreadable record.</summary>
    public static bool TryReadState(string? words, out HoldingState state)
    {
        switch (words)
        {
            case "held":
                state = HoldingState.Held;
                return true;
            case "moving":
                state = HoldingState.Moving;
                return true;
            default:
                state = HoldingState.Moving;
                return false;
        }
    }

    /// <summary>
    /// Writes and reads the state as the words record.json carries - "held" and "moving" - rather
    /// than as the number the default enum writer would put there. A person reading a record in an
    /// editor, or a build of the tool reading a record an older build wrote, meets the same words.
    /// </summary>
    private sealed class HoldingStateConverter : JsonConverter<HoldingState>
    {
        public override HoldingState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !TryReadState(reader.GetString(), out var state))
                throw new JsonException($"A holding state must be the word \"held\" or \"moving\", not this.");
            return state;
        }

        public override void Write(Utf8JsonWriter writer, HoldingState value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(StateWords(value));
        }
    }
}
