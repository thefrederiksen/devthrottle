using CcDirector.ControlApi.Drain;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// Everything the way up needs from the Gateway, behind a seam so its rules are provable without one - the
/// same arrangement <see cref="IRestoreGateway"/> makes for the restore.
///
/// Every method THROWS when the Gateway cannot be reached. That is deliberate and it is the whole reason
/// the seam is shaped this way: the engine turns the throw into a refusal carrying the reason, and an
/// empty list is never used to mean "the Gateway did not answer".
/// </summary>
public interface IWayUpGateway
{
    /// <summary>Every workspace this Gateway holds, enough of each to choose without fetching documents.</summary>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<WorkspaceSummaryDto>> ListWorkspacesAsync(CancellationToken ct);

    /// <summary>One workspace document, or null when the Gateway no longer has it.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct);

    /// <summary>
    /// Start one session on THIS Director, on this Director's own credential - the same door the restore
    /// uses. Used only to reopen a seat that ended without a handover; everything else goes through the
    /// restore.
    /// </summary>
    /// <param name="request">The session to start.</param>
    /// <param name="ct">Cancellation.</param>
    Task<SessionDto> StartSessionAsync(NewSessionRequest request, CancellationToken ct);

    /// <summary>
    /// The live roster of the whole account, with each Director's reachability - THE SAME QUESTION
    /// <see cref="IRestoreGateway.GetRosterAsync"/> asks, so that the reopen can refuse a seat that may
    /// still be running by the product's own rule rather than a second one written here.
    ///
    /// IT IS ASKED ONLY WHEN A SESSION IS ABOUT TO BE STARTED. The start-up check is a check on the RECORD
    /// and never on what is running, and it does not call this.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    Task<RestoreRoster> GetRosterAsync(CancellationToken ct);

    /// <summary>
    /// WRITE THE REOPEN ONTO THE RECORD (product issue 3230): this seat's saved conversation is being reopened,
    /// so it is never offered again - after the next Director restart as much as during this run.
    ///
    /// It ANSWERS rather than throwing, because its three answers are three different things to say to the
    /// person: recorded, already dealt with, or the record could not be marked at all. See
    /// <see cref="WayUpMarkState"/>.
    /// </summary>
    /// <param name="workspaceId">The record.</param>
    /// <param name="seatSessionId">The seat's captured session id.</param>
    /// <param name="reopenedSessionId">The session it was reopened as, or null on the mark that CLAIMS the
    /// reopen before the create is sent.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WayUpMarkOutcome> MarkReopenedAsync(
        string workspaceId, string seatSessionId, string? reopenedSessionId, CancellationToken ct);

    /// <summary>
    /// WRITE THE CLEARING ONTO THE RECORD (the owner's ruling of 20 September 2026): he does not want this
    /// record offered when the Director starts, and does not want to be asked again.
    ///
    /// It names no seat, because it is a fact about the whole record. Like
    /// <see cref="MarkReopenedAsync"/> it ANSWERS rather than throwing, because a Gateway that cannot record
    /// it must produce a different sentence from one that did: telling the owner he will not be asked again
    /// when the record says nothing of the kind is the one outcome this must never have.
    /// </summary>
    /// <param name="workspaceId">The record.</param>
    /// <param name="ct">Cancellation.</param>
    Task<WayUpMarkOutcome> MarkClearedFromStartUpOfferAsync(string workspaceId, CancellationToken ct);
}

/// <summary>What became of a write of the reopen mark. Three states, kept apart because they are three
/// different sentences: an ordinary refusal that the person has already dealt with this seat, and a record
/// that could not be marked at all, must never read as each other.</summary>
public enum WayUpMarkState
{
    /// <summary>The record now carries the reopen.</summary>
    Marked,

    /// <summary>This seat's conversation was already reopened, so it is not reopened again. An ordinary
    /// outcome and not a failure - it is the guarantee working.</summary>
    AlreadyDealtWith,

    /// <summary>The record could not be marked. <see cref="WayUpMarkOutcome.Reason"/> holds the Gateway's own
    /// words, which on a Gateway too old to know the mark name those it does know.</summary>
    Refused,
}

/// <summary>The answer to a reopen mark.</summary>
/// <param name="State">Marked, already dealt with, or refused.</param>
/// <param name="Reason">The Gateway's own reason, on either refusing state. Null when it was marked.</param>
public sealed record WayUpMarkOutcome(WayUpMarkState State, string? Reason);

