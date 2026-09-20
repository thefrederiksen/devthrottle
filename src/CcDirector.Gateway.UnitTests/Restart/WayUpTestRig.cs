using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// The way up with NO GATEWAY: a fake for each of its two seams, and a builder for the records it reads.
///
/// This is the whole point of the seams. Every rule the way up owns - which record may be offered, how the
/// rows are built, what the seed file says, what order is handed to the restore - is decided from a stored
/// record and nothing else, so it is provable here with no Gateway, no database and no sessions.
///
/// The fakes COUNT what they were asked, because several of these rules are about what the way up does NOT
/// do: it never asks how many sessions are running, and it never starts a session except to reopen one.
///
/// THE ONCE-ONLY REOPEN CLAIM IS ON THE RECORD, so the rig holds no process state to reset. It used to be
/// a static set inside the engine, which every rig had to clear (product issue 3230): a claim a restart
/// forgets is no claim at all, so it moved onto the workspace, and <see cref="FakeWayUpGateway"/> keeps it
/// exactly where the real Gateway's store does - on the seat.
/// </summary>
public sealed class WayUpTestRig
{
    /// <summary>The machine every record in this rig was captured on.</summary>
    public const string ThisMachine = "SOREN_NORTH";

    /// <summary>The display name of the Director these records belong to.</summary>
    public const string ThisDirector = "DevThrottle_1";

    /// <summary>Another Director on the SAME machine. Its records are never this Director's.</summary>
    public const string OtherDirector = "DevThrottle_2";

    /// <summary>The Gateway seam.</summary>
    public FakeWayUpGateway Gateway { get; } = new();

    /// <summary>The restore seam.</summary>
    public FakeWayUpRestore Restore { get; } = new();

    /// <summary>
    /// A moment every record in this rig is dated against, so an age rule is measured rather than raced. Every
    /// record built by <see cref="Record"/> defaults to being shut down at exactly this moment.
    /// </summary>
    public static readonly DateTime Now = new(2026, 9, 20, 12, 36, 0, DateTimeKind.Utc);

    /// <summary>The engine under test, reading this Director's own name.</summary>
    /// <param name="directorName">The display name this Director answers with.</param>
    /// <param name="nowUtc">The moment the engine reads as now. <see cref="Now"/> by default, which is when
    /// every record in this rig was shut down, so nothing is too old unless a test says so.</param>
    public IDirectorWayUp WayUp(string? directorName = ThisDirector, DateTime? nowUtc = null)
        => new DirectorWayUp(Gateway, Restore, ThisMachine, () => directorName, () => nowUtc ?? Now);

    /// <summary>A record of a smart shutdown belonging to this Director on this machine.</summary>
    /// <param name="id">The workspace slug.</param>
    /// <param name="atUtc">When the shutdown finished.</param>
    /// <param name="seats">Its seats.</param>
    /// <param name="reason">The owner's reason, or null.</param>
    /// <param name="directorName">Which Director it belongs to.</param>
    /// <param name="shutdownKind">Which kind of shutdown wrote it.</param>
    public static WorkspaceDocument Record(
        string id,
        DateTime atUtc,
        IEnumerable<WorkspaceSeat> seats,
        string? reason = "update to 2.9.0",
        string directorName = ThisDirector,
        string shutdownKind = WorkspaceShutdownKinds.SmartShutdown) => new()
    {
        Id = id,
        Name = id,
        Origin = WorkspaceOrigins.Captured,
        Machine = ThisMachine,
        DirectorId = "director-before-the-restart",
        DirectorName = directorName,
        ShutdownKind = shutdownKind,
        Reason = reason,
        StartedAtUtc = atUtc.AddMinutes(-10),
        CompletedAtUtc = atUtc,
        CreatedUtc = atUtc.AddMinutes(-10),
        UpdatedUtc = atUtc,
        Seats = seats.ToList(),
    };

