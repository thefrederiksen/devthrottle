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

    /// <summary>Seats (captured session ids) to start again EVEN THOUGH an earlier start of them may have
    /// landed (inspection 7, ruling 3). Only for a seat whose earlier start left a token and no restored id,
    /// after the caller has checked the session list. Every other rule still applies.</summary>
    public List<string>? ForceSeats { get; set; }
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

    /// <summary>See <see cref="WorkspaceRestoreRequest.ForceSeats"/>.</summary>
    public List<string>? ForceSeats { get; set; }

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

/// <summary>The kinds of <see cref="WorkspaceRestoreMark"/>.</summary>
public static class WorkspaceRestoreMarkKinds
{
    /// <summary>About to send the create for a seat: its token, stored BEFORE the create leaves.</summary>
    public const string Started = "started";

    /// <summary>The seat came back as <see cref="WorkspaceRestoreMark.RestoredSessionId"/>.</summary>
    public const string Restored = "restored";

    /// <summary>The seat did not come back; <see cref="WorkspaceRestoreMark.Failure"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>The run is over: the lease is given back.</summary>
    public const string Finished = "finished";

    /// <summary>
    /// THE SEAT'S SAVED CONVERSATION WAS REOPENED (the Smart Director Restart mission, product issue 3230).
    /// Written onto <see cref="WorkspaceSeatRestore.ReopenedAtUtc"/> and its two companions, so a seat that
    /// ended without a handover is never offered twice - across a Director restart as well as within one run.
    ///
    /// IT IS THE ONE KIND THAT NEEDS NO RESTORE LEASE, and that is deliberate rather than an omission.
    /// Reopening a saved conversation is not a restore: it starts one session, reads no ordering, and there is
    /// nothing for two Directors to interleave - while the lease is only ever granted by asking for a restore,
    /// so a reopen that needed one could not be recorded at all. What it must not do is cut across a restore
    /// that IS running, so a reopened mark is refused while ANOTHER Director holds a live lease; it never
    /// grants, renews or releases one.
    ///
    /// AN OLDER GATEWAY DOES NOT KNOW IT and refuses it by name (HTTP 400, "kind must be one of: ..."), which
    /// is the answer we want: the reopen is claimed on the record before anything is started, so a Gateway
    /// that cannot record the claim starts nothing and says so.
    /// </summary>
    public const string Reopened = "reopened";

    /// <summary>
    /// THE OWNER ASKED NOT TO BE OFFERED THIS RECORD AT START-UP AGAIN (the Smart Director Restart mission,
    /// the owner's ruling of 20 September 2026). Written onto
    /// <see cref="WorkspaceDocument.ClearedFromStartUpOfferAtUtc"/> and its companion, so the Director stops
    /// putting the record in front of him every time it starts.
    ///
    /// IT NAMES NO SEAT, because it is a fact about the whole record and not about one session, and it is the
    /// second kind that needs no restore lease, for the reason <see cref="Reopened"/> gives: the lease is
    /// granted only by asking for a restore, and clearing is not a restore - it starts nothing, brings nothing
    /// back and deletes nothing. What it must not do is cut across a restore that IS running, so it is refused
    /// while ANOTHER Director holds a live lease, and it never grants, renews or releases one.
    ///
    /// CLEARING A RECORD THAT IS ALREADY CLEARED CHANGES NOTHING AND IS NOT AN ERROR. The first clearing's
    /// moment and Director stand, and the answer is success - unlike a second <see cref="Reopened"/>, which is
    /// refused because it would put a second agent into one saved conversation. Here there is no such harm:
    /// the owner asked for the record to stop appearing, and after either call it has.
    ///
    /// AN OLDER GATEWAY DOES NOT KNOW IT and refuses it by name (HTTP 400, "kind must be one of: ..."), which
    /// is the answer we want: the Director says the record could not be marked and nothing is hidden, rather
    /// than telling him it will not ask again when it will.
    /// </summary>
    public const string Cleared = "cleared";

    /// <summary>Every kind.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Started, Restored, Failed, Finished, Reopened, Cleared };
}