/// <summary>
/// The restore, behind a seam. ONE method, because the way up does not re-implement any part of a restore:
/// it builds the order and hands it over.
/// </summary>
public interface IWayUpRestore
{
    /// <summary>
    /// Ask for the order and answer what became of each seat. Throws <see cref="InvalidOperationException"/>
    /// with the reason when the restore cannot start at all - another restore is running, the record has no
    /// seat left to bring back, a named seat is still running, another Director holds the restore lease - so
    /// the caller can say so plainly instead of starting nothing and reporting success.
    ///
    /// WHO CHECKS THOSE RULES is not the way up and is not this seam: the real implementation asks the
    /// Gateway's restore door, and the reasons above are the Gateway's own words carried up unchanged. See
    /// <see cref="DirectorRestoreWayUp"/> for why there is no other way in.
    /// </summary>
    /// <param name="order">The seats to bring back, and their seed files.</param>
    /// <param name="ct">Cancellation.</param>
    Task<DirectorRestoreResult> RestoreAsync(WorkspaceRestoreOrder order, CancellationToken ct);
}

/// <summary>
/// The real Gateway seam, over the Director's existing outbound client.
///
/// THE CLIENT IS READ AT THE MOMENT OF EACH CALL, through a function rather than held: a settings change
/// replaces the host's client, and an engine holding the old one would be answering from a connection that
/// no longer exists. Phase 1 made the same arrangement for the same reason.
/// </summary>
public sealed class GatewayClientWayUp : IWayUpGateway
{
    /// <summary>What is said when this Director has no Gateway client at all.</summary>
    public const string NotConnected =
        "this Director is not connected to a Gateway, and the records of what it shut down are kept there " +
        "rather than on this machine";

    private readonly Func<GatewayClient?> _client;
    private readonly string _directorId;

    /// <summary>Create the seam.</summary>
    /// <param name="client">Reads the host's CURRENT Gateway client, or null when there is none.</param>
    /// <param name="directorId">THIS Director's id, which every mark it writes is stamped with. The Gateway
    /// refuses a mark whose Director is not the one the credential is connected on, so it is not the engine's
    /// to choose and is not read from anything a caller supplies.</param>
    public GatewayClientWayUp(Func<GatewayClient?> client, string directorId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _directorId = string.IsNullOrWhiteSpace(directorId)
            ? throw new ArgumentException("directorId is required", nameof(directorId))
            : directorId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkspaceSummaryDto>> ListWorkspacesAsync(CancellationToken ct)
        => await Required().ListWorkspacesAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
        => Required().GetWorkspaceAsync(id, ct);

    /// <inheritdoc />
    public Task<SessionDto> StartSessionAsync(NewSessionRequest request, CancellationToken ct)
        => Required().SpawnOnThisDirectorAsync(request, ct);

    /// <inheritdoc />
    public async Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
    {
        // The envelope, not the plain list: the plain list says nothing about which Directors it could not
        // reach, and "not on the list" is exactly the fact this check acts on. Word for word what
        // GatewayClientRestoreGateway does, for the same reason.
        var (sessions, directors) = await Required().ListFleetSessionsWithReachabilityAsync(ct).ConfigureAwait(false);
        return new RestoreRoster(sessions, directors);
    }

    /// <inheritdoc />
    public async Task<WayUpMarkOutcome> MarkReopenedAsync(
        string workspaceId, string seatSessionId, string? reopenedSessionId, CancellationToken ct)
    {
        var mark = new WorkspaceRestoreMark
        {
            DirectorId = _directorId,
            Kind = WorkspaceRestoreMarkKinds.Reopened,
            SeatSessionId = seatSessionId,
            ReopenedSessionId = reopenedSessionId,
        };

        var (status, error) = await Required().RecordReopenMarkAsync(workspaceId, mark, ct).ConfigureAwait(false);
        if (status is >= 200 and < 300) return new WayUpMarkOutcome(WayUpMarkState.Marked, null);

        // 409 is the store saying this seat was already reopened, which is the guarantee doing its job. Every
        // other status - including the 400 a Gateway too old to know this mark answers with - is a record that
        // could not be marked, and is never softened into "it worked".
        return status == 409
            ? new WayUpMarkOutcome(WayUpMarkState.AlreadyDealtWith, error)
            : new WayUpMarkOutcome(WayUpMarkState.Refused, error);
    }

    /// <inheritdoc />
    public async Task<WayUpMarkOutcome> MarkClearedFromStartUpOfferAsync(string workspaceId, CancellationToken ct)
    {
        var mark = new WorkspaceRestoreMark
        {
            DirectorId = _directorId,
            Kind = WorkspaceRestoreMarkKinds.Cleared,
        };

        var (status, error) = await Required().RecordReopenMarkAsync(workspaceId, mark, ct).ConfigureAwait(false);
        if (status is >= 200 and < 300) return new WayUpMarkOutcome(WayUpMarkState.Marked, null);

        // THERE IS NO "ALREADY DEALT WITH" ANSWER HERE, and that is the store's rule and not a gap: clearing a
        // record that is already cleared changes nothing and answers 2xx, because after either call the record
        // has stopped appearing. Every other status - including the 400 a Gateway too old to know this mark
        // answers with - is a record that could not be marked, and is never softened into "it worked".
        return new WayUpMarkOutcome(WayUpMarkState.Refused, error);
    }

    private GatewayClient Required()
        => _client() ?? throw new InvalidOperationException(NotConnected);
}

/// <summary>
/// THE REAL RESTORE SEAM: THE WAY UP ASKS THE GATEWAY'S RESTORE DOOR (product issue 3395).
///
/// WHY IT CANNOT RUN <see cref="DirectorRestore"/> ITSELF, which is what it did until 25 September 2026. The
/// Gateway grants one restore lease per workspace, at one place only - <c>POST /gateway/workspaces/{id}/restore</c> -
/// and it refuses every restore mark written by a Director that does not hold it. A restore constructed inside
/// the Director holds no lease, so the very FIRST mark it writes - the "started" mark, written before the create
/// is sent - came back refused:
///
///   Director '...' does not hold the restore lease on workspace "..." (nobody does), so it may not write what
///   a restore did. Ask for the restore again.
///
/// Nothing was started and nothing was damaged; the whole bring back simply stopped there, on both the start-up
/// window and File, Restart history, which share <see cref="DirectorWayUp"/>. Going through the door is not a
/// second path - it IS the existing path: the Gateway takes the lease and relays the order back down to this
/// same Director, which runs the same <see cref="DirectorRestore"/> it always ran, with the lease held.
///
/// THERE IS NO LOCAL CLAIM HERE, and that is deliberate rather than an omission. The relayed order claims the
/// Director's one-at-a-time gate when it arrives, so a claim taken before asking would make this Director
/// refuse its own restore.
///
/// IT IS THE SAME CLASS THE CANCELLED SHUTDOWN USES, <see cref="GatewaySmartShutdownBringBack"/>, rather than a
/// second copy of it: the door answers "taken" and not "done", so somebody has to read each seat's outcome off
/// the record, and one such reader is enough for both surfaces.
/// </summary>
public sealed class DirectorRestoreWayUp : IWayUpRestore
{
    private readonly Func<GatewayClient?> _client;
    private readonly string _directorId;

