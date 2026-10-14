using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>The four kinds of definition file that have a schema.</summary>
public enum DefinitionFileKind
{
    Factory,
    Seat,
    Grants,
    Rules,
}

/// <summary>
/// THE DEFINITION'S SCHEMAS, AND THE ONE VALIDATOR THAT ENFORCES THEM (Factory Definition, phase 1).
///
/// The schemas are JSON Schema documents (draft 2020-12), embedded beside this file, one per file type. They are the
/// contract: phase 3 serves them so an editor or an agent can check a folder without saving. So a schema must never
/// SAY more than this validator DOES - a keyword the validator silently skipped would be a promise nobody keeps. The
/// loader therefore refuses any keyword outside <see cref="ImplementedKeywords"/>, any <c>type</c> it does not
/// implement, and any object schema without <c>"additionalProperties": false</c>: a schema that would need more
/// throws at load, before it can be served.
///
/// Messages are built from the schema itself: an object's <c>title</c> names it ("The key 'prompt' is required in a
/// start."), and a property's <c>description</c> says what a pattern means, so the sentence a person reads and the
/// schema an editor reads cannot disagree.
/// </summary>
public sealed class DefinitionSchema
{
    /// <summary>Every keyword this validator understands. Anything else in a schema is refused at load.</summary>
    public static readonly IReadOnlySet<string> ImplementedKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description",
        "type", "properties", "required", "additionalProperties",
        "enum", "pattern", "minLength", "maxLength", "items", "minItems", "uniqueItems",
    };

    /// <summary>The <c>type</c> values this validator understands.</summary>
    public static readonly IReadOnlySet<string> ImplementedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "object", "array", "string", "boolean",
    };

    // Keywords that describe and never constrain, allowed on any schema.
    private static readonly HashSet<string> AnnotationKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "type",
    };

    // The constraining keywords each type enforces. Together with the annotations these are ImplementedKeywords.
    private static readonly IReadOnlyDictionary<string, HashSet<string>> KeywordsByType = new Dictionary<string, HashSet<string>>
    {
        ["object"] = new(StringComparer.Ordinal) { "properties", "required", "additionalProperties" },
        ["array"] = new(StringComparer.Ordinal) { "items", "minItems", "uniqueItems" },
        ["string"] = new(StringComparer.Ordinal) { "enum", "pattern", "minLength", "maxLength" },
        ["boolean"] = new(StringComparer.Ordinal),
    };

    /// <summary>The draft every schema declares.</summary>
    public const string Draft = "https://json-schema.org/draft/2020-12/schema";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Lazy<IReadOnlyDictionary<DefinitionFileKind, DefinitionSchema>> Embedded = new(LoadEmbedded);

    private readonly JsonElement _root;

    private DefinitionSchema(string name, string text, JsonElement root)
    {
        Name = name;
        Text = text;
        _root = root;
    }

    /// <summary>The schema's file name, such as <c>seat.schema.json</c>.</summary>
    public string Name { get; }

    /// <summary>The schema's text exactly as embedded - what phase 3 serves.</summary>
    public string Text { get; }

    /// <summary>The schema's root, for tests and for the people's document test.</summary>
    public JsonElement Root => _root;

    /// <summary>The embedded schema for a file kind.</summary>
    public static DefinitionSchema For(DefinitionFileKind kind) => Embedded.Value[kind];

    /// <summary>The resource file name of each kind's schema.</summary>
    public static string ResourceFileName(DefinitionFileKind kind) => kind switch
    {
        DefinitionFileKind.Factory => "factory.schema.json",
        DefinitionFileKind.Seat => "seat.schema.json",
        DefinitionFileKind.Grants => "grants.schema.json",
        DefinitionFileKind.Rules => "rules.schema.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Load a schema from its text, refusing anything the validator does not enforce. Throws
    /// <see cref="InvalidOperationException"/> naming the keyword and where it is.
    /// </summary>
    public static DefinitionSchema Load(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Schema {name}: the root must be an object.");
        if (!root.TryGetProperty("$schema", out var draft) || draft.GetString() != Draft)
            throw new InvalidOperationException($"Schema {name}: '$schema' must be '{Draft}'.");
        Guard(name, root, "#");
        return new DefinitionSchema(name, text, root);
    }

    private static IReadOnlyDictionary<DefinitionFileKind, DefinitionSchema> LoadEmbedded()
    {
        var assembly = typeof(DefinitionSchema).Assembly;
        var result = new Dictionary<DefinitionFileKind, DefinitionSchema>();
        foreach (var kind in Enum.GetValues<DefinitionFileKind>())
        {
            var file = ResourceFileName(kind);
            var resource = $"CcDirector.Gateway.Factory.Definition.Schemas.{file}";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The definition schema resource '{resource}' is not embedded in {assembly.GetName().Name}.");
            using var reader = new StreamReader(stream);
            result[kind] = Load(file, reader.ReadToEnd());
        }
        return result;
    }

    // Walk a schema and refuse what is not enforced.
    private static void Guard(string name, JsonElement schema, string where)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Schema {name} at {where}: a schema must be an object.");
        foreach (var p in schema.EnumerateObject())
        {
            if (!ImplementedKeywords.Contains(p.Name))
                throw new InvalidOperationException(
                    $"Schema {name} at {where}: the keyword '{p.Name}' is not implemented by the definition validator, so the schema cannot use it.");
        }
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Schema {name} at {where}: every schema must name one 'type'.");
        var t = type.GetString()!;
        if (!ImplementedTypes.Contains(t))
            throw new InvalidOperationException($"Schema {name} at {where}: the type '{t}' is not implemented by the definition validator.");

        // A keyword the validator implements still does nothing on a type that does not read it ("maxLength" on an
        // array), so each keyword is allowed only on the type that enforces it.
        foreach (var p in schema.EnumerateObject())
        {
            if (AnnotationKeywords.Contains(p.Name)) continue;
            if (!KeywordsByType[t].Contains(p.Name))
                throw new InvalidOperationException(
                    $"Schema {name} at {where}: the keyword '{p.Name}' does nothing on type '{t}', so the schema cannot use it there.");
        }

        if (t == "object")
        {
            if (!schema.TryGetProperty("additionalProperties", out var ap) || ap.ValueKind != JsonValueKind.False)
                throw new InvalidOperationException($"Schema {name} at {where}: every object must say \"additionalProperties\": false.");
            if (!schema.TryGetProperty("title", out _))
                throw new InvalidOperationException($"Schema {name} at {where}: every object must have a title; its messages name it.");
            if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Schema {name} at {where}: every object must list its properties.");
            foreach (var p in props.EnumerateObject())
                Guard(name, p.Value, $"{where}/properties/{p.Name}");
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var r in required.EnumerateArray())
                {
                    if (!props.TryGetProperty(r.GetString()!, out _))
                        throw new InvalidOperationException($"Schema {name} at {where}: required key '{r.GetString()}' is not among its properties.");
                }
            }
        }
        if (t == "array")
        {
            if (!schema.TryGetProperty("items", out var items))
                throw new InvalidOperationException($"Schema {name} at {where}: every array must say what its items are.");
            Guard(name, items, $"{where}/items");
        }
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (!schema.TryGetProperty("description", out _))
                throw new InvalidOperationException($"Schema {name} at {where}: a pattern needs a description; its message quotes it.");
            _ = new Regex(pattern.GetString()!, RegexOptions.ECMAScript, RegexTimeout);
        }
    }

    /// <summary>
    /// Check a parsed file against this schema. Each problem is reported on the line of the key it is about.
    /// </summary>
    public IReadOnlyList<DefinitionParseError> Validate(DefinitionNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var errors = new List<DefinitionParseError>();
        Check(_root, root, "the file", root.Line, errors);
        return errors;
    }

    // `label` names the value in a sentence ("'agent'", "item 2 of 'grants'", "the file"); `line` is where a finding
    // about the value as a whole goes - its key's line, or its own line for a list item.
    private static void Check(JsonElement schema, DefinitionNode node, string label, int line, List<DefinitionParseError> errors)
    {
        var type = schema.GetProperty("type").GetString()!;
        switch (type)
        {
            case "object":
                if (node is not DefinitionMap map)
                {
                    errors.Add(new DefinitionParseError(line, $"{Capital(label)} must be a mapping of keys, not {What(node)}."));
                    return;
                }
                CheckObject(schema, map, line, errors);
                return;

            case "array":
                if (node is not DefinitionList list)
                {
                    errors.Add(new DefinitionParseError(line, $"{Capital(label)} must be a list, not {What(node)}."));
                    return;
                }
                CheckArray(schema, list, label, line, errors);
                return;

            case "boolean":
                if (node is not DefinitionScalar { Kind: DefinitionScalarKind.Boolean })
                    errors.Add(new DefinitionParseError(line, $"{Capital(label)} must be true or false, not {What(node)}."));
                return;

            case "string":
                if (node is not DefinitionScalar { Kind: DefinitionScalarKind.Text } text)
                {
                    errors.Add(new DefinitionParseError(line, $"{Capital(label)} must be text, not {What(node)}."));
                    return;
                }
                CheckString(schema, text.Value, label, line, errors);
                return;

            default:
                throw new InvalidOperationException($"Type '{type}' passed the schema guard but is not validated.");
        }
    }

    private static void CheckObject(JsonElement schema, DefinitionMap map, int line, List<DefinitionParseError> errors)
    {
        var title = schema.GetProperty("title").GetString()!;
        var props = schema.GetProperty("properties");
        var known = props.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var entry in map.Entries)
        {
            if (!props.TryGetProperty(entry.Key, out var propSchema))
            {
                errors.Add(new DefinitionParseError(entry.KeyLine,
                    $"'{entry.Key}' is not a key of {title}. The keys are: {string.Join(", ", known)}."));
                continue;
            }
            Check(propSchema, entry.Value, $"'{entry.Key}'", entry.KeyLine, errors);
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var r in required.EnumerateArray())
            {
                var key = r.GetString()!;
                if (map.Find(key) is null)
                    errors.Add(new DefinitionParseError(line, $"The key '{key}' is required in {title}."));
            }
        }
    }

    private static void CheckArray(JsonElement schema, DefinitionList list, string label, int line, List<DefinitionParseError> errors)
    {
        if (schema.TryGetProperty("minItems", out var minItems) && list.Items.Count < minItems.GetInt32())
        {
            var n = minItems.GetInt32();
            errors.Add(new DefinitionParseError(line, $"{Capital(label)} needs at least {n} {(n == 1 ? "item" : "items")}."));
        }

        var items = schema.GetProperty("items");
        for (var i = 0; i < list.Items.Count; i++)
            Check(items, list.Items[i], $"item {i + 1} of {label}", list.Items[i].Line, errors);

        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in list.Items)
            {
                var identity = Identity(item);
                if (seen.TryGetValue(identity, out var firstLine))
                {
                    var shown = item is DefinitionScalar s ? $"'{s.Value}'" : "the same item";
                    errors.Add(new DefinitionParseError(item.Line,
                        $"{Capital(label)} lists {shown} twice (first on line {firstLine}); list it once."));
                }
                else
                {
                    seen[identity] = item.Line;
                }
            }
        }
    }

    private static void CheckString(JsonElement schema, string value, string label, int line, List<DefinitionParseError> errors)
    {
        var length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("enum", out var allowed))
        {
            var values = allowed.EnumerateArray().Select(v => v.GetString()!).ToList();
            if (!values.Contains(value, StringComparer.Ordinal))
            {
                errors.Add(new DefinitionParseError(line,
                    $"{Capital(label)} is '{value}', which is not one of: {string.Join(", ", values)}."));
                return;
            }
        }
        if (schema.TryGetProperty("minLength", out var minLength) && length < minLength.GetInt32())
        {
            errors.Add(new DefinitionParseError(line, minLength.GetInt32() == 1
                ? $"{Capital(label)} must not be empty."
                : $"{Capital(label)} is {length} characters; it takes at least {minLength.GetInt32()}."));
            return;
        }
        if (schema.TryGetProperty("maxLength", out var maxLength) && length > maxLength.GetInt32())
        {
            errors.Add(new DefinitionParseError(line,
                $"{Capital(label)} is {length} characters; it takes at most {maxLength.GetInt32()}."));
            return;
        }
        if (schema.TryGetProperty("pattern", out var pattern)
            && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.ECMAScript, RegexTimeout))
        {
            var description = schema.GetProperty("description").GetString()!;
            errors.Add(new DefinitionParseError(line,
                $"{Capital(label)} is '{Shorten(value)}', which is not right. {description}"));
        }
    }

    private static string Identity(DefinitionNode node) => node switch
    {
        DefinitionScalar s => $"{s.Kind}:{s.Value}",
        DefinitionList l => "[" + string.Join(",", l.Items.Select(Identity)) + "]",
        DefinitionMap m => "{" + string.Join(",", m.Entries.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key + "=" + Identity(e.Value))) + "}",
        _ => throw new InvalidOperationException("Unknown node."),
    };

    /// <summary>How a value is described when it is the wrong type.</summary>
    private static string What(DefinitionNode node) => node switch
    {
        DefinitionMap => "a mapping",
        DefinitionList => "a list",
        DefinitionScalar { Kind: DefinitionScalarKind.Nothing } => "empty",
        DefinitionScalar { Kind: DefinitionScalarKind.Boolean } s => $"true or false ({s.Value})",
        DefinitionScalar { Kind: DefinitionScalarKind.Number } s => $"a number ({s.Value})",
        DefinitionScalar s => $"text ('{Shorten(s.Value)}')",
        _ => "something else",
    };

    private static string Shorten(string value)
    {
        var oneLine = value.ReplaceLineEndings(" ");
        return oneLine.Length <= 60 ? oneLine : oneLine[..57] + "...";
    }

    private static string Capital(string s)
        => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
}
