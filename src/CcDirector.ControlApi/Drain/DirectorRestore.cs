using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>Everything the restore needs from the Gateway, behind a seam so its rules are provable without one.</summary>
public interface IRestoreGateway
{
    /// <summary>The workspace as it is stored now, or null when there is none.</summary>
    Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct);

    /// <summary>Store the workspace as it stands. Called after every seat, so a restore that is interrupted still
    /// says which seats came back.</summary>
    Task<WorkspaceDocument> SaveWorkspaceAsync(WorkspaceDocument doc, CancellationToken ct);

    /// <summary>
    /// Start one session on THIS Director through the Gateway's <c>POST /directors/{id}/sessions</c>, on this
    /// Director's own credential. Throws with the Gateway's reason when the session is not started.
    /// </summary>
    Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct);
}

/// <summary>The real seam, over the Director's existing outbound Gateway client.</summary>
public sealed class GatewayClientRestoreGateway : IRestoreGateway
{
    private readonly GatewayClient _client;

    /// <summary>Create the seam over a connected Gateway client.</summary>
    /// <param name="client">The Director's Gateway client.</param>
    public GatewayClientRestoreGateway(GatewayClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct) => _client.GetWorkspaceAsync(id, ct);

    /// <inheritdoc />
    public Task<WorkspaceDocument> SaveWorkspaceAsync(WorkspaceDocument doc, CancellationToken ct) => _client.SaveWorkspaceAsync(doc, ct);

    /// <inheritdoc />
    public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
        => _client.SpawnOnThisDirectorAsync(request, ct);
}

/// <summary>What happened to one seat.</summary>
/// <param name="SessionId">The captured session id.</param>
/// <param name="Name">The seat's name.</param>
/// <param name="RestoredSessionId">The new session's id, or null when the seat did not come back.</param>
/// <param name="OwnerSessionId">The owner the new session was started under, or null for the user.</param>
/// <param name="Failure">Why it did not come back, or null.</param>
public sealed record SeatRestoreOutcome(
    string SessionId, string Name, string? RestoredSessionId, string? OwnerSessionId, string? Failure);

/// <summary>The finished restore: one outcome per seat that was asked for, in the order they were attempted.</summary>
public sealed record DirectorRestoreResult(string WorkspaceId, IReadOnlyList<SeatRestoreOutcome> Seats);

/// <summary>
/// THE RESTORE, INSIDE THE DIRECTOR (the Message Load mission, slice 6; owner decision 2, 17 September 2026).
///
/// A drain records every seat's facts and a restore decision. Bringing a seat back used to be a session's job:
/// it ran the spawn line the drain wrote, which named the seat's owner with <c>--controlled-by</c>. A session key
/// may no longer name anyone but itself as an owner, so for every seat owned by somebody else that line is
/// refused. This class does the same work from the Director instead, through the one spawn door every client
/// uses (<c>POST /directors/{id}/sessions</c>) on the Director's own credential, which the Gateway trusts to
/// name owners. There is no second spawn path: the Gateway still resolves the mission and the workflow seat,
/// and the Director still creates the session through its ordinary create verb.
///
/// WHERE THE OWNER COMES FROM. Only from the seat's <see cref="WorkspaceSeat.ReportsTo"/>, which the Gateway
/// observed when it captured the workspace and restores from its stored copy on every write. Whoever asked for
/// the restore cannot choose it, which is why an AUTHORED workspace - a list somebody typed - is refused.
///
/// THE ORDER, AND THE PLACEHOLDER. A seat whose owner was also a seat in this drain comes back after its owner,
/// under the owner's NEW id - the old one died with the restart. The rule, per seat:
///  - no owner: the user owns it;
///  - the owner is not a seat here (it lives on another Director and survived): its current id, verbatim;
///  - the owner is a seat here and has come back (in this run or an earlier one): its restored id;
///  - the owner is a seat here and did NOT come back (it failed, was decided "close", or was not asked for in
///    this run and has never been restored): this seat FAILS with that reason. It is not started unowned and
///    not started under a dead id - either would be a guess about who collects its work.
///
/// ONE SEAT FAILING DOES NOT STOP THE REST. Each failure is written onto its own seat
/// (<see cref="WorkspaceSeatRestore.Failure"/>) and the restore moves on. Only the seats that depended on it
/// fail with it, and they say so.
///
/// A SEAT COMES BACK ONCE. A seat that already names a restored session is never started again, so asking twice
/// cannot give a mission two of the same seat. ONE RESTORE AT A TIME on a Director, for the same reason.
///
/// WHAT THIS DOES NOT COVER, stated rather than left to be found: two DIFFERENT Directors asked to restore the
/// same workspace at the same moment are not serialised against each other; and a session that writes the
/// workspace while the restore runs can overwrite a seat's result with its own older copy (the store keeps the
/// caller's copy of every judgment field).
/// </summary>
public sealed class DirectorRestore
{
    private static readonly object Gate = new();
    private static DirectorRestore? _running;

