using System.Text.RegularExpressions;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Detects file paths and URLs in terminal output text.
/// Pure logic with no WPF dependencies. Designed for testability.
/// </summary>
public static class LinkDetector
{
    public enum LinkType { None, Path, Url }

    /// <summary>
    /// A detected link match with its column range in the source line.
    /// </summary>
    public readonly record struct LinkMatch(int StartCol, int EndCol, string Text, LinkType Type);

    /// <summary>
    /// A quoted span found in text, with outer (including quotes) and inner (path only) positions.
    /// </summary>
    internal readonly record struct QuotedSpan(int OuterStart, int OuterEnd, int InnerStart, int InnerEnd, string InnerText);

    // 50ms timeout to prevent catastrophic backtracking
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    // Absolute Windows paths (e.g., C:\path\to\file or C:/path/to/file)
    internal static readonly Regex AbsoluteWindowsPathRegex =
        new(@"[A-Za-z]:[/\\][^\s""'`<>|*?()\[\]]+", RegexOptions.Compiled, RegexTimeout);

    // Unix-style absolute paths (e.g., /c/path/to/file for Git Bash / WSL)
    internal static readonly Regex AbsoluteUnixPathRegex =
        new(@"/[a-z]/[^\s""'`<>|*?()\[\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // Relative paths (e.g., ./src/file.cs, ../other/file.txt, src/dir/file.cs)
    internal static readonly Regex RelativePathRegex =
        new(@"\.{0,2}[/\\][^\s""'`<>|*?:()\[\]]+|[A-Za-z_][A-Za-z0-9_\-]*[/\\][^\s""'`<>|*?:()\[\]]+",
            RegexOptions.Compiled, RegexTimeout);

    // URLs (http/https or git@)
    internal static readonly Regex UrlRegex =
        new(@"https?://[^\s""'`<>()\[\]]+|git@[^\s""'`<>()\[\]]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // Characters that look like a path start inside a quoted span
    private static readonly Regex PathLikeRegex =
        new(@"^(?:[A-Za-z]:[/\\]|/[a-z]/|\.{0,2}[/\\]|[A-Za-z_][A-Za-z0-9_\-]*[/\\])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// Find all link matches (paths and URLs) in a line of text.
    /// </summary>
    /// <param name="lineText">The terminal line text to scan.</param>
    /// <param name="repoPath">Optional repo root path for resolving relative paths.</param>
    /// <param name="pathExistsCheck">
    /// Callback that returns true if a full path exists on disk.
    /// Return false or null to skip relative path validation.
    /// </param>
    public static List<LinkMatch> FindAllLinkMatches(string? lineText, string? repoPath, Func<string, bool>? pathExistsCheck)
    {
        var matches = new List<LinkMatch>();
        if (string.IsNullOrWhiteSpace(lineText))
            return matches;

        var claimedRanges = new List<(int start, int end)>();

        // 1. Quoted paths (highest priority - handles spaces)
        var quotedSpans = ExtractQuotedSpans(lineText);
        foreach (var span in quotedSpans)
        {
            string inner = span.InnerText;
            if (PathLikeRegex.IsMatch(inner))
            {
                string path = StripTrailingPunctuation(StripLineNumber(inner));
                if (path.Length > 0)
                {
                    // For relative paths, check existence
                    if (IsRelativePath(path))
                    {
                        if (repoPath != null && pathExistsCheck != null)
                        {
                            string fullPath = System.IO.Path.Combine(repoPath, path.Replace('/', '\\'));
                            if (!pathExistsCheck(fullPath))
                                continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Underline covers inner path only (not quotes)
                    matches.Add(new LinkMatch(span.InnerStart, span.InnerEnd, path, LinkType.Path));
                    // Claim the full quoted span so unquoted regex won't double-match
                    claimedRanges.Add((span.OuterStart, span.OuterEnd));
                }
            }
        }

        // 2. URLs
        foreach (Match m in UrlRegex.Matches(lineText))
        {
            if (Overlaps(claimedRanges, m.Index, m.Index + m.Length))
                continue;
            string url = StripTrailingPunctuation(m.Value);
            int endCol = m.Index + url.Length;
            matches.Add(new LinkMatch(m.Index, endCol, url, LinkType.Url));
            claimedRanges.Add((m.Index, m.Index + m.Length));
        }

        // 3. Absolute Windows paths
        foreach (Match m in AbsoluteWindowsPathRegex.Matches(lineText))
        {
            if (Overlaps(claimedRanges, m.Index, m.Index + m.Length))
                continue;
            int rawEnd = ExtendAbsolutePathThroughSpaces(lineText, m.Index, m.Index + m.Length, pathExistsCheck);
            string rawText = lineText.Substring(m.Index, rawEnd - m.Index);
            string path = StripTrailingPunctuation(StripLineNumber(rawText));
            int endCol = m.Index + path.Length;
            matches.Add(new LinkMatch(m.Index, endCol, path, LinkType.Path));
            claimedRanges.Add((m.Index, rawEnd));
        }

        // 4. Unix-style absolute paths
        foreach (Match m in AbsoluteUnixPathRegex.Matches(lineText))
        {
            if (Overlaps(claimedRanges, m.Index, m.Index + m.Length))
                continue;
            int rawEnd = ExtendAbsolutePathThroughSpaces(lineText, m.Index, m.Index + m.Length, pathExistsCheck);
            string rawText = lineText.Substring(m.Index, rawEnd - m.Index);
            string path = StripTrailingPunctuation(StripLineNumber(rawText));
            int endCol = m.Index + path.Length;
            matches.Add(new LinkMatch(m.Index, endCol, path, LinkType.Path));
            claimedRanges.Add((m.Index, rawEnd));
        }

        // 5. Relative paths (only if session has repo path)
        if (repoPath != null)
        {
            foreach (Match m in RelativePathRegex.Matches(lineText))
            {
                if (Overlaps(claimedRanges, m.Index, m.Index + m.Length))
                    continue;
                string relativePath = StripTrailingPunctuation(StripLineNumber(m.Value));
                string fullPath = System.IO.Path.Combine(repoPath, relativePath.Replace('/', '\\'));

                if (pathExistsCheck != null && pathExistsCheck(fullPath))
                {
                    int endCol = m.Index + relativePath.Length;
                    matches.Add(new LinkMatch(m.Index, endCol, relativePath, LinkType.Path));
                    claimedRanges.Add((m.Index, m.Index + m.Length));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Detect if there's a path or URL at the specified column position in a line.
    /// </summary>
    public static (string? text, LinkType type) DetectLinkAtPosition(
        string? lineText, int col, string? repoPath, Func<string, bool>? pathExistsCheck)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return (null, LinkType.None);

        // 1. Quoted paths first
        var quotedSpans = ExtractQuotedSpans(lineText);
        foreach (var span in quotedSpans)
        {
            if (col >= span.InnerStart && col < span.InnerEnd)
            {
                string inner = span.InnerText;
                if (PathLikeRegex.IsMatch(inner))
                {
                    string path = StripTrailingPunctuation(StripLineNumber(inner));
                    if (path.Length > 0)
                    {
                        if (IsRelativePath(path))
                        {
                            if (repoPath != null && pathExistsCheck != null)
                            {
                                string fullPath = System.IO.Path.Combine(repoPath, path.Replace('/', '\\'));
                                if (pathExistsCheck(fullPath))
                                    return (path, LinkType.Path);
                            }
                        }
                        else
                        {
                            return (path, LinkType.Path);
                        }
                    }
                }
            }
        }

        // 2. URLs
        var urlMatch = UrlRegex.Match(lineText);
        while (urlMatch.Success)
        {
            string url = StripTrailingPunctuation(urlMatch.Value);
            if (col >= urlMatch.Index && col < urlMatch.Index + url.Length)
                return (url, LinkType.Url);
            urlMatch = urlMatch.NextMatch();
        }

        // 3. Absolute Windows paths
        var winMatch = AbsoluteWindowsPathRegex.Match(lineText);
        while (winMatch.Success)
        {
            int rawEnd = ExtendAbsolutePathThroughSpaces(lineText, winMatch.Index, winMatch.Index + winMatch.Length, pathExistsCheck);
            string rawText = lineText.Substring(winMatch.Index, rawEnd - winMatch.Index);
            string path = StripTrailingPunctuation(StripLineNumber(rawText));
            if (col >= winMatch.Index && col < winMatch.Index + path.Length)
                return (path, LinkType.Path);
            winMatch = winMatch.NextMatch();
        }

        // 4. Unix-style absolute paths
        var unixMatch = AbsoluteUnixPathRegex.Match(lineText);
        while (unixMatch.Success)
        {
            int rawEnd = ExtendAbsolutePathThroughSpaces(lineText, unixMatch.Index, unixMatch.Index + unixMatch.Length, pathExistsCheck);
            string rawText = lineText.Substring(unixMatch.Index, rawEnd - unixMatch.Index);
            string path = StripTrailingPunctuation(StripLineNumber(rawText));
            if (col >= unixMatch.Index && col < unixMatch.Index + path.Length)
                return (path, LinkType.Path);
            unixMatch = unixMatch.NextMatch();
        }

        // 5. Relative paths
        if (repoPath != null)
        {
            var relMatch = RelativePathRegex.Match(lineText);
            while (relMatch.Success)
            {
                string relativePath = StripTrailingPunctuation(StripLineNumber(relMatch.Value));
                if (col >= relMatch.Index && col < relMatch.Index + relativePath.Length)
                {
                    string fullPath = System.IO.Path.Combine(repoPath, relativePath.Replace('/', '\\'));
                    if (pathExistsCheck != null && pathExistsCheck(fullPath))
                        return (relativePath, LinkType.Path);
                }
                relMatch = relMatch.NextMatch();
            }
        }

        return (null, LinkType.None);
    }

    /// <summary>
    /// Strip line number suffix from path (e.g., "file.cs:42" -> "file.cs", "file.cs:10:20" -> "file.cs").
    /// </summary>
    public static string StripLineNumber(string path)
    {
        // Handle :line:col and :line formats by stripping from the first trailing colon
        // that is followed only by digits and colons.
        // E.g., "file.cs:10:20" -> "file.cs", "file.cs:42" -> "file.cs"
        // But "D:\path" -> "D:\path" (drive letter colon at index 1 is not stripped)
        for (int i = path.Length - 1; i > 1; i--)
        {
            char c = path[i];
            if (char.IsDigit(c) || c == ':')
                continue;
            // Found a non-digit, non-colon character
            // If the next character is ':', everything after it is a line number suffix
            if (i + 1 < path.Length && path[i + 1] == ':')
                return path.Substring(0, i + 1);
            return path;
        }
        return path;
    }

    /// <summary>
    /// Resolve a detected path to an absolute Windows path.
    /// </summary>
    public static string ResolvePath(string path, string? repoPath)
    {
        // Unix-style path /c/path -> C:\path
        if (path.StartsWith("/") && path.Length >= 3 && path[2] == '/')
        {
            char driveLetter = char.ToUpper(path[1]);
            string remainder = path.Substring(3).Replace('/', '\\');
            return $"{driveLetter}:\\{remainder}";
        }

        // Already an absolute Windows path
        if (path.Length >= 2 && path[1] == ':')
            return path;

        // Relative path - resolve against repo path
        if (repoPath != null)
        {
            string normalized = path.Replace('/', '\\');
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, normalized));
        }

        // No repo path, return as-is
        return path;
    }

    /// <summary>
    /// Strip trailing punctuation that is likely sentence-end rather than part of a path or URL.
    /// Strips trailing comma, semicolon, and period (when it follows an existing extension).
    /// </summary>
    public static string StripTrailingPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        while (text.Length > 0)
        {
            char last = text[^1];

            // Comma and semicolon are always sentence punctuation
            if (last == ',' || last == ';')
            {
                text = text[..^1];
                continue;
            }

            // Trailing period: strip unless the text has no path separators/colons AND
            // the period forms the only dot (i.e., it looks like a bare file extension).
            // Examples that get stripped:
            //   "http://localhost:4001."  -> "http://localhost:4001"
            //   "path/file.txt."         -> "path/file.txt"
            //   "https://example.com."   -> "https://example.com"
            // Example that is kept:
            //   "file.txt" (the dot is the file extension itself, no trailing period)
            if (last == '.')
            {
                text = text[..^1];
                continue;
            }

            break;
        }

        return text;
    }

    /// <summary>
    /// Extract quoted spans from text. Supports double quotes, single quotes, and backticks.
    /// </summary>
    internal static List<QuotedSpan> ExtractQuotedSpans(string text)
    {
        var spans = new List<QuotedSpan>();
        if (string.IsNullOrEmpty(text))
            return spans;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"' || c == '\'' || c == '`')
            {
                int closeIndex = text.IndexOf(c, i + 1);
                if (closeIndex > i + 1)
                {
                    string inner = text.Substring(i + 1, closeIndex - i - 1);
                    spans.Add(new QuotedSpan(
                        OuterStart: i,
                        OuterEnd: closeIndex + 1,
                        InnerStart: i + 1,
                        InnerEnd: closeIndex,
                        InnerText: inner));
                    i = closeIndex + 1;
                    continue;
                }
            }
            i++;
        }

        return spans;
    }

    /// <summary>
    /// Extend an absolute path match through spaces. The base regex stops at any whitespace,
    /// but real paths may contain spaces (e.g., "D:\Center Consulting\Click Funnels\file.md").
    /// Walks forward through whitespace-separated segments, picking the longest range where
    /// the candidate path exists on disk. If no callback is provided or no longer prefix
    /// exists, returns initialEnd unchanged (preserving baseline regex behavior).
    /// </summary>
    internal static int ExtendAbsolutePathThroughSpaces(
        string lineText, int pathStart, int initialEnd, Func<string, bool>? pathExistsCheck)
    {
        if (pathExistsCheck is null) return initialEnd;

        int bestEnd = initialEnd;
        int pos = initialEnd;

        while (pos < lineText.Length)
        {
            char c = lineText[pos];
            // The regex stopped here. If the stop char is whitespace we can try to extend;
            // otherwise it was a forbidden char (quote, paren, etc.) and we must stop.
            if (c != ' ' && c != '\t') break;

            while (pos < lineText.Length && (lineText[pos] == ' ' || lineText[pos] == '\t'))
                pos++;
            if (pos >= lineText.Length) break;
            if (IsForbiddenPathChar(lineText[pos])) break;

            while (pos < lineText.Length
                   && lineText[pos] != ' ' && lineText[pos] != '\t'
                   && !IsForbiddenPathChar(lineText[pos]))
                pos++;

            string candidate = StripTrailingPunctuation(StripLineNumber(
                lineText.Substring(pathStart, pos - pathStart)));
            string resolved = ResolvePath(candidate, null);
            if (pathExistsCheck(resolved))
                bestEnd = pos;
        }

        return bestEnd;
    }

    private static bool IsForbiddenPathChar(char c)
    {
        return c == '"' || c == '\'' || c == '`' || c == '<' || c == '>'
            || c == '|' || c == '*' || c == '?' || c == '(' || c == ')'
            || c == '[' || c == ']';
    }

    private static bool IsRelativePath(string path)
    {
        // Not an absolute Windows path or Unix absolute path
        if (path.Length >= 2 && path[1] == ':') return false;
        if (path.StartsWith("/") && path.Length >= 3 && path[2] == '/') return false;
        return true;
    }

    private static bool Overlaps(List<(int start, int end)> ranges, int start, int end)
    {
        foreach (var (s, e) in ranges)
        {
            if (start < e && end > s)
                return true;
        }
        return false;
    }
}
