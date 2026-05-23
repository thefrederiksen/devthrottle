namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Result of the Wingman's goal-management check for one session. Produced by
/// <c>WingmanService.AssessGoalAsync</c>: given the session's stated goal and its
/// recent turn summaries, a one-shot Haiku call judges whether the session is
/// still working toward the goal, has drifted, or has completed it.
///
/// This is observational only. The Wingman surfaces the assessment; it does not
/// (in this slice) change the status color or send anything to the session.
///
/// On any failure (no claude CLI, parse error, timeout) the state is
/// <see cref="GoalStates.Unknown"/> with a reason describing what happened. We never
/// fabricate an on_track / drifting / complete verdict when the call did not produce one.
/// </summary>
public sealed class GoalAssessment
{
    /// <summary>One of <see cref="GoalStates"/>: on_track | drifting | complete | unknown.</summary>
    public string State { get; set; } = GoalStates.Unknown;

    /// <summary>One short sentence explaining the verdict, in plain language.</summary>
    public string Reason { get; set; } = "";

    /// <summary>UTC time the assessment was produced.</summary>
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Body for <c>POST /sessions/{sid}/wingman/goal</c>. Sets the session's stated
/// goal. An empty/null <see cref="Goal"/> clears it and stops goal-tracking.
/// </summary>
public sealed class WingmanGoalRequest
{
    public string? Goal { get; set; }
}

/// <summary>The four valid <see cref="GoalAssessment.State"/> values.</summary>
public static class GoalStates
{
    public const string OnTrack = "on_track";
    public const string Drifting = "drifting";
    public const string Complete = "complete";
    public const string Unknown = "unknown";

    public static bool IsValid(string? s) =>
        s is OnTrack or Drifting or Complete or Unknown;
}
