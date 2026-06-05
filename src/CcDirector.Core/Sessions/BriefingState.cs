namespace CcDirector.Core.Sessions;

/// <summary>
/// The wingman briefing-pipeline state of a session, ORTHOGONAL to <see cref="ActivityState"/>
/// (TURN_BRIEFING.md / plan DT3): a session can be asking the user AND still working, so
/// briefing progress is its own dimension. The rail derives color from both: detector-red +
/// Briefing -> yellow "briefing..."; detector-red + Briefed -> red with the brief's railLine.
/// </summary>
public enum BriefingState
{
    /// <summary>No brief activity for the current turn.</summary>
    None,

    /// <summary>A turn just ended; the wingman is reading it (the yellow window).</summary>
    Briefing,

    /// <summary>The current turn has a stored brief.</summary>
    Briefed,

    /// <summary>Generation failed and the degrade tiers were exhausted; consumers fall back.</summary>
    Failed,
}
