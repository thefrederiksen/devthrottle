using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A SEAT'S BRIEF, READ FROM AND WRITTEN INTO ITS SEAT FILE (Factory Definition, phase 4, design section 9). For a
/// defined factory a seat's brief is the <c>brief:</c> value of <c>seats/&lt;seat&gt;.yaml</c>, so reading a brief is
/// reading that one value and saving a brief is replacing it.
///
/// THE SAVE IS A TEXT EDIT, NEVER A RE-SERIALISATION. Only the bytes of the <c>brief:</c> value change; every comment,
/// every other key, its order, its quoting and its blank lines stay exactly as the person wrote them. Writing the file
/// back from a parsed tree would quietly drop the comments and re-quote every value, and the owner would find a
/// file they did not write. The new value is always written as a literal block (<c>|</c>), the one YAML form that
/// holds any text exactly as it is, line by line.
///
/// HOW THE VALUE IS FOUND. The YAML parser reports where every value starts and ends. A block value (<c>|</c> or
/// <c>&gt;</c>) ends where the next line with less indentation starts, after any blank lines that trail it; those
/// trailing blank lines are kept as they are. A plain or quoted value ends right after its text, and whatever follows
/// it on its last line (a comment) is kept on the line of the new value's <c>|</c>.
///
/// WHAT IS WRITTEN IS PROVEN BEFORE IT IS RETURNED. The edited file is parsed again and its <c>brief:</c> must read
/// back as exactly the text that was asked for. A text YAML cannot hold as a literal block is refused in words,
/// never stored as something else.
///
/// LINE ENDS. YAML reads a line end inside a value as one line feed whatever the file was written with, so a text's
/// carriage returns before a line feed are not part of what is stored: <c>"a\r\nb"</c> is the brief <c>"a\nb"</c>.
/// The new lines are written with the line end the file already uses.
///
/// Pure: it reads only the text it is handed.
/// </summary>
public static class SeatBriefText
{
    /// <summary>The key the brief is held under in a seat file.</summary>
    public const string BriefKey = "brief";

    /// <summary>
    /// The brief written in a seat file's text, exactly as YAML reads it, or null when the file does not parse or
    /// holds no text <c>brief:</c> at its top level.
    /// </summary>
    public static string? Read(string path, string seatFileText)
    {
        ArgumentNullException.ThrowIfNull(seatFileText);
        var (root, error) = DefinitionDocument.ParseYaml(path, seatFileText);
        if (error is not null || root is not DefinitionMap map) return null;
        return map.Find(BriefKey)?.Value is DefinitionScalar { Kind: DefinitionScalarKind.Text } scalar ? scalar.Value : null;
    }

    /// <summary>
    /// The seat file's text with only its <c>brief:</c> value replaced by <paramref name="brief"/>, or the reason it
    /// cannot be. Exactly one of the two results is set.
    /// </summary>
    public static SeatBriefEdit Replace(string path, string seatFileText, string brief)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(seatFileText);
        ArgumentNullException.ThrowIfNull(brief);

        var wanted = brief.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (wanted.Contains('\r'))
            return SeatBriefEdit.Refused($"The brief holds a carriage return that does not end a line, and {path} cannot hold one in a brief. Remove it.");
        if (string.IsNullOrWhiteSpace(wanted))
            return SeatBriefEdit.Refused("a brief needs text: a seat told nothing has nothing to do");

        if (FindValue(path, seatFileText) is not { } found)
            return SeatBriefEdit.Refused($"{path} has no brief: at its top level that this edit can replace. Edit the definition itself.");
        if (found.Refusal is not null) return SeatBriefEdit.Refused(found.Refusal);

        var newLine = seatFileText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indent = new string(' ', found.KeyColumn + 2);

        string before, onHeaderLine, after;
        if (found.IsBlock)
        {
            // The block's own header line ("|-  # a note"): its indicator goes, anything after the indicator stays.
            var headerEnd = LineEnd(seatFileText, found.Start);
            var header = seatFileText[found.Start..headerEnd];
            var indicator = 1;
            while (indicator < header.Length && (header[indicator] is '+' or '-' || char.IsAsciiDigit(header[indicator]))) indicator++;
            onHeaderLine = header[indicator..];
            before = seatFileText[..found.Start];
            // The blank lines that trail the block are kept, so only up to the end of its last line with text goes.
            after = seatFileText[ContentEnd(seatFileText, headerEnd, found.End)..];
        }
        else
        {
            var lineEnd = LineEnd(seatFileText, found.End);
            onHeaderLine = seatFileText[found.End..lineEnd];
            before = seatFileText[..found.Start];
            after = seatFileText[SkipLineBreak(seatFileText, lineEnd)..];
        }

