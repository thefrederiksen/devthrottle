namespace CcDirector.Gateway.DevReports;

/// <summary>
/// THE OWNER'S OWN ANSWER (Report Answers by Voice). The note-taking script adds one last option to every question in
/// a dev report, "Something else"; picking it lets the owner answer in their own words instead of with one of the
/// author's options. It is an answer of its own kind, recorded as such:
/// <list type="bullet">
/// <item>its <c>optionValue</c> is the reserved <see cref="Value"/>, which no report may use for an option of its own -
/// <see cref="DevReportShapeCheck"/> refuses a report that does, so the two can never be confused;</item>
/// <item>the owner's words travel as the item's <c>ownAnswer</c> and are stored in the item's <c>Text</c>, which an
/// answer otherwise leaves empty;</item>
/// <item>its label is always <see cref="Label"/>, whatever a page sends - the Gateway decides it once (repository rule 7).</item>
/// </list>
/// A team member's own answer takes the team path's privacy rule: its words go to the person who sent the report, never
/// into the session (<see cref="DevReportPersonComments"/>), so the choice item it becomes carries no words.
/// </summary>
internal static class DevReportOwnAnswer
{
    /// <summary>The reserved option value of an own answer.</summary>
    public const string Value = "own-answer";

    /// <summary>The label of the option the owner picked, as the page draws it and every reader is shown it.</summary>
    public const string Label = "Something else";

    /// <summary>How an agent reading the answers is told this is not one of its options (<c>cc-dev-reports answers</c>).</summary>
    public const string ReaderLabel = "OWN ANSWER (not one of your options)";

    /// <summary>True when <paramref name="optionValue"/> is the reserved own-answer value.</summary>
    public static bool Is(string optionValue) => string.Equals(optionValue, Value, StringComparison.Ordinal);
}
