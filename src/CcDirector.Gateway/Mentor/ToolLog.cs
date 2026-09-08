using System.Globalization;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The port of mentor_tools/log.py ToolLog: one JSON line per tool call, appended to the run's
/// tool-log.jsonl whether the call succeeded or raised - <c>{"seq", "ts_utc", "tool", "args", "ok",
/// "summary", "ms"}</c> plus the keys of a tool's extra. Prompt TEXT is never written here; a citation
/// string, a fragment, a session id or name, a count or an error message may be. "How this was made" is
/// later rendered FROM this log, and the log check reads it to refuse a report whose citations were not
/// returned by the tools. ASCII only, in the tool log's own line form (Python's default json.dumps).
/// </summary>
public sealed class ToolLog
{
    public const string FileName = "tool-log.jsonl";

    public string Path { get; }
    public int Seq { get; private set; }

    public ToolLog(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        Seq = Entries().Count;
    }

    public static string UtcNow()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public Dictionary<string, object?> Record(string tool, IReadOnlyDictionary<string, object?> args, bool ok, string summary, double ms,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        Seq++;
        var entry = new Dictionary<string, object?>
        {
            ["seq"] = Seq,
            ["ts_utc"] = UtcNow(),
            ["tool"] = tool,
            ["args"] = new Dictionary<string, object?>(args),
            ["ok"] = ok,
            ["summary"] = summary,
            ["ms"] = (long)ms,
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (entry.ContainsKey(key))
                    throw new MentorDataException("a tool's extra log key '" + key + "' would overwrite the call's own '" + key + "'.");
                entry[key] = value;
            }
        }
        File.AppendAllText(Path, ParityJson.Compact(entry) + "\n", new UTF8Encoding(false));
        FileLog.Write($"[ToolLog] {tool} seq={Seq} ok={(ok ? "true" : "false")} ms={(long)ms}");
        return entry;
    }

    /// <summary>The entries in order, read back from the file as plain JSON values.</summary>
    public List<Dictionary<string, object?>> Entries()
    {
        if (!File.Exists(Path)) return new List<Dictionary<string, object?>>();
        var entries = new List<Dictionary<string, object?>>();
        var number = 0;
        foreach (var raw in File.ReadAllLines(Path, new UTF8Encoding(false)))
        {
            number++;
            var line = PyText.Strip(raw);
            if (line.Length == 0) continue;
            try
            {
                entries.Add((Dictionary<string, object?>)JsonValues.Parse(line)!);
            }
            catch (System.Text.Json.JsonException error)
            {
                throw new MentorDataException(Path + " line " + number + " is not JSON: " + error.Message);
            }
        }
        return entries;
    }
}

/// <summary>
/// JSON text as the plain value model <see cref="ParityJson"/> writes: objects become insertion-ordered
/// dictionaries, arrays lists, whole numbers longs, other numbers doubles. The same model on both sides of
/// a file, so a value read back serializes to the bytes it came from.
/// </summary>
public static class JsonValues
{
    public static object? Parse(string text)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return FromElement(document.RootElement);
    }

    public static object? FromElement(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var dictionary = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject()) dictionary[property.Name] = FromElement(property.Value);
                return dictionary;
            case System.Text.Json.JsonValueKind.Array:
                return element.EnumerateArray().Select(FromElement).ToList();
            case System.Text.Json.JsonValueKind.String:
                return element.GetString();
            case System.Text.Json.JsonValueKind.Number:
                // A whole number is a Python int whether or not it was written with digits only; a
                // number with a fraction or an exponent is a float. json.loads decides by the TEXT, and
                // so does this: "12.0" stays a float, "12" stays an int.
                var raw = element.GetRawText();
                if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && element.TryGetInt64(out var whole)) return whole;
                return element.GetDouble();
            case System.Text.Json.JsonValueKind.True:
                return true;
            case System.Text.Json.JsonValueKind.False:
                return false;
            case System.Text.Json.JsonValueKind.Null:
                return null;
            default:
                throw new MentorDataException("Unexpected JSON value kind " + element.ValueKind + ".");
        }
    }
}
