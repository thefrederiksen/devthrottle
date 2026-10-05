namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The owner's lessons and standing preferences as the Fleet Manager page draws them (issue #3559): the "That was a
/// mistake" action, then one list with the lessons first and the preferences second, each row with its date, who kept
/// it, whether it is confirmed, and its Confirm, Edit and Remove. Folded on the Gateway; the page renders it as sent
/// (CLAUDE.md rule 7). Answered by <c>GET /gateway/fleet-manager/standing</c>, the owner's alone.
/// </summary>
public sealed class FleetStandingDto
{
    /// <summary>"That was a mistake": opens an empty box for the owner's correction. Not offered while the account
    /// already holds the most confirmed lessons it may, with the reason in its note.</summary>
    public FleetManagerActionDto Mistake { get; set; } = new();

    /// <summary>The box's heading.</summary>
    public string BoxTitle { get; set; } = "";

    /// <summary>The line under the box's heading: what keeping the words does.</summary>
    public string BoxNote { get; set; } = "";

    /// <summary>The box's placeholder.</summary>
    public string BoxPlaceholder { get; set; } = "";

    /// <summary>The most characters a lesson may hold; the box allows no more.</summary>
    public int MaxLessonLength { get; set; }

    /// <summary>The button that keeps the owner's words, and its busy line.</summary>
    public string KeepLabel { get; set; } = "";
    public string KeepBusyLabel { get; set; } = "";

    /// <summary>The line shown after a lesson was kept.</summary>
    public string KeptSentence { get; set; } = "";

    /// <summary>The edit box's save button, its busy line, and the cancel button every box shares.</summary>
    public string SaveLabel { get; set; } = "";
    public string SaveBusyLabel { get; set; } = "";
    public string CancelLabel { get; set; } = "";

    /// <summary>The button that shows the list, with its count, and the one that hides it.</summary>
    public string ShowLabel { get; set; } = "";
    public string HideLabel { get; set; } = "";

    /// <summary>"1 lesson waits for you to confirm it.", or null when none waits. Shown beside the show button.</summary>
    public string? WaitingNote { get; set; }

    public FleetStandingSectionDto Lessons { get; set; } = new();

    public FleetStandingSectionDto Preferences { get; set; } = new();
}

/// <summary>One part of the list: its heading, count, rows, and the sentence when it is empty.</summary>
public sealed class FleetStandingSectionDto
{
    public string Title { get; set; } = "";

    public int Count { get; set; }

    /// <summary>A line under the heading saying how the rows are used, or null.</summary>
    public string? Note { get; set; }

    /// <summary>Shown when there are no rows; null otherwise.</summary>
    public string? EmptyText { get; set; }

    public List<FleetStandingRowDto> Rows { get; set; } = new();
}

/// <summary>One lesson or preference: the owner's words verbatim, and what can be done with it.</summary>
public sealed class FleetStandingRowDto
{
    public const string ToneConfirmed = "confirmed";
    public const string ToneWaiting = "waiting";
    public const string TonePreference = "preference";

    public string Id { get; set; } = "";

    /// <summary><c>lesson</c> or <c>preference</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The words, exactly as kept.</summary>
    public string Text { get; set; } = "";

    /// <summary>A lesson's one line saying what went wrong, written as shown ("What went wrong: ..."), or null.</summary>
    public string? MistakeLine { get; set; }

    /// <summary>"Kept 5 October 2026 by you", in the account's time zone.</summary>
    public string KeptLine { get; set; } = "";

    /// <summary>The most characters this row's words may hold; the edit box allows no more.</summary>
    public int MaxLength { get; set; }

    /// <summary>What removing it is, in the owner's terms ("remove this lesson") - the words a failed removal is
    /// reported with.</summary>
    public string RemoveAction { get; set; } = "";

    /// <summary>A lesson's confirmation, as shown, or null for a preference.</summary>
    public string? StatusLine { get; set; }

    /// <summary>The row's edge colour: one of the Tone constants.</summary>
    public string Tone { get; set; } = "";

    /// <summary>One press confirms a lesson the Fleet Manager kept. Offered only on such a lesson.</summary>
    public FleetManagerActionDto Confirm { get; set; } = new();

    /// <summary>Rewrite the words in a box prefilled with them.</summary>
    public FleetManagerActionDto Edit { get; set; } = new();

    /// <summary>Remove the row, after one question.</summary>
    public FleetManagerActionDto Remove { get; set; } = new();
}