    private readonly IRestoreGateway _gateway;
    private readonly string _directorId;
    private readonly Func<DateTime> _utcNow;

    /// <summary>The restore running on this Director right now, or null.</summary>
    public static DirectorRestore? Running { get { lock (Gate) return _running; } }

    /// <summary>The order this restore is running, once <see cref="RunAsync"/> has been called.</summary>
    public WorkspaceRestoreOrder? Order { get; private set; }

    /// <summary>Create a restore.</summary>
    /// <param name="gateway">The Gateway seam.</param>
    /// <param name="directorId">This Director's id, for the log and the record.</param>
    /// <param name="utcNow">The clock.</param>
    public DirectorRestore(IRestoreGateway gateway, string directorId, Func<DateTime>? utcNow = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _directorId = string.IsNullOrWhiteSpace(directorId)
            ? throw new ArgumentException("directorId is required", nameof(directorId))
            : directorId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Claim the one-at-a-time gate NOW, before the restore is scheduled, so "taken" is only ever
    /// answered to the restore that holds it. False when another restore holds it.</summary>
    public bool TryClaim()
    {
        lock (Gate)
        {
            if (_running is not null && !ReferenceEquals(_running, this)) return false;
            _running = this;
            return true;
        }
    }

    /// <summary>Give the gate back when a claimed restore will not run after all (its check refused it).</summary>
    public void Release()
    {
        lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
    }

    /// <summary>
    /// The check made BEFORE a restore is answered "taken": the workspace exists, was captured, and the order names
    /// seats that can come back. Returns the captured ids of the seats this restore would bring back. Throws
    /// <see cref="InvalidOperationException"/> with the reason otherwise - a refusal the caller reads at once,
    /// rather than a restore that fails later in a log nobody is watching.
    /// </summary>
    public async Task<IReadOnlyList<string>> PrepareAsync(WorkspaceRestoreOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        FileLog.Write($"[DirectorRestore] PrepareAsync: workspace={order.WorkspaceId}");
        var doc = await _gateway.GetWorkspaceAsync(order.WorkspaceId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"there is no workspace '{order.WorkspaceId}' to restore from.");
        var targets = SelectTargets(doc, order.Seats);
        if (targets.Count == 0)
            throw new InvalidOperationException(
                $"workspace '{order.WorkspaceId}' has no seat left to bring back: none is decided \"restore\" without " +
                "having come back already.");
        return targets.Select(t => t.SessionId!).ToList();
    }

    /// <summary>
    /// Restore the seats the order names onto this Director. Throws when the restore cannot start at all (no such
    /// workspace, an authored one, another restore running); per-seat failures are reported in the result and on
    /// the seats, never thrown.
    /// </summary>
    public async Task<DirectorRestoreResult> RunAsync(WorkspaceRestoreOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (string.IsNullOrWhiteSpace(order.WorkspaceId))
            throw new ArgumentException("the restore order names no workspace", nameof(order));
        if (!TryClaim())
            throw new InvalidOperationException(
                $"a restore is already running on this Director (workspace '{Running?.Order?.WorkspaceId}'); two at once " +
                "could start the same seat twice, so this one is refused before it starts anything.");

        Order = order;
        FileLog.Write($"[DirectorRestore] RunAsync: workspace={order.WorkspaceId}, director={_directorId}, " +
                      $"seats={(order.Seats is null ? "all owed" : string.Join(",", order.Seats))}, " +
                      $"askedBy={order.RequestedBySessionId ?? "owner"}");
        try
        {
            var result = await RunStepsAsync(order, ct).ConfigureAwait(false);
            FileLog.Write($"[DirectorRestore] finished: workspace={order.WorkspaceId}, " +
                          $"restored={result.Seats.Count(s => s.RestoredSessionId is not null)}, " +
                          $"failed={result.Seats.Count(s => s.Failure is not null)}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRestore] RunAsync FAILED: workspace={order.WorkspaceId}: {ex.Message}");
            throw;
        }
        finally
        {
            lock (Gate) { if (ReferenceEquals(_running, this)) _running = null; }
        }
    }

    private async Task<DirectorRestoreResult> RunStepsAsync(WorkspaceRestoreOrder order, CancellationToken ct)
    {
        var doc = await _gateway.GetWorkspaceAsync(order.WorkspaceId, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"there is no workspace '{order.WorkspaceId}' to restore from.");
        var targets = SelectTargets(doc, order.Seats);

        var failedHere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<SeatRestoreOutcome>();

        // The order is fixed once, as ids. Every save hands back the stored document, and each seat is then
        // looked up in THAT copy, so the next save carries every result written so far.
        var ordered = OrderSeniorsFirst(targets, SeatsById(doc)).Select(s => s.SessionId!).ToList();

        foreach (var sid in ordered)
        {
            var byId = SeatsById(doc);
            var seat = byId[sid];
            var (owner, failure) = ResolveOwner(seat, byId, failedHere);
            string? newId = null;

            NewSessionRequest? request = null;
            if (failure is null)
            {
                try { request = BuildRequest(seat, owner, order); }
                catch (InvalidOperationException ex) { failure = ex.Message; }
            }

            if (request is not null)
            {
                try
                {
                    var created = await _gateway.SpawnOnThisDirectorAsync(request, ct).ConfigureAwait(false);
                    newId = string.IsNullOrWhiteSpace(created.SessionId) ? null : created.SessionId;
                    if (newId is null)
                        failure = "the Gateway answered the spawn without a session id, so nothing can be said to have come back.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One seat's failure is that seat's answer, not the whole restore's: it is recorded against the
                    // seat and the next seat is attempted.
                    failure = $"the Gateway did not start it: {ex.Message}";
                }
            }

            seat.Restore!.AttemptedAtUtc = _utcNow();
            if (failure is null)
            {
                seat.RestoredSessionId = newId;
                seat.Restore.Failure = null;
                if (order.Seeds is not null && order.Seeds.TryGetValue(sid, out var seed) && !string.IsNullOrWhiteSpace(seed))
                    seat.RestoredSeedFile = seed;
                FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(sid)} \"{seat.Name}\" restored as {newId}, owner={owner ?? "user"}");
            }
            else
            {
                failedHere[sid] = failure;
                seat.Restore.Failure = failure;
                FileLog.Write($"[DirectorRestore] seat {DrainPaths.ShortId(sid)} \"{seat.Name}\" NOT restored: {failure}");
            }
            outcomes.Add(new SeatRestoreOutcome(sid, seat.Name, newId, failure is null ? owner : null, failure));

            doc.RestoredBy = new WorkspaceRestoredBy
            {
                SessionId = order.RequestedBySessionId,
                AtUtc = _utcNow(),
                Method = $"director restore on {_directorId}",
                Note = order.RequestedBySessionId is null
                    ? "asked for by the owner; every spawn made by the Director on its own credential"
                    : "asked for by the session named here; every spawn made by the Director on its own credential",
            };
            doc = await _gateway.SaveWorkspaceAsync(doc, ct).ConfigureAwait(false);
        }

