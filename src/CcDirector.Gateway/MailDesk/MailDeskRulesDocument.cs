using System.Text.Json;
using System.Text.Json.Serialization;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// ONE ACCOUNT'S MAIL DESK RULES, AS ONE DOCUMENT (Mail Desk mission, phase 1, contract section 3): the fixed rules
/// (layer 1, first match wins), the allow-list (layer 2), the block list (layer 3) and the routes the model may choose
/// from (layer 4). Stored as JSON, one document per version (<see cref="MailDeskRulesStore"/>).
///
/// The document is a value: every change makes a new one (<see cref="WithAllow"/>, <see cref="WithoutAllow"/> and the
/// rest), an add appends to the end of its list, and nothing here decides whether the result is allowed - that is
/// <see cref="MailDeskRulesValidator"/>'s one job.
/// </summary>
public sealed record MailDeskRulesDocument
{
    /// <summary>The document with nothing in it: what an account has at version 0, before its first save.</summary>
    public static readonly MailDeskRulesDocument Empty = new();

    public IReadOnlyList<MailDeskRule> Rules { get; init; } = Array.Empty<MailDeskRule>();
    public IReadOnlyList<MailDeskAllowEntry> Allow { get; init; } = Array.Empty<MailDeskAllowEntry>();
    public IReadOnlyList<MailDeskBlockEntry> Block { get; init; } = Array.Empty<MailDeskBlockEntry>();
    public IReadOnlyList<MailDeskRouteEntry> Routes { get; init; } = Array.Empty<MailDeskRouteEntry>();

    /// <summary>The JSON shape: camelCase, and a field the document does not have is refused rather than dropped, so a
    /// misspelt <c>subjectContain</c> cannot quietly become a rule with no condition.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The document as stored: one JSON text, every list present even when empty.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Read a stored document. A stored document was written by <see cref="ToJson"/>, so one that cannot be
    /// read is a damaged row, and that throws.</summary>
    public static MailDeskRulesDocument Parse(string json)
    {
        if (!TryParse(json, out var document, out var refusal))
            throw new InvalidOperationException($"A stored Mail Desk rules document cannot be read: {refusal}");
        return document!;
    }