/// <summary>
/// RESTORE MARKS ARE PROVENANCE, NEVER JUDGMENT (inspection 7, ruling 1). What a restore did to a seat - the
/// restored session id, the failure, the attempt time, the start token - is written ONLY through
/// <c>POST /gateway/workspaces/{id}/restore/marks</c>, by the Director holding the workspace's restore lease, on
/// its own credential. An ordinary write of the workspace keeps the stored copy of every one of these fields,
/// exactly as it keeps the seat facts the capture observed. That matters because the restored id of an owner
/// seat is the OWNER the Director names for its workers: a field any writer could set would let any writer
/// choose who owns a restored worker.
/// </summary>
public sealed class WorkspaceRestoreMark
{
    /// <summary>The Director writing the mark. It must hold the workspace's restore lease.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>One of <see cref="WorkspaceRestoreMarkKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The seat (captured session id). Required for every kind but "finished".</summary>
    public string? SeatSessionId { get; set; }

    /// <summary>"started": the token the create will carry. "restored": the token of that start - a restored mark
    /// without the token its own Director stored is refused.</summary>
    public string? Token { get; set; }

    /// <summary>"restored": the new session's id.</summary>
    public string? RestoredSessionId { get; set; }

    /// <summary>"reopened": the session the saved conversation was reopened as, when it is known. Left null on
    /// the mark that CLAIMS the reopen before the create is sent; the same Director fills it in afterwards.</summary>
    public string? ReopenedSessionId { get; set; }

    /// <summary>"restored": the seed file the new session was pointed at, if one was given.</summary>
    public string? SeedFile { get; set; }

    /// <summary>"failed": why, in plain words.</summary>
    public string? Failure { get; set; }

    /// <summary>"failed": the Gateway refused the create outright, so nothing was started and the start token
    /// is cleared. False (a timeout, an unreadable answer) keeps the token: the seat MAY have been started.</summary>
    public bool NothingStarted { get; set; }

    /// <summary>"started": the session that asked for the restore, or null for the owner - recorded as
    /// <see cref="WorkspaceDocument.RestoredBy"/>.</summary>
    public string? RequestedBySessionId { get; set; }
}

/// <summary>
/// THE RESTORE ASKS FOR A SEAT'S DEV REPORTS TO PASS TO THE SESSION IT CAME BACK AS (the Smart Director Restart
/// mission, section 5.3 item 13), through <c>POST /gateway/workspaces/{id}/restore/dev-reports</c>, on the
/// Director's own credential, while it holds the workspace's restore lease.
///
/// IT NAMES A SEAT AND NOTHING ELSE. The old session id and the new one are both read by the Gateway from the
/// stored seat (<see cref="WorkspaceSeat.SessionId"/> and <see cref="WorkspaceSeat.RestoredSessionId"/>), which
/// only a capture and a token-checked restore mark can write. A caller that could name either id could hand any
/// session's reports to any other, so there is deliberately no field for them.
/// </summary>
public sealed class WorkspaceDevReportPassRequest
{
    /// <summary>The Director asking. It must hold the workspace's restore lease.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The seat (captured session id) whose reports pass to the session it was restored as.</summary>
    public string SeatSessionId { get; set; } = "";
}

/// <summary>What a dev report pass did (<c>POST /gateway/workspaces/{id}/restore/dev-reports</c>).</summary>
public sealed class WorkspaceDevReportPassResult
{
    /// <summary>The session the reports belonged to - the seat's captured session.</summary>
    public string FromSessionId { get; set; } = "";

    /// <summary>The session they belong to now - what the seat was restored as.</summary>
    public string ToSessionId { get; set; } = "";

    /// <summary>How many reports passed. Zero when the old session had none, or when this was already asked.</summary>
    public int Passed { get; set; }

    /// <summary>The keys of reports that did NOT pass, because the restored session had already published a report
    /// under the same key before this was asked. Those stay with the old session, frozen, exactly as before.</summary>
    public List<string> KeptBecauseTheNewSessionAlreadyHasTheKey { get; set; } = new();
}

/// <summary>
/// Carried on a restore's create (<see cref="NewSessionRequest.RestoreClaim"/>): which workspace seat this
/// session is, and the token the Director stored on that seat before sending it. The Gateway that performs the
/// create writes the new session id onto the seat when the token matches, so the record does not depend on the
/// answer getting back to the Director (inspection 7, ruling 3). Accepted only from a Director's credential.
/// </summary>
public sealed class WorkspaceRestoreClaim
{
    /// <summary>The workspace.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>The seat (captured session id).</summary>
    public string SeatSessionId { get; set; } = "";

    /// <summary>The token stored on the seat as <see cref="WorkspaceSeatRestore.StartedToken"/>.</summary>
    public string Token { get; set; } = "";
}
