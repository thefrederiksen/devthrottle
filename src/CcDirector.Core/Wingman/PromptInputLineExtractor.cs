using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Extracts the text currently sitting in Claude Code's input-prompt line, parsed
/// out of the raw terminal buffer.
///
/// Claude Code (Ink TUI) renders its input area like this at the bottom of the
/// terminal, optionally wrapped in a box-drawing border:
///
/// <code>
/// ╭───────────────────────────────────────────────╮
/// │ > commit the cc-playwright changes too         │
/// ╰───────────────────────────────────────────────╯
///   >> bypass permissions on (shift+tab to cycle)
/// </code>
///
/// The mode-status line below the input ("bypass permissions on ...", "plan mode
/// on ...", "accept edits on ...") is a strong anchor — it is rendered only at
/// the very bottom of the active TUI frame. From the most-recent mode-status
/// line we walk up a few rows looking for the input-prompt line — a line whose
/// first non-whitespace, non-box-drawing character is "&gt; " (single &gt; then a
/// space, to disambiguate from the "&gt;&gt; mode" arrow).
///
/// The extractor is a pure function: same input bytes -&gt; same output. The
/// session wingman calls it on a debounce; this class does not poll or
/// schedule anything itself.
///
/// LIMITATION: this is heuristic. Claude Code's prompt rendering can change
/// between versions; if Anthropic switches the mode-status wording or the
/// prompt marker, the extractor will silently start returning null. Treat
/// non-null results as best-effort, and log mismatches loudly in the caller.
/// </summary>
public static class PromptInputLineExtractor
{
    /// <summary>How far back from the mode-status line to look for an input-prompt line.</summary>
    private const int MaxLinesUpFromMode = 10;

    /// <summary>
    /// Substrings (case-insensitive) that mark the mode-status line Claude Code
    /// renders directly below its input box. Any of these is enough to anchor.
    /// Listed in rough order of how often the user encounters them.
    /// </summary>
    private static readonly string[] ModeStatusAnchors =
    {
        "bypass permissions",         // bypass-permissions mode
        "plan mode",                  // /plan mode
        "accept edits",               // /accept-edits mode (covers "auto-accept edits" too)
        "shift+tab to cycle",         // generic mode-cycle hint
        "? for shortcuts",            // help-line hint sometimes shown
    };

    /// <summary>
    /// Box-drawing glyphs and ASCII pipes that Claude Code uses to draw the
    /// input border. We strip these from line edges so a bordered input
    /// like "│ &gt; text │" becomes "&gt; text" before pattern-matching.
    /// </summary>
    private static readonly char[] BorderOrSpace =
    {
        '│','┃','┆','┇','┊','┋','╎','╏','║',
        '╭','╮','╰','╯','┌','┐','└','┘','╔','╗','╚','╝',
        '─','━','═','┄','┅','┈','┉',
        '|',
        ' ','\t','\r',
    };

    /// <summary>
    /// Parse raw terminal-buffer bytes and return the text Claude Code currently
    /// has sitting in its input-prompt line, or <c>null</c> if there is no
    /// detectable Claude Code input frame or the input is empty.
    /// </summary>
    public static string? ExtractClaudeCodeInputLine(byte[]? bufferBytes)
    {
        if (bufferBytes is null || bufferBytes.Length == 0) return null;
        var raw = Encoding.UTF8.GetString(bufferBytes);
        var clean = TerminalOutputParser.StripAnsi(raw);
        return ExtractFromCleanText(clean);
    }

    /// <summary>
    /// Test-friendly overload: parse already-ANSI-stripped terminal text.
    /// </summary>
    internal static string? ExtractFromCleanText(string? cleanText)
    {
        if (string.IsNullOrEmpty(cleanText)) return null;

        var lines = cleanText.Split('\n');

        // Find the most recent mode-status anchor line.
        int modeIdx = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (IsModeStatusLine(lines[i]))
            {
                modeIdx = i;
                break;
            }
        }
        if (modeIdx < 0) return null; // no Claude Code TUI frame visible

        // Walk up a small window above the mode line, looking for the prompt-input row.
        int floor = Math.Max(0, modeIdx - MaxLinesUpFromMode);
        for (int i = modeIdx - 1; i >= floor; i--)
        {
            var extracted = TryExtractInputText(lines[i]);
            if (extracted is not null) return extracted; // may be "" — caller treats "" as empty box
        }

