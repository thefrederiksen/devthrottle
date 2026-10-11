using System.Text;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The License Switching mission, trap 4 (contract rule 6.3): Claude Code's usage-limit dialog. When a session hits its
/// session, weekly or monthly spend limit, Claude Code opens a menu - "What do you want to do?", first option "Stop and
/// wait for limit to reset" - and another of its options turns on PAID extra usage. An Enter, or a digit, typed into
/// it by anything automatic could spend money nobody agreed to. The only key the Director ever sends it is Escape.
///
/// Recognised from REAL captured screens (the saved turn packages), which come in several shapes: rows intact, rows
/// run together, spaces dropped ("2. Addfundstocontinuewithusage credits"), and box-drawing and status characters in
/// between. So each row is SQUASHED - lower case, letters and digits only - and the check asks for words that survive
/// every one of those shapes, in the dialog's own SHAPE:
///
///   - the cancel hint, "Esc to cancel", in the lower half of the screen, where Claude Code draws an open menu; and
///   - the numbered first option, "1. Stop and wait for limit to reset", before it and at most
///     <see cref="OptionRowsAboveHint"/> rows above it, read across row breaks.
///
/// That is the CERTAIN check, and the only one the Director answers with Escape. The input guard blocks on a wider
/// one, <see cref="MayBeShowing"/>, so a dialog drawn in a shape nobody has captured yet still gets no keystroke.
///
/// The shape is what keeps a session that merely SHOWS these words - an agent reading this file, or quoting the
/// dialog in a report - from being taken for one stuck in it: there the words are not a numbered option sitting on top
/// of a cancel hint at the bottom of the screen. That matters, because the Director answers the dialog with Escape.
///
/// It deliberately does NOT key on the paid options' wording: those are the parts that change between versions and
/// plans ("Add funds to continue with usage credits", "Switch to extra usage", "Switch to Team plan"), and the dialog is
/// just as dangerous whichever of them it offers.
/// </summary>
public static class UsageLimitDialog
{
    /// <summary>How far above the cancel hint the first option may start. The real screens measured 0 to 3.</summary>
    public const int OptionRowsAboveHint = 6;

    private const string CancelHint = "esctocancel";
    private const string ConfirmHint = "entertoconfirm";
    private const string FirstOption = "1stopandwaitforlimittoreset";

    /// <summary>
    /// Words that belong to a usage limit. Any one of them inside an open menu at the bottom of the screen is enough
    /// for <see cref="MayBeShowing"/>: a reworded option, a reordered menu or one cut short still carries some.
    /// </summary>
    private static readonly string[] LimitWords =
    {
        "stopandwait", "limittoreset", "usagecredits", "extrausage", "addfunds", "ratelimitoptions",
        "spendlimit", "sessionlimit", "weeklylimit", "usagelimit", "hityourlimit",
    };

    /// <summary>
    /// True when the screen CERTAINLY shows the usage-limit dialog open and waiting: the numbered first option, then
    /// the cancel hint, in the lower half of the screen, the option starting at most <see cref="OptionRowsAboveHint"/>
    /// rows above the end of the hint. The rows are read JOINED, so an option or hint wrapped over two rows of a narrow
    /// terminal still matches (review of pull request 3863, finding 2). This is what the Director answers with Escape.
    /// </summary>
    public static bool IsShowing(IReadOnlyList<string> visibleRows)
    {
        var rows = SquashedRows(visibleRows);
        for (var end = rows.Count - 1; end >= rows.Count / 2; end--)
        {
            var window = string.Concat(rows.Skip(Math.Max(0, end - OptionRowsAboveHint)).Take(end - Math.Max(0, end - OptionRowsAboveHint) + 1));
            var option = window.IndexOf(FirstOption, StringComparison.Ordinal);
            if (option >= 0 && window.IndexOf(CancelHint, option + FirstOption.Length, StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the screen MAY show the dialog: an open menu (its cancel or confirm hint) in the lower half of the
    /// screen, with a word of a usage limit INSIDE THAT MENU. The input guard blocks on this, because a dialog the
    /// certain check does not recognise - a changed label, a changed order, an option cut short - must not let a
    /// keystroke through: when the screen looks like the dialog and is not recognised with certainty, the guard leans
    /// to blocking (finding 2).
    ///
    /// The limit words must belong to the same menu as the hint (<see cref="MenuText"/>), not merely sit near it: an
    /// ordinary permission prompt under a line that happens to say "usage limit" is still answered (fix-round review of
    /// pull request 3863).
    /// </summary>
    public static bool MayBeShowing(IReadOnlyList<string> visibleRows)
    {
        ArgumentNullException.ThrowIfNull(visibleRows);
        var rows = visibleRows.Where(r => Squash(r).Length > 0).ToList();
        var squashed = rows.Select(Squash).ToList();
        for (var hint = rows.Count - 1; hint >= rows.Count / 2; hint--)
        {
            if (!squashed[hint].Contains(CancelHint, StringComparison.Ordinal) && !squashed[hint].Contains(ConfirmHint, StringComparison.Ordinal))
                continue;
            var menu = MenuText(rows, squashed, hint);
            if (LimitWords.Any(w => menu.Contains(w, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>
    /// The menu a hint row closes, squashed and joined: from the menu's question ("What do you want to do?", "Do you
    /// want to proceed?" - the nearest row with a question mark, at most <see cref="OptionRowsAboveHint"/> rows up) down
    /// to the hint. Text above the question is output the menu was drawn under, not part of it. When no question is in
    /// reach the menu's top is not known, and every row in reach counts - leaning to blocking.
    /// </summary>
    private static string MenuText(IReadOnlyList<string> rows, IReadOnlyList<string> squashed, int hint)
    {
        var reach = Math.Max(0, hint - OptionRowsAboveHint);
        var top = reach;
        for (var i = hint; i >= reach; i--)
        {
            if (!rows[i].Contains('?', StringComparison.Ordinal)) continue;
            top = i;
            break;
        }
        return string.Concat(squashed.Skip(top).Take(hint - top + 1));
    }

    private static List<string> SquashedRows(IReadOnlyList<string> visibleRows)
    {
        ArgumentNullException.ThrowIfNull(visibleRows);
        return visibleRows.Select(Squash).Where(r => r.Length > 0).ToList();
    }

    /// <summary>The text in lower case with everything but ASCII letters and digits removed.</summary>
    internal static string Squash(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append((char)(c + 32));
        }
        return sb.ToString();
    }
}
