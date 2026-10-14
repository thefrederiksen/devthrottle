using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>What a scalar value is, read the way YAML 1.2's core schema reads it (and JSON reads it).</summary>
public enum DefinitionScalarKind
{
    Text,
    Number,
    Boolean,
    Nothing,
}

/// <summary>
/// One value in a parsed definition file, with the 1-based line it starts on. YAML and JSON both parse to this one
/// tree, so the schema validator and the check read every file type the same way and every finding carries a line.
/// </summary>
public abstract record DefinitionNode(int Line);

/// <summary>A text, number, true/false or empty value.</summary>
public sealed record DefinitionScalar(int Line, string Value, DefinitionScalarKind Kind) : DefinitionNode(Line);

/// <summary>A list.</summary>
public sealed record DefinitionList(int Line, IReadOnlyList<DefinitionNode> Items) : DefinitionNode(Line);

/// <summary>One key of a mapping, with the line the KEY is on (the line a finding about that key names).</summary>
public sealed record DefinitionEntry(string Key, int KeyLine, DefinitionNode Value);

/// <summary>A mapping, its keys in the order they are written. Keys are unique: a duplicate is refused on parse.</summary>
public sealed record DefinitionMap(int Line, IReadOnlyList<DefinitionEntry> Entries) : DefinitionNode(Line)
{
    /// <summary>The entry for <paramref name="key"/>, or null when the mapping does not hold it.</summary>
    public DefinitionEntry? Find(string key)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Key, key, StringComparison.Ordinal)) return e;
        return null;
    }
}

/// <summary>A problem found while parsing a file: the line it is on and one plain sentence.</summary>
public sealed record DefinitionParseError(int Line, string Message);