        var sb = new StringBuilder(seatFileText.Length + wanted.Length + 16);
        sb.Append(before).Append(Header(wanted, indent.Length)).Append(onHeaderLine).Append(newLine);
        var lines = wanted.Split('\n');
        // The text's own final line feeds are said by the header's chomping indicator; the lines before them are written.
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0) count--;
        var trailingBreaks = lines.Length - count; // how many line feeds end the text
        for (var i = 0; i < count; i++)
        {
            if (lines[i].Length > 0) sb.Append(indent).Append(lines[i]);
            sb.Append(newLine);
        }
        // Keep (|+) holds every final line feed: the first ends the last line written above, the rest are blank lines.
        for (var i = 1; i < trailingBreaks; i++) sb.Append(newLine);
        sb.Append(after);
        var edited = sb.ToString();

        // PROVEN, NOT ASSUMED: the edited file must parse and read back exactly the brief that was asked for.
        var (_, parseError) = DefinitionDocument.ParseYaml(path, edited);
        if (parseError is not null)
            return SeatBriefEdit.Refused($"The brief cannot be written into {path}: the file would not parse ({parseError.Message}).");
        if (!string.Equals(Read(path, edited), wanted, StringComparison.Ordinal))
            return SeatBriefEdit.Refused($"The brief cannot be written into {path} as a literal block exactly as it is " +
                                         "(its first or last lines are blank in a way the block cannot hold beside the lines around it). " +
                                         "Remove the blank lines at its start or end.");
        return SeatBriefEdit.Edited(edited);
    }

    /// <summary>
    /// The literal block's header: <c>|</c>, with an indentation indicator when the text's first line with text starts
    /// with a space or a blank line leads it (YAML could not tell the indentation otherwise), and the chomping
    /// indicator that keeps the text's own final line feeds: none (one), <c>-</c> (none) or <c>+</c> (more than one).
    /// </summary>
    private static string Header(string text, int indent)
    {
        var sb = new StringBuilder("|");
        var firstWithText = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        if (firstWithText.StartsWith(' ') || text.StartsWith('\n') || text.Split('\n')[0].Trim().Length == 0)
            sb.Append(indent);
        if (!text.EndsWith('\n')) sb.Append('-');
        else if (text.EndsWith("\n\n", StringComparison.Ordinal)) sb.Append('+');
        return sb.ToString();
    }

    /// <summary>Where the brief's value is, by character index, and the column of its key.</summary>
    private sealed record Found(int Start, int End, bool IsBlock, int KeyColumn, string? Refusal);

    /// <summary>The top-level <c>brief:</c> value's position, a refusal when it is not a single text value, or null
    /// when the file has no such key or does not parse.</summary>
    private static Found? FindValue(string path, string text)
    {
        if (DefinitionDocument.BeginsWithByteOrderMark(path, text) is not null) return null;
        try
        {
            var parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (!parser.TryConsume<DocumentStart>(out _)) return null;
            if (!parser.TryConsume<MappingStart>(out _)) return null;
            while (!parser.Accept<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                if (!string.Equals(key.Value, BriefKey, StringComparison.Ordinal))
                {
                    Skip(parser);
                    continue;
                }
                if (parser.Current is not Scalar value)
                    return new Found(0, 0, false, 0, $"The brief in {path} is not one text value, so it cannot be replaced as one. Edit the definition itself.");
                var keyStart = (int)key.Start.Index;
                var column = keyStart - (text.LastIndexOf('\n', Math.Max(0, keyStart - 1)) + 1);
                if (keyStart == 0) column = 0;
                return new Found((int)value.Start.Index, (int)value.End.Index,
                    value.Style is ScalarStyle.Literal or ScalarStyle.Folded, column, null);
            }
            return null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    /// <summary>Move the parser past one value, however deep it nests.</summary>
    private static void Skip(IParser parser)
    {
        var depth = 0;
        do
        {
            if (parser.Current is MappingStart or SequenceStart) depth++;
            else if (parser.Current is MappingEnd or SequenceEnd) depth--;
            parser.MoveNext();
        } while (depth > 0);
    }

    /// <summary>The index of the line end (or the end of the text) at or after <paramref name="from"/>, before any
    /// carriage return.</summary>
    private static int LineEnd(string text, int from)
    {
        var n = text.IndexOf('\n', from);
        if (n < 0) return text.Length;
        return n > from && text[n - 1] == '\r' ? n - 1 : n;
    }

    /// <summary>The index just after the line break at <paramref name="at"/>, or <paramref name="at"/> at the end.</summary>
    private static int SkipLineBreak(string text, int at)
    {
        if (at < text.Length && text[at] == '\r') at++;
        if (at < text.Length && text[at] == '\n') at++;
        return at;
    }

    /// <summary>
    /// The end of a block value's last line with text, after its line break: the blank lines between there and
    /// <paramref name="blockEnd"/> trail the block and are kept. A block with no line of text ends after its header.
    /// </summary>
    private static int ContentEnd(string text, int headerLineEnd, int blockEnd)
    {
        var end = SkipLineBreak(text, headerLineEnd);
        var at = end;
        while (at < blockEnd)
        {
            var lineEnd = LineEnd(text, at);
            var next = SkipLineBreak(text, lineEnd);
            if (text[at..lineEnd].Trim().Length > 0) end = next;
            if (next == at) break;
            at = next;
        }
        return end;
    }
}

/// <summary>The result of replacing a seat file's brief: the edited file, or why it was not edited.</summary>
public sealed record SeatBriefEdit(string? Text, string? Refusal)
{
    public bool Ok => Text is not null;

    public static SeatBriefEdit Edited(string text) => new(text, null);

    public static SeatBriefEdit Refused(string refusal) => new(null, refusal);
}
