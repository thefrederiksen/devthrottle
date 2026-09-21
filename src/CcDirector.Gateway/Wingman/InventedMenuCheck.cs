using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE JUDGE DOES NOT GET TO INVENT A PICKER (issue 2976). The verdict judge answered plain prose questions as menus:
/// it said the person answers with keys, wrote a menu question and options, and the screen showed no picker at all.
/// An invented menu tells a listener to press a button on the phone for a question they could have answered by
/// speaking, and the button does not exist - the answer route re-reads the screen and refuses to press anything.
///
/// CODE DECIDES WHETHER A PICKER IS DRAWN, and the judge's claim is checked against it
/// (<see cref="PickerOnScreen"/>, owner ruling 2026-09-20). The judge is told that answer in its prompt rather than
/// asked for it, and this check is what makes the answer binding: a menu with options on a screen where no picker is
/// drawn is removed before the record is stored.
///
/// WHAT THE PREVIOUS VERSION COULD NOT DO. It asked whether every option label the judge wrote appeared somewhere on
/// the screen, and prose labels always do - it corrected 12 of the corpus's 32 invented menus and called the other 20
/// a limit no screen check could pass. That was a limit of reading LABELS. Reading the picker's own furniture - its
/// footer, its selected row - separates all 32 from real pickers. The measurement, both ways, is in the private
/// repository at docs/missions/wingman-picker-flag-2026-09-19/.
///
/// WHAT IS NEVER CORRECTED:
///   - an UNREAD screen. No rows means the screen could not be read, not that there is no picker; a real picker whose
///     grid failed to arrive keeps its buttons.
///   - a menu with NO options. That is the typed-but-unsent confirm the prompt asks for - the composer holds text the
///     person already typed, and the one button sends it. No picker is drawn for it, by definition.
///
/// WHY CORRECT AND NOT REFUSE. Refusing throws the whole verdict away - colour, label, receipt and all - over one
/// field. The judge's account of what the session is doing is not in doubt; only its claim about a picker is. The
/// reason travels on the record (<see cref="TurnVerdictDto.OptionsDroppedReason"/>) so every correction is answerable
/// by query, not only by reading a log.
/// </summary>
public static class InventedMenuCheck
{
    /// <summary>
    /// Correct, in place, a keys answer with options on a screen where no picker is drawn. True when this record was
    /// corrected. <paramref name="reading"/> is what the screen showed, for the caller to log whether or not anything
    /// changed.
    /// </summary>
    /// <param name="verdict">The parsed record, or a salvaged decision read for the narration call.</param>
    /// <param name="screenRows">The screen the verdict was formed on. Empty or null means it was not read.</param>
    /// <param name="agentKind">The agent the session runs, as the package names it.</param>
    /// <param name="reading">The picker reading of that screen.</param>
    public static bool Correct(TurnVerdictDto? verdict, IReadOnlyList<string>? screenRows, string? agentKind,
        out PickerReading reading)
    {
        reading = PickerOnScreen.Read(screenRows, agentKind);
        if (verdict is null) return false;
        if (!string.Equals(verdict.AnswerVia, TurnVerdictContract.AnswerViaKeys, StringComparison.Ordinal)) return false;
        if (!reading.ScreenRead || reading.Drawn) return false;
        if (verdict.Options is null || verdict.Options.Count == 0) return false;

        verdict.AnswerVia = TurnVerdictContract.AnswerViaReply;
        verdict.Menu = null;
        verdict.Options = new List<TurnVerdictOptionDto>();
        verdict.OptionsDroppedReason = "the judge offered a menu and no picker is drawn on the screen: " + reading.Describe();
        return true;
    }
}
