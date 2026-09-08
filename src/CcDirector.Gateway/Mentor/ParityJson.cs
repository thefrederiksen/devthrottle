using System.Collections;
using System.Globalization;
using System.Text;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The serializer that matches Python's <c>json.dumps</c> byte for byte, because the proof of the port is a
/// structural diff against files the reference wrote with it.
///
/// <see cref="Pretty"/> is <c>json.dumps(value, sort_keys=True, indent=2, ensure_ascii=True)</c>: keys sorted
/// ordinally, two-space indent, <c>": "</c> and <c>",\n"</c>, non-ASCII escaped as <c>\uXXXX</c> (lowercase
/// hex, a surrogate pair as two escapes), integers as integers, floats in Python's shortest round-trip repr
/// (<c>12.0</c>, <c>1e-05</c>), <c>true</c>/<c>false</c>/<c>null</c>. <see cref="Compact"/> is the default
/// <c>json.dumps(value, ensure_ascii=True)</c> the tool log writes: insertion order, <c>", "</c> and
/// <c>": "</c>. Neither adds a trailing newline; the writer that wants one adds it.
///
/// The value model is deliberately plain so the port always knows which numbers are floats: null, bool,
/// string, int, long, double, a dictionary of string to object (insertion ordered), and any enumerable of
/// objects. A C# <c>int</c> or <c>long</c> is a Python int; a <c>double</c> is a Python float. Nothing else
/// is accepted, so a value the port did not decide the type of cannot reach a file.
/// </summary>
public static class ParityJson
{
    public static string Pretty(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, sorted: true, indent: 2, level: 0);
        return builder.ToString();
    }

    public static string Compact(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value, sorted: false, indent: null, level: 0);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value, bool sorted, int? indent, int level)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case bool b:
                builder.Append(b ? "true" : "false");
                return;
            case string s:
                WriteString(builder, s);
                return;
            case int i:
                builder.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            case long l:
                builder.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new MentorDataException("A NaN or infinite float reached the parity serializer; the reference never writes one.");
                builder.Append(PythonFloat.Repr(d));
                return;
            case IDictionary<string, object?> dictionary:
                WriteObject(builder, dictionary, sorted, indent, level);
                return;
            case IReadOnlyDictionary<string, object?> readOnly:
                WriteObject(builder, readOnly, sorted, indent, level);
                return;
            case IEnumerable enumerable:
                WriteArray(builder, enumerable.Cast<object?>().ToList(), sorted, indent, level);
                return;
            default:
                throw new MentorDataException("The parity serializer does not know the type " + value.GetType().FullName
                    + "; answers are built from null, bool, string, int, long, double, dictionaries and lists only.");
        }
    }

    private static void WriteObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> pairs, bool sorted, int? indent, int level)
    {
        var items = pairs.ToList();
        if (sorted) items = items.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (items.Count == 0)
        {
            builder.Append("{}");
            return;
        }
        builder.Append('{');
        var first = true;
        foreach (var (key, item) in items)
        {
            if (!first) builder.Append(',');
            first = false;
            Newline(builder, indent, level + 1);
            WriteString(builder, key);
            builder.Append(": ");
            Write(builder, item, sorted, indent, level + 1);
        }
        Newline(builder, indent, level);
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, IReadOnlyList<object?> items, bool sorted, int? indent, int level)
    {
        if (items.Count == 0)
        {
            builder.Append("[]");
            return;
        }
        builder.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first) builder.Append(',');
            first = false;
            Newline(builder, indent, level + 1);
            Write(builder, item, sorted, indent, level + 1);
        }
        Newline(builder, indent, level);
        builder.Append(']');
    }

    private static void Newline(StringBuilder builder, int? indent, int level)
    {
        if (indent is null) { if (builder[^1] == ',') builder.Append(' '); return; }
        builder.Append('\n').Append(' ', indent.Value * level);
    }

    /// <summary>Python's <c>ensure_ascii</c> string: the short escapes for the control characters that have
    /// one, <c>\u00XX</c> for the rest, and <c>\uXXXX</c> for every character outside 0x20 to 0x7e.</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
