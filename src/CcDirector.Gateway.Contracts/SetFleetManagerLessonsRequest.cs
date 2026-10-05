namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The payload of the <c>set-fleet-manager-lessons</c> command (issue #3559): the Gateway telling a Director the
/// confirmed lessons its marked Fleet Manager session must be given whenever its context starts again - a compaction
/// or a clear - through the session-start hook.
///
/// A FACT being delivered, already finished. The Gateway writes the block (its <c>FleetManagerLessons</c>); the
/// Director stores it verbatim on the session and puts it, unchanged, in front of the preamble it maintains. It never
/// reads, filters or rewrites the lessons.
/// </summary>
public sealed class SetFleetManagerLessonsRequest
{
    /// <summary>The finished lessons block, or null/empty to clear it - the session is no longer the Fleet Manager, or
    /// the account holds no confirmed lesson.</summary>
    public string? Lessons { get; set; }
}
