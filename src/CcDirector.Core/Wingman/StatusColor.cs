using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// String constants for the five session status colors the SessionStatusWingman
/// writes onto each <see cref="CcDirector.Core.Sessions.Session"/>. The UI renders
/// these verbatim and never derives them from other fields.
///
/// Meaning:
///   green   = "greenfield" - brand new, or just finished a task cleanly, sitting idle.
///   blue    = agent is working / a turn is in progress.
///   yellow  = soft warning (idle with uncommitted work, soft rule violation).
///   red     = hard, needs the user (waiting for input/permission, error, blocked).
///   purple  = the session looks idle to the dumb timer (red), but the Wingman has read the
///             screen and determined it is actually parked on its OWN background task (a long
///             build, "N shell still running") and will resume on its own - so it does NOT
///             need the user. A Wingman-set overlay that sits on top of a red turn-end.
///   unknown = data-quality state - the data source itself is unreachable or unparseable.
///             Rendered as gray. NOT a session state per se.
///
/// Phase 3 of the SessionWingman goal makes color a first-class, wingman-owned
/// field on the Session itself. The static helper below is kept as an internal
/// utility for the wingman's slow path (turn-summary interpretation).
/// </summary>
public static class StatusColor
{
    public const string Red = "red";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Blue = "blue";
    public const string Purple = "purple";
    public const string Unknown = "unknown";

    /// <summary>
    /// Map a completed turn's <see cref="TurnSummary"/> to a color decision. Used by
    /// the wingman's slow path AFTER a turn finishes. The caller (the wingman)
    /// is responsible for stamping the chosen color back onto the Session.
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

/// <summary>
/// How confident a particular <c>SetStatusColor</c> write is, used to arbitrate
/// between the multiple paths that can set a session's color (issue #136, option C).
/// Higher values win. The rule (enforced in <c>Session.SetStatusColor</c>): within a
/// single activity-state generation a <see cref="PositiveEvidence"/> verdict is
/// sticky -- a lower-confidence write cannot repaint over it. A real activity-state
/// change releases the stickiness. This replaces blind last-writer-wins, which let
/// a cosmetic byte-burst or a re-evaluated mapping flip a genuine "needs you" badge.
/// </summary>
public enum StatusColorSource
{
    /// <summary>A guess inferred from the raw byte stream (e.g. the output-activity
    /// watcher promoting to blue on a burst). Lowest confidence.</summary>
    Inferred = 0,

    /// <summary>Mapped from the authoritative <c>ActivityState</c> (the fast path,
    /// or the terminal LLM state verdict). The normal baseline.</summary>
    ActivityState = 1,

    /// <summary>Backed by deterministic on-screen evidence the user must act: a
    /// matched question/confirmation marker, a permission box, or a corroborated
    /// turn-summary "needs user" verdict. Highest confidence.</summary>
    PositiveEvidence = 2,
}
