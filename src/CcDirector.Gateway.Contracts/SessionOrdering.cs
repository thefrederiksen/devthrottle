namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Shared client-side policy for how a roster of <see cref="SessionDto"/> is ordered and
/// triaged. Lives next to the DTO so every client (Cockpit today, others later) agrees on
/// the rules instead of each re-implementing them, and so the rules are unit-testable
/// without spinning up a UI.
/// </summary>
public static class SessionOrdering
{
    /// <summary>
    /// The stable "desktop order": honor the owning Director's <see cref="SessionDto.SortOrder"/>
    /// (the user-controlled, drag-to-reorder, persisted order), then <see cref="SessionDto.CreatedAt"/>
    /// as a deterministic tie-break. The tie-break is also the only signal when a Director predates
    /// SortOrder (every session reports 0). This is what keeps a session in a fixed slot instead of
    /// reshuffling as its name or activity state changes.
    /// </summary>
    public static IReadOnlyList<SessionDto> InDesktopOrder(IEnumerable<SessionDto> sessions) =>
        sessions.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList();

    /// <summary>Triage priority bucket for the "needs-you-first" view.</summary>
    public enum TriageBucket
    {
        /// <summary>Wants the user now (effective color "red"), and not parked.</summary>
        NeedsYou = 0,
        /// <summary>Anything else that isn't parked.</summary>
        Active = 1,
        /// <summary>Parked by the user or the agent (<see cref="SessionDto.OnHold"/>) - sinks to the bottom.</summary>
        OnHold = 2,
    }

    /// <summary>
    /// True while the session must present as "the wingman is reading": the Gateway's brief
    /// agent has the finished turn queued or in flight (<see cref="SessionDto.BriefingState"/>
    /// "Briefing") AND the raw turn-end color is red. While a NEW turn is already running
    /// (blue) the stale in-flight brief is irrelevant - raw activity wins, no chip.
    /// </summary>
    public static bool IsBriefing(SessionDto s) =>
        s.BriefingState == "Briefing" && string.Equals(s.StatusColor, "red", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ONE effective status color every client renders and triages on (issue #196).
    /// The Director stamps the raw <see cref="SessionDto.StatusColor"/> (it no longer knows
    /// about briefing since #187 moved the pipeline to the Gateway), and the Gateway stamps
    /// <see cref="SessionDto.BriefingState"/> on top. Folding the two HERE - instead of in
    /// each view - is what keeps the dot, the "wingman reading..." chip, and the triage
    /// bucket atomic: while the wingman reads a finished turn the session IS yellow; red
    /// ("needs you") may only appear after the brief lands.
    /// </summary>
    public static string EffectiveColor(SessionDto s) =>
        IsBriefing(s) ? "yellow" : s.StatusColor;

    /// <summary>
    /// Classify a session for triage. On-hold takes precedence over color: a parked session sinks
    /// to the bottom even if it would otherwise be "needs you", because the user has explicitly
    /// deferred it. Uses <see cref="EffectiveColor"/>, NOT the raw Director color: a session the
    /// wingman is still reading stays in Active until the brief lands, instead of flopping into
    /// NEEDS YOU mid-brief and possibly back out (issue #196).
    /// </summary>
    public static TriageBucket Classify(SessionDto s) =>
        s.OnHold ? TriageBucket.OnHold
        : EffectiveColor(s) == "red" ? TriageBucket.NeedsYou
        : TriageBucket.Active;

    /// <summary>All sessions in a given triage bucket, in desktop order.</summary>
    public static IReadOnlyList<SessionDto> InBucket(IEnumerable<SessionDto> sessions, TriageBucket bucket) =>
        InDesktopOrder(sessions.Where(s => Classify(s) == bucket));
}
