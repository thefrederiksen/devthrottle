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
///   - the numbered first option, "1. Stop and wait for limit to reset", on that row or at most
///     <see cref="OptionRowsAboveHint"/> rows above it.
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
    /// <summary>How far above the cancel hint the first option may be. The real screens measured 0 to 3.</summary>
    public const int OptionRowsAboveHint = 6;

    private const string CancelHint = "esctocancel";
    private const string FirstOption = "1stopandwaitforlimittoreset";

    /// <summary>True when the visible screen shows the usage-limit dialog open and waiting.</summary>
    public static bool IsShowing(IReadOnlyList<string> visibleRows)
    {
        ArgumentNullException.ThrowIfNull(visibleRows);
        var rows = visibleRows.Select(Squash).Where(r => r.Length > 0).ToList();
        for (var hint = rows.Count - 1; hint >= rows.Count / 2; hint--)
        {
            if (!rows[hint].Contains(CancelHint, StringComparison.Ordinal)) continue;
            for (var option = hint; option >= Math.Max(0, hint - OptionRowsAboveHint); option--)
                if (rows[option].Contains(FirstOption, StringComparison.Ordinal))
                    return true;
        }
        return false;
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
