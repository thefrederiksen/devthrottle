using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workspaces;

/// <summary>A workspace write lost a race - the id is already taken by a create. Maps to HTTP 409.</summary>
public sealed class WorkspaceConflictException : Exception
{
    /// <summary>Create the exception with the message the caller is shown verbatim.</summary>
    public WorkspaceConflictException(string message) : base(message) { }
}

/// <summary>
/// The Gateway-owned store of workspaces (issue #2722, Phase 3 of #2719): a named set of seats, authored
/// by hand or captured from a running Director, kept where the machine it describes cannot take it down.
///
/// This REPLACES the Director-local workspace files. Those lived under the Director's own configuration
/// directory, which means they were readable only while that machine was up and editable only at that
/// keyboard - and the one moment a workspace is worth having is the moment the machine has just been
/// restarted out from under its fleet.
///
/// Criticality: FAIL-LOUD, not quarantine. A workspace is the record somebody acts on when the sessions
/// it describes no longer exist; a write that silently did not happen means a fleet nobody can bring
/// back. So a failed write throws with the reason named rather than returning false and continuing.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context, and a create does its existence check and its insert INSIDE that one lock - a
/// check followed by a write is two operations however carefully they are written.
/// </summary>
public sealed class WorkspaceStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>
    /// How the document is written to and read from the JSON column. camelCase so the stored document
    /// reads the same as the hand-written index it was taken from, and so anyone reading the column
    /// directly sees the field names the skill and the API use.
    /// </summary>
    public static readonly JsonSerializerOptions DocumentJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public WorkspaceStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Create or replace a workspace, the way an ordinary write does it.
    ///
    /// THE CAPTURE HEADER IS NOT WRITABLE THROUGH HERE. When the row already exists, where it came from -
    /// origin, machine, Director id, Director name, the version it was on, and when the capture started -
    /// is taken from the STORED document and the body's values for those fields are ignored. And a
    /// workspace can never be CREATED here claiming to be captured.
    ///
    /// That is the whole point of capture being a separate verb. Those facts are ones the Gateway holds
    /// firsthand, and this document is read after the sessions are gone, when nobody can check; a write
    /// path that let a caller assemble them would let anyone mint a record of a fleet that never ran, or
    /// rewrite one that did. What a caller MAY write onto a captured workspace is everything a person
    /// decides afterwards: the handover paths, the drain states, the restore decisions, and what the
    /// restart actually produced.
    ///
    /// <see cref="WorkspaceDocument.CreatedUtc"/> is preserved from the existing row and
    /// <see cref="WorkspaceDocument.UpdatedUtc"/> is stamped here - a caller cannot backdate either.
    ///
    /// LAST WRITE WINS, deliberately and with its limit stated: there is no revision check, so two
    /// writers editing one workspace can overwrite each other's judgments. Two drains at once on one
    /// Director is a race with no winner and the lock that settles it belongs where the drain runs
    /// (Phase 4 of issue #2719), not here.
    /// </summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="nowUtc">The write time. Injected so tests are deterministic.</param>
    /// <returns>The stored document, with the store's own timestamps and provenance on it.</returns>
    /// <exception cref="WorkspaceValidationException">The document breaks a content rule.</exception>
    public WorkspaceDocument Save(WorkspaceDocument doc, DateTime nowUtc)
        => Write(doc, nowUtc, createOnly: false, trustedCapture: false);

    /// <summary>
    /// Create a workspace that must not already exist, with its capture header written as given. Used by
    /// the capture verb, which is the only caller allowed to state where a workspace came from.
    ///
    /// Create-only because silently replacing an existing record would destroy a drain somebody is
    /// halfway through.
    /// </summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="nowUtc">The write time.</param>
    /// <returns>The stored document.</returns>
    /// <exception cref="WorkspaceConflictException">A workspace with that id already exists.</exception>
    public WorkspaceDocument Create(WorkspaceDocument doc, DateTime nowUtc)
        => Write(doc, nowUtc, createOnly: true, trustedCapture: true);

    /// <summary>
    /// Store an AUTHORED workspace that must not already exist - the create-only write, for a caller who
    /// intends to add one and not to replace whatever happens to be there.
    ///
    /// It exists because "list, check the id is absent, then write" is three operations with two gaps,
    /// and anything created in either gap is silently overwritten. That is not hypothetical here: it is
    /// exactly how the one-time legacy import could destroy a workspace somebody had created a moment
    /// earlier and then archive the file it came from. One atomic create closes it.
    /// </summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="nowUtc">The write time.</param>
    /// <exception cref="WorkspaceConflictException">A workspace with that id already exists.</exception>
    public WorkspaceDocument CreateAuthored(WorkspaceDocument doc, DateTime nowUtc)
        => Write(doc, nowUtc, createOnly: true, trustedCapture: false);

    private WorkspaceDocument Write(WorkspaceDocument doc, DateTime nowUtc, bool createOnly, bool trustedCapture)
    {
        ArgumentNullException.ThrowIfNull(doc);

        // The id is checked first, outside everything: it is the key of the row about to be read, so
        // there is nothing sensible to do before it is known to be one.
        WorkspaceValidation.ValidateId(doc.Id);

        var at = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var existing = ctx.Workspaces.FirstOrDefault(e => e.Id == doc.Id);

            if (existing is not null && createOnly)
                throw new WorkspaceConflictException(
                    $"A workspace with id \"{doc.Id}\" already exists. Read it, or choose another id - " +
                    "replacing it would destroy whatever it already records.");

            if (!trustedCapture)
            {
                if (existing is null) RefuseForgedCapture(doc);
                else ApplyStoredProvenance(doc, existing);
            }

            // VALIDATION RUNS HERE, after the stored facts have been put back, and not before. Those
            // fields are documented as ignored on an update, so validating the INCOMING copy of them
            // would refuse a legitimate write for a value the store was about to overwrite anyway - an
            // update that simply omitted directorId would be a 400 about a field the caller cannot set.
            WorkspaceValidation.Validate(doc);

            doc.CreatedUtc = existing?.CreatedUtc ?? at;
            doc.UpdatedUtc = at;

            var json = JsonSerializer.Serialize(doc, DocumentJsonOptions);

            if (existing is null)
            {
                ctx.Workspaces.Add(new WorkspaceEntity
                {
                    Id = doc.Id,
                    TenantId = ctx.ActiveTenant!,
                    Name = doc.Name,
                    Origin = doc.Origin,
                    Machine = doc.Machine,
                    DirectorId = doc.DirectorId,
                    DirectorName = doc.DirectorName,
                    Description = doc.Description,
                    SeatCount = doc.Seats.Count,
                    CreatedUtc = doc.CreatedUtc,
                    UpdatedUtc = doc.UpdatedUtc,
                    DocumentJson = json,
                });
            }
            else
            {
                existing.Name = doc.Name;
                existing.Origin = doc.Origin;
                existing.Machine = doc.Machine;
                existing.DirectorId = doc.DirectorId;
                existing.DirectorName = doc.DirectorName;
                existing.Description = doc.Description;
                existing.SeatCount = doc.Seats.Count;
                existing.UpdatedUtc = doc.UpdatedUtc;
                existing.DocumentJson = json;
            }

            ctx.SaveChanges();
            FileLog.Write(
                $"[WorkspaceStore] Write: id={doc.Id}, origin={doc.Origin}, seats={doc.Seats.Count}, " +
                $"director={doc.DirectorOutcome ?? "none"}, created={existing is null}, capture={trustedCapture}");
            return doc;
        }
    }

    /// <summary>
    /// Refuse a CREATE, through the ordinary path, that carries anything only a capture may say.
    ///
    /// Refusing the origin alone was not enough. A document created as "authored" while carrying a
    /// machine, a Director and a start time keeps those values for ever, because every later write
    /// restores them from what is stored - so a caller could mint a fleet record that never ran, in two
    /// steps, and then not be able to clear it. An authored workspace belongs to no machine, so it says
    /// nothing about one.
    /// </summary>
    private static void RefuseForgedCapture(WorkspaceDocument doc)
    {
        if (doc.Origin == WorkspaceOrigins.Captured)
            throw new WorkspaceValidationException(
                "A captured workspace can only be created by capturing a Director " +
                "(POST /gateway/workspaces). Store an authored workspace instead, or capture one.");

        var offending = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.Machine)) offending.Add("machine");
        if (!string.IsNullOrWhiteSpace(doc.DirectorId)) offending.Add("directorId");
        if (!string.IsNullOrWhiteSpace(doc.DirectorName)) offending.Add("directorName");
        if (!string.IsNullOrWhiteSpace(doc.DirectorVersionBefore)) offending.Add("directorVersionBefore");
        if (!string.IsNullOrWhiteSpace(doc.DirectorVersionAfter)) offending.Add("directorVersionAfter");
        if (doc.StartedAtUtc is not null) offending.Add("startedAtUtc");

        if (offending.Count > 0)
            throw new WorkspaceValidationException(
                $"An authored workspace belongs to no machine, so it cannot carry {string.Join(", ", offending)}. " +
                "Those are written by capturing a Director (POST /gateway/workspaces) and by nothing else.");
    }

    /// <summary>
    /// Put back everything the CAPTURE owns, so an ordinary write can only ever change a judgment.
    ///
    /// The header was not enough on its own, and that gap is worth stating because it looked closed.
    /// Preserving origin, machine and Director while leaving the SEATS writable means a caller can PUT
    /// invented seats onto a real capture and the store hands the genuine header straight back to them -
    /// a record that says a fleet was read firsthand from a Director when its contents were typed. So the
    /// seat set is fixed too: a write may not add a seat, remove one, or change what the Gateway observed
    /// about one. What it may change is everything a person decides afterwards.
    /// </summary>
    private static void ApplyStoredProvenance(WorkspaceDocument doc, WorkspaceEntity existing)
    {
        WorkspaceDocument? stored = null;
        try
        {
            stored = JsonSerializer.Deserialize<WorkspaceDocument>(existing.DocumentJson, DocumentJsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WorkspaceStore] ApplyStoredProvenance FAILED for id={existing.Id}: {ex.Message}");
            throw new InvalidOperationException(
                $"Workspace \"{existing.Id}\" could not be read before writing over it: {ex.Message}. " +
                "Read the document_json column of the workspaces table directly to recover it.", ex);
        }

        if (stored is null)
            throw new InvalidOperationException(
                $"Workspace \"{existing.Id}\" could not be read before writing over it: its stored " +
                "document deserialized to null.");

        doc.Origin = stored.Origin;
        doc.Machine = stored.Machine;
        doc.DirectorId = stored.DirectorId;
        doc.DirectorName = stored.DirectorName;
        doc.DirectorVersionBefore = stored.DirectorVersionBefore;
        doc.StartedAtUtc = stored.StartedAtUtc;

        // The restore lease is the Gateway's own bookkeeping (inspection 7, ruling 4): no caller grants or
        // clears it by writing the document.
        doc.RestoreLease = stored.RestoreLease;

        // WHETHER THE OWNER HAS ASKED NOT TO BE OFFERED THIS RECORD AT START-UP is provenance too, and is
        // written only by a "cleared" restore mark. A writer who could set it would make a record vanish from
        // the owner's start-up; one who could clear it would put a record he has dismissed back in front of
        // him. It is restored BEFORE the authored early return below, so no origin escapes it.
        doc.ClearedFromStartUpOfferAtUtc = stored.ClearedFromStartUpOfferAtUtc;
        doc.ClearedFromStartUpOfferByDirectorId = stored.ClearedFromStartUpOfferByDirectorId;

        // AUTHORED - and ONLY authored - has no captured seats, so its seats are the caller's to edit
        // entirely. Written as "is authored" and not as "is not captured", which is not the same test:
        // an origin this build has never heard of, written by a newer one, would fall through the second
        // form into the permissive branch and have its seats opened up. Anything that is not the one
        // origin known to be free gets the strict treatment.
        if (stored.Origin == WorkspaceOrigins.Authored) return;

        var storedSeats = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var seat in stored.Seats.Where(x => !string.IsNullOrWhiteSpace(x.SessionId)))
            storedSeats[seat.SessionId!] = seat;

        var incoming = doc.Seats ?? new List<WorkspaceSeat>();
        var incomingIds = new HashSet<string>(
            incoming.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.SessionId)).Select(x => x.SessionId!),
            StringComparer.OrdinalIgnoreCase);

        var added = incomingIds.Except(storedSeats.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = storedSeats.Keys.Except(incomingIds, StringComparer.OrdinalIgnoreCase).ToList();
        var unidentified = incoming.Count(x => x is null || string.IsNullOrWhiteSpace(x.SessionId));

        if (added.Count > 0 || removed.Count > 0 || unidentified > 0)
            throw new WorkspaceValidationException(
                $"The seats of a captured workspace are what the Gateway read from Director " +
                $"\"{stored.DirectorId}\" and cannot be changed by writing to it. " +
                (added.Count > 0 ? $"These are not in it: {string.Join(", ", added)}. " : "") +
                (removed.Count > 0 ? $"These are missing from your copy: {string.Join(", ", removed)}. " : "") +
                (unidentified > 0 ? $"{unidentified} seat(s) name no session. " : "") +
                "Read the workspace, change the judgments on it, and write that back.");

        foreach (var seat in incoming)
        {
            var from = storedSeats[seat.SessionId!];

            // What the Gateway OBSERVED. Restored from the stored copy on every write.
            //
            // The SESSION ID is restored as well as matched on, so a caller who spells the stored GUID
            // in another case does not change the stored spelling; and the seat's UNKNOWN fields are
            // restored, because a fact a newer Gateway observed and this build has no name for is still
            // an observation - leaving that bag to the caller would let exactly the fields nobody here
            // can read be the ones that are rewritten.
            seat.SessionId = from.SessionId;
            seat.Unknown = from.Unknown;
            seat.Name = from.Name;
            seat.Agent = from.Agent;
            seat.Model = from.Model;
            seat.ModelDisplay = from.ModelDisplay;
            seat.RepoPath = from.RepoPath;
            // The pooled worktree is an OBSERVATION, not a judgment, and it is the one on this list a
            // caller would most want to choose: the Director hands it straight back on the restore's
            // create and runs the session in the slot it names, on the lease it names. Restored from
            // the stored copy like every other observation, so writing to a workspace cannot point a
            // restore at somebody else's worktree.
            seat.PooledWorktree = from.PooledWorktree;
            seat.Mission = from.Mission;
            seat.Role = from.Role;
            seat.ReportsTo = from.ReportsTo;
            seat.ParentSessionId = from.ParentSessionId;
            seat.WorkflowRunId = from.WorkflowRunId;
            seat.SortOrder = from.SortOrder;
            seat.StateAtDrain = from.StateAtDrain;
            seat.ClaudeSessionId = from.ClaudeSessionId;
            seat.ClaudeTranscriptPath = from.ClaudeTranscriptPath;
            seat.CreatedAt = from.CreatedAt;

            // What a RESTORE did (inspection 7, ruling 1). The restored id of an owner seat is the owner the
            // Director names for that seat's workers, so a writer who could set it could choose who owns a
            // restored worker - and an unrelated session key can write this document. These are written only
            // by RecordRestoreMark and RecordRestoredByClaim, and every ordinary write gets the stored copy.
            // The caller keeps only the DECISION, its reason and command, and the handover path.
            RestoreStoredMarks(seat, from);

            // Everything else on the seat - the handover path, the drain state, the restore decision,
            // what came back - is a judgment somebody made, and the caller's copy is kept.
            //
            // KNOWN LIMIT, stated rather than left to be found: the DOCUMENT-level unknown bag is NOT
            // restored. A newer build could put either kind of field there - a provenance fact or a
            // judgment - and this build cannot tell which, so restoring it would silently discard
            // legitimate new judgments and leaving it does allow a future provenance field to be
            // rewritten. It is left with the caller because losing a judgment is the worse of the two.
        }

        // Who restored it is written by the restore marks as well, for the same reason.
        doc.RestoredBy = stored.RestoredBy;
    }

    /// <summary>Put the stored restore marks back onto an incoming seat. See <see cref="WorkspaceRestoreMark"/>.</summary>
    private static void RestoreStoredMarks(WorkspaceSeat seat, WorkspaceSeat from)
    {
        seat.RestoredSessionId = from.RestoredSessionId;
        seat.RestoredSeedFile = from.RestoredSeedFile;

        var stored = from.Restore;
        if (seat.Restore is null)
        {
            // A caller that dropped the whole restore block keeps no decision, but it does not erase what a
            // restore did either.
            if (stored is not null && HasMarks(stored))
                seat.Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };
            else
                return;
        }

        seat.Restore!.Failure = stored?.Failure;
        seat.Restore.AttemptedAtUtc = stored?.AttemptedAtUtc;
        seat.Restore.StartedToken = stored?.StartedToken;
        seat.Restore.StartedAtUtc = stored?.StartedAtUtc;
        seat.Restore.StartedByDirectorId = stored?.StartedByDirectorId;

        // The reopen claim is provenance like the rest of this list: a writer who could set it would make a
        // session vanish from the way up's offer, and a writer who could clear it would put a dealt-with
        // session back into it (product issue 3230).
        seat.Restore.ReopenedAtUtc = stored?.ReopenedAtUtc;
        seat.Restore.ReopenedSessionId = stored?.ReopenedSessionId;
        seat.Restore.ReopenedByDirectorId = stored?.ReopenedByDirectorId;
    }

    private static bool HasMarks(WorkspaceSeatRestore r)
        => r.Failure is not null || r.AttemptedAtUtc is not null || r.StartedToken is not null
           || r.StartedAtUtc is not null || r.StartedByDirectorId is not null
           || r.ReopenedAtUtc is not null || r.ReopenedSessionId is not null
           || r.ReopenedByDirectorId is not null;

    // ===================================================================================================
    // THE RESTORE'S OWN WRITES (the Message Load mission, inspection 7, rulings 1, 3 and 4). Each one reads
    // the stored document, changes only restore bookkeeping, and writes it back - all inside the one write
    // lock, so a caller's PUT cannot land between the read and the write.
    // ===================================================================================================

    /// <summary>
    /// Grant the restore lease on a captured workspace to <paramref name="directorId"/>, unless another Director
    /// holds a live one. Returns whether the lease was NEWLY granted (false when this Director already held a live
    /// one - the caller must then not release it on a refusal, because a run of that Director may be using it).
    /// </summary>
    /// <exception cref="WorkspaceConflictException">Another Director holds a live lease; the message names it.</exception>
    /// <exception cref="WorkspaceValidationException">There is no such workspace.</exception>
    public bool TakeRestoreLease(string id, string directorId, string? requestedBySessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(directorId)) throw new ArgumentException("directorId is required", nameof(directorId));
        var at = nowUtc.ToUniversalTime();
        var granted = false;
        MutateStored(id, at, doc =>
        {
            var lease = doc.RestoreLease;
            if (lease is not null && lease.IsLiveAt(at))
            {
                if (!string.Equals(lease.DirectorId, directorId, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceConflictException(
                        $"Director '{lease.DirectorId}' is already restoring workspace \"{doc.Id}\" (since " +
                        $"{lease.GrantedAtUtc:u}, last heard {lease.RenewedAtUtc:u}). Two Directors restoring one " +
                        "workspace could each start the same seat, so this one is refused. Ask again when that " +
                        $"restore has finished, or after {WorkspaceRestoreLease.Expiry.TotalMinutes:0} minutes without word from it.");
                return false;
            }

            doc.RestoreLease = new WorkspaceRestoreLease
            {
                DirectorId = directorId,
                RequestedBySessionId = requestedBySessionId,
                GrantedAtUtc = at,
                RenewedAtUtc = at,
            };
            granted = true;
            return true;
        });
        FileLog.Write($"[WorkspaceStore] TakeRestoreLease: id={id}, director={directorId}, newlyGranted={granted}");
        return granted;
    }

    /// <summary>Give back the restore lease if <paramref name="directorId"/> holds it. No-op otherwise.</summary>
    public void ReleaseRestoreLease(string id, string directorId)
    {
        var released = false;
        MutateStored(id, DateTime.UtcNow, doc =>
        {
            if (doc.RestoreLease is null
                || !string.Equals(doc.RestoreLease.DirectorId, directorId, StringComparison.OrdinalIgnoreCase))
                return false;
            doc.RestoreLease = null;
            released = true;
            return true;
        });
        FileLog.Write($"[WorkspaceStore] ReleaseRestoreLease: id={id}, director={directorId}, released={released}");
    }

    /// <summary>
    /// Write one restore mark. Only the Director holding a live lease may write one; every mark renews it, and
    /// "finished" releases it. Returns the stored document.
    /// </summary>
    /// <exception cref="WorkspaceConflictException">The writer does not hold a live lease, or the mark contradicts
    /// what is already recorded (a different restored id).</exception>
    /// <exception cref="WorkspaceValidationException">The mark is malformed or names no seat of this workspace.</exception>
    public WorkspaceDocument RecordRestoreMark(string id, WorkspaceRestoreMark mark, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (!WorkspaceRestoreMarkKinds.All.Contains(mark.Kind))
            throw new WorkspaceValidationException(
                $"kind must be one of: {string.Join(", ", WorkspaceRestoreMarkKinds.All)}.");
        if (string.IsNullOrWhiteSpace(mark.DirectorId))
            throw new WorkspaceValidationException("directorId is required on a restore mark.");

        var at = nowUtc.ToUniversalTime();
        var result = MutateStored(id, at, doc =>
        {
            var lease = doc.RestoreLease;

            // A REOPEN IS NOT A RESTORE, so it neither needs the lease nor touches it - see
            // WorkspaceRestoreMarkKinds.Reopened for why it cannot have one. What it must not do is cut across
            // a restore another Director is running, so that is the one thing checked.
            // NEITHER A REOPEN NOR A CLEARING TAKES THE LEASE, and both are refused underneath another
            // Director's live one. See WorkspaceRestoreMarkKinds.Reopened and .Cleared for why each cannot
            // have a lease of its own.
            var reopen = string.Equals(mark.Kind, WorkspaceRestoreMarkKinds.Reopened, StringComparison.Ordinal);
            var cleared = string.Equals(mark.Kind, WorkspaceRestoreMarkKinds.Cleared, StringComparison.Ordinal);
            if (reopen || cleared)
            {
                if (lease is not null && lease.IsLiveAt(at)
                    && !string.Equals(lease.DirectorId, mark.DirectorId, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceConflictException(
                        $"Director '{lease.DirectorId}' is restoring workspace \"{doc.Id}\" (since " +
                        $"{lease.GrantedAtUtc:u}), so Director '{mark.DirectorId}' does not " +
                        (reopen ? "reopen one of its seats" : "change what it offers") +
                        " underneath it. Ask again when that restore has finished.");
            }
            else if (lease is null || !lease.IsLiveAt(at)
                     || !string.Equals(lease.DirectorId, mark.DirectorId, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceConflictException(
                    $"Director '{mark.DirectorId}' does not hold the restore lease on workspace \"{doc.Id}\" " +
                    (lease is null ? "(nobody does)" : $"('{lease.DirectorId}' {(lease.IsLiveAt(at) ? "does" : "did, and it has expired")})") +
                    ", so it may not write what a restore did. Ask for the restore again.");

            if (mark.Kind == WorkspaceRestoreMarkKinds.Finished)
            {
                doc.RestoreLease = null;
                return true;
            }

            // THE OWNER ASKED NOT TO BE OFFERED THIS RECORD AT START-UP AGAIN. It names no seat, so it is
            // answered here, before the seat below is looked up.
            if (cleared)
            {
                // ALREADY CLEARED IS NOT AN ERROR, and the FIRST clearing's moment stands: after either call
                // the record has stopped appearing, which is the whole of what was asked for. Nothing is
                // written, so the document's UpdatedUtc is not moved by a second press either.
                if (doc.ClearedFromStartUpOfferAtUtc is not null) return false;
                doc.ClearedFromStartUpOfferAtUtc = at;
                doc.ClearedFromStartUpOfferByDirectorId = mark.DirectorId;
                return true;
            }

            if (!reopen) lease!.RenewedAtUtc = at;

            var seat = doc.Seats.FirstOrDefault(x => string.Equals(x.SessionId, mark.SeatSessionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new WorkspaceValidationException($"workspace \"{doc.Id}\" has no seat '{mark.SeatSessionId}'.");
            var restore = seat.Restore ??= new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };

            switch (mark.Kind)
            {
                case WorkspaceRestoreMarkKinds.Started:
                    if (string.IsNullOrWhiteSpace(mark.Token))
                        throw new WorkspaceValidationException("a \"started\" mark carries the token its create will carry.");
                    if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId))
                        throw new WorkspaceConflictException(
                            $"seat '{seat.SessionId}' has already come back as {seat.RestoredSessionId}; it is not started again.");
                    restore.StartedToken = mark.Token;
                    restore.StartedAtUtc = at;
                    restore.StartedByDirectorId = mark.DirectorId;
                    restore.AttemptedAtUtc = at;
                    restore.Failure = null;
                    doc.RestoredBy = new WorkspaceRestoredBy
                    {
                        SessionId = mark.RequestedBySessionId,
                        AtUtc = at,
                        Method = $"director restore on {mark.DirectorId}",
                        Note = mark.RequestedBySessionId is null
                            ? "asked for by the owner; every spawn made by the Director on its own credential"
                            : "asked for by the session named here; every spawn made by the Director on its own credential",
                    };
                    break;

                case WorkspaceRestoreMarkKinds.Restored:
                    if (string.IsNullOrWhiteSpace(mark.RestoredSessionId))
                        throw new WorkspaceValidationException("a \"restored\" mark names the new session.");
                    // A seat is restored only by the start this Director recorded (inspection 11, ruling 1): the mark
                    // carries the token of that start, so "restored" can never name a session no create of this
                    // restore made.
                    if (string.IsNullOrWhiteSpace(mark.Token))
                        throw new WorkspaceValidationException("a \"restored\" mark carries the token of the start it completes.");
                    if (!string.Equals(restore.StartedToken, mark.Token, StringComparison.Ordinal)
                        || !string.Equals(restore.StartedByDirectorId, mark.DirectorId, StringComparison.OrdinalIgnoreCase))
                        throw new WorkspaceConflictException(
                            $"seat '{seat.SessionId}' has no start by Director '{mark.DirectorId}' with that token, so it cannot be " +
                            "recorded as restored. Only the Director that started a seat records what came back.");
                    if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId)
                        && !string.Equals(seat.RestoredSessionId, mark.RestoredSessionId, StringComparison.OrdinalIgnoreCase))
                        throw new WorkspaceConflictException(
                            $"seat '{seat.SessionId}' is already recorded as restored as {seat.RestoredSessionId}, not {mark.RestoredSessionId}.");
                    seat.RestoredSessionId = mark.RestoredSessionId;
                    if (!string.IsNullOrWhiteSpace(mark.SeedFile)) seat.RestoredSeedFile = mark.SeedFile;
                    restore.Failure = null;
                    restore.AttemptedAtUtc = at;
                    break;

                case WorkspaceRestoreMarkKinds.Failed:
                    if (string.IsNullOrWhiteSpace(mark.Failure))
                        throw new WorkspaceValidationException("a \"failed\" mark says why.");
                    if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId))
                    {
                        // The Gateway recorded the create by its token while the Director was still waiting for an
                        // answer it never got. The record of what came back wins over "no answer came".
                        FileLog.Write($"[WorkspaceStore] RecordRestoreMark: id={doc.Id}, seat={seat.SessionId}: failure ignored, the seat is already restored as {seat.RestoredSessionId}");
                        return true;
                    }
                    restore.Failure = mark.Failure;
                    restore.AttemptedAtUtc = at;
                    if (mark.NothingStarted)
                    {
                        restore.StartedToken = null;
                        restore.StartedAtUtc = null;
                        restore.StartedByDirectorId = null;
                    }
                    break;

                case WorkspaceRestoreMarkKinds.Reopened:
                    // THE FIRST MARK CLAIMS IT, and the claim is what stops the seat being offered again. It is
                    // written before the create is sent, so a start whose answer never came back leaves the
                    // claim standing rather than the seat offered for ever (product issue 3230).
                    if (restore.ReopenedAtUtc is null)
                    {
                        restore.ReopenedAtUtc = at;
                        restore.ReopenedByDirectorId = mark.DirectorId;
                        restore.ReopenedSessionId = string.IsNullOrWhiteSpace(mark.ReopenedSessionId)
                            ? null
                            : mark.ReopenedSessionId;
                        break;
                    }

                    // The SAME Director filling in the session id of the claim it already holds. Nothing else
                    // may write here: a second reopen is exactly what the claim exists to refuse, and two agents
                    // in one saved conversation would interleave their turns into one transcript.
                    if (string.IsNullOrWhiteSpace(restore.ReopenedSessionId)
                        && !string.IsNullOrWhiteSpace(mark.ReopenedSessionId)
                        && string.Equals(restore.ReopenedByDirectorId, mark.DirectorId, StringComparison.OrdinalIgnoreCase))
                    {
                        restore.ReopenedSessionId = mark.ReopenedSessionId;
                        break;
                    }

                    throw new WorkspaceConflictException(
                        $"seat '{seat.SessionId}' of workspace \"{doc.Id}\" had its saved conversation reopened on " +
                        $"{restore.ReopenedAtUtc:u}" +
                        (string.IsNullOrWhiteSpace(restore.ReopenedSessionId) ? "" : $" as {restore.ReopenedSessionId}") +
                        ", so it is not reopened again. Two agents in one saved conversation would interleave " +
                        "their turns into one transcript.");
            }
            return true;
        });
        FileLog.Write($"[WorkspaceStore] RecordRestoreMark: id={id}, director={mark.DirectorId}, kind={mark.Kind}, seat={mark.SeatSessionId ?? "-"}");
        return result;
    }

    /// <summary>
    /// The spawn door's record of a restore's create (inspection 7, ruling 3): when the seat's stored start token
    /// is <paramref name="claim"/>'s token and the start was <paramref name="directorId"/>'s - the Director the create
    /// was performed on - the seat is recorded as restored as <paramref name="newSessionId"/>.
    /// Returns whether it was recorded. A token that does not match records nothing - the seat has been started
    /// again since, or the claim is not this seat's.
    /// </summary>
    public bool RecordRestoredByClaim(WorkspaceRestoreClaim claim, string directorId, string newSessionId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (string.IsNullOrWhiteSpace(claim.Token) || string.IsNullOrWhiteSpace(newSessionId)) return false;
        var at = nowUtc.ToUniversalTime();
        var recorded = false;
        MutateStored(claim.WorkspaceId, at, doc =>
        {
            var seat = doc.Seats.FirstOrDefault(x => string.Equals(x.SessionId, claim.SeatSessionId, StringComparison.OrdinalIgnoreCase));
            if (seat?.Restore is not { } restore
                || !string.Equals(restore.StartedToken, claim.Token, StringComparison.Ordinal)
                || !string.Equals(restore.StartedByDirectorId, directorId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId)) return false;
            seat.RestoredSessionId = newSessionId;
            restore.Failure = null;
            restore.AttemptedAtUtc = at;
            recorded = true;
            return true;
        });
        FileLog.Write($"[WorkspaceStore] RecordRestoredByClaim: id={claim.WorkspaceId}, seat={claim.SeatSessionId}, session={newSessionId}, recorded={recorded}");
        return recorded;
    }

    /// <summary>
    /// Read the stored document, let <paramref name="change"/> alter it, and write it back when it returns true -
    /// all under the write lock, validated like every other write. Returns the stored document.
    /// </summary>
    private WorkspaceDocument MutateStored(string? id, DateTime atUtc, Func<WorkspaceDocument, bool> change)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new WorkspaceValidationException("a workspace id is required.");
        var slug = id.Trim();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.Workspaces.FirstOrDefault(e => e.Id == slug)
                      ?? throw new WorkspaceValidationException($"no workspace with id '{slug}'.");
            var doc = Deserialize(row);
            if (!change(doc)) return doc;

            WorkspaceValidation.Validate(doc);
            doc.UpdatedUtc = atUtc;
            row.UpdatedUtc = atUtc;
            row.DocumentJson = JsonSerializer.Serialize(doc, DocumentJsonOptions);
            ctx.SaveChanges();
            return doc;
        }
    }

    /// <summary>One workspace by id, or null when there is none.</summary>
    /// <param name="id">The workspace slug.</param>
    public WorkspaceDocument? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var slug = id.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.Workspaces.AsNoTracking().FirstOrDefault(e => e.Id == slug);
            FileLog.Write($"[WorkspaceStore] Get: id={slug}, found={row is not null}");
            return row is null ? null : Deserialize(row);
        }
    }

    /// <summary>
    /// Every workspace, most recently written first. Summaries only - the list is a chooser, and a fleet
    /// of documents each carrying twenty seats is not something to send to render a list.
    /// </summary>
    public IReadOnlyList<WorkspaceSummaryDto> List()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.Workspaces.AsNoTracking().ToList();

            // Read from the head columns ONLY - the list never parses a stored document. That is what keeps
            // one damaged row from taking the whole list down with it: a workspace that cannot be read is
            // still listed, and the failure surfaces where it belongs, on the read of THAT workspace.
            var summaries = rows
                .OrderByDescending(e => e.UpdatedUtc)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new WorkspaceSummaryDto
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = e.Description,
                    Origin = e.Origin,
                    Machine = e.Machine,
                    DirectorName = e.DirectorName,
                    SeatCount = e.SeatCount,
                    CreatedUtc = e.CreatedUtc,
                    UpdatedUtc = e.UpdatedUtc,
                })
                .ToList();

            FileLog.Write($"[WorkspaceStore] List: {summaries.Count} workspace(s)");
            return summaries;
        }
    }

    /// <summary>Delete a workspace. Returns false when there was none to delete.</summary>
    /// <param name="id">The workspace slug.</param>
    public bool Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var slug = id.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.Workspaces.FirstOrDefault(e => e.Id == slug);
            if (row is null)
            {
                FileLog.Write($"[WorkspaceStore] Delete: id={slug} not found");
                return false;
            }

            ctx.Workspaces.Remove(row);
            ctx.SaveChanges();
            FileLog.Write($"[WorkspaceStore] Delete: id={slug} deleted");
            return true;
        }
    }

    /// <summary>
    /// Read the stored document. A workspace that cannot be read fails LOUDLY and names where the bytes
    /// are, because the alternative - handing back a half-built document - is a fleet restored from a
    /// record that was quietly missing seats.
    /// </summary>
    private static WorkspaceDocument Deserialize(WorkspaceEntity row)
    {
        WorkspaceDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<WorkspaceDocument>(row.DocumentJson, DocumentJsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WorkspaceStore] Get FAILED for id={row.Id}: {ex.Message}");
            throw new InvalidOperationException(
                $"Workspace \"{row.Id}\" could not be read: {ex.Message}. The row is intact - read the " +
                "document_json column of the workspaces table directly to recover what it holds.", ex);
        }

        if (doc is null)
            throw new InvalidOperationException(
                $"Workspace \"{row.Id}\" could not be read: its stored document deserialized to null. " +
                "The row is intact - read the document_json column of the workspaces table directly.");

        return doc;
    }
}
