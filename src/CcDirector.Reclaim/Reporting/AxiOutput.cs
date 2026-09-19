using System.Text;

namespace CcDirector.Reclaim.Reporting;

/// <summary>
/// Renders the list shape the AXI standard sets out, the one every DevThrottle command line tool
/// prints: a header line naming the list, how many rows it has and its fields, then one indented row
/// per record.
///
///     largest-folders[2]{path,bytes,size,files}:
///       D:\repos,1000,1000 bytes,1
///       "D:\one, two",500,500 bytes,2
///
/// This is the same format as the shared Python helper in tools/cc_shared/axi_output.py, written out
/// again here because this is the first tool in the family written in C# to print it. The rules are
/// taken from that module and are repeated in <see cref="Value"/> so they can be read without leaving
/// this file.
///
/// It lives in the engine rather than in the command line tool because the engine produces the
/// finished lines a screen or a tool prints without deciding anything of its own - critical rule 7 in
/// CLAUDE.md. Rendering a row is part of producing those lines.
/// </summary>
public static class AxiOutput
{
    private const string RowIndent = "  ";

    /// <summary>
    /// Render one value for a list row.
    ///
    /// A value is written plainly when it is non-empty, has no leading or trailing whitespace, is
    /// pure printable ASCII, and holds no comma and no double quote. Anything else is wrapped in
    /// double quotes, with a backslash, a double quote, a newline, a carriage return and a tab
    /// written as two-character escapes and every other character written as a numbered escape. So
    /// every row stays on one line and stays pure ASCII, and a reader can take the value back out
    /// exactly as it went in.
    /// </summary>
    /// <param name="value">The value to render. Null renders as nothing at all.</param>
    public static string Value(string? value)
    {
        if (value is null) return string.Empty;
        return IsPlain(value) ? value : "\"" + Escape(value) + "\"";
    }

    /// <summary>Render a whole number for a list row.</summary>
    /// <param name="value">The number to render.</param>
    public static string Value(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Render a list block: the header line, then one indented row per record. An empty list renders
    /// as its header alone, which is what makes an empty result definitive instead of blank.
    /// </summary>
    /// <param name="name">The list name, letters, digits, underscore, hyphen and full stop only.</param>
    /// <param name="fields">The field names, in the order the rows write them.</param>
    /// <param name="rows">The rows, each already rendered value by value through <see cref="Value(string?)"/>.</param>
    public static IReadOnlyList<string> List(
        string name,
        IReadOnlyList<string> fields,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(rows);
        CheckName("list name", name);
        if (fields.Count == 0)
            throw new ArgumentException("A list needs at least one field.", nameof(fields));
        foreach (var field in fields) CheckName("field name", field);

        var lines = new List<string>(rows.Count + 1)
        {
            $"{name}[{rows.Count}]{{{string.Join(",", fields)}}}:"
        };

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Count != fields.Count)
            {
                throw new ArgumentException(
                    $"Row {index} has {row.Count} values and the list has {fields.Count} fields.",
                    nameof(rows));
            }

            lines.Add(RowIndent + string.Join(",", row));
        }

        return lines;
    }

    /// <summary>
    /// Render the help block: the concrete next commands, one per indented line, with runtime values
    /// written as placeholders rather than guessed.
    /// </summary>
    /// <param name="commands">The commands to offer.</param>
    public static IReadOnlyList<string> Help(IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("Help needs at least one command; leave the block out instead.", nameof(commands));

        var lines = new List<string>(commands.Count + 1) { $"help[{commands.Count}]:" };
        foreach (var command in commands)
        {
            if (!IsPlainHelpLine(command))
            {
                throw new ArgumentException(
                    $"A help command must be one trimmed line of printable ASCII: {command}",
                    nameof(commands));
            }

            lines.Add(RowIndent + command);
        }

        return lines;
    }

    private static bool IsPlain(string text)
    {
        if (text.Length == 0) return false;
        if (text != text.Trim()) return false;
        foreach (var character in text)
        {
            if (character is ',' or '"') return false;
            if (character < 0x20 || character > 0x7E) return false;
        }

        return true;
    }

    private static bool IsPlainHelpLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text != text.Trim()) return false;
        foreach (var character in text)
        {
            if (character < 0x20 || character > 0x7E) return false;
        }

        return true;
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (rune.Value >= 0x20 && rune.Value <= 0x7E)
                        builder.Append((char)rune.Value);
                    else if (rune.Value <= 0xFFFF)
                        builder.Append("\\u").Append(rune.Value.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else
                        builder.Append("\\U").Append(rune.Value.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        return builder.ToString();
    }

    private static void CheckName(string kind, string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException($"A {kind} cannot be blank.", nameof(name));

        foreach (var character in name)
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '-' or '.';
            if (!allowed)
            {
                throw new ArgumentException(
                    $"The {kind} {name} is not allowed; use only letters, digits, underscore, hyphen and full stop.",
                    nameof(name));
            }
        }
    }
}
