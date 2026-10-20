using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Parses raw terminal output bytes: strips ANSI, extracts URLs, finds session IDs.
/// All methods are static and side-effect free — safe to call from any thread.
/// </summary>
public static partial class TerminalOutputParser
{
    /// <summary>
    /// Result of parsing terminal startup output.
    /// </summary>
    public sealed class StartupInfo
    {
        /// <summary>Clean text with ANSI stripped but URLs preserved as markdown links.</summary>
        public string CleanText { get; init; } = string.Empty;

        /// <summary>All URLs found in OSC 8 hyperlink sequences.</summary>
        public IReadOnlyList<string> Urls { get; init; } = [];

        /// <summary>Raw UTF-8 text before any stripping (for debugging).</summary>
        public string RawText { get; init; } = string.Empty;
    }

    /// <summary>
    /// Parse raw terminal bytes into structured startup info.
    /// Extracts URLs from OSC 8 hyperlinks before stripping ANSI sequences.
    /// </summary>
    public static StartupInfo Parse(byte[] rawBytes)
    {
        if (rawBytes.Length == 0)
            return new StartupInfo();

        var raw = Encoding.UTF8.GetString(rawBytes);
        var urls = ExtractOsc8Urls(raw);
        var clean = StripAnsi(raw);

        return new StartupInfo
        {
            RawText = raw,
            CleanText = clean,
            Urls = urls,
        };
    }

    /// <summary>
    /// Convenience overload: read directly from a CircularTerminalBuffer.
    /// </summary>
    public static StartupInfo Parse(CircularTerminalBuffer buffer)
    {
        var bytes = buffer.DumpAll();
        return Parse(bytes);
    }

    /// <summary>
    /// Extract all URLs from OSC 8 hyperlink sequences.
    /// OSC 8 format: ESC]8;params;URL ST display_text ESC]8;; ST
    /// where ST is either BEL (0x07) or ESC\ (0x1B 0x5C).
    /// </summary>
    public static List<string> ExtractOsc8Urls(string raw)
    {
        var urls = new List<string>();
        var matches = Osc8Regex().Matches(raw);
        foreach (Match m in matches)
        {
            var url = m.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url);
        }
        return urls;
    }

    /// <summary>
    /// Strip ANSI escape sequences from terminal text.
    /// Preserves OSC 8 hyperlink URLs as markdown-style links before stripping.
    /// Collapses runs of blank lines.
    /// </summary>
    public static string StripAnsi(string raw)
    {
        // First, convert OSC 8 hyperlinks to markdown-style [text](url)
        var withUrls = Osc8Regex().Replace(raw, "[$2]($1)");

        // Strip remaining ANSI sequences:
        //   CSI: ESC[ with optional ? or > prefix, params, letter
        //   OSC: ESC] ... BEL  or  ESC] ... ESC\
        //   Simple two-char: ESC followed by single char (e.g. ESC=, ESC>)
        var stripped = AnsiSequenceRegex().Replace(withUrls, "");

        // Collapse 3+ consecutive blank lines into one
        stripped = BlankLineRegex().Replace(stripped, "\n\n");

        return stripped.Trim();
    }

    /// <summary>
    /// Write a debug dump of startup info to a file.
    /// Overwrites the file each time.
    /// </summary>
    public static void WriteDump(string filePath, StartupInfo info, Guid sessionId, string repoPath, int pid)
    {
        FileLog.Write($"[TerminalOutputParser] WriteDump: path={filePath}, urls={info.Urls.Count}");

        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId}");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Repo: {repoPath}");
        sb.AppendLine($"PID: {pid}");
        sb.AppendLine(new string('-', 80));

        if (info.Urls.Count > 0)
        {
            sb.AppendLine("URLS FOUND:");
            foreach (var url in info.Urls)
                sb.AppendLine($"  {url}");
            sb.AppendLine(new string('-', 80));
        }

        sb.AppendLine("CLEAN TEXT:");
        sb.AppendLine(info.CleanText);

        File.WriteAllText(filePath, sb.ToString());
        FileLog.Write($"[TerminalOutputParser] WriteDump: wrote {sb.Length} chars to {filePath}");
    }

    // Every pattern here carries a match timeout (CodingStyle, Regex Safety). It is ONE SECOND, not the 100 ms it
    // was: the timeout is wall-clock time, and a busy machine spent more than 100 ms matching a 60-character test
    // input (continuous integration on main, 20 October 2026), which would throw on real terminal output just the
    // same. None of these patterns can backtrack badly; the timeout is the guard for input nobody foresaw.
    private const int MatchTimeoutMilliseconds = 1000;

    // OSC 8 hyperlink: ESC]8;params;URL (BEL|ESC\) display_text ESC]8;; (BEL|ESC\)
    [GeneratedRegex(@"\x1B\]8;[^;]*;([^\x07\x1B]*?)(?:\x07|\x1B\\)(.*?)\x1B\]8;;(?:\x07|\x1B\\)", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex Osc8Regex();

    // All remaining ANSI: CSI sequences, OSC sequences, two-char escapes
    [GeneratedRegex(@"\x1B\[[\?>]?[0-9;]*[A-Za-z]|\x1B\][^\x07]*\x07|\x1B\].*?\x1B\\|\x1B[A-Za-z=<>]", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex AnsiSequenceRegex();

    // Three or more consecutive newlines
    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex BlankLineRegex();
}