/// <summary>
/// Parses a definition file's text into a <see cref="DefinitionNode"/> tree. Pure; reads nothing else.
///
/// YAML is read from YamlDotNet's EVENT stream rather than its representation model, because the model refuses a
/// duplicate key with an exception that carries no usable line, and silently resolves anchors and aliases - and
/// anchors, aliases and tags are refused here outright: an alias can carry keys into a mapping where no key is
/// written, so the schema would pass something the reader never sees.
///
/// JSON is read with System.Text.Json's strict reader (no comments, no trailing commas), building the same tree, with
/// each token's line computed from its byte offset. JSON is NOT handed to the YAML parser: YAML refuses tab
/// indentation that is perfectly good JSON, so a valid rule set would be refused for its whitespace.
/// </summary>
public static partial class DefinitionDocument
{
    /// <summary>Parse YAML text. Exactly one of the two results is non-null.</summary>
    public static (DefinitionNode? Root, DefinitionParseError? Error) ParseYaml(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        YamlTreeReader? reader = null;
        try
        {
            var parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _))
                return (null, new DefinitionParseError(0, $"{path} is empty. It must hold one mapping of keys."));
            parser.Consume<DocumentStart>();
            reader = new YamlTreeReader(parser);
            var root = reader.Read();
            if (reader.Error is not null) return (null, reader.Error);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _))
            {
                var line = parser.Current is null ? 0 : (int)parser.Current.Start.Line;
                return (null, new DefinitionParseError(line, $"{path} holds more than one YAML document; it must hold exactly one."));
            }
            return (root, null);
        }
        catch (YamlException ex)
        {
            return (null, new DefinitionParseError((int)ex.Start.Line, $"{path} is not valid YAML: {YamlReason(ex)}"));
        }
        catch (InvalidOperationException ex) when (ex.TargetSite?.DeclaringType?.Namespace?.StartsWith("YamlDotNet", StringComparison.Ordinal) == true)
        {
            // YamlDotNet's scanner throws this, with no position, when the text ends inside an unclosed flow list or
            // mapping ("role: [Writer"). It is a syntax error in the file, so it is reported as one, on the last
            // line the reader reached.
            return (null, new DefinitionParseError(reader?.LastLine ?? 0,
                $"{path} is not valid YAML: it ends before everything opened is closed. Check for an unclosed [, {{ or quote."));
        }
    }

    /// <summary>Parse JSON text strictly. Exactly one of the two results is non-null.</summary>
    public static (DefinitionNode? Root, DefinitionParseError? Error) ParseJson(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var lines = new LineIndex(bytes);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (!reader.Read())
                return (null, new DefinitionParseError(0, $"{path} is empty. It must hold one JSON object."));
            DefinitionParseError? error = null;
            var root = ReadJsonValue(ref reader, lines, ref error);
            if (error is not null) return (null, error);
            if (reader.Read())
                return (null, new DefinitionParseError(lines.LineOf(reader.TokenStartIndex), $"{path} is not valid JSON: there is more after the first value."));
            return (root, null);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } l ? (int)l + 1 : 0;
            return (null, new DefinitionParseError(line, $"{path} is not valid JSON: {JsonReason(ex)}"));
        }
    }

    private static DefinitionNode ReadJsonValue(ref Utf8JsonReader reader, LineIndex lines, ref DefinitionParseError? error)
    {
        var line = lines.LineOf(reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var entries = new List<DefinitionEntry>();
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString()!;
                    var keyLine = lines.LineOf(reader.TokenStartIndex);
                    reader.Read();
                    var value = ReadJsonValue(ref reader, lines, ref error);
                    if (error is not null) return value;
                    if (seen.TryGetValue(key, out var first))
                    {
                        error = new DefinitionParseError(keyLine, DuplicateKey(key, first));
                        return value;
                    }
                    seen[key] = keyLine;
                    entries.Add(new DefinitionEntry(key, keyLine, value));
                }
                return new DefinitionMap(line, entries);
            }
            case JsonTokenType.StartArray:
            {
                var items = new List<DefinitionNode>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadJsonValue(ref reader, lines, ref error));
                    if (error is not null) break;
                }
                return new DefinitionList(line, items);
            }
            case JsonTokenType.String:
                return new DefinitionScalar(line, reader.GetString()!, DefinitionScalarKind.Text);
            case JsonTokenType.Number:
                return new DefinitionScalar(line, Encoding.UTF8.GetString(reader.ValueSpan), DefinitionScalarKind.Number);
            case JsonTokenType.True:
                return new DefinitionScalar(line, "true", DefinitionScalarKind.Boolean);
            case JsonTokenType.False:
                return new DefinitionScalar(line, "false", DefinitionScalarKind.Boolean);
            case JsonTokenType.Null:
                return new DefinitionScalar(line, "", DefinitionScalarKind.Nothing);
            default:
                throw new InvalidOperationException($"Unexpected JSON token {reader.TokenType} at line {line}.");
        }
    }

    /// <summary>The sentence for a key written twice in one mapping.</summary>
    public static string DuplicateKey(string key, int firstLine)
        => $"The key '{key}' appears twice in this mapping; it was first on line {firstLine}. Keep one.";

    // YamlDotNet prefixes its message with "(Line: 3, Col: 1, Idx: 10) - (Line: 3, Col: 2, Idx: 11): "; the line is
    // reported on its own, so only the reason is kept.
    [GeneratedRegex(@"^\(Line:[^)]*\)( - \(Line:[^)]*\))?:\s*")]
    private static partial Regex YamlLocationPrefix();

    private static string YamlReason(YamlException ex)
    {
        var reason = YamlLocationPrefix().Replace(ex.Message, "").Trim();
        return reason.EndsWith('.') ? reason : reason + ".";
    }

    private static string JsonReason(JsonException ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0) cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        var reason = (cut >= 0 ? message[..cut] : message).Trim();
        return reason.EndsWith('.') ? reason : reason + ".";
    }

    /// <summary>Maps a UTF-8 byte offset to its 1-based line.</summary>
    private sealed class LineIndex
    {
        private readonly List<long> _lineStarts = new() { 0 };

        public LineIndex(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n') _lineStarts.Add(i + 1);
        }

        public int LineOf(long offset)
        {
            var index = _lineStarts.BinarySearch(offset);
            return (index >= 0 ? index : ~index - 1) + 1;
        }
    }

    /// <summary>Builds the tree from YAML events, refusing anchors, aliases, tags, complex keys and duplicate keys.</summary>
    private sealed class YamlTreeReader
    {
        private readonly IParser _parser;

        public YamlTreeReader(IParser parser) => _parser = parser;

        /// <summary>The first problem met; once set, reading stops.</summary>
        public DefinitionParseError? Error { get; private set; }

        /// <summary>The line of the last event read, for a parser failure that carries no position of its own.</summary>
        public int LastLine { get; private set; }

        public DefinitionNode? Read()
        {
            var ev = _parser.Current ?? throw new InvalidOperationException("The YAML parser has no current event.");
            var line = (int)ev.Start.Line;
            LastLine = line;

            if (ev is AnchorAlias alias)
            {
                Error = new DefinitionParseError(line,
                    $"An alias (*{alias.Value}) is not allowed: anchors and aliases can hide keys from the check. Write the value out in full.");
                return null;
            }
            if (ev is NodeEvent node)
            {
                if (!node.Anchor.IsEmpty)
                {
                    Error = new DefinitionParseError(line,
                        $"An anchor (&{node.Anchor}) is not allowed: anchors and aliases can hide keys from the check. Write the value out in full.");
                    return null;
                }
                if (!node.Tag.IsEmpty)
                {
                    Error = new DefinitionParseError(line,
                        $"A tag ({node.Tag}) is not allowed: a tag can change what a value means. Remove it.");
                    return null;
                }
            }

            switch (ev)
            {
                case Scalar scalar:
                    _parser.MoveNext();
                    return new DefinitionScalar(line, scalar.Value, scalar.Style == ScalarStyle.Plain
                        ? PlainKind(scalar.Value)
                        : DefinitionScalarKind.Text);

                case SequenceStart:
                {
                    _parser.MoveNext();
                    var items = new List<DefinitionNode>();
                    while (_parser.Current is not SequenceEnd)
                    {
                        var item = Read();
                        if (Error is not null) return null;
                        items.Add(item!);
                    }
                    _parser.MoveNext();
                    return new DefinitionList(line, items);
                }

                case MappingStart:
                {
                    _parser.MoveNext();
                    var entries = new List<DefinitionEntry>();
                    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                    while (_parser.Current is not MappingEnd)
                    {
                        var keyEvent = _parser.Current!;
                        var keyLine = (int)keyEvent.Start.Line;
                        var key = Read();
                        if (Error is not null) return null;
                        if (key is not DefinitionScalar keyScalar || keyScalar.Kind == DefinitionScalarKind.Nothing)
                        {
                            Error = new DefinitionParseError(keyLine, "A key must be plain text, written on its own.");
                            return null;
                        }
                        var value = Read();
                        if (Error is not null) return null;
                        if (seen.TryGetValue(keyScalar.Value, out var first))
                        {
                            Error = new DefinitionParseError(keyLine, DuplicateKey(keyScalar.Value, first));
                            return null;
                        }
                        seen[keyScalar.Value] = keyLine;
                        entries.Add(new DefinitionEntry(keyScalar.Value, keyLine, value!));
                    }
                    _parser.MoveNext();
                    return new DefinitionMap(line, entries);
                }

                default:
                    throw new InvalidOperationException($"Unexpected YAML event {ev.GetType().Name} at line {line}.");
            }
        }
    }

    [GeneratedRegex(@"^(null|Null|NULL|~)?$")]
    private static partial Regex PlainNull();

    [GeneratedRegex(@"^(true|True|TRUE|false|False|FALSE)$")]
    private static partial Regex PlainBool();

    [GeneratedRegex(@"^([-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+|[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex PlainNumber();

    /// <summary>What an unquoted YAML value is, by YAML 1.2's core schema: so <c>name: 12</c> is a number, not text.</summary>
    private static DefinitionScalarKind PlainKind(string value)
    {
        if (PlainNull().IsMatch(value)) return DefinitionScalarKind.Nothing;
        if (PlainBool().IsMatch(value)) return DefinitionScalarKind.Boolean;
        if (PlainNumber().IsMatch(value)) return DefinitionScalarKind.Number;
        return DefinitionScalarKind.Text;
    }
}