        return new DirectorRestoreResult(doc.Id, outcomes);
    }

    private static Dictionary<string, WorkspaceSeat> SeatsById(WorkspaceDocument doc)
        => doc.Seats
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
            .ToDictionary(s => s.SessionId!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The seats this run brings back: decided "restore", not already back, and - when the order names seats -
    /// named by it. Throws for a workspace that cannot be restored at all, or an order naming a seat that is not
    /// restorable, because an unexplained skip is exactly how a seat stays dead with nobody noticing.
    /// </summary>
    internal static List<WorkspaceSeat> SelectTargets(WorkspaceDocument doc, IReadOnlyCollection<string>? named)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!string.Equals(doc.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"workspace '{doc.Id}' is {doc.Origin}, not captured. A Director restores only a workspace the Gateway " +
                "captured from a running Director, because only there are the owners facts the Gateway observed " +
                "rather than names somebody typed.");

        var owed = doc.Seats
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId)
                        && s.Restore is { Decision: WorkspaceRestoreDecisions.Restore }
                        && string.IsNullOrWhiteSpace(s.RestoredSessionId))
            .ToList();

        if (named is null) return owed;

        var result = new List<WorkspaceSeat>();
        foreach (var id in named.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var seat = doc.Seats.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"workspace '{doc.Id}' has no seat '{id}'.");
            if (seat.Restore is not { Decision: WorkspaceRestoreDecisions.Restore })
                throw new InvalidOperationException(
                    $"seat '{id}' (\"{seat.Name}\") is decided \"{seat.Restore?.Decision ?? "none"}\", not \"restore\". " +
                    "Change the decision on the workspace first if it should come back.");
            if (!string.IsNullOrWhiteSpace(seat.RestoredSessionId))
                throw new InvalidOperationException(
                    $"seat '{id}' (\"{seat.Name}\") has already come back as {seat.RestoredSessionId}; a seat is restored once.");
            result.Add(seat);
        }
        return result;
    }

    /// <summary>
    /// Owners before the seats they own, when both are in this run. Otherwise the drain's own order (sort order,
    /// then name) is kept. A loop in the reporting chain cannot be ordered, so the seats in it keep their place;
    /// each then fails on its owner not having come back, which is the honest answer.
    /// </summary>
    internal static List<WorkspaceSeat> OrderSeniorsFirst(
        IReadOnlyList<WorkspaceSeat> targets, IReadOnlyDictionary<string, WorkspaceSeat> byId)
    {
        var inRun = new HashSet<string>(targets.Select(t => t.SessionId!), StringComparer.OrdinalIgnoreCase);
        int Depth(WorkspaceSeat seat)
        {
            var depth = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seat.SessionId! };
            var up = seat.ReportsTo;
            while (!string.IsNullOrWhiteSpace(up) && inRun.Contains(up) && seen.Add(up) && byId.TryGetValue(up, out var boss))
            {
                depth++;
                up = boss.ReportsTo;
            }
            return depth;
        }

        return targets
            .Select((seat, index) => (seat, index, depth: Depth(seat)))
            .OrderBy(x => x.depth)
            .ThenBy(x => x.seat.SortOrder)
            .ThenBy(x => x.index)
            .Select(x => x.seat)
            .ToList();
    }

    /// <summary>
    /// Who owns this seat when it comes back: the session id to name, or null for the user - or the reason it
    /// cannot come back. See the class comment for the four cases.
    /// </summary>
    internal static (string? Owner, string? Failure) ResolveOwner(
        WorkspaceSeat seat, IReadOnlyDictionary<string, WorkspaceSeat> byId, IReadOnlyDictionary<string, string> failedHere)
    {
        var reportsTo = seat.ReportsTo?.Trim();
        if (string.IsNullOrEmpty(reportsTo)) return (null, null);

        if (!byId.TryGetValue(reportsTo, out var boss))
            return (reportsTo, null);

        var bossName = $"{DrainPaths.ShortId(reportsTo)} \"{boss.Name}\"";
        if (!string.IsNullOrWhiteSpace(boss.RestoredSessionId))
            return (boss.RestoredSessionId, null);
        if (failedHere.TryGetValue(reportsTo, out var why))
            return (null, $"its owner {bossName} was restarted in the same drain and could not be brought back ({why}), " +
                          "so there is no session to own it. Restore the owner, then this seat.");
        if (boss.Restore is not { Decision: WorkspaceRestoreDecisions.Restore })
            return (null, $"its owner {bossName} was restarted in the same drain and is decided " +
                          $"\"{boss.Restore?.Decision ?? "none"}\", so it is not coming back and nobody would own this seat. " +
                          "Its senior re-seats it, or the owner decides who should own it.");
        return (null, $"its owner {bossName} was restarted in the same drain and has not been brought back yet. " +
                      "Restore the owner first; this seat is then started under the owner's new id.");
    }

    /// <summary>
    /// The create for one seat - the same fields the drain's old spawn line carried, with the owner resolved.
    /// Who asked is stated plainly: an agent's restore is an agent starting a session (parent = that session),
    /// the owner's is a person's.
    /// </summary>
    internal static NewSessionRequest BuildRequest(WorkspaceSeat seat, string? owner, WorkspaceRestoreOrder order)
    {
        var req = new NewSessionRequest
        {
            RepoPath = seat.RepoPath,
            Agent = seat.Agent,
            Name = seat.Name,
            Role = string.IsNullOrWhiteSpace(seat.Role) ? null : seat.Role,
            ControllerSessionId = owner,
            PrePrompt = SeedPrompt(seat, order.Seeds),
            OriginSurface = Core.Sessions.SessionOriginSurfaces.Api,
        };
        if (Guid.TryParse(seat.Mission?.Id, out var missionId)) req.MissionId = missionId;
        if (Guid.TryParse(seat.WorkflowRunId, out var runId)) req.WorkflowRunId = runId;

        if (string.IsNullOrWhiteSpace(order.RequestedBySessionId))
        {
            req.Origin = Core.Sessions.SessionOriginKinds.Human;
        }
        else
        {
            req.Origin = Core.Sessions.SessionOriginKinds.Agent;
            req.ParentSessionId = order.RequestedBySessionId;
        }
        return req;
    }

    /// <summary>The one-line seed: the seed file the restoring session wrote for this seat, or its handover.
    /// One line, pointing at a file - a long prompt parks unsubmitted in the agent's composer.</summary>
    internal static string SeedPrompt(WorkspaceSeat seat, IReadOnlyDictionary<string, string>? seeds)
    {
        if (seeds is not null && seeds.TryGetValue(seat.SessionId ?? "", out var seed) && !string.IsNullOrWhiteSpace(seed))
            return $"Read {seed} - it is your whole mandate. Follow it.";
        if (string.IsNullOrWhiteSpace(seat.HandoverPath))
            throw new InvalidOperationException(
                $"seat {seat.SessionId} (\"{seat.Name}\") has no handover and no seed file, so a restored session would " +
                "start with nothing to read.");
        return $"Read {seat.HandoverPath} - it is your whole mandate. You are a RESTORED session with no transcript; " +
               "treat that document as your own history and follow its exact next action.";
    }
}
