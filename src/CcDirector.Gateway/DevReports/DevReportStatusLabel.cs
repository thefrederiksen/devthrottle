namespace CcDirector.Gateway.DevReports;

/// <summary>
/// A report's status in words. The status itself is a code from the report's header
/// (<see cref="DevReportShapeCheck.Statuses"/>); a screen shows these words beside the title and never the code (rule 7).
/// A contributor reads no word about agents (Team implementation 4.1), so the one status that names the agent has a
/// contributor's wording.
/// </summary>
public static class DevReportStatusLabel
{
    internal const string WaitingOnYou = "Waiting on you";
    internal const string AgentWorking = "Agent working";
    internal const string BeingWorkedOn = "Being worked on";
    internal const string Done = "Done";

    /// <summary>The words for <paramref name="status"/>, for a reader who is a contributor or not. A status that is not
    /// one of the three a published report can carry is a defect upstream, and throws.</summary>
    public static string For(string status, bool contributor) => status switch
    {
        "waiting-on-you" => WaitingOnYou,
        "agent-working" => contributor ? BeingWorkedOn : AgentWorking,
        "done" => Done,
        _ => throw new InvalidOperationException(
            $"A report carries the status '{status}', which is not one of {string.Join(", ", DevReportShapeCheck.Statuses)}."),
    };
}
