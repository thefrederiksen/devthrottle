using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// String constants for the session status colors the SessionStatusWingman writes
/// onto each <see cref="CcDirector.Core.Sessions.Session"/>. The UI renders these
/// verbatim and never derives them from other fields.
///
/// Live meaning (see SessionStatusWingman.ColorFor - the single source of truth):
///   blue    = agent is working / a turn is in progress.
///   green   = ready for the user - parked at the prompt with nothing needed. Currently set
///             only on a brand-new session (before its first turn); the Wingman will reuse it
///             for its own "ready" verdicts later.
///   red     = needs the user (waiting for input/permission, idle at a turn-end).
///   yellow  = the Wingman is reading the screen and narrating a briefing.
///   purple  = the Wingman read the screen and determined the session is parked on its OWN
///             background task (a long build, "N shell still running") and will resume on
///             its own - so it does NOT need the user. An overlay on top of a red turn-end.
///   unknown = process exited, or the data source is unreachable/unparseable.
///             Rendered as gray. NOT a session state per se.
///
/// In practice the TerminalStateDetector only emits Working / WaitingForInput, so the
/// activity-state badge is just blue or red; yellow and purple are Wingman overlays.
///
/// On-hold is NOT one of these: it is a separate, user-driven override (Session.OnHold)
/// painted by the UI (light gray), not a color the wingman writes here.
///
/// NOTE: <see cref="Green"/> is emitted by <c>SessionStatusWingman.ColorFor</c> for a
/// brand-new "ready" session (parked at its prompt, nothing needed). <see cref="From"/>
/// is the older turn-summary mapping and is used only by tests now.
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