    /// <summary>Create the seam.</summary>
    /// <param name="client">Reads the host's CURRENT Gateway client, or null when there is none.</param>
    /// <param name="directorId">This Director's id. The restore is asked for ONTO it, and it is the Director the
    /// Gateway grants the lease to - so it is not the caller's to choose.</param>
    public DirectorRestoreWayUp(Func<GatewayClient?> client, string directorId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _directorId = string.IsNullOrWhiteSpace(directorId)
            ? throw new ArgumentException("directorId is required", nameof(directorId))
            : directorId;
    }

    /// <inheritdoc />
    public async Task<DirectorRestoreResult> RestoreAsync(WorkspaceRestoreOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        var seats = order.Seats ?? new List<string>();
        FileLog.Write($"[DirectorRestoreWayUp] RestoreAsync: {seats.Count} seat(s) from workspace {order.WorkspaceId}, " +
                      $"through the Gateway restore door onto Director {_directorId}");

        var bringBack = new GatewaySmartShutdownBringBack(
            () => _client() is { } client ? new GatewayClientBringBackGateway(client) : null,
            _directorId);

        var result = await bringBack
            .BringBackAsync(order.WorkspaceId, seats, order.Seeds, ct)
            .ConfigureAwait(false);

        // THE GATEWAY'S OWN WORDS REACH THE WINDOW. A restore the door would not take - another restore
        // running, a seat still running, another Director holding the lease - is a refusal that started
        // nothing, and this seam's contract is to THROW it so the caller says so plainly instead of
        // reporting a run that never happened. See IWayUpRestore.RestoreAsync.
        if (result.CouldNotStart is { Length: > 0 } why)
        {
            FileLog.Write($"[DirectorRestoreWayUp] RestoreAsync REFUSED: {why}");
            throw new InvalidOperationException(why);
        }

        FileLog.Write($"[DirectorRestoreWayUp] RestoreAsync: workspace={order.WorkspaceId}, " +
                      $"back={result.Seats.Count(s => s.RestoredSessionId is not null)}, " +
                      $"not={result.Seats.Count(s => s.RestoredSessionId is null)}");
        return new DirectorRestoreResult(order.WorkspaceId, result.Seats);
    }
}
