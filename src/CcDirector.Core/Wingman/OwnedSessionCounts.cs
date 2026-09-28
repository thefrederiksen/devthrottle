namespace CcDirector.Core.Wingman;

/// <summary>
/// How the sessions a session owns stand at a stop, at EVERY level (a Manager's Workers are an Architect's too).
/// Carried on the turn-verdict package, where Call A's code steps read it (the simpler session colours ruling,
/// 2026-09-28): a stop with nothing running under it, or with everything under it stopped, needs its owner.
///
/// <see cref="Working"/> is the ONE definition of working - the blue row, <c>SessionOrdering.IsWorkingSession</c> -
/// and deliberately NOT the crew line's "working" bucket, which counts every row that is neither red nor snoozed, so
/// an exited or a calm child would read there as still running. <see cref="NeedYou"/> is a child that is not
/// working and sits in the needs-you bucket; <see cref="Stopped"/> is every other child.
/// </summary>
public sealed record OwnedSessionCounts(int Working, int Stopped, int NeedYou)
{
    /// <summary>How many sessions are under it, at every level. Not stored: it is the sum of the three.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Total => Working + Stopped + NeedYou;
}
