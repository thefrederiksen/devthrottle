using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// WRITES A DEFINITION FILE'S YAML, THE SAME BYTES EVERY TIME (Factory Definition, phase 3). Used by the export of a
/// factory that has no definition yet (mission decision A8), which composes <c>factory.yaml</c> and one file per seat
/// from what the Gateway holds today. A factory that HAS a definition is exported as its stored text, untouched; this
/// writer is never applied to a person's own file.
///
/// Every value is written so that <see cref="DefinitionDocument.ParseYaml"/> reads back exactly the text it was given,
/// as text: a value that YAML's core schema would read as a number, true or false, or nothing (<c>123</c>,
/// <c>true</c>, <c>null</c>), or that holds anything YAML gives a meaning to, is double-quoted. A value of several
/// lines is written as a literal block when that keeps it exactly, and double-quoted with escapes when it would not
/// (a carriage return, a line of spaces, a first line that begins with a space). Lines end in <c>\n</c>, keys come in
/// the order the caller writes them, and nothing in the text depends on a clock, a culture or a machine.
/// </summary>
public sealed partial class DefinitionYamlWriter
{
    private const int IndentStep = 2;

    private readonly StringBuilder _text = new();

    /// <summary>
    /// Text safe to write plainly: a letter or digit first, then letters, digits, single spaces and <c>_ . / ( ) , -
    /// + @</c>, ending in neither a space nor a colon. Nothing here can begin a YAML indicator, form <c>: </c> or
    /// <c> #</c>, or end a mapping key.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9_./(),+@-]| (?! ))*(?<! )\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSafe();

    /// <summary>What YAML 1.2's core schema reads as something other than text when written plainly.</summary>
    [GeneratedRegex(@"\A(?:~|null|Null|NULL|true|True|TRUE|false|False|FALSE|[-+]?(?:[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)|[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))\z", RegexOptions.CultureInvariant)]
    private static partial Regex CoreSchemaNonText();

    /// <summary><c>key: value</c> at <paramref name="depth"/>, the value text.</summary>
    public DefinitionYamlWriter Scalar(int depth, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Key(depth, key);
        Value(depth + 1, value);
        return this;
    }

    /// <summary><c>key: true</c> or <c>key: false</c> at <paramref name="depth"/>.</summary>
    public DefinitionYamlWriter Boolean(int depth, string key, bool value)
    {
        Key(depth, key);
        _text.Append(value ? " true\n" : " false\n");
        return this;
    }

    /// <summary><c>key:</c> alone on its line, opening a mapping or a list written below it at
    /// <paramref name="depth"/> + 1.</summary>
    public DefinitionYamlWriter Open(int depth, string key)
    {
        Key(depth, key);
        _text.Append('\n');
        return this;
    }

    /// <summary>A list of texts under <paramref name="key"/>, one <c>- item</c> per line. Nothing is written for an
    /// empty list: an absent key and an empty list mean the same to a definition.</summary>
    public DefinitionYamlWriter List(int depth, string key, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return this;
        Open(depth, key);
        foreach (var item in items)
        {
            Indent(depth + 1);
            _text.Append('-');
            Value(depth + 2, item);
        }
        return this;
    }

    /// <summary>
    /// Begins one mapping item of a list (<c>- </c> at <paramref name="depth"/>); its keys are then written at
    /// <paramref name="depth"/> + 1, the first of them by <paramref name="firstKey"/> on this same line.
    /// </summary>
    public DefinitionYamlWriter Item(int depth, string firstKey, string firstValue)
    {
        ArgumentNullException.ThrowIfNull(firstKey);
        ArgumentNullException.ThrowIfNull(firstValue);
        Indent(depth);
        _text.Append("- ").Append(firstKey).Append(':');
        Value(depth + 2, firstValue);
        return this;
    }

    /// <summary>The file's text: every line ends in <c>\n</c>.</summary>
    public override string ToString() => _text.ToString();

    private void Key(int depth, string key)
    {
        Indent(depth);
        _text.Append(key).Append(':');
    }

    private void Indent(int depth) => _text.Append(' ', depth * IndentStep);

    /// <summary>The value after a key or a dash, starting with the space that follows it. A block's lines are
    /// indented to <paramref name="contentDepth"/>, one step deeper than the key or dash that holds it.</summary>
    private void Value(int contentDepth, string value)
    {
        if (IsPlainSafe(value))
        {
            _text.Append(' ').Append(value).Append('\n');
            return;
        }
        if (BlockChomping(value) is { } chomp)
        {
            _text.Append(" |").Append(chomp).Append('\n');
            var body = chomp switch
            {
                "" => value[..^1],
                "+" => value[..^1],
                _ => value,
            };
            foreach (var line in body.Split('\n'))
            {
                if (line.Length > 0) Indent(contentDepth);
                _text.Append(line).Append('\n');
            }
            return;
        }
        _text.Append(' ').Append(Quoted(value)).Append('\n');
    }

    /// <summary>Whether <paramref name="value"/> reads back as itself, as text, when written plainly.</summary>
    public static bool IsPlainSafe(string value)
        => value.Length > 0 && PlainSafe().IsMatch(value) && !CoreSchemaNonText().IsMatch(value);

    /// <summary>
    /// The chomping indicator that writes <paramref name="value"/> as a literal block exactly - "" (keep one final line
    /// break), "-" (none) or "+" (keep them all) - or null when a block cannot hold it exactly: no line break at all, a
    /// carriage return or other control character, a byte-order mark or a Unicode line separator, a line that holds only spaces or tabs, a first line that is empty
    /// or begins with a space or a tab, or nothing but line breaks.
    /// </summary>
    public static string? BlockChomping(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.TrimEnd('\n');
        if (trimmed.Length == 0 || !value.Contains('\n')) return null;
        if (value.Any(c => c != '\n' && IsUnsafeRaw(c))) return null;
        var lines = trimmed.Split('\n');
        if (lines[0].Length == 0 || lines[0][0] is ' ') return null;
        if (lines.Any(l => l.Length > 0 && l.All(c => c is ' '))) return null;
        var breaks = value.Length - trimmed.Length;
        return breaks switch
        {
            0 => "-",
            1 => "",
            _ => "+",
        };
    }

    /// <summary>
    /// A character that cannot be written as itself: a control character, the byte-order mark, and the two Unicode line
    /// separators U+2028 and U+2029, which the definition's parser (YamlDotNet, following YAML 1.1) reads as line breaks,
    /// so a block holding one fails to parse or comes back with a line break where the separator was. Each is never put
    /// in a block and is escaped in a quoted value (review of 4007, finding 1).
    /// </summary>
    private static bool IsUnsafeRaw(char c) => char.IsControl(c) || c is '\uFEFF' or '\u2028' or '\u2029';

    /// <summary>A double-quoted YAML scalar holding exactly <paramref name="value"/>.</summary>
    public static string Quoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var q = new StringBuilder(value.Length + 2);
        q.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': q.Append("\\\""); break;
                case '\\': q.Append("\\\\"); break;
                case '\n': q.Append("\\n"); break;
                case '\r': q.Append("\\r"); break;
                case '\t': q.Append("\\t"); break;
                default:
                    if (IsUnsafeRaw(c))
                        q.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        q.Append(c);
                    break;
            }
        }
        q.Append('"');
        return q.ToString();
    }
}
