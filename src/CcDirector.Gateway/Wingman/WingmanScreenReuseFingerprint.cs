using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// A deliberately narrower identity for deciding whether a stopped screen needs to be judged again.
/// <see cref="WingmanScreenVerdictCache.HashRows"/> remains the exact, full-grid identity used to refuse a stale
/// answer. This one removes only terminal presentation that can repaint without changing what the agent said:
/// whitespace and line wrapping, the visible input-composer row, and known bottom-of-screen footer chrome.
/// </summary>
internal static class WingmanScreenReuseFingerprint
{
    private const string Version = "screen-reuse-v1:";
    private const int FooterRegionRows = 6;

    public static string Hash(ScreenGridResponse? grid)
    {
        if (grid is not { HasGrid: true, Rows.Count: > 0 }) return "";

        var canonical = Canonical(grid.Rows, grid.CursorRow, grid.CursorVisible, grid.IsAlternateScreen);
        // A screen made entirely of composer/footer chrome has no stable content to reuse. Falling back to the
        // exact grid keeps two unrelated empty-looking screens from becoming the same stop.
        if (canonical.Length == 0) return Version + WingmanScreenVerdictCache.HashRows(grid.Rows);

        return Version + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Canonical(
        IReadOnlyList<string> rows,
        int cursorRow,
        bool cursorVisible,
        bool alternateScreen)
    {
        var content = new StringBuilder();
        var footerStart = Math.Max(0, rows.Count - FooterRegionRows);

        for (var i = 0; i < rows.Count; i++)
        {
            var compact = WithoutWhitespace(rows[i]);
            if (compact.Length == 0) continue;

            var inFooter = i >= footerStart;
            // The hardware cursor in the normal buffer sits in the text composer. Menus use the alternate screen
            // and draw their own selection marker, so their row remains part of the identity.
            if (inFooter && !alternateScreen && cursorVisible && i == cursorRow && IsComposer(compact)) continue;
            if (inFooter && (IsKnownFooter(compact) || IsDecoration(compact))) continue;

            // No separator on purpose: wrapping moves a word between rows without changing its characters.
            content.Append(compact);
        }

        return content.ToString();
    }

    private static string WithoutWhitespace(string row)
    {
        var compact = new StringBuilder(row.Length);
        foreach (var c in row)
            if (!char.IsWhiteSpace(c)) compact.Append(c);
        return compact.ToString();
    }

    private static bool IsKnownFooter(string compact)
    {
        var lower = compact.ToLowerInvariant();
        if (lower.StartsWith("⏵⏵", StringComparison.Ordinal)
            && lower.EndsWith("(shift+tabtocycle)", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("newtask?/clear", StringComparison.Ordinal)
            && lower.EndsWith("tokens", StringComparison.Ordinal)) return true;

        const string compactHint = "%untilauto-compact";
        var hintStart = lower.IndexOf(compactHint, StringComparison.Ordinal);
        return hintStart > 0
               && hintStart + compactHint.Length == lower.Length
               && lower.AsSpan(0, hintStart).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsComposer(string compact)
        => compact.StartsWith('❯') || compact.StartsWith('›');

    private static bool IsDecoration(string compact)
    {
        foreach (var c in compact)
            if (c is not ('-' or '─' or '━' or '═' or '│' or '┃' or '┌' or '┐' or '└' or '┘'
                or '├' or '┤' or '┬' or '┴' or '┼' or '╭' or '╮' or '╯' or '╰'))
                return false;
        return true;
    }
}