    /// <summary>Read a document from JSON, saying in a sentence why it cannot be read.</summary>
    public static bool TryParse(string? json, out MailDeskRulesDocument? document, out string? refusal)
    {
        document = null;
        refusal = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            refusal = "the rules document is empty: send a JSON object with 'rules', 'allow', 'block' and 'routes'";
            return false;
        }
        try
        {
            var read = JsonSerializer.Deserialize<MailDeskRulesDocument>(json, Json);
            if (read is null)
            {
                refusal = "the rules document is null: send a JSON object with 'rules', 'allow', 'block' and 'routes'";
                return false;
            }
            // A list left out, or sent as null, is an empty list.
            document = read with
            {
                Rules = read.Rules ?? Array.Empty<MailDeskRule>(),
                Allow = read.Allow ?? Array.Empty<MailDeskAllowEntry>(),
                Block = read.Block ?? Array.Empty<MailDeskBlockEntry>(),
                Routes = read.Routes ?? Array.Empty<MailDeskRouteEntry>(),
            };
            return true;
        }
        catch (JsonException ex)
        {
            refusal = $"the rules document cannot be read as JSON ({ex.Message}); fix the JSON and send it again";
            return false;
        }
    }

    // ---------- changes: an add appends, a remove takes out the one entry with that key ----------

    public MailDeskRulesDocument WithRule(MailDeskRule rule) => this with { Rules = Rules.Append(rule).ToList() };
    public MailDeskRulesDocument WithAllow(MailDeskAllowEntry entry) => this with { Allow = Allow.Append(entry).ToList() };
    public MailDeskRulesDocument WithBlock(MailDeskBlockEntry entry) => this with { Block = Block.Append(entry).ToList() };
    public MailDeskRulesDocument WithRoute(MailDeskRouteEntry entry) => this with { Routes = Routes.Append(entry).ToList() };

    /// <summary>The document without the rule named <paramref name="name"/> (names compare without regard to case), or
    /// null when it has no such rule.</summary>
    public MailDeskRulesDocument? WithoutRule(string name)
        => Without(Rules, r => SameName(r.Name, name), kept => this with { Rules = kept });

    /// <summary>The document without the allow-list entry for <paramref name="pattern"/>, or null when there is none.</summary>
    public MailDeskRulesDocument? WithoutAllow(string pattern)
        => Without(Allow, a => SamePattern(a.From, pattern), kept => this with { Allow = kept });

    /// <summary>The document without the block-list entry for <paramref name="pattern"/>, or null when there is none.</summary>
    public MailDeskRulesDocument? WithoutBlock(string pattern)
        => Without(Block, b => SamePattern(b.From, pattern), kept => this with { Block = kept });

    /// <summary>The document without the route <paramref name="key"/> (<c>factory/seat</c>), or null when there is none.</summary>
    public MailDeskRulesDocument? WithoutRoute(string key)
        => Without(Routes, r => string.Equals(r.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase), kept => this with { Routes = kept });

    private static MailDeskRulesDocument? Without<T>(IReadOnlyList<T> list, Func<T, bool> matches,
        Func<IReadOnlyList<T>, MailDeskRulesDocument> rebuild)
    {
        var kept = list.Where(e => !matches(e)).ToList();
        return kept.Count == list.Count ? null : rebuild(kept);
    }

    private static bool SameName(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePattern(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A fixed rule: when the subject contains any of the words, or the sender matches any pattern, route it.
/// When both are given, both must hold - that is phase 3's matching; phase 1 only holds the rule.</summary>
public sealed record MailDeskRule
{
    public string Name { get; init; } = "";
    public MailDeskRuleWhen? When { get; init; }
    public MailDeskRouteTarget? Route { get; init; }
}

/// <summary>A rule's conditions. At least one list holds something.</summary>
public sealed record MailDeskRuleWhen
{
    public IReadOnlyList<string>? SubjectContains { get; init; }
    public IReadOnlyList<string>? FromAny { get; init; }
}

/// <summary>A factory and one of its seats.</summary>
public sealed record MailDeskRouteTarget
{
    public string Factory { get; init; } = "";
    public string Seat { get; init; } = "";

    /// <summary>The route as one key: <c>factory/seat</c>.</summary>
    [JsonIgnore]
    public string Key => $"{Factory}/{Seat}";
}

/// <summary>An allow-list entry: a sender we never wrote to but want, what it is, and what to do with its mail (D6).</summary>
public sealed record MailDeskAllowEntry
{
    public string From { get; init; } = "";
    public string What { get; init; } = "";
    public MailDeskAllowDo? Do { get; init; }
}

/// <summary>
/// What an allow-list entry does: <c>"archive"</c>, <c>"keep-for-owner"</c>, or <c>{ "route": { factory, seat } }</c>.
/// Exactly one of <see cref="Action"/> and <see cref="Route"/> is set; the JSON is a string or an object accordingly.
/// </summary>
[JsonConverter(typeof(MailDeskAllowDoConverter))]
public sealed record MailDeskAllowDo
{
    public string? Action { get; init; }
    public MailDeskRouteTarget? Route { get; init; }

    public static MailDeskAllowDo Archive => new() { Action = MailDeskWords.AllowActions.Archive };
    public static MailDeskAllowDo KeepForOwner => new() { Action = MailDeskWords.AllowActions.KeepForOwner };
    public static MailDeskAllowDo RouteTo(string factory, string seat) => new() { Route = new MailDeskRouteTarget { Factory = factory, Seat = seat } };
}

/// <summary>A block-list entry: a sender whose mail goes to Junk, and why.</summary>
public sealed record MailDeskBlockEntry
{
    public string From { get; init; } = "";
    public string Why { get; init; } = "";
}

/// <summary>A route the model may choose, with one line saying what it handles.</summary>
public sealed record MailDeskRouteEntry
{
    public string Factory { get; init; } = "";
    public string Seat { get; init; } = "";
    public string Handles { get; init; } = "";

    /// <summary>The route as one key: <c>factory/seat</c>.</summary>
    [JsonIgnore]
    public string Key => $"{Factory}/{Seat}";
}

/// <summary>Reads and writes <see cref="MailDeskAllowDo"/> as a string or as <c>{ "route": { ... } }</c>.</summary>
internal sealed class MailDeskAllowDoConverter : JsonConverter<MailDeskAllowDo>
{
    private sealed record RouteForm
    {
        public MailDeskRouteTarget? Route { get; init; }
    }

    public override MailDeskAllowDo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new MailDeskAllowDo { Action = reader.GetString() };
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var form = JsonSerializer.Deserialize<RouteForm>(ref reader, options);
            return new MailDeskAllowDo { Route = form?.Route };
        }
        throw new JsonException("'do' is \"archive\", \"keep-for-owner\", or { \"route\": { \"factory\": ..., \"seat\": ... } }");
    }

    public override void Write(Utf8JsonWriter writer, MailDeskAllowDo value, JsonSerializerOptions options)
    {
        if (value.Route is not null)
            JsonSerializer.Serialize(writer, new RouteForm { Route = value.Route }, options);
        else
            writer.WriteStringValue(value.Action);
    }
}
