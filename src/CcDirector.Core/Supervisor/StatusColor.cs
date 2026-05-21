using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Supervisor;

/// <summary>
/// Phase 4 of the SessionSupervisor goal.  Maps the latest TurnSummary's
/// <c>needs_user</c> field (plus a few other signals) to a single red /
/// yellow / green / unknown colour for the cards UI on the Manager dashboard.
/// </summary>
public static class StatusColor
{
    public const string Red = "red";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Unknown = "unknown";

    /// <summary>
    /// Compute a status colour from the latest TurnSummary plus optional extra
    /// signals (git dirty, supervisor warnings, etc - set up for Phases 5/6).
    /// </summary>
    public static string From(TurnSummary? latestSummary, bool gitDirty = false, bool hasWarnings = false)
    {
        if (latestSummary is null) return Unknown;
        var n = (latestSummary.NeedsUser ?? "").Trim().ToLowerInvariant();
        if (n is "question" or "error" or "permission") return Red;
        if (hasWarnings) return Yellow;
        if (n == "idle" && gitDirty) return Yellow;
        return Green;
    }
}