    /// <summary>A seat that handed over and is waiting to come back.</summary>
    /// <param name="id">Its captured session id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="handover">Its handover, named by its path.</param>
    /// <param name="reportsTo">The captured session id of the seat it reports to, or null.</param>
    /// <param name="mission">The mission it was on.</param>
    /// <param name="role">Its role.</param>
    /// <param name="sortOrder">Where it sits in the Director's own order.</param>
    public static WorkspaceSeat Owed(
        string id,
        string name,
        string handover = @"C:\handovers\seat.md",
        string? reportsTo = null,
        string? mission = null,
        string? role = null,
        int sortOrder = 0) => new()
    {
        SessionId = id,
        Name = name,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle",
        ReportsTo = reportsTo,
        Role = role,
        SortOrder = sortOrder,
        Mission = mission is null ? null : new WorkspaceMissionRef { Name = mission },
        HandoverPath = handover,
        DrainState = WorkspaceDrainStates.Drained,
        ClaudeSessionId = $"conversation-of-{id}",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Why = "it was mid-task" },
    };

    /// <summary>A seat the smart shutdown ended when time was up, with no handover.</summary>
    /// <param name="id">Its captured session id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="agent">The agent it was running.</param>
    /// <param name="conversationId">Its saved conversation, or null when none was recorded.</param>
    /// <param name="drainState">Ended at the limit, or never answered.</param>
    public static WorkspaceSeat Ended(
        string id,
        string name,
        string agent = "ClaudeCode",
        string? conversationId = "the-saved-conversation",
        string drainState = WorkspaceDrainStates.EndedAtLimit) => new()
    {
        SessionId = id,
        Name = name,
        Agent = agent,
        RepoPath = @"D:\ReposFred\devthrottle",
        DrainState = drainState,
        ClaudeSessionId = conversationId,
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
    };

    /// <summary>A seat that has already come back, so nothing is owed for it.</summary>
    /// <param name="id">Its captured session id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="restoredAs">The session it came back as.</param>
    public static WorkspaceSeat AlreadyBack(string id, string name, string restoredAs = "11112222-3333")
    {
        var seat = Owed(id, name);
        seat.RestoredSessionId = restoredAs;
        return seat;
    }

    /// <summary>
    /// A seat that ended without a handover and HAS ALREADY BEEN DEALT WITH: its saved conversation was
    /// reopened, and the record says so. This is the state that stops it being offered again after a restart
    /// (product issue 3230).
    /// </summary>
    /// <param name="id">Its captured session id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="reopenedAs">The session the conversation was reopened as, or null when only the claim was
    /// ever recorded - which is what a start whose answer never came back leaves behind.</param>
    /// <param name="atUtc">When it was reopened.</param>
    public static WorkspaceSeat AlreadyReopened(
        string id, string name, string? reopenedAs = "77778888-9999", DateTime? atUtc = null)
    {
        var seat = Ended(id, name);
        seat.Restore!.ReopenedAtUtc = atUtc ?? Now.AddMinutes(-5);
        seat.Restore.ReopenedByDirectorId = "director-after-the-restart";
        seat.Restore.ReopenedSessionId = reopenedAs;
        return seat;
    }
}

/// <summary>The Gateway seam, faked. It holds records and counts what it was asked.</summary>
public sealed class FakeWayUpGateway : IWayUpGateway
{
    private readonly List<WorkspaceDocument> _records = new();

    /// <summary>Thrown by every call when it is set: the Gateway that will not answer.</summary>
    public string? Unreachable { get; set; }

    /// <summary>How many times the whole list was asked for.</summary>
    public int Listed { get; private set; }

    /// <summary>Which documents were read, in order. The cap on start-up reads is proved by counting these.</summary>
    public List<string> Read { get; } = new();

    /// <summary>Every session this fake was asked to start.</summary>
    public List<NewSessionRequest> Started { get; } = new();

