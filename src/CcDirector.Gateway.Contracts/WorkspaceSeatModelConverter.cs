using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Reads a seat's model when the document writes it as a STRING and when it writes it as an OBJECT.
///
/// ONE FIELD WITH TWO TYPES, which is a shape worth naming because it is not the one this schema spent
/// its life removing. Everywhere else the defect was one VALUE carrying two facts - "restored" meaning a
/// restart and a return - and the fix was to separate the facts. Here the hand-written restart index
/// writes <c>"model": "claude-opus-5"</c> when the agent had reported one and the whole folded display
/// object when it had not, so the type itself depends on the state, and a reader written against either
/// shape is silently wrong against the other. Without this converter the object form does not merely
/// lose something: it throws, because a JSON object cannot be read into a string.
///
/// WHAT HAPPENS TO EACH SHAPE:
///
///  - a STRING is the recorded model id, and is kept as <see cref="WorkspaceSeat.Model"/>;
///  - an OBJECT is the folded <see cref="ModelDisplay"/>, and its <c>modelId</c> is kept in the same
///    place. Its other four fields - kind, text, tooltip, isAbsent - are NOT stored from here, and that
///    is not a loss of fact: <see cref="ModelDisplay"/> is a FOLD, computed by the Gateway from the
///    recorded model and the driver's capabilities, and a workspace carries the folded verdict in its
///    own <see cref="WorkspaceSeat.ModelDisplay"/> field when a capture stamps one. A rendering that can
///    be recomputed is not a record that can be lost.
///
/// Written back as a STRING always, so a document that has been through this build has one shape.
/// </summary>
public sealed class WorkspaceSeatModelConverter : JsonConverter<string?>
{
    /// <summary>Read either shape.</summary>
    /// <param name="reader">The JSON reader.</param>
    /// <param name="typeToConvert">The target type.</param>
    /// <param name="options">Serializer options.</param>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "modelId", StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }

            // An object with no modelId is the "no model recorded" case, and null is exactly what it says.
            return null;
        }

        throw new JsonException(
            $"A seat's model must be the recorded model id as a string, or the folded model object; " +
            $"this document has a {reader.TokenType}.");
    }

    /// <summary>Write the model id, or nothing.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The model id.</param>
    /// <param name="options">Serializer options.</param>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
