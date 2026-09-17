namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE RESTORE IS A DIRECTOR ACT (the Message Load mission, slice 6; owner decision 2, 17 September 2026).
///
/// A drained seat used to come back by a SESSION running a <c>cc-devthrottle session spawn ... --controlled-by
/// &lt;the controller's id&gt;</c> line written into the drain record. Since a session key may name only itself
/// or the user as the owner of what it starts, the Gateway refuses that line for every seat whose owner is
/// somebody else - which is nearly every restored Worker. The pin stays; the restore moved instead.
///
/// Now the restoring session (or the owner) asks the Gateway to restore a captured workspace onto a Director:
/// <c>POST /gateway/workspaces/{id}/restore</c>. The Gateway relays <see cref="WorkspaceRestoreVerbs.Restore"/>
/// to that Director, and the DIRECTOR performs every spawn itself, through
/// <c>POST /directors/{its own id}/sessions</c> on its own credential - the arm the Gateway already trusts to
/// name owners. The owners it names are not the caller's to choose: they are the seat facts the Gateway
/// OBSERVED at capture time (<see cref="WorkspaceSeat.ReportsTo"/>), which no write to the workspace can
/// change.
/// </summary>
public static class WorkspaceRestoreVerbs
{
    /// <summary>The tunnel verb that asks a Director to restore a captured workspace onto itself. Answered as
    /// soon as the restore is TAKEN: the Director's own spawns ride back through the Gateway to this same
    /// Director, so answering only when they finish would wait on itself.</summary>
    public const string Restore = "workspace-restore";
}

/// <summary>The body of <c>POST /gateway/workspaces/{id}/restore</c>.</summary>
public sealed class WorkspaceRestoreRequest
{
    /// <summary>The Director to restore onto - after a restart, the NEW Director's id. Required.</summary>
    public string? DirectorId { get; set; }

    /// <summary>Only these seats (their captured session ids), or null for every seat decided "restore" that
    /// has not come back yet. The Director still restores them seniors first.</summary>
    public List<string>? Seats { get; set; }

    /// <summary>A seed file per seat (captured session id to path), for a restoring session that wrote the
    /// "what changed while you were gone" file the director-restart skill asks for. A seat with no entry is
    /// seeded from its own handover.</summary>
    public Dictionary<string, string>? Seeds { get; set; }
}

/// <summary>
/// What the Gateway sends down the tunnel with <see cref="WorkspaceRestoreVerbs.Restore"/>. The same three
/// fields the caller sent, plus WHO ASKED - stamped by the Gateway from the verified credential, never read
/// from the body.
/// </summary>
public sealed class WorkspaceRestoreOrder
{
    /// <summary>The workspace to restore from.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>See <see cref="WorkspaceRestoreRequest.Seats"/>.</summary>
    public List<string>? Seats { get; set; }

    /// <summary>See <see cref="WorkspaceRestoreRequest.Seeds"/>.</summary>
    public Dictionary<string, string>? Seeds { get; set; }

    /// <summary>The session whose key asked, or null when the owner's own credential asked.</summary>
    public string? RequestedBySessionId { get; set; }
}

/// <summary>What the Gateway answers once the Director has taken the restore.</summary>
public sealed class WorkspaceRestoreAccepted
{
    /// <summary>Always true on a 202: the Director has taken the restore and is running it.</summary>
    public bool Taken { get; set; }

    /// <summary>The workspace being restored.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>The Director doing it.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The captured session ids the Director was asked to bring back, in no particular order.
    /// Each one's result is written onto its seat in the workspace: <see cref="WorkspaceSeat.RestoredSessionId"/>
    /// on success, <see cref="WorkspaceSeatRestore.Failure"/> otherwise, both with
    /// <see cref="WorkspaceSeatRestore.AttemptedAtUtc"/>.</summary>
    public List<string> Seats { get; set; } = new();
}