    /// <summary>The session id the next start answers with.</summary>
    public string NextSessionId { get; set; } = "99990000-aaaa";

    /// <summary>Records the Gateway lists but no longer holds, to stand in for one deleted between the two calls.</summary>
    public HashSet<string> ListedButGone { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the roster was asked for. The start-up check must never ask: a check on what
    /// is RUNNING is exactly what the presence check is not.</summary>
    public int RosterAsked { get; private set; }

    /// <summary>The sessions the roster answers with. Empty by default: nothing is running.</summary>
    public List<SessionDto> RosterSessions { get; } = new();

    /// <summary>Each Director's reachability on the roster. A Director absent from this list is unreachable.</summary>
    public List<DirectorReachabilityDto> RosterDirectors { get; } = new();

    /// <summary>Put a session on the roster, running on a Director the Gateway can reach.</summary>
    /// <param name="sessionId">The session id, which is a seat's captured session id when it is still alive.</param>
    /// <param name="directorId">The Director it is running on.</param>
    public FakeWayUpGateway Running(string sessionId, string directorId = "some-other-director")
    {
        RosterSessions.Add(new SessionDto { SessionId = sessionId, DirectorId = directorId, Name = sessionId });
        if (!RosterDirectors.Any(d => string.Equals(d.DirectorId, directorId, StringComparison.OrdinalIgnoreCase)))
            RosterDirectors.Add(new DirectorReachabilityDto { DirectorId = directorId, State = DirectorReachabilityDto.StateOnline });
        return this;
    }

    /// <summary>Put a record on this Gateway.</summary>
    /// <param name="doc">The record.</param>
    public FakeWayUpGateway With(WorkspaceDocument doc)
    {
        _records.Add(doc);
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkspaceSummaryDto>> ListWorkspacesAsync(CancellationToken ct)
    {
        if (Unreachable is not null) throw new HttpRequestException(Unreachable);
        Listed++;
        IReadOnlyList<WorkspaceSummaryDto> rows = _records
            .Select(d => new WorkspaceSummaryDto
            {
                Id = d.Id,
                Name = d.Name,
                Origin = d.Origin,
                Machine = d.Machine,
                DirectorName = d.DirectorName,
                SeatCount = d.Seats.Count,
                CreatedUtc = d.CreatedUtc,
                UpdatedUtc = d.UpdatedUtc,
            })
            .ToList();
        return Task.FromResult(rows);
    }

    /// <inheritdoc />
    public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
    {
        if (Unreachable is not null) throw new HttpRequestException(Unreachable);
        Read.Add(id);
        if (ListedButGone.Contains(id)) return Task.FromResult<WorkspaceDocument?>(null);
        return Task.FromResult(_records.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc />
    public Task<SessionDto> StartSessionAsync(NewSessionRequest request, CancellationToken ct)
    {
        if (Unreachable is not null) throw new HttpRequestException(Unreachable);
        Started.Add(request);
        return Task.FromResult(new SessionDto { SessionId = NextSessionId, Name = request.Name ?? "" });
    }

    /// <inheritdoc />
    public Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
    {
        if (Unreachable is not null) throw new HttpRequestException(Unreachable);
        RosterAsked++;
        return Task.FromResult(new RestoreRoster(RosterSessions, RosterDirectors));
    }

    /// <summary>Every reopen mark this fake was asked to write, in order: the seat, and the session id if the
    /// mark carried one. The engine claims first with no id and fills the id in afterwards, so a test reads
    /// the ORDER here to prove the claim went in before anything was started.</summary>
    public List<(string Seat, string? SessionId)> ReopenMarks { get; } = new();

    /// <summary>
    /// When set, every reopen mark is refused with this reason instead of being written - which is what a
    /// Gateway too old to know the mark does (it answers HTTP 400 naming the kinds it knows).
    /// </summary>
    public string? RefuseReopenMarks { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// IT KEEPS THE CLAIM WHERE THE REAL STORE KEEPS IT - on the seat - rather than in a set of its own, so a
    /// test that marks a seat and then asks for the offer again is reading the same fact the product reads.
    /// The rules are the store's: the first mark claims it, the Director that claimed it may fill in the
    /// session id, and anything else is refused as already dealt with.
    /// </remarks>
    public Task<WayUpMarkOutcome> MarkReopenedAsync(
        string workspaceId, string seatSessionId, string? reopenedSessionId, CancellationToken ct)
    {
        if (Unreachable is not null) throw new HttpRequestException(Unreachable);
        ReopenMarks.Add((seatSessionId, reopenedSessionId));

        if (RefuseReopenMarks is not null)
            return Task.FromResult(new WayUpMarkOutcome(WayUpMarkState.Refused, RefuseReopenMarks));

        var doc = _records.FirstOrDefault(d => string.Equals(d.Id, workspaceId, StringComparison.OrdinalIgnoreCase));
        var seat = doc?.Seats.FirstOrDefault(x => string.Equals(x.SessionId, seatSessionId, StringComparison.OrdinalIgnoreCase));
        if (seat is null)
            return Task.FromResult(new WayUpMarkOutcome(
                WayUpMarkState.Refused, $"workspace \"{workspaceId}\" has no seat '{seatSessionId}'."));

        var restore = seat.Restore ??= new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };
        if (restore.ReopenedAtUtc is null)
        {
            restore.ReopenedAtUtc = WayUpTestRig.Now;
            restore.ReopenedByDirectorId = MarkingDirectorId;
            restore.ReopenedSessionId = reopenedSessionId;
            return Task.FromResult(new WayUpMarkOutcome(WayUpMarkState.Marked, null));
        }

        if (string.IsNullOrWhiteSpace(restore.ReopenedSessionId) && !string.IsNullOrWhiteSpace(reopenedSessionId)
            && string.Equals(restore.ReopenedByDirectorId, MarkingDirectorId, StringComparison.OrdinalIgnoreCase))
        {
            restore.ReopenedSessionId = reopenedSessionId;
            return Task.FromResult(new WayUpMarkOutcome(WayUpMarkState.Marked, null));
        }

        return Task.FromResult(new WayUpMarkOutcome(
            WayUpMarkState.AlreadyDealtWith,
            $"seat '{seatSessionId}' had its saved conversation reopened on {restore.ReopenedAtUtc:u}."));
    }

    /// <summary>The Director this fake stamps a reopen claim with. The real seam reads it from the Director
    /// itself, which is why the engine never supplies it.</summary>
    public string MarkingDirectorId { get; set; } = "director-after-the-restart";
}

/// <summary>The restore seam, faked. It records the order it was handed and answers what the test sets.</summary>
public sealed class FakeWayUpRestore : IWayUpRestore
{
    /// <summary>Every order this restore was handed.</summary>
    public List<WorkspaceRestoreOrder> Orders { get; } = new();

    /// <summary>When set, the restore refuses with this reason and starts nothing.</summary>
    public string? Refusal { get; set; }

    /// <summary>The outcome each seat gets, by captured session id. A seat with no entry came back.</summary>
    public Dictionary<string, string> FailureBySeat { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<DirectorRestoreResult> RestoreAsync(WorkspaceRestoreOrder order, CancellationToken ct)
    {
        Orders.Add(order);
        if (Refusal is not null) throw new InvalidOperationException(Refusal);

        var seats = (order.Seats ?? new List<string>())
            .Select(id => FailureBySeat.TryGetValue(id, out var why)
                ? new SeatRestoreOutcome(id, id, null, null, why)
                : new SeatRestoreOutcome(id, id, $"back-{id}", null, null))
            .ToList();
        return Task.FromResult(new DirectorRestoreResult(order.WorkspaceId, seats));
    }
}
