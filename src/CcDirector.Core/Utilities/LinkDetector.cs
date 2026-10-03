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

    // EVERY PATTERN STOPS AT BOX-DRAWING CHARACTERS (U+2500-U+257F). An agent draws separator rules
    // and frames right up against text, and a rule is never part of an address or a path.

    // Absolute Windows paths (e.g., C:\path\to\file or C:/path/to/file). The (?<![A-Za-z]) guard keeps
    // a drive letter that is really part of a longer word from being mis-claimed: without it, scanning
    // "file:///D:/..." matches the "e" of "file" (e:///D:/...), so the highlight starts in the wrong
    // place and the target is garbage (issue #252). The same guard stops "node:", "http:" etc.
    // Brackets are allowed ("C:\Program Files (x86)\..."); an unbalanced closing bracket at the end
    // is sentence punctuation and is trimmed by StripTrailingPunctuation.
    internal static readonly Regex AbsoluteWindowsPathRegex =
        new(@"(?<![A-Za-z])[A-Za-z]:[/\\][^\s""'`<>|*?\[\]\u2500-\u257F]+", RegexOptions.Compiled, RegexTimeout);

    // Git Bash / MSYS drive paths (e.g., /c/path/to/file). The left guard stops the pattern starting in
    // the MIDDLE of a longer path: without it "/mnt/c/Users/x" was cut down to "/c/Users/x" and
    // "src/a/b.cs" to "/a/b.cs", and both were opened as drive paths (C:\Users\x, A:\b.cs).
    internal static readonly Regex AbsoluteUnixPathRegex =
        new(@"(?<![\w.~/\\:-])/[a-z]/[^\s""'`<>|*?()\[\]\u2500-\u257F]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // Windows network paths (e.g., \\server\share\file.txt).
    internal static readonly Regex UncPathRegex =
        new(@"(?<![\w\\:])\\\\[A-Za-z0-9_.$\-]+\\[^\s""'`<>|*?\[\]\u2500-\u257F]+", RegexOptions.Compiled, RegexTimeout);

    // Home-folder paths (e.g., ~/.config/app/settings.json or ~\notes\x.md).
    internal static readonly Regex HomePathRegex =
        new(@"(?<![\w./\\~-])~[/\\][^\s""'`<>|*?()\[\]\u2500-\u257F]+", RegexOptions.Compiled, RegexTimeout);

    // Relative paths (e.g., ./src/file.cs, ../other/file.txt, src/dir/file.cs)
    internal static readonly Regex RelativePathRegex =
        new(@"\.{0,2}[/\\][^\s""'`<>|*?:()\[\]\u2500-\u257F]+|[A-Za-z_][A-Za-z0-9_\-]*[/\\][^\s""'`<>|*?:()\[\]\u2500-\u257F]+",
            RegexOptions.Compiled, RegexTimeout);

    // A bare file name with an extension and no folder (e.g., README.md). Only a link when that file
    // exists in the repository, so "args.Name" and "example.com" in prose stay plain text. The
    // extension must start with a letter, so a number such as "2.10.0" or "3.14" is never looked up.
    internal static readonly Regex BareFileNameRegex =
        new(@"(?<![\w./\\~:@-])[A-Za-z0-9_][\w\-]*(?:\.[\w\-]+)*\.[A-Za-z][A-Za-z0-9]{0,9}(?![\w/\\])",
            RegexOptions.Compiled, RegexTimeout);

    // URLs (http/https or git@). Brackets and apostrophes are part of real addresses
    // (https://en.wikipedia.org/wiki/Foo_(bar), ?ids=[1,2], /it's), so they are matched and only an
    // UNBALANCED closing bracket or trailing punctuation is trimmed off the end (StripTrailingPunctuation).
    internal static readonly Regex UrlRegex =
        new(@"https?://[^\s""`<>\u2500-\u257F]+|git@[^\s""`<>\u2500-\u257F]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // file:// URLs (RFC 8089), e.g. file:///D:/path/file.html. These name a LOCAL file, so they are
    // surfaced as Path links (View File / Open in File Manager) with the whole file://... span
    // highlighted and the resolved local path as the target (issue #252). Matched before the Windows
    // and Unix path matchers so the whole span is claimed as one link instead of the inner drive path.
    internal static readonly Regex FileUrlRegex =
        new(@"file://[^\s""`<>|*?\u2500-\u257F]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // A compiler's position suffix on a path: "(42)", "(42,7)", "(42-45)" or "(42,7,42,12)" at the very end, with the colon that
    // follows it in "File.cs(42,7): error" when the match ran on to it.
    private static readonly Regex CompilerPositionRegex =
        new(@"\(\d+(?:[,-]\d+){0,3}\):?$", RegexOptions.Compiled, RegexTimeout);

    // Characters that look like a path start inside a quoted span
    private static readonly Regex PathLikeRegex =
        new(@"^(?:[A-Za-z]:[/\\]|/[a-z]/|\.{0,2}[/\\]|[A-Za-z_][A-Za-z0-9_\-]*[/\\])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// Find all link matches (paths and URLs) in a line of text.
    ///
    /// The underlined range (StartCol..EndCol) is always EXACTLY the text on screen that the link is:
    /// never shortened, never stretched into the words around it. Owner ruling 3 October 2026: "We
    /// should not mess around and change URLs ever." For a URL, Text is that same text. For a path,
    /// Text is the path with any ":line" suffix removed, and for a file:// URL it is the local path
    /// it names - the target to open - while the underline stays on the URL as written.
    /// </summary>
    /// <param name="lineText">The terminal line text to scan.</param>
    /// <param name="repoPath">Optional repo root path for resolving relative paths.</param>
    /// <param name="pathExistsCheck">
    /// Callback that returns true if a full path exists on disk. Relative paths, bare file names,
    /// home-folder paths and Git Bash drive paths are links only when it answers true; without it,
    /// relative paths, bare names and home paths are never links.
    /// </param>
    public static List<LinkMatch> FindAllLinkMatches(string? lineText, string? repoPath, Func<string, bool>? pathExistsCheck)
    {
        var matches = new List<LinkMatch>();
        if (string.IsNullOrWhiteSpace(lineText))
            return matches;

        var claimedRanges = new List<(int start, int end)>();
        bool[] quoteOpenBefore = QuoteOpenBefore(lineText);

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
                        if (repoPath == null || pathExistsCheck == null || !pathExistsCheck(ResolvePath(path, repoPath)))
                            continue;
                    }

                    // Underline covers inner path only (not quotes)
                    matches.Add(new LinkMatch(span.InnerStart, span.InnerStart + path.Length, path, LinkType.Path));
                    // Claim the full quoted span so unquoted regex won't double-match
                    claimedRanges.Add((span.OuterStart, span.OuterEnd));
                }
            }
        }

        // 2. URLs. Each scan resumes where the previous link really ENDED, not where the pattern
        // stopped: in 'https://a.com/x','https://b.com/y' the pattern runs on through the second
        // address, and resuming after it would lose that address entirely. For the same reason the
        // "already claimed" test uses the link's real end, not the pattern's.
        int pos = 0;
        for (Match m = UrlRegex.Match(lineText); m.Success; m = UrlRegex.Match(lineText, pos))
        {
            int end = LinkEnd(lineText, m.Index, m.Index + m.Length, isUrl: true, quoteOpenBefore[m.Index]);
            end = ClipToClaimed(claimedRanges, m.Index, end);
            pos = Math.Max(end, m.Index + 1);
            if (Overlaps(claimedRanges, m.Index, end))
                continue;
            string url = StripTrailingPunctuation(lineText.Substring(m.Index, end - m.Index));
            if (url.Length == 0)
                continue;
            matches.Add(new LinkMatch(m.Index, m.Index + url.Length, url, LinkType.Url));
            claimedRanges.Add((m.Index, end));
        }

        // 2b. file:// URLs -> the resolved LOCAL file path (a Path link). The whole file://... span is
        // highlighted, but the match Text is the local path so clicking opens the actual file. Runs
        // before the path matchers so they cannot mis-claim the drive letter inside the URL.
        pos = 0;
        for (Match m = FileUrlRegex.Match(lineText); m.Success; m = FileUrlRegex.Match(lineText, pos))
        {
            int end = LinkEnd(lineText, m.Index, m.Index + m.Length, isUrl: false, quoteOpenBefore[m.Index]);
            end = ClipToClaimed(claimedRanges, m.Index, end);
            pos = Math.Max(end, m.Index + 1);
            if (Overlaps(claimedRanges, m.Index, end))
                continue;
            string span = StripTrailingPunctuation(lineText.Substring(m.Index, end - m.Index));
            if (!TryConvertFileUrlToLocalPath(span, out string localPath))
                continue;
            matches.Add(new LinkMatch(m.Index, m.Index + span.Length, localPath, LinkType.Path));
            claimedRanges.Add((m.Index, end));
        }

        // 3. Absolute Windows paths, then network paths. Drive paths go first so an escaped
        // "C:\\Users\\x" is a drive path, not a network path "\\Users\\x".
        AddAbsolutePaths(AbsoluteWindowsPathRegex, lineText, pathExistsCheck, requireExists: false, extendThroughSpaces: true, quoteOpenBefore, matches, claimedRanges);

        // A network path is a link only when it exists: "\\n\\t" in a log line has the same shape.
        // It is never extended through spaces, because each try asks the network, and a missing
        // host takes seconds to answer.
        if (pathExistsCheck != null)
            AddAbsolutePaths(UncPathRegex, lineText, pathExistsCheck, requireExists: true, extendThroughSpaces: false, quoteOpenBefore, matches, claimedRanges);

        // 4. Git Bash drive paths. "/r/programming" has the same shape as "/c/Users", so when existence
        // can be checked, only a path that exists is a link.
        AddAbsolutePaths(AbsoluteUnixPathRegex, lineText, pathExistsCheck, requireExists: pathExistsCheck != null, extendThroughSpaces: true, quoteOpenBefore, matches, claimedRanges);

        // 5. Home-folder paths - only when they exist
        if (pathExistsCheck != null)
            AddAbsolutePaths(HomePathRegex, lineText, pathExistsCheck, requireExists: true, extendThroughSpaces: true, quoteOpenBefore, matches, claimedRanges);

        // 6. Relative paths and bare file names (only if session has repo path) - only when they exist
        if (repoPath != null && pathExistsCheck != null)
        {
            AddRelativePaths(RelativePathRegex, lineText, repoPath, pathExistsCheck, matches, claimedRanges);
            AddRelativePaths(BareFileNameRegex, lineText, repoPath, pathExistsCheck, matches, claimedRanges);
        }

        return matches;
    }

    private static void AddAbsolutePaths(Regex regex, string lineText, Func<string, bool>? pathExistsCheck,
        bool requireExists, bool extendThroughSpaces, bool[] quoteOpenBefore,
        List<LinkMatch> matches, List<(int start, int end)> claimedRanges)
    {
        int pos = 0;
        for (Match m = regex.Match(lineText); m.Success; m = regex.Match(lineText, pos))
        {
            int regexEnd = m.Index + m.Length;
            int rawEnd = ClipToClaimed(claimedRanges, m.Index, LinkEnd(lineText, m.Index, regexEnd, isUrl: false, quoteOpenBefore[m.Index]));
            pos = Math.Max(rawEnd, m.Index + 1);
            if (Overlaps(claimedRanges, m.Index, rawEnd))
                continue;
            // Only a match that ran to a space can continue through it ("C:\My Documents\x"), and
            // never into text another link already holds.
            if (extendThroughSpaces && rawEnd == regexEnd)
            {
                int extended = ExtendAbsolutePathThroughSpaces(lineText, m.Index, rawEnd, pathExistsCheck);
                if (!Overlaps(claimedRanges, m.Index, extended))
                    rawEnd = extended;
                pos = Math.Max(rawEnd, m.Index + 1);
            }
            string rawText = lineText.Substring(m.Index, rawEnd - m.Index);
            string path = StripTrailingPunctuation(StripLineNumber(rawText));
            if (path.Length == 0)
                continue;
            if (requireExists && !pathExistsCheck!(ResolvePath(path, null)))
            {
                // Claimed even though it is not a link, so the relative-path pattern does not ask about
                // the same text again (as a different path, for an escaped "\\n\\t").
                claimedRanges.Add((m.Index, rawEnd));
                continue;
            }
            matches.Add(new LinkMatch(m.Index, m.Index + path.Length, path, LinkType.Path));
            claimedRanges.Add((m.Index, rawEnd));
        }
    }

    private static void AddRelativePaths(Regex regex, string lineText, string repoPath, Func<string, bool> pathExistsCheck,
        List<LinkMatch> matches, List<(int start, int end)> claimedRanges)
    {
        foreach (Match m in regex.Matches(lineText))
        {
            if (Overlaps(claimedRanges, m.Index, m.Index + m.Length))
                continue;
            string relativePath = StripTrailingPunctuation(StripLineNumber(m.Value));
            if (relativePath.Length == 0 || !pathExistsCheck(ResolvePath(relativePath, repoPath)))
                continue;
            matches.Add(new LinkMatch(m.Index, m.Index + relativePath.Length, relativePath, LinkType.Path));
            claimedRanges.Add((m.Index, m.Index + m.Length));
        }
    }

    /// <summary>
    /// Where a link that starts at <paramref name="start"/> really ends, at or before the pattern's own
    /// end <paramref name="end"/>. The patterns accept quotes and brackets because real addresses and
    /// paths contain them (Foo_(bar), ?ids=[1,2], getbytitle('Documents'), "Program Files (x86)"), so the
    /// end is found by walking the link from its START, not by trimming its tail - trimming cannot see
    /// that the link closed earlier, and swallowed "').then(r" in fetch('https://x').then(r => ...).
    ///
    /// Brackets: a closing bracket the link never opened ends it, and so does an opening bracket that is
    /// never closed ("https://x.io/a(see below)" ends before the bracket).
    ///
    /// Quotes. A link that opened right after a quote ends at the next of that same quote. For a URL, an
    /// apostrophe before the path ends it, because a host name cannot hold one ("https://x.io's page").
    /// An apostrophe between two letters is part of a word (it's, o'neil) and never a quote. Every other
    /// apostrophe depends on WHERE THE LINK STARTS:
    ///  - inside a quoted string - an apostrophe that is not part of a word is still open before the
    ///    link - the link is the end of that string, so the next such apostrophe ends it. This is how an
    ///    address sits in code and logs: {'error': 'see https://x.io/limits'},
    ///    log('see https://x.io/docs/'+page), it's {'k': 'see https://x.io/a'}.
    ///  - outside one, apostrophes in the link pair up, because the address holds them itself:
    ///    ?$filter=Name%20eq%20'Chai', ?q='', items(guid'1234'). One that never closes ends the link
    ///    (https://x.io/a?b=c'), and so does one right after a comma or a semicolon outside any bracket
    ///    (https://x.io/a,'hello') or two in a row right after a letter, digit or slash (https://x.io/a'').
    /// The accepted costs: a closing quote with a letter straight after it ('see https://x.io/a'if b)
    /// reads as part of a word, and an address glued to a quoted word outside any string
    /// (https://x.io/a'b') keeps it - neither can be told apart from a real address by its characters.
    ///
    /// Whenever the link ends early, it also ends before any bracket or quote it left open.
    /// </summary>
    internal static int LinkEnd(string line, int start, int end, bool isUrl, bool insideQuotedString)
    {
        char before = start > 0 ? line[start - 1] : '\0';
        char openQuote = before is '\'' or '"' or '`' ? before : '\0';

        // For a URL, where the host ends: the first '/', '?' or '#' after "://" (or from the start for git@).
        int hostEnd = end;
        if (isUrl)
        {
            int sep = line.IndexOf("://", start, end - start, StringComparison.Ordinal);
            int hostStart = sep >= 0 ? sep + 3 : start;
            for (int i = hostStart; i < end; i++)
            {
                if (line[i] is '/' or '?' or '#')
                {
                    hostEnd = i;
                    break;
                }
            }
        }

        // Positions of the brackets still open, innermost last, and of a quote opened inside the link.
        var open = new List<int>();
        int quoteOpenedAt = -1;
        int EndAt(int i)
        {
            if (open.Count > 0) i = Math.Min(i, open[0]);
            if (quoteOpenedAt >= 0) i = Math.Min(i, quoteOpenedAt);
            return i;
        }

        for (int i = start; i < end; i++)
        {
            char c = line[i];
            if (openQuote != '\0' && c == openQuote)
                return EndAt(i);

            if (c == '\'')
            {
                if (isUrl && i < hostEnd)
                    return EndAt(i);
                if (IsWordApostrophe(line, i, start, end))
                    continue;
                if (insideQuotedString)
                    return EndAt(i);
                if (quoteOpenedAt >= 0)
                {
                    quoteOpenedAt = -1;
                    continue;
                }
                char prev = i > start ? line[i - 1] : '\0';
                if (prev is ',' or ';' && open.Count == 0)
                    return EndAt(i);
                if ((char.IsLetterOrDigit(prev) || prev == '/') && i + 1 < end && line[i + 1] == '\'')
                    return EndAt(i);
                quoteOpenedAt = i;
                continue;
            }

            switch (c)
            {
                case '(':
                case '[':
                    open.Add(i);
                    break;
                case ')':
                case ']':
                    char opener = c == ')' ? '(' : '[';
                    if (open.Count == 0 || line[open[^1]] != opener)
                        return EndAt(i);
                    open.RemoveAt(open.Count - 1);
                    break;
            }
        }

        if (quoteOpenedAt >= 0)
            return EndAt(quoteOpenedAt);
        return open.Count > 0 ? open[0] : end;
    }

    /// <summary>An apostrophe between two letters is part of a word (it's, o'neil), not a quote.</summary>
    private static bool IsWordApostrophe(string line, int i, int from, int to)
    {
        return i > from && char.IsLetter(line[i - 1]) && i + 1 < to && char.IsLetter(line[i + 1]);
    }

    /// <summary>
    /// For every column of <paramref name="line"/>, whether a quoted string is open just before it -
    /// that is, whether a link starting there is the inside of a string. Found in ONE pass over the
    /// line. Apostrophes are read by what they can be, not toggled:
    ///  - while no quote is open, an apostrophe opens one only when the character before it is not a
    ///    letter or a digit, or the letters before it are a string prefix (f'..., rb'..., N'...).
    ///    Any other apostrophe - users', James', it's, 6' - is part of the prose and is skipped;
    ///  - while a quote is open, an apostrophe that is not between two letters closes it.
    /// </summary>
    private static bool[] QuoteOpenBefore(string line)
    {
        var state = new bool[line.Length + 1];
        bool open = false;
        for (int i = 0; i < line.Length; i++)
        {
            state[i] = open;
            if (line[i] != '\'')
                continue;
            if (open)
            {
                if (!IsWordApostrophe(line, i, 0, line.Length))
                    open = false;
                continue;
            }
            char prev = i > 0 ? line[i - 1] : '\0';
            if (!char.IsLetterOrDigit(prev) || IsStringPrefixBefore(line, i))
                open = true;
        }
        state[line.Length] = open;
        return state;
    }

    /// <summary>True when the one or two letters right before the apostrophe at <paramref name="i"/>
    /// are a string prefix (f, b, r, u in either case, or N, as in f'..', rb'..', N'..') that is not
    /// itself the end of a longer word or number.</summary>
    private static bool IsStringPrefixBefore(string line, int i)
    {
        int j = i;
        while (j > 0 && char.IsLetter(line[j - 1])) j--;
        int len = i - j;
        if (len < 1 || len > 2) return false;
        if (j > 0 && (char.IsDigit(line[j - 1]) || line[j - 1] == '\'' || line[j - 1] == '_')) return false;
        for (int k = j; k < i; k++)
            if ("fFbBrRuUN".IndexOf(line[k]) < 0) return false;
        return true;
    }

    /// <summary>
    /// Detect if there's a path or URL at the specified column position in a line. Answered from
    /// <see cref="FindAllLinkMatches"/>, so a click finds exactly the link that is underlined.
    /// </summary>
    public static (string? text, LinkType type) DetectLinkAtPosition(
        string? lineText, int col, string? repoPath, Func<string, bool>? pathExistsCheck)
    {
        foreach (var m in FindAllLinkMatches(lineText, repoPath, pathExistsCheck))
        {
            if (col >= m.StartCol && col < m.EndCol)
                return (m.Text, m.Type);
        }
        return (null, LinkType.None);
    }

    /// <summary>
    /// Strip line number suffix from path (e.g., "file.cs:42" -> "file.cs", "file.cs:10:20" -> "file.cs").
    /// </summary>
    public static string StripLineNumber(string path)
    {
        // A .NET stack trace writes "in D:\src\file.cs:line 12"; the path match stops at the space.
        if (path.EndsWith(":line", StringComparison.OrdinalIgnoreCase))
            return path[..^":line".Length];

        // A compiler writes "D:\src\File.cs(42,7): error ..." - the position in brackets is not the path.
        var position = CompilerPositionRegex.Match(path);
        if (position.Success)
            return path[..position.Index];

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
    /// Convert a <c>file://</c> URL to the local filesystem path it names, e.g.
    /// <c>file:///D:/Repos/x.html</c> to <c>D:\Repos\x.html</c> (issue #252). Uses <see cref="Uri"/>
    /// so percent-encoding and UNC hosts (<c>file://server/share</c>) are handled correctly. Returns
    /// false for anything that does not parse as an absolute file URL, so the caller falls through to
    /// the ordinary path matchers rather than surfacing a broken link.
    /// </summary>
    public static bool TryConvertFileUrlToLocalPath(string fileUrl, out string localPath)
    {
        localPath = string.Empty;
        if (string.IsNullOrEmpty(fileUrl))
            return false;
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            return false;
        string candidate = uri.LocalPath;
        if (string.IsNullOrEmpty(candidate))
            return false;
        localPath = candidate;
        return true;
    }

    /// <summary>
    /// Resolve a detected path to an absolute path for THIS platform.
    ///
    /// This was Windows-only, and silently wrong everywhere else. Two faults, both of which made a
    /// clicked link in a Mac session open nothing:
    ///
    /// A RELATIVE PATH HAD ITS SEPARATORS REWRITTEN. "src/file.cs" became "src\file.cs" and was joined
    /// to the repository root, so the result named a SINGLE file called "src\file.cs" in the root
    /// rather than a file in the "src" directory. A backslash is an ordinary, legal character in a Unix
    /// file name, so nothing downstream could detect the mistake - the path simply did not exist.
    ///
    /// AN ABSOLUTE UNIX PATH COULD BE TURNED INTO A DRIVE LETTER. The "/c/path -&gt; C:\path" rule that
    /// Git Bash output needs on Windows matched any path whose second segment starts at index two, so a
    /// genuine macOS path like "/a/file.cs" was rewritten to "A:\file.cs".
    ///
    /// The Windows behaviour below is unchanged, including the Git Bash translation, which only makes
    /// sense there.
    /// </summary>
    public static string ResolvePath(string path, string? repoPath)
    {
        // Home-folder path: ~/x or ~\x
        if (path.StartsWith("~/") || path.StartsWith(@"~\"))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string rest = path.Substring(2);
            if (OperatingSystem.IsWindows())
                rest = rest.Replace('/', '\\');
            return System.IO.Path.Combine(home, rest);
        }

        if (OperatingSystem.IsWindows())
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

        // macOS and Linux: there are no drive letters, and the separator is already the right one.
        if (System.IO.Path.IsPathRooted(path))
            return path;

        if (repoPath != null)
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, path));

        return path;
    }

    /// <summary>
    /// Strip trailing characters that belong to the sentence around a path or URL rather than to it:
    /// comma, semicolon, period, exclamation and question marks, colon, Markdown emphasis (*),
    /// a quote with no partner inside the link, and a closing bracket with no opening partner. A
    /// BALANCED closing bracket or quote is kept, because it is part of the address
    /// (https://en.wikipedia.org/wiki/Foo_(bar), ?y='2').
    /// </summary>
    public static string StripTrailingPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Counted once and kept current as characters come off the end, so a long tail of
        // punctuation is linear, not quadratic.
        int openParens = Count(text, '('), closeParens = Count(text, ')');
        int openSquares = Count(text, '['), closeSquares = Count(text, ']');
        int singleQuotes = CountQuoteApostrophes(text), doubleQuotes = Count(text, '"'), backticks = Count(text, '`');

        int end = text.Length;
        while (end > 0)
        {
            char last = text[end - 1];

            // Always sentence punctuation (or Markdown emphasis)
            if (last is ',' or ';' or '!' or '?' or ':' or '*')
            {
                end--;
                continue;
            }

            // A quote with no partner inside the link
            if ((last == '\'' && singleQuotes % 2 == 1)
                || (last == '"' && doubleQuotes % 2 == 1)
                || (last == '`' && backticks % 2 == 1))
            {
                if (last == '\'') singleQuotes--;
                else if (last == '"') doubleQuotes--;
                else backticks--;
                end--;
                continue;
            }

            // A closing bracket the link never opened, e.g. "(see https://x.io/a)" or "[x](https://x.io/a)"
            if (last == ')' && openParens < closeParens)
            {
                closeParens--;
                end--;
                continue;
            }
            if (last == ']' && openSquares < closeSquares)
            {
                closeSquares--;
                end--;
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
                end--;
                continue;
            }

            break;
        }

        return end == text.Length ? text : text[..end];
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
    /// but real paths may contain spaces (e.g., "C:\My Documents\Project Files\file.md").
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

    /// <summary>Apostrophes that are quotes - not the ones inside a word (it's).</summary>
    private static int CountQuoteApostrophes(string text)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\'' && !IsWordApostrophe(text, i, 0, text.Length)) n++;
        return n;
    }

    private static int Count(string text, char c)
    {
        int n = 0;
        foreach (char ch in text)
            if (ch == c) n++;
        return n;
    }

    // Brackets are NOT forbidden: "C:\Program Files (x86)\..." extends through "(x86)\..." when
    // that longer path exists.
    private static bool IsForbiddenPathChar(char c)
    {
        return c == '"' || c == '\'' || c == '`' || c == '<' || c == '>'
            || c == '|' || c == '*' || c == '?' || c == '[' || c == ']'
            || (c >= '\u2500' && c <= '\u257F');
    }

    private static bool IsRelativePath(string path)
    {
        // Not an absolute Windows path, Git Bash drive path, network path or home-folder path
        if (path.Length >= 2 && path[1] == ':') return false;
        if (path.StartsWith("/") && path.Length >= 3 && path[2] == '/') return false;
        if (path.StartsWith(@"\\")) return false;
        if (path.StartsWith("~/") || path.StartsWith(@"~\")) return false;
        if (!OperatingSystem.IsWindows() && path.StartsWith("/")) return false;
        return true;
    }

    /// <summary>End a link at the first range another link already holds, e.g. the quoted path in
    /// https://a.com/x,'src/file.cs' - otherwise the address overlaps that path and is lost.</summary>
    private static int ClipToClaimed(List<(int start, int end)> ranges, int start, int end)
    {
        foreach (var (s, _) in ranges)
        {
            if (s > start && s < end)
                end = s;
        }
        return end;
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
