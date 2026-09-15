using System.Security.Cryptography;
using System.Text.Json;

namespace CcDirector.TurnRuleScorer;

/// <summary>
/// One saved turn-review screen, read back off disk exactly as the Python harness behind the
/// published measurement read it: every cell's text on a row joined in order, then the row's
/// trailing whitespace removed. Nothing else is interpreted.
///
/// WHAT A SAVED SCREEN DOES NOT CARRY, and it is the largest single limitation of this whole
/// exercise: the CURSOR ROW. The detector that ships splits the body at the real cursor
/// (<c>TerminalStateDetector.TryExtractBodyRows</c>). A saved screen has no cursor, so the scorer
/// has to guess where the input box began - see <see cref="ScreenBodySplit"/>. Every number this
/// tool prints is therefore a number about the rule run over a GUESSED body, not over the body
/// production feeds it.
/// </summary>
internal sealed record SavedScreen(IReadOnlyList<string> Rows)
{
    /// <summary>
    /// Read a screen file. Throws on anything malformed rather than returning an empty screen: an
    /// unreadable screen that scored as "no rows" would silently become a pair where nothing was
    /// gained, which is a verdict rather than a failure.
    /// </summary>
    internal static SavedScreen Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("ScreenCells", out var cells)
            || cells.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} has no ScreenCells array.");
        }

        var rows = new List<string>(cells.GetArrayLength());
        foreach (var row in cells.EnumerateArray())
        {
            rows.Add(TrimEnd(RowText(row, path)));
        }

        return new SavedScreen(rows);
    }

    /// <summary>
    /// A row is a list of cells, each with a <c>Text</c>. The two degenerate shapes the harness
    /// tolerated - a bare object, and anything else - are tolerated here for the same reason: the
    /// two readers have to agree on every file in the corpus, including the odd ones.
    /// </summary>
    private static string RowText(JsonElement row, string path) => row.ValueKind switch
    {
        JsonValueKind.Array => JoinCells(row),
        JsonValueKind.Object => CellText(row),
        JsonValueKind.Null => "",
        JsonValueKind.String => row.GetString() ?? "",
        _ => throw new InvalidDataException($"{path} has a screen row of an unexpected shape ({row.ValueKind})."),
    };

    private static string JoinCells(JsonElement row)
    {
        var text = new System.Text.StringBuilder();
        foreach (var cell in row.EnumerateArray())
        {
            text.Append(cell.ValueKind == JsonValueKind.Object ? CellText(cell) : cell.ToString());
        }
        return text.ToString();
    }

    private static string CellText(JsonElement cell) =>
        cell.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? ""
            : "";

    /// <summary>
    /// Python's <c>str.rstrip()</c> with no argument, which strips Unicode whitespace. .NET's
    /// <c>TrimEnd()</c> strips on <c>char.IsWhiteSpace</c>, which is the same set for everything a
    /// terminal produces. The one glyph worth naming is the non-breaking space (U+00A0), which
    /// BOTH treat as whitespace - it appears in these screens and a disagreement there would move
    /// row keys.
    /// </summary>
    private static string TrimEnd(string row) => row.TrimEnd();

    /// <summary>
    /// The lower-case hexadecimal SHA-256 of the file's BYTES, which is what the manifest pins. A
    /// hash taken over parsed rows would not catch a file that was re-serialised, and the point of
    /// the pin is that the evidence is byte-identical to the evidence the corpus was frozen from.
    /// </summary>
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