        return null;
    }

    /// <summary>
    /// Grid-aware extraction. Given the resolved terminal grid rows and the live
    /// cursor cell, return the text Claude Code's input box holds that the user
    /// (or Claude Code) actually authored — as opposed to a dim history /
    /// autocomplete SUGGESTION.
    ///
    /// The discriminator is the cursor. Claude Code parks the edit cursor at the
    /// boundary between authored text and the suggestion: everything from the
    /// cursor rightward is the suggestion. So:
    /// <list type="bullet">
    /// <item>cursor at the END of the box text  -> the whole box is the entry;</item>
    /// <item>cursor parked at the START         -> a pure suggestion, authored text is "";</item>
    /// <item>cursor in the middle (partial type)-> authored text is everything up to the cursor;</item>
    /// <item>cursor on another row entirely      -> the box is committed/injected, take the whole text.</item>
    /// </list>
    ///
    /// Returns <c>null</c> when no Claude Code frame is detectable, <c>""</c> for an
    /// empty box or a pure suggestion, or the authored text.
    ///
    /// LIMITATION: a multi-line (wrapped) input parks the cursor on a continuation
    /// row, not the "&gt; " row, so this takes the whole first-line text in that
    /// case. That is acceptable for the prompt-mirroring caller, which only needs
    /// to avoid mistaking a ghost suggestion for a real entry.
    /// </summary>
    public static string? ExtractUserAuthoredInput(string[]? rows, int cursorRow, int cursorCol)
    {
        if (rows is null || rows.Length == 0) return null;

        // Find the most recent mode-status anchor line.
        int modeIdx = -1;
        for (int i = rows.Length - 1; i >= 0; i--)
        {
            if (IsModeStatusLine(rows[i]))
            {
                modeIdx = i;
                break;
            }
        }
        if (modeIdx < 0) return null; // no Claude Code TUI frame visible

        int floor = Math.Max(0, modeIdx - MaxLinesUpFromMode);
        for (int i = modeIdx - 1; i >= floor; i--)
        {
            var located = TryLocateInputText(rows[i]);
            if (located is null) continue;
            var (text, startCol, endCol) = located.Value;

            if (text.Length == 0 || endCol <= startCol)
                return ""; // empty input box

            // Cursor inside this row's text -> keep only what is left of it.
            if (i == cursorRow && cursorCol >= startCol && cursorCol < endCol)
                return rows[i].Substring(startCol, cursorCol - startCol).Trim();

            // Cursor at/after the end, or on a different row -> the whole box is authored.
            return text;
        }

        return null;
    }

    private static bool IsModeStatusLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var lower = line.ToLowerInvariant();
        foreach (var anchor in ModeStatusAnchors)
            if (lower.Contains(anchor)) return true;
        return false;
    }

    /// <summary>
    /// Returns the text content of an input-prompt line ("&gt; text"), or null if
    /// this line is not an input-prompt line. An empty input box ("&gt; " with
    /// nothing after) returns the empty string — callers should treat that as
    /// "no injected text" but distinguish it from "frame not detected".
    /// </summary>
    private static string? TryExtractInputText(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Strip leading/trailing box-drawing characters and whitespace.
        var content = line.Trim(BorderOrSpace);
        if (content.Length == 0) return null;

        // Mode arrows are ">> ..." — reject anything that starts with two angle
        // brackets so we don't confuse a stale mode line with the input prompt.
        if (content.Length >= 2 && content[0] == '>' && content[1] == '>') return null;

        // Real prompt is "> " (single ">" followed by space) or just ">" on its own.
        if (content == ">") return ""; // empty input box
        if (content.Length >= 2 && content[0] == '>' && content[1] == ' ')
        {
            var text = content[2..].Trim();
            return text;
        }
        return null;
    }

    /// <summary>
    /// Locate the input prompt on a single GRID row, returning the trimmed text plus
    /// the grid columns where the text begins and ends (exclusive). Column indices
    /// equal string indices because grid rows are built one cell per column. Returns
    /// null when the row is not an input-prompt line; returns ("", startCol, startCol)
    /// for an empty box ("&gt; " with nothing after it).
    /// </summary>
    private static (string Text, int StartCol, int EndCol)? TryLocateInputText(string row)
    {
        if (string.IsNullOrWhiteSpace(row)) return null;

        // First non-border, non-space column — must be the prompt marker.
        int first = 0;
        while (first < row.Length && IsBorderOrSpace(row[first])) first++;
        if (first >= row.Length || row[first] != '>') return null;

        // ">> ..." is the mode-cycle arrow, not the input prompt.
        if (first + 1 < row.Length && row[first + 1] == '>') return null;

        // Text begins after "> " (single '>' + space); a bare ">" is an empty box.
        int startCol = (first + 1 < row.Length && row[first + 1] == ' ') ? first + 2 : first + 1;

        // Last non-border, non-space column (skips the trailing box edge / padding).
        int last = row.Length - 1;
        while (last >= 0 && IsBorderOrSpace(row[last])) last--;

        if (last < startCol) return ("", startCol, startCol); // empty input box

        var text = row.Substring(startCol, last - startCol + 1).Trim();
        return (text, startCol, last + 1);
    }

    private static bool IsBorderOrSpace(char c) => System.Array.IndexOf(BorderOrSpace, c) >= 0;
}
