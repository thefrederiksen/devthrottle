namespace CcDirector.Core.Sessions;

/// <summary>Kind of a parsed prompt segment.</summary>
public enum PromptSegmentKind
{
    Text,
    Image,
}

/// <summary>One renderable piece of a prompt: either a block of text or an image path.</summary>
public sealed class PromptSegment
{
    public PromptSegmentKind Kind { get; init; }

    /// <summary>For Text: the text block. For Image: the file path to the image.</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// Splits prompt text into renderable segments. A line that, on its own, is a path
/// to an image file is surfaced as an <see cref="PromptSegmentKind.Image"/> segment
/// so a preview can render the picture inline; everything else stays as text.
///
/// This is pure string logic (no filesystem access) so it is unit-testable. Whether
/// the image file actually exists on disk is the renderer's concern, not this parser's.
/// </summary>
public static class PromptContentParser
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    /// <summary>
    /// True when the line, by itself, looks like a path to an image file. Requires an
    /// image extension plus either a path separator or a single whitespace-free token,
    /// so prose that merely ends in ".png" (e.g. "see the diagram.png") is not matched.
    /// </summary>
    public static bool LooksLikeImagePath(string line)
    {
        var trimmed = StripQuotes(line.Trim());
        if (trimmed.Length == 0)
            return false;
        if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0)
            return false;

        var hasImageExtension = ImageExtensions.Any(ext =>
            trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        if (!hasImageExtension)
            return false;

        var hasSeparator = trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0;
        var hasWhitespace = trimmed.Any(char.IsWhiteSpace);

        return hasSeparator || !hasWhitespace;
    }

    /// <summary>Strip a single pair of surrounding double quotes, if present.</summary>
    public static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value.Substring(1, value.Length - 2);
        return value;
    }

    /// <summary>
    /// Parse text into ordered segments. Consecutive non-image lines coalesce into one
    /// text segment; each image-path line becomes its own image segment.
    /// </summary>
    public static IReadOnlyList<PromptSegment> Parse(string? text)
    {
        var segments = new List<PromptSegment>();
        if (string.IsNullOrEmpty(text))
            return segments;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var textBuffer = new List<string>();

        void FlushText()
        {
            if (textBuffer.Count == 0)
                return;

            var joined = string.Join("\n", textBuffer).Trim('\n');
            if (joined.Length > 0)
                segments.Add(new PromptSegment { Kind = PromptSegmentKind.Text, Content = joined });

            textBuffer.Clear();
        }

        foreach (var line in lines)
        {
            if (LooksLikeImagePath(line))
            {
                FlushText();
                segments.Add(new PromptSegment
                {
                    Kind = PromptSegmentKind.Image,
                    Content = StripQuotes(line.Trim()),
                });
            }
            else
            {
                textBuffer.Add(line);
            }
        }

        FlushText();
        return segments;
    }
}
