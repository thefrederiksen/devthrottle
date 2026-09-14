namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A Mission: the named unit of work a pod of sessions is collectively chartered to accomplish
/// (see docs/new_architecture/fleet.html). A Mission is its OWN
/// persisted record - not merely an attachment field on a session - so it survives a Manager
/// restart and later anchors the cockpit map. Sessions ATTACH to a Mission by its
/// <see cref="MissionId"/>; that attachment (not the spawn tree) is what binds a pod together.
///
/// Role cardinality (one Architect, one Manager, N Workers) is enforced by the derived role model
/// (see <see cref="SessionRoles"/>); the Mission record deliberately does NOT store role seats.
///
/// MISSIONS ARE FLAT - there is no parent link and no tree. Nesting was specified, built and tested, then
/// never used once; it was removed on 2026-08-07. See <see cref="Core.Sessions.Mission"/> and the design
/// document for the reasoning.
/// </summary>
public sealed class MissionDto
{
    /// <summary>Stable identity of the Mission, minted at creation. This is the value sessions attach by.</summary>
    public Guid MissionId { get; set; }

    /// <summary>Human-friendly name of the Mission (e.g. "Session Lifecycle").</summary>
    public string MissionName { get; set; } = "";

    /// <summary>WHY this mission exists, in the owner's own words. Empty means UNSET, and the client shows
    /// its "no why set" flag rather than a blank. Keyed to the mission by <see cref="MissionId"/> - it used
    /// to live in a separate table keyed by the mission's lower-cased NAME, which a rename would have
    /// silently orphaned.</summary>
    public string Why { get; set; } = "";

    /// <summary>When the WHY was last set (UTC), or null if it has never been set.</summary>
    public DateTimeOffset? WhyUpdatedAt { get; set; }

    /// <summary>"active", "complete", or "removed". Older Gateways send nothing, which reads as active.</summary>
    public string State { get; set; } = "active";

    /// <summary>When the state last changed (UTC), or null while it has only ever been active.</summary>
    public DateTimeOffset? StateChangedAt { get; set; }

    /// <summary>ADDITIVE (Workflows mission, phase 4, issue #1771): the workflow run opened beside
    /// this Mission when it was created through the Gateway - a mission is a run of the built-in
    /// "mission" workflow. Null on reads that do not resolve it and on missions predating the spine.
    /// Existing clients ignore it.</summary>
    public Guid? WorkflowRunId { get; set; }
}

/// <summary>
/// Body of POST /missions on a Director's Control API: create a new Mission record.
/// </summary>
public sealed class NewMissionRequest
{
    /// <summary>Required. The Mission's human-friendly name. A blank name is rejected with HTTP 400.</summary>
    public string? MissionName { get; set; }
}

/// <summary>
/// Body of PATCH /missions/{mid}: change something about a Mission that already exists.
///
/// Only the fields present are changed. Today that is just the WHY; Phase 2 adds the display name and the
/// mission's state (complete / removed) as further optional fields on this same body, which is why this is
/// a PATCH rather than a route per verb.
/// </summary>
public sealed class MissionPatchRequest
{
    /// <summary>The mission's WHY. An empty or whitespace value CLEARS it, returning the card to its "no
    /// why set" flag. Null means "do not change the why".</summary>
    public string? Why { get; set; }

    /// <summary>The mission's display name. Null means "do not rename"; blank is REJECTED rather than
    /// treated as a clear, because a mission with no name cannot be referred to.</summary>
    public string? MissionName { get; set; }

    /// <summary>"active", "complete", or "removed". Null means "do not change the state". Setting "active"
    /// on an ended mission REOPENS it, which is the way back from a mistaken ending.</summary>
    public string? State { get; set; }
}

/// <summary>
/// The result of PATCH /missions/{mid}: the updated Mission, plus anything the caller needs TOLD that the
/// mission record alone does not say.
/// </summary>
public sealed class MissionPatchResultDto
{
    /// <summary>The mission after the change.</summary>
    public MissionDto? Mission { get; set; }

    /// <summary>
    /// A plain sentence about something that happened alongside the change and that the caller cannot see
    /// from the mission itself - a workflow run that could not be advanced, or an ending applied while
    /// sessions were still attached. Null when the outcome speaks for itself.
    ///
    /// Returned rather than left to be inferred, for the same reason MissionAttachResultDto returns its
    /// seat note: only the Gateway knows what happened to the run, and a caller told nothing would report a
    /// clean ending it has no basis for.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>How many sessions were still attached when the mission was ended. Zero otherwise.</summary>
    public int AttachedSessionCount { get; set; }
}

