using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE JUDGE DOES NOT GET TO INVENT A PICKER (issue 2976). The verdict judge sometimes answers a plain prose
/// question as a menu: it says the person answers with keys, writes a menu question and options, and the screen
/// shows no picker at all. On the corpus of 381 gradable turns that was 32 stops on the shipped prompt.
///
/// It was always wrong and it is now LOUD, because the narration call reads the judge's decision: an invented menu
/// tells a listener to press a button on the phone for a question they could have answered by speaking, and the
/// button does not exist - the answer route re-reads the screen and refuses to press anything.
///
/// THE SCREEN DECIDES, and it decides on the judge's OWN OPTION LABELS:
/// <see cref="WingmanMenuLogic.MenuHasAnswerableOptions"/>, the same test the send-time guards use, which asks
/// whether every label the judge wrote is actually on the live grid. A menu whose labels are not on the screen
/// cannot be pressed even if the person tries - the answer route re-reads the screen and refuses - so correcting
/// it to a reply takes away nothing that worked. When it is corrected the menu and the options go with it, because
/// a keys option carries the bytes that SELECT it (a "1", an arrow) and typing those into a composer as if they
/// were words is worse than offering nothing.
///
/// IT IS DELIBERATELY NOT <see cref="WingmanMenuLogic.LiveScreenHasMenuSelection"/>, which the first version of
/// this check used and which cost a live defect on 2026-09-17. That one asks for a DRAWN MARKER ON A NUMBERED
/// OPTION ROW - the shape Claude Code's Ink picker usually draws, but not the only shape a real picker takes.
/// Measured over the corpus's 35 keys stops it stripped the buttons off two of the three GENUINE pickers in it:
/// the folder-trust prompt, whose options read "❯ No, exit" / "Yes, I trust this folder" with no numbers at all,
/// and the feedback survey, which puts "1: Bad  2: Fine  3: Good" on ONE line with the marker on another. Taking
/// the buttons off a real picker is worse than leaving an invented menu, so this check corrects only what it can
/// positively show the screen does not offer.
///
/// WHAT IT CATCHES, MEASURED. Over those same 35 stops the label check keeps all 3 real pickers and corrects 12
/// of the 32 invented menus. The other 20 invent labels that DO appear somewhere in the agent's prose, so no
/// screen check can tell them from a picker; those belong to the judge's prompt, not here.
///
/// WHY CORRECT AND NOT REFUSE. Refusing would throw the whole verdict away - colour, label, receipt and all - over
/// one field, and a red row with no reading is worse for the owner than a right reading with no buttons. The judge's
/// account of what the session is doing is not in doubt; only its claim about a picker is.
///
/// AN UNREAD SCREEN CORRECTS NOTHING. No rows means the screen could not be read, not that there is no menu, and
/// the two must never share a code path: a real picker whose grid failed to arrive keeps its buttons.
/// </summary>
public static class InventedMenuCheck
{
    /// <summary>
    /// Correct a keys answer the screen does not support, in place. True when this record was corrected - the
    /// caller logs it and says so in its trace.
    /// </summary>
    /// <param name="verdict">The parsed record, or a salvaged decision read for the narration call.</param>
    /// <param name="screenRows">The screen the verdict was formed on. Empty or null means it was not read.</param>
    public static bool Correct(TurnVerdictDto? verdict, IReadOnlyList<string>? screenRows)
    {
        if (verdict is null) return false;
        if (!string.Equals(verdict.AnswerVia, "keys", StringComparison.Ordinal)) return false;
        if (screenRows is null || screenRows.Count == 0) return false;
        if (WingmanMenuLogic.MenuHasAnswerableOptions(AsMenu(verdict.Options), screenRows)) return false;

        verdict.AnswerVia = "reply";
        verdict.Menu = null;
        verdict.Options = new List<TurnVerdictOptionDto>();
        return true;
    }

    /// <summary>The record's options in the shape the send-time guard reads. Only the label matters here.</summary>
    private static WingmanMenu AsMenu(IReadOnlyList<TurnVerdictOptionDto>? options)
    {
        var menu = new WingmanMenu { IsMenu = true };
        if (options is null) return menu;
        foreach (var o in options)
            menu.Options.Add(new WingmanMenuOption { Key = o.Key ?? "", Send = o.Send ?? "" });
        return menu;
    }
}
