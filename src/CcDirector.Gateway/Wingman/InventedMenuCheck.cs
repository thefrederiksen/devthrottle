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
/// THE SCREEN DECIDES, and the check is the same one the send-time guards use:
/// <see cref="WingmanMenuLogic.LiveScreenHasMenuSelection"/>, a DRAWN selection marker on an option row. When the
/// screen was read and carries no such marker, a keys answer is corrected to a reply: the menu and the options go
/// with it, because a keys option carries the bytes that SELECT it (a "1", an arrow) and typing those into a
/// composer as if they were words is worse than offering nothing.
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
        if (WingmanMenuLogic.LiveScreenHasMenuSelection(screenRows)) return false;

        verdict.AnswerVia = "reply";
        verdict.Menu = null;
        verdict.Options = new List<TurnVerdictOptionDto>();
        return true;
    }
}