/// <summary>
/// Body of POST /sessions/{id}/mission: attach a session that ALREADY EXISTS to a Mission (issue #2387).
/// <see cref="MissionId"/> is the Mission the session attaches to; a null/absent value DETACHES the session
/// (clears its attachment), mirroring how <see cref="SetRoleRequest"/> clears an explicit role.
///
/// Attaching is a MOVE, not a one-way door: a session that already carries a Mission is re-pointed by the
/// same call. A mission's shape is discovered rather than planned - that is what makes it a mission - so the
/// first classification of a session is a guess, and a one-way attach would make every wrong guess permanent
/// until the session was killed. The attachment rules are written up in
/// docs/new_architecture/fleet.html.
/// </summary>
public sealed class SetMissionRequest
{
    public Guid? MissionId { get; set; }

    /// <summary>
    /// The Mission's display name, RESOLVED BY THE GATEWAY against its own tenant-scoped store and cached
    /// onto the session so a client can render the attachment without a second lookup. Present on the
    /// Gateway path (the end state, exactly like <c>NewSessionRequest.MissionName</c> at spawn); blank on
    /// the transitional bridge, where the Director resolves the name from its own local store instead.
    ///
    /// A Director never TRUSTS this to decide whether the attachment is allowed - the Gateway has already
    /// resolved the mission inside the caller's own tenant before sending it, and that resolution is the
    /// authorization. This field only saves the Director a lookup it cannot do correctly anyway (its local
    /// store is not the fleet's).
    /// </summary>
    public string? MissionName { get; set; }

    /// <summary>
    /// True when this call must also move the session's WORKFLOW SEAT, and false when the seat must be left
    /// exactly as it is. The Gateway decides which - see the seat block on the attach route - and the
    /// Director obeys; it has no way to decide correctly, because whether a run belongs to a mission is a
    /// fact only the Gateway's run store holds.
    ///
    /// WHY THE SEAT IS PART OF THIS CALL AT ALL. A Mission is not only a record: it is also a RUN of the
    /// built-in "mission" workflow, and a mission-scoped spawn seats the session on that run, which is what
    /// pins the conduct the agent was told to follow. Moving the mission link alone would show a session
    /// under one mission while it was GOVERNED by the one it left - taking its conduct from the mission it
    /// is no longer in. That is worse than an inconsistent label, and it would happen in exactly the case
    /// this feature was built for.
    /// </summary>
    public bool MoveSeat { get; set; }

    /// <summary>The workflow run to seat the session on when <see cref="MoveSeat"/> is true. Null clears the
    /// seat - a mission with no run of its own (an ungoverned mission), or a detach.</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>The seated run's workflow id, Gateway-resolved. Ignored unless <see cref="MoveSeat"/>.</summary>
    public string? WorkflowId { get; set; }

    /// <summary>The seated run's PINNED version, Gateway-resolved. Ignored unless <see cref="MoveSeat"/>.</summary>
    public int? WorkflowVersion { get; set; }
}

/// <summary>
/// The result of POST /sessions/{sid}/mission: the updated session, plus WHAT HAPPENED TO ITS WORKFLOW
/// SEAT.
///
/// The seat outcome is returned rather than left to be inferred, and that is deliberate. Only the Gateway
/// knows the seat the session held before the call and whether that seat belonged to the mission it left,
/// so only the Gateway can say whether the seat moved. A Director asked to work it out for a session it
/// does not host would be comparing against a value it never had, and would report a preserved seat as a
/// moved one - stating a fact it is not in a position to know.
/// </summary>
public sealed class MissionAttachResultDto
{
    /// <summary>The session as the owning Director reports it after the change.</summary>
    public SessionDto? Session { get; set; }

    /// <summary>True when the workflow seat was moved or cleared by this call; false when it was left alone.</summary>
    public bool SeatMoved { get; set; }

    /// <summary>The workflow run the session was seated on BEFORE the call, or null if it was seated on nothing.</summary>
    public Guid? PreviousWorkflowRunId { get; set; }

    /// <summary>
    /// A plain sentence about the seat when there is something the caller needs told - a seat preserved
    /// because it was never the mission's, or a destination mission that seats nobody. Null when the
    /// outcome speaks for itself.
    /// </summary>
    public string? SeatNote { get; set; }
}
