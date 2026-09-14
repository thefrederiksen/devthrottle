using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Wingman;

/// <summary>
/// String constants for the session status colour NAMES. This is a vocabulary, not a machine: the names
/// are shared with the Gateway's fold, which is the only thing that picks one.
///
/// WHAT THE WINGMAN ACTUALLY WRITES: blue, red, unknown. That is all, and it has been all since phase 2.3.
/// This comment used to say these were "the session status colors the SessionStatusWingman writes onto each
/// Session" and to describe green / yellow / purple / supporting as live wingman output, citing
/// "SessionStatusWingman.ColorFor - the single source of truth". That method does not exist (renamed in
/// phase 2.3), the wingman is not the single writer (two other paths write StatusColor), and every colour
/// below except blue/red/unknown is folded at the GATEWAY from raw facts on the wire. The single source of
/// truth is <c>SessionOrdering</c>, and the specification is docs/new_architecture/sessions.html.
///
/// The vocabulary, and who decides each one:
///   blue    = the agent is WORKING. Written here by the wingman's activity map; folded at the Gateway
///             from ActivityState. NOTHING OUTRANKS IT - see the law in the specification.
///   red     = needs the user (silent past the quiet threshold). Written here; folded there.
///   unknown = process exited, or the source is unreachable. Written here; the Gateway folds Exited to
///             "grey" instead, deliberately - it is the single source of truth for the fold.
///   green   = brand-new, never took a turn. GATEWAY ONLY, from SessionDto.IsBrandNew.
///   yellow  = the wingman is reading the finished turn, or a voice summary is being prepared.
///             GATEWAY ONLY.
///   purple  = parked on its OWN background task. GATEWAY ONLY, from SessionDto.IsBackgroundRunning.
///   supporting = a controlled sub-agent whose controller is STILL ALIVE (issue #815). GATEWAY ONLY - it
///             needs the whole fleet to know the controller lives.
///
/// READ THIS BEFORE YOU TRUST ANY OLDER DESCRIPTION OF "supporting". This comment used to end that entry
/// with: "Honored only while the controlling session is still alive; a red 'needs you' still breaks through
/// (red &gt; supporting &gt; the rest)." BOTH halves are wrong, in opposite directions, and it is the most
/// dangerous sentence this file has carried:
///   - Red does NOT break through. The suppression fires PRECISELY when the session is red - that is its
///     whole job: a live-controlled Worker's need routes to its manager instead of nagging the human
///     (SessionOrdering, the Worker arm). The escape hatch is the controller DYING, not the red.
///   - It also implied slate outranks blue ("supporting > the rest"). It does not, and must not. The
///     owner's law, 14 July 2026: IF A SESSION IS WORKING, IT IS BLUE. NO MATTER WHAT. The 2026-07-10
///     decision that a controlled worker always recedes (#1286) is VOIDED and must not be restored or
///     cited as precedent - it is what hid 23 minutes of real work behind a grey "Sub-agent" dot.
/// A sentence describing a system that never existed, sitting next to the constants, is how that bug kept
/// coming back. Ownership travels on the role badge; colour says what a session is DOING.
///
/// On-hold is NOT one of these: it is a separate fact (Session.HoldState) the Gateway folds to grey.
///
/// NOTE: <see cref="From"/> is the older turn-summary mapping and is used only by tests now.
/// </summary>
public static class StatusColor
{
    public const string Red = "red";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Blue = "blue";
    public const string Purple = "purple";

    /// <summary>The session's agent process ended unexpectedly - a crash: a non-zero exit, or the
    /// process dropping out while it was actively working. The row is kept in this Error state so the
    /// user sees that work stopped instead of the session silently disappearing (issue #959).
    /// Rendered as a deep red (<c>#B91C1C</c>), deliberately distinct from the bright "red" that
    /// means "needs you".</summary>
    public const string Error = "error";

    /// <summary>The session is receiving a dictated message: the Speak dialog released the screen and
    /// the recorded audio is being transcribed and submitted in the background. An overlay on top of
    /// whatever the session is doing (it wins over every activity colour), so the operator - and
    /// anyone else on the fleet - can see the session is busy and does not start typing into it
    /// mid-dictation. Cleared the moment the transcript is submitted or the attempt fails. Rendered as
    /// <c>#F97316</c> by every client. On-hold still wins over it (a parked session stays grey).</summary>
    public const string Orange = "orange";

    /// <summary>A controlled sub-agent (issue #815): another session spawned and drives it, so it
    /// recedes to a muted slate. Painted only while its controller is alive; red "needs you" wins
    /// over it. Rendered as <c>#64748B</c> by every client.</summary>
    public const string Supporting = "supporting";

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
