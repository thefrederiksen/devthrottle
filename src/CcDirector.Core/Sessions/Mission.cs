namespace CcDirector.Core.Sessions;

/// <summary>
/// The states a Mission can be in. Stored as a string rather than an enum so an unrecognised value from a
/// newer writer round-trips instead of deserialising to whatever happens to be zero.
///
/// There are exactly three, and the two endings are DIFFERENT ENDINGS rather than one with a flag:
/// COMPLETE means the work finished and is worth keeping - "what did we ship in July" is a real question.
/// REMOVED means the mission should not have existed - a duplicate, a mistake, an abandoned idea. It is
/// not an outcome, and lumping it in with completed work would quietly corrupt the answer to that question.
/// </summary>
public static class MissionStates
{
    /// <summary>Live work. The ordinary state, and what a mission is created in.</summary>
    public const string Active = "active";

    /// <summary>The work finished. Kept forever; out of the default view.</summary>
    public const string Complete = "complete";

    /// <summary>This should not exist. Soft-deleted: the record stays, out of every default view.</summary>
    public const string Removed = "removed";

    /// <summary>The states that are an ENDING - a mission in one of these is no longer live work.</summary>
    public static readonly string[] Ended = { Complete, Removed };

    /// <summary>Normalize a caller-supplied state, or null when it is not one of the three.</summary>
    public static string? Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v == Active || v == Complete || v == Removed ? v : null;
    }
}

/// <summary>
/// A Mission: the named unit of work a pod of sessions is collectively chartered to accomplish
/// (see docs/new_architecture/fleet.html). A Mission is its OWN persisted
/// record - not merely an attachment field on a session - so it survives a Director/Manager restart and
/// later anchors the cockpit map. Sessions ATTACH to a Mission by its <see cref="MissionId"/>.
///
/// Role cardinality (one Architect, one Manager, N Workers) is enforced by the derived role model, so a
/// Mission deliberately stores NO role seats - only its identity and name.
///
/// MISSIONS ARE FLAT. Nesting (a parent link, making a tree of Missions) was specified in the design
/// document, built, and tested - and then never used once across every Mission this fleet created. It was
/// removed on 2026-08-07 rather than carried indefinitely: an unused field still has to be understood by
/// everyone who reads this type, kept correct in every store and route that touches it, and reasoned about
/// by every feature added afterwards. If a real case for sub-Missions turns up, add it back deliberately
/// then - the design document records the original reasoning and the removal.
/// </summary>
public sealed class Mission
{
    /// <summary>Stable identity of the Mission, minted at creation. Sessions attach by this value.</summary>
    public Guid MissionId { get; set; }

    /// <summary>Human-friendly name of the Mission (e.g. "Session Lifecycle").</summary>
    public string MissionName { get; set; } = string.Empty;

    /// <summary>
    /// WHY this mission exists, in the owner's own words - shown front and center on its card, because a
    /// mission with no stated reason is a red flag the screen makes obvious rather than a silent blank.
    /// Empty means UNSET, and the card shows its flag; there is no separate "has a why" boolean.
    ///
    /// This lives ON THE MISSION, keyed by <see cref="MissionId"/> like everything else. It used to live in
    /// its own <c>mission_notes</c> table keyed by the mission's LOWER-CASED NAME, which meant the WHY was
    /// attached to a string rather than to a mission: two missions sharing a name shared one WHY, and
    /// renaming a mission would have orphaned it silently - the card simply falling back to "no why set".
    /// That is why this moved here BEFORE rename was built rather than after.
    /// </summary>
    public string Why { get; set; } = string.Empty;

    /// <summary>When <see cref="Why"/> was last set (UTC), or null if it has never been set.</summary>
    public DateTimeOffset? WhyUpdatedAt { get; set; }

    /// <summary>
    /// One of <see cref="MissionStates"/>. Defaults to Active, which is also what a record written before
    /// this field existed reads as - every mission that predates it was, by definition, never ended.
    ///
    /// A mission can be ended while sessions are still attached to it, and that is deliberate. Refusing
    /// would make ending hardest exactly when a mission has sprawled, which is when it most needs ending,
    /// and a single idle-but-alive session would block it for no reason. The contradiction is shown rather
    /// than prevented - see the Cockpit's mission card.
    /// </summary>
    public string State { get; set; } = MissionStates.Active;

    /// <summary>When <see cref="State"/> last changed (UTC), or null while it has only ever been Active.</summary>
    public DateTimeOffset? StateChangedAt { get; set; }

    /// <summary>True when this mission is still live work - the ordinary case, and what the default view shows.</summary>
    public bool IsActive => MissionStates.Normalize(State) is null or MissionStates.Active;

    /// <summary>UTC timestamp the Mission was created. Used for stable list ordering.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The tenant that owns this Mission, stamped at creation from the CALLER's authenticated identity -
    /// never from anything a client sends. Every read is filtered on it, so a mission is only ever served to
    /// the tenant that wrote it.
    ///
    /// Null means UNATTRIBUTED: a record written before missions carried an owner. On a single-tenant
    /// install there is exactly one tenant by construction, so <see cref="MissionStore"/> may be told to
    /// adopt those rows as that tenant; where more than one tenant shares the store, an unattributed row
    /// belongs to nobody and is served to nobody. See <see cref="MissionStore"/> for the rule.
    /// </summary>
    public string? TenantId { get; set; }
}
