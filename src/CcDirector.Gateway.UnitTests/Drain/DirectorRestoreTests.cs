using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The restore as a Director act (the Message Load mission, slice 6, and its fix round for inspection 7): the REAL
/// <see cref="DirectorRestore"/> against a fake Gateway whose workspace is the REAL <see cref="WorkspaceStore"/> over
/// a throwaway database - so every write the restore makes, and every write a caller makes around it, goes through
/// the same provenance, lease and token rules the hosted Gateway applies. The fake records every spawn, performs the
/// spawn door's token record, and serves a roster the test controls. The route test
/// (<c>WorkspaceRestoreRouteTests</c>) proves the same spawn on a real host.
///
/// In the collection every test that takes one of the Director's one-at-a-time gates shares: a restore holds a
/// process-wide gate, and the smart shutdown's cancel tests now run a real restore too, so side by side each
/// would refuse the other.
/// </summary>
[Collection(CcDirector.Gateway.UnitTests.Restart.DirectorGatesCollection.Name)]
public sealed class DirectorRestoreTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private const string ThisDirector = "new-director";
    private const string OtherDirector = "other-director";
    private const string DeadDirector = "old-director";

    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Thrown by the fake to stand in for the Director process dying mid-restore.</summary>
    private sealed class DirectorDied : Exception
    {
        public DirectorDied() : base("the Director process died") { }
    }

    private sealed class FakeGateway : IRestoreGateway
    {
        private int _next;

        public FakeGateway(WorkspaceStore store, WorkspaceDocument doc)
        {
            Store = store;
            WorkspaceId = doc.Id;
            Store.Create(doc, Now);
        }

        public WorkspaceStore Store { get; }
        public string WorkspaceId { get; }

        /// <summary>The Gateway's own clock, for the store's writes and the lease.</summary>
        public DateTime StoreNow { get; set; } = Now;

        public List<NewSessionRequest> Spawns { get; } = new();

        /// <summary>Spawns whose name is here are refused outright (HTTP 409) with this reason.</summary>
        public Dictionary<string, string> RefuseByName { get; } = new();

        /// <summary>Spawns whose name is here fail with a relay error (HTTP 502): nothing proves they did not start.</summary>
        public Dictionary<string, string> RelayErrorByName { get; } = new();

        /// <summary>Spawns whose name is here time out on the client side BEFORE the Gateway creates anything.</summary>
        public HashSet<string> TimeOutByName { get; } = new();

        /// <summary>Spawns whose name is here are CREATED and recorded by the Gateway, and then the client times out.</summary>
        public HashSet<string> CreateThenTimeOutByName { get; } = new();

        /// <summary>Spawns whose name is here are created and recorded, and the Director dies before it writes
        /// anything more.</summary>
        public HashSet<string> CreateThenDieByName { get; } = new();

        private bool _dieOnNextMark;

        /// <summary>The roster: sessions, and which Directors are reachable.</summary>
        public List<SessionDto> Live { get; } = new();
        public Dictionary<string, string> DirectorStates { get; } = new()
        {
            [ThisDirector] = DirectorReachabilityDto.StateOnline,
            [OtherDirector] = DirectorReachabilityDto.StateOnline,
            [DeadDirector] = DirectorReachabilityDto.StateOffline,
        };

        public WorkspaceDocument Stored => Store.Get(WorkspaceId)!;

        /// <summary>A new Director process after the one that died: the lease it held is released by hand here,
        /// as its expiry would.</summary>
        public void Revive()
        {
            _dieOnNextMark = false;
            Store.ReleaseRestoreLease(WorkspaceId, ThisDirector);
        }

        /// <summary>What the restore route does before relaying.</summary>
        public void Lease(string director = ThisDirector) => Store.TakeRestoreLease(WorkspaceId, director, null, StoreNow);

        public void AddLive(string sid, string director, string? name = null, DateTime? created = null)
            => Live.Add(new SessionDto { SessionId = sid, DirectorId = director, Name = name, CreatedAt = created ?? StoreNow });

        public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
            => Task.FromResult(Store.Get(id));

        public Task<WorkspaceDocument> RecordMarkAsync(string workspaceId, WorkspaceRestoreMark mark, CancellationToken ct)
        {
            if (_dieOnNextMark) throw new DirectorDied();
            // Through JSON, as the route reads it.
            var wire = JsonSerializer.Deserialize<WorkspaceRestoreMark>(JsonSerializer.Serialize(mark, Json), Json)!;
            try
            {
                return Task.FromResult(Store.RecordRestoreMark(workspaceId, wire, StoreNow));
            }
            catch (WorkspaceConflictException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        public Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
            => Task.FromResult(new RestoreRoster(
                Live.ToList(),
                DirectorStates.Select(kv => new DirectorReachabilityDto { DirectorId = kv.Key, State = kv.Value }).ToList()));

        /// <summary>The REAL dev report store, over the same database as the workspace.</summary>
        public required DevReportStore Reports { get; init; }

        /// <summary>Every dev report pass the restore asked for, as it arrived.</summary>
        public List<WorkspaceDevReportPassRequest> PassRequests { get; } = new();

        /// <summary>When set, the Gateway refuses every dev report pass with this reason.</summary>
        public string? RefuseThePassWith { get; set; }

        public Task<WorkspaceDevReportPassResult> PassDevReportsAsync(string workspaceId, WorkspaceDevReportPassRequest request, CancellationToken ct)
        {
            // Through JSON, as the route reads it, and then through the REAL rule over the REAL stored workspace.
            var wire = JsonSerializer.Deserialize<WorkspaceDevReportPassRequest>(JsonSerializer.Serialize(request, Json), Json)!;
            PassRequests.Add(wire);
            if (RefuseThePassWith is { } why) throw new InvalidOperationException(why);
            try
            {
                return Task.FromResult(DevReportInheritance.Pass(Store.Get(workspaceId)!, wire, Reports, TenantId.Local, StoreNow));
            }
            catch (Exception ex) when (ex is WorkspaceConflictException or WorkspaceValidationException)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
        {
            Spawns.Add(JsonSerializer.Deserialize<NewSessionRequest>(JsonSerializer.Serialize(request, Json), Json)!);
            var name = request.Name ?? "";
            if (TimeOutByName.Contains(name))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
            if (RefuseByName.TryGetValue(name, out var why))
                throw new GatewaySpawnFailedException(409, why);
            if (RelayErrorByName.TryGetValue(name, out var relay))
                throw new GatewaySpawnFailedException(502, relay);

            // The spawn door: the create happens, the session appears, and the claim is recorded by its token.
            _next++;
            var id = $"new-{_next}";
            AddLive(id, ThisDirector, name);
            if (request.RestoreClaim is { } claim)
                Store.RecordRestoredByClaim(claim, ThisDirector, id, StoreNow);

            if (CreateThenTimeOutByName.Contains(name))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
            if (CreateThenDieByName.Contains(name))
                _dieOnNextMark = true;
            return Task.FromResult(new SessionDto { SessionId = id, Name = request.Name });
        }
    }

    private static WorkspaceSeat Seat(string id, string name, string? reportsTo = null, string decision = WorkspaceRestoreDecisions.Restore, int order = 0)
        => new()
        {
            SessionId = id,
            Name = name,
            Agent = "ClaudeCode",
            RepoPath = "/repos/devthrottle",
            Role = reportsTo is null ? "Manager" : "Worker",
            ReportsTo = reportsTo,
            SortOrder = order,
            HandoverPath = $"/handovers/{id}.md",
            DrainState = WorkspaceDrainStates.Drained,
            Restore = new WorkspaceSeatRestore { Decision = decision, Why = "test", Command = "cc-devthrottle director restore ..." },
        };

    private static WorkspaceSeat Blocked(WorkspaceSeat seat, DateTime? closed)
    {
        seat.DrainState = WorkspaceDrainStates.Blocked;
        seat.BlockedReason = "mid-merge";
        seat.ClosedAtUtc = closed;
        return seat;
    }

    private static WorkspaceDocument Doc(params WorkspaceSeat[] seats) => new()
    {
        Id = "drain-1",
        Name = "Drain",
        Origin = WorkspaceOrigins.Captured,
        DirectorId = DeadDirector,
        Machine = "MAC",
        Seats = seats.ToList(),
        RestoreAfterRestart = seats.Where(s => s.Restore!.Decision == WorkspaceRestoreDecisions.Restore).Select(s => s.SessionId!).ToList(),
    };

    private FakeGateway Gateway(params WorkspaceSeat[] seats) => Gateway(Doc(seats));

    /// <summary>One database under both stores, as on a Gateway: the workspace and the dev reports it names.</summary>
    private FakeGateway Gateway(WorkspaceDocument doc)
    {
        var db = _h.Open();
        return new FakeGateway(new WorkspaceStore(db), doc) { Reports = new DevReportStore(db) };
    }

    /// <summary>A restore as the route starts one: the lease is granted to this Director first.</summary>
    private static DirectorRestore NewRestore(FakeGateway gw, DateTime? clock = null)
    {
        gw.Lease();
        var at = clock ?? Now;
        return new DirectorRestore(gw, ThisDirector, () => at);
    }

    private static WorkspaceRestoreOrder Order(string? askedBy = "restoring-session", List<string>? seats = null,
        Dictionary<string, string>? seeds = null, List<string>? force = null)
        => new() { WorkspaceId = "drain-1", RequestedBySessionId = askedBy, Seats = seats, Seeds = seeds, ForceSeats = force };

    // ================= the placeholder, resolved in order =================

    [Fact]
    public async Task RunAsync_AWorkerListedBeforeItsManager_ComesBackAfterIt_OwnedByTheManagersNewId()
    {
        // The worker is FIRST in the record. Restoring in record order would start it before its owner had an id.
        var gw = Gateway(
            Seat("worker", "M - Worker", reportsTo: "manager", order: 0),
            Seat("manager", "M - Manager", order: 1));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "M - Manager", "M - Worker" }, gw.Spawns.Select(s => s.Name));
        Assert.Null(gw.Spawns[0].ControllerSessionId);
        Assert.Equal("new-1", gw.Spawns[1].ControllerSessionId);
        Assert.Equal("new-1", result.Seats.Single(s => s.SessionId == "worker").OwnerSessionId);

        var stored = gw.Stored.Seats.ToDictionary(s => s.SessionId!);
        Assert.Equal("new-1", stored["manager"].RestoredSessionId);
        Assert.Equal("new-2", stored["worker"].RestoredSessionId);
        Assert.Equal(Now, stored["worker"].Restore!.AttemptedAtUtc);
        Assert.Null(stored["worker"].Restore!.Failure);
        Assert.Null(gw.Stored.RestoreLease);
    }

    [Fact]
    public async Task RunAsync_AThreeLevelChain_ResolvesEachPlaceholderToTheLevelAbove()
    {
        var gw = Gateway(
            Seat("w", "Worker", reportsTo: "m"),
            Seat("m", "Manager", reportsTo: "a"),
            Seat("a", "Architect"));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "Architect", "Manager", "Worker" }, gw.Spawns.Select(s => s.Name));
        Assert.Equal("new-1", gw.Spawns[1].ControllerSessionId);
        Assert.Equal("new-2", gw.Spawns[2].ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerRestoredInAnEarlierRun_AndStillRunning_IsUsedByItsNewId()
    {
        var gw = Gateway(Seat("manager", "Manager"), Seat("worker", "Worker", reportsTo: "manager"));
        await NewRestore(gw).RunAsync(Order(seats: new() { "manager" }));
        var managerNewId = gw.Stored.Seats.Single(s => s.SessionId == "manager").RestoredSessionId;

        await NewRestore(gw).RunAsync(Order(seats: new() { "worker" }));

        Assert.Equal(2, gw.Spawns.Count);
        Assert.Equal("Worker", gw.Spawns[1].Name);
        Assert.Equal(managerNewId, gw.Spawns[1].ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerRestoredInAnEarlierRun_ThatHasSinceStopped_FailsTheWorker()
    {
        var gw = Gateway(Seat("manager", "Manager"), Seat("worker", "Worker", reportsTo: "manager"));
        await NewRestore(gw).RunAsync(Order(seats: new() { "manager" }));
        gw.Live.Clear();

        var result = await NewRestore(gw).RunAsync(Order(seats: new() { "worker" }));

        Assert.Single(gw.Spawns);
        Assert.Contains("is not running", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_AnOwnerOnAnotherDirector_ThatIsRunning_KeepsItsRealId()
    {
        const string outside = "11111111-2222-3333-4444-555555555555";
        var gw = Gateway(Seat("worker", "Worker", reportsTo: outside));
        gw.AddLive(outside, OtherDirector);

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal(outside, Assert.Single(gw.Spawns).ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerOnAnotherDirector_ThatIsNotRunning_FailsTheSeat_AndStartsNothing()
    {
        // Inspection 7, ruling 5: the outside owner was stopped since the capture.
        const string outside = "11111111-2222-3333-4444-555555555555";
        var gw = Gateway(Seat("worker", "Worker", reportsTo: outside));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("is not running", Assert.Single(result.Seats).Failure);
        Assert.Contains("is not running", gw.Stored.Seats.Single().Restore!.Failure);
    }

    [Fact]
    public async Task RunAsync_AnOwnerOnAnUnreachableDirector_IsNotTakenForRunning()
    {
        const string outside = "11111111-2222-3333-4444-555555555555";
        var gw = Gateway(Seat("worker", "Worker", reportsTo: outside));
        gw.AddLive(outside, DeadDirector);

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("is not running", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_AnOwnerThatBlockedTheDrain_AndIsStillRunning_KeepsItsId()
    {
        // The restore after a blocked drain: no restart happened, the owner would not stop, its worker did.
        var gw = Gateway(Blocked(Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Undecided), closed: null),
            Seat("w", "Worker", reportsTo: "m"));
        gw.AddLive("m", ThisDirector);

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal("m", Assert.Single(gw.Spawns).ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerThatBlockedTheDrain_ButWasClosedByHandWithoutARecord_FailsTheWorker()
    {
        // Inspection 7, ruling 5: the record still says blocked and never closed; the roster says it is gone.
        var gw = Gateway(Blocked(Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Undecided), closed: null),
            Seat("w", "Worker", reportsTo: "m"));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("is not running", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_ABlockedOwnerThatWasClosedAfterAll_IsNotTakenForRunning()
    {
        var gw = Gateway(Blocked(Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Undecided), closed: Now),
            Seat("w", "Worker", reportsTo: "m"));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.NotNull(Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_ASeatWithNoOwner_IsTheUsers_AndTheRequestSaysWhoAsked()
    {
        var gw = Gateway(Seat("solo", "Solo"));

        await NewRestore(gw).RunAsync(Order(askedBy: "restoring-session"));

        var spawn = Assert.Single(gw.Spawns);
        Assert.Null(spawn.ControllerSessionId);
        Assert.Equal("agent", spawn.Origin);
        Assert.Equal("restoring-session", spawn.ParentSessionId);
        Assert.Equal("/repos/devthrottle", spawn.RepoPath);
        Assert.Equal("ClaudeCode", spawn.Agent);
        Assert.Contains("/handovers/solo.md", spawn.PrePrompt);
        Assert.Equal("restoring-session", gw.Stored.RestoredBy!.SessionId);
    }

    [Fact]
    public async Task RunAsync_AskedByTheOwner_IsAPersonsSpawn()
    {
        var gw = Gateway(Seat("solo", "Solo"));

        await NewRestore(gw).RunAsync(Order(askedBy: null));

        var spawn = Assert.Single(gw.Spawns);
        Assert.Equal("human", spawn.Origin);
        Assert.Null(spawn.ParentSessionId);
    }

    [Fact]
    public async Task RunAsync_ASeedFileGiven_IsTheSeed_AndIsRecorded()
    {
        var gw = Gateway(Seat("solo", "Solo"));

        await NewRestore(gw).RunAsync(Order(seeds: new() { ["solo"] = "/index/SEED-solo.md" }));

        Assert.Equal("Read /index/SEED-solo.md - it is your whole mandate. Follow it.", Assert.Single(gw.Spawns).PrePrompt);
        Assert.Equal("/index/SEED-solo.md", gw.Stored.Seats.Single().RestoredSeedFile);
    }

    [Fact]
    public async Task RunAsync_EveryCreateCarriesTheTokenStoredOnItsSeatBeforeItWasSent()
    {
        var gw = Gateway(Seat("solo", "Solo"));

        await NewRestore(gw).RunAsync(Order());

        var claim = Assert.Single(gw.Spawns).RestoreClaim!;
        Assert.Equal("drain-1", claim.WorkspaceId);
        Assert.Equal("solo", claim.SeatSessionId);
        var restore = gw.Stored.Seats.Single().Restore!;
        Assert.Equal(restore.StartedToken, claim.Token);
        Assert.Equal(ThisDirector, restore.StartedByDirectorId);
    }

    // ================= inspection 7, ruling 1: restored ids are provenance =================

    [Fact]
    public async Task RunAsync_ASessionPutsAnotherSessionAsTheBossesRestoredId_TheWorkerIsNeverOwnedByIt()
    {
        // The inspector's sequence: boss B and worker W reporting to B; an unrelated writer PUTs B's restoredSessionId
        // to X and asks for W alone. B is not back, so W must not start under X - and once B really comes back, W is
        // owned by B's restored id.
        const string x = "99999999-9999-9999-9999-999999999999";
        var gw = Gateway(Seat("b", "Boss"), Seat("w", "Worker", reportsTo: "b"));
        gw.AddLive(x, OtherDirector);

        var copy = gw.Stored;
        copy.Seats.Single(s => s.SessionId == "b").RestoredSessionId = x;
        gw.Store.Save(copy, Now);
        Assert.Null(gw.Stored.Seats.Single(s => s.SessionId == "b").RestoredSessionId);

        var first = await NewRestore(gw).RunAsync(Order(seats: new() { "w" }));
        Assert.Empty(gw.Spawns);
        Assert.Contains("has not been brought back yet", Assert.Single(first.Seats).Failure);

        await NewRestore(gw).RunAsync(Order(seats: new() { "b" }));
        await NewRestore(gw).RunAsync(Order(seats: new() { "w" }));

        var bossNewId = gw.Stored.Seats.Single(s => s.SessionId == "b").RestoredSessionId;
        Assert.Equal("new-1", bossNewId);
        Assert.Equal(bossNewId, gw.Spawns.Single(s => s.Name == "Worker").ControllerSessionId);
        Assert.DoesNotContain(gw.Spawns, s => s.ControllerSessionId == x);
    }

    // ================= inspection 7, ruling 2: only a drained seat comes back =================

    [Fact]
    public async Task PrepareAsync_ANamedSeatStillRunning_IsRefused_AndStartsOnceItHasClosed()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.AddLive("a", OtherDirector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order(seats: new() { "a" })));
        Assert.Contains("still running", ex.Message);

        var run = await NewRestore(gw).RunAsync(Order(seats: new() { "a" }));
        Assert.Empty(gw.Spawns);
        Assert.Contains("still running", Assert.Single(run.Seats).Failure);

        gw.Live.Clear();
        await NewRestore(gw).RunAsync(Order(seats: new() { "a" }));
        Assert.Equal("Alpha", Assert.Single(gw.Spawns).Name);
    }

    [Fact]
    public async Task RunAsync_AllOwed_ARunningSeatIsReportedOnItsRecord_AndTheRestComeBack()
    {
        var gw = Gateway(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1));
        gw.AddLive("a", ThisDirector);

        Assert.Equal(new[] { "a", "b" }, await NewRestore(gw).PrepareAsync(Order()));
        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal("Bravo", Assert.Single(gw.Spawns).Name);
        Assert.Contains("still running", result.Seats.Single(s => s.SessionId == "a").Failure);
        Assert.Contains("still running", gw.Stored.Seats.Single(s => s.SessionId == "a").Restore!.Failure);
    }

    [Fact]
    public async Task PrepareAsync_EverySeatStillRunning_IsRefused()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.AddLive("a", ThisDirector);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order()));

        Assert.Contains("no seat that can come back now", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ASeatListedUnderAnUnreachableDirector_ComesBackOnlyIfTheDrainRecordedItClosed()
    {
        var open = Seat("a", "Alpha");
        var closed = Seat("b", "Bravo");
        closed.ClosedAtUtc = Now;
        var gw = Gateway(open, closed);
        gw.AddLive("a", DeadDirector);
        gw.AddLive("b", DeadDirector);

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal("Bravo", Assert.Single(gw.Spawns).Name);
        Assert.Contains("cannot reach", result.Seats.Single(s => s.SessionId == "a").Failure);
    }

    [Fact]
    public async Task RunAsync_ASeatRecordedClosedButStillRunningOnAReachableDirector_IsNotStartedAgain()
    {
        // closedAtUtc is a drain judgment a writer can set; the roster outranks it.
        var seat = Seat("a", "Alpha");
        seat.ClosedAtUtc = Now;
        var gw = Gateway(seat);
        gw.AddLive("a", OtherDirector);

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("still running", Assert.Single(result.Seats).Failure);
    }

    // ================= inspection 7, ruling 3: a seat starts at most once =================

    [Fact]
    public async Task RunAsync_ATimedOutStartAskedAgain_IsNeverCreatedTwice_UntilForced()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.TimeOutByName.Add("Alpha");

        var first = await NewRestore(gw).RunAsync(Order());
        Assert.Equal(DirectorRestore.TimedOut, Assert.Single(first.Seats).Failure);
        Assert.NotNull(gw.Stored.Seats.Single().Restore!.StartedToken);
        gw.TimeOutByName.Clear();

        // Straight away: the start may still be landing.
        var soon = await NewRestore(gw).RunAsync(Order());
        Assert.Contains("still in progress", Assert.Single(soon.Seats).Failure);

        // Later: not known to have started, and not started again blind.
        var later = await NewRestore(gw, Now.AddMinutes(10)).RunAsync(Order());
        Assert.Contains("MAY have been started already", Assert.Single(later.Seats).Failure);
        Assert.Contains("--force-seat a", later.Seats.Single().Failure);
        Assert.Single(gw.Spawns);

        // The caller checked and forces it.
        var forced = await NewRestore(gw, Now.AddMinutes(10)).RunAsync(Order(force: new() { "a" }));
        Assert.Null(Assert.Single(forced.Seats).Failure);
        Assert.Equal(2, gw.Spawns.Count);
        Assert.NotEqual(gw.Spawns[0].RestoreClaim!.Token, gw.Spawns[1].RestoreClaim!.Token);
    }

    [Fact]
    public async Task RunAsync_ATimeoutAfterTheGatewayCreatedTheSeat_IsResolvedByItsToken_AndNotCreatedAgain()
    {
        var gw = Gateway(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1));
        gw.CreateThenTimeOutByName.Add("Alpha");

        var result = await NewRestore(gw).RunAsync(Order());

        // The Director never heard back, but the Gateway recorded the create, and the late failure did not erase it.
        Assert.Equal(DirectorRestore.TimedOut, result.Seats.Single(s => s.SessionId == "a").Failure);
        var alpha = gw.Stored.Seats.Single(s => s.SessionId == "a");
        Assert.Equal("new-1", alpha.RestoredSessionId);
        Assert.Null(alpha.Restore!.Failure);
        Assert.NotNull(gw.Stored.Seats.Single(s => s.SessionId == "b").RestoredSessionId);

        gw.CreateThenTimeOutByName.Clear();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).RunAsync(Order(seats: new() { "a" })));
        Assert.Contains("restored once", ex.Message);
        Assert.Equal(2, gw.Spawns.Count);
    }

    [Fact]
    public async Task RunAsync_ADirectorThatDiedAfterTheCreate_LeavesTheSeatRecorded_AndTheRetryStartsNothing()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.CreateThenDieByName.Add("Alpha");

        await Assert.ThrowsAsync<DirectorDied>(() => NewRestore(gw).RunAsync(Order()));
        gw.CreateThenDieByName.Clear();
        gw.Revive();

        Assert.Equal("new-1", gw.Stored.Seats.Single().RestoredSessionId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order()));
        Assert.Contains("no seat left", ex.Message);
        Assert.Single(gw.Spawns);
    }

    [Fact]
    public async Task RunAsync_ARelayErrorIsAMaybe_AndIsNotStartedAgainWithoutForce()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.RelayErrorByName["Alpha"] = "director not connected to the tunnel";

        var first = await NewRestore(gw).RunAsync(Order());
        Assert.Contains("MAY have been", Assert.Single(first.Seats).Failure);
        gw.RelayErrorByName.Clear();

        var later = await NewRestore(gw, Now.AddMinutes(10)).RunAsync(Order());
        Assert.Contains("MAY have been started already", Assert.Single(later.Seats).Failure);
        Assert.Single(gw.Spawns);
    }

    [Fact]
    public async Task RunAsync_AnEarlierStartWithARunningCandidate_NamesIt()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.TimeOutByName.Add("Alpha");
        await NewRestore(gw).RunAsync(Order());
        gw.TimeOutByName.Clear();
        gw.AddLive("created-behind-our-back", ThisDirector, "Alpha", Now);

        var later = await NewRestore(gw, Now.AddMinutes(10)).RunAsync(Order());

        Assert.Contains("created-behind-our-back", Assert.Single(later.Seats).Failure);
        Assert.Single(gw.Spawns);
    }

    // ================= inspection 7, ruling 4: one Director per workspace =================

    [Fact]
    public async Task RunAsync_WithoutTheLease_StartsNothing()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.Lease(OtherDirector);
        var restore = new DirectorRestore(gw, ThisDirector, () => Now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RunAsync(Order()));

        Assert.Contains("does not hold the restore lease", ex.Message);
        Assert.Empty(gw.Spawns);
        Assert.Equal(OtherDirector, gw.Stored.RestoreLease!.DirectorId);
    }

    // ================= a failure per seat =================

    [Fact]
    public async Task RunAsync_OneSeatRefused_IsReportedOnThatSeat_AndTheRestStillComeBack()
    {
        var gw = Gateway(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1), Seat("c", "Charlie", order: 2));
        gw.RefuseByName["Bravo"] = "repository not found";

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, gw.Spawns.Select(s => s.Name));
        var bravo = result.Seats.Single(s => s.SessionId == "b");
        Assert.Null(bravo.RestoredSessionId);
        Assert.Contains("repository not found", bravo.Failure);

        var stored = gw.Stored.Seats.ToDictionary(s => s.SessionId!);
        Assert.Null(stored["b"].RestoredSessionId);
        Assert.Contains("repository not found", stored["b"].Restore!.Failure);
        Assert.Null(stored["b"].Restore!.StartedToken);
        Assert.Equal(Now, stored["b"].Restore!.AttemptedAtUtc);
        Assert.NotNull(stored["a"].RestoredSessionId);
        Assert.NotNull(stored["c"].RestoredSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerThatFailed_FailsItsWorkers_WithTheReason_AndNeverStartsThem()
    {
        var gw = Gateway(
            Seat("m", "Manager"),
            Seat("w", "Worker", reportsTo: "m"),
            Seat("solo", "Solo"));
        gw.RefuseByName["Manager"] = "repository not found";

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.DoesNotContain(gw.Spawns, s => s.Name == "Worker");
        Assert.Contains(gw.Spawns, s => s.Name == "Solo");
        var worker = result.Seats.Single(s => s.SessionId == "w");
        Assert.Contains("could not be brought back", worker.Failure);
        Assert.Contains("repository not found", worker.Failure);
        Assert.Contains("could not be brought back", gw.Stored.Seats.Single(s => s.SessionId == "w").Restore!.Failure);
    }

    [Fact]
    public async Task RunAsync_AnOwnerDecidedClose_FailsItsWorker_RatherThanStartingItUnowned()
    {
        var gw = Gateway(
            Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Close),
            Seat("w", "Worker", reportsTo: "m"));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("\"close\"", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_OnlyTheWorkerAsked_AndItsOwnerNotBackYet_FailsSayingRestoreTheOwnerFirst()
    {
        var gw = Gateway(Seat("m", "Manager"), Seat("w", "Worker", reportsTo: "m"));

        var result = await NewRestore(gw).RunAsync(Order(seats: new() { "w" }));

        Assert.Empty(gw.Spawns);
        Assert.Contains("Restore the owner first", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_ARefusedSeatAskedAgain_ComesBack_AndItsFailureIsCleared()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.RefuseByName["Alpha"] = "not today";
        await NewRestore(gw).RunAsync(Order());
        Assert.NotNull(gw.Stored.Seats.Single().Restore!.Failure);

        gw.RefuseByName.Clear();
        await NewRestore(gw).RunAsync(Order());

        var seat = gw.Stored.Seats.Single();
        Assert.NotNull(seat.RestoredSessionId);
        Assert.Null(seat.Restore!.Failure);
    }

    // ================= what is never restored =================

    [Fact]
    public async Task RunAsync_ASeatAlreadyBack_IsNeverStartedTwice()
    {
        var gw = Gateway(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1));
        await NewRestore(gw).RunAsync(Order(seats: new() { "a" }));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "Alpha", "Bravo" }, gw.Spawns.Select(s => s.Name));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).RunAsync(Order(seats: new() { "a" })));
        Assert.Contains("restored once", ex.Message);
        Assert.Equal(2, gw.Spawns.Count);
    }

    [Fact]
    public async Task PrepareAsync_AnAuthoredWorkspace_IsRefused_BecauseItsOwnersAreTyped()
    {
        var doc = Doc(Seat("w", "Worker", reportsTo: "someone"));
        var restore = new DirectorRestore(new AuthoredOnly(doc), ThisDirector, () => Now);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.PrepareAsync(Order()));

        Assert.Contains("not captured", ex.Message);
    }

    /// <summary>An authored document as a store would hand it back - the store itself refuses the drain fields on one.</summary>
    private sealed class AuthoredOnly(WorkspaceDocument doc) : IRestoreGateway
    {
        public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
        {
            doc.Origin = WorkspaceOrigins.Authored;
            return Task.FromResult<WorkspaceDocument?>(doc);
        }

        public Task<WorkspaceDocument> RecordMarkAsync(string workspaceId, WorkspaceRestoreMark mark, CancellationToken ct)
            => throw new InvalidOperationException("not reached");

        public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("not reached");

        public Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
            => Task.FromResult(new RestoreRoster(Array.Empty<SessionDto>(), Array.Empty<DirectorReachabilityDto>()));

        public Task<WorkspaceDevReportPassResult> PassDevReportsAsync(string workspaceId, WorkspaceDevReportPassRequest request, CancellationToken ct)
            => throw new InvalidOperationException("not reached");
    }

    [Fact]
    public async Task PrepareAsync_NothingLeftToBringBack_IsRefused_AndNamesTheSeatsOtherwise()
    {
        var gw = Gateway(Seat("a", "Alpha", decision: WorkspaceRestoreDecisions.Close));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order()));
        Assert.Contains("no seat left", ex.Message);

        var gw2 = Gateway(WithId(Doc(Seat("a", "Alpha"), Seat("b", "Bravo")), "drain-2"));
        gw2.Lease();
        var restore2 = new DirectorRestore(gw2, ThisDirector, () => Now);
        Assert.Equal(new[] { "a", "b" }, await restore2.PrepareAsync(new WorkspaceRestoreOrder { WorkspaceId = "drain-2" }));
    }

    private static WorkspaceDocument WithId(WorkspaceDocument doc, string id)
    {
        doc.Id = id;
        return doc;
    }

    [Fact]
    public async Task PrepareAsync_ASeatNotDecidedRestore_OrUnknown_IsRefusedByName()
    {
        var gw = Gateway(Seat("a", "Alpha", decision: WorkspaceRestoreDecisions.Close));

        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order(seats: new() { "a" })));
        Assert.Contains("\"close\"", closed.Message);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order(seats: new() { "zzz" })));
        Assert.Contains("no seat 'zzz'", unknown.Message);
    }

    [Fact]
    public async Task RunAsync_ASecondRestoreWhileOneIsClaimed_IsRefused()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        var first = NewRestore(gw);
        Assert.True(first.TryClaim());
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).RunAsync(Order()));
            Assert.Contains("already running", ex.Message);
            Assert.Empty(gw.Spawns);
        }
        finally
        {
            first.Release();
        }
    }

    // ================= restored sessions inherit dev reports (Smart Director Restart, section 5.3 item 13) =================

    private const string ReportFile = @"D:\repo\docs\report.html";

    private static DevReportEntityRef Publish(FakeGateway gw, string sessionId, string key = ReportFile)
    {
        var (report, created) = gw.Reports.Publish(TenantId.Local, sessionId, key, "<p>html</p>", "waiting-on-you", "Report", Now);
        return new DevReportEntityRef(report.Id, report.Version, created);
    }

    /// <summary>What a publish answered: the report's id IS its link.</summary>
    private sealed record DevReportEntityRef(Guid Id, int Version, bool Created);

    [Fact]
    public async Task RunAsync_ARestoredSeatRepublishingTheSameFile_UpdatesTheSameReportAtTheSameLink()
    {
        var gw = Gateway(Seat("manager", "Manager"));
        var before = Publish(gw, "manager");

        var result = await NewRestore(gw).RunAsync(Order());

        // The restored session is "new-1". It publishes the same file, as a restored agent does.
        var after = Publish(gw, "new-1");
        Assert.False(after.Created);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(2, after.Version);
        Assert.Equal("manager", Assert.Single(gw.PassRequests).SeatSessionId);
        Assert.Equal(ThisDirector, gw.PassRequests[0].DirectorId);
        Assert.Contains("1 dev report(s) passed to new-1", Assert.Single(result.Seats).DevReports);
    }

    [Fact]
    public async Task RunAsync_ASeatThatIsNotBroughtBack_KeepsItsReportsFrozen_AndNothingIsAskedForIt()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.RefuseByName["Alpha"] = "the repository is gone";
        var before = Publish(gw, "a");

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.NotNull(Assert.Single(result.Seats).Failure);
        Assert.Null(result.Seats[0].DevReports);
        Assert.Empty(gw.PassRequests);
        Assert.Equal(before.Id, Assert.Single(gw.Reports.List(TenantId.Local, "a")).Id);
    }

    [Fact]
    public async Task RunAsync_TheGatewayRefusesThePass_TheSeatStillComesBack_AndItsOutcomeSaysTheLinksStayFrozen()
    {
        var gw = Gateway(Seat("a", "Alpha"));
        gw.RefuseThePassWith = "HTTP 404: this Gateway has no such route";
        Publish(gw, "a");

        var result = await NewRestore(gw).RunAsync(Order());

        var seat = Assert.Single(result.Seats);
        Assert.Null(seat.Failure);
        Assert.Equal("new-1", seat.RestoredSessionId);
        Assert.Contains("did NOT pass", seat.DevReports);
        Assert.Contains("HTTP 404", seat.DevReports);
        Assert.Equal("new-1", gw.Stored.Seats.Single().RestoredSessionId);
    }

    [Fact]
    public async Task RunAsync_ADirectorThatDiedBeforeItCouldAsk_TheNextRunAsksForTheSeatThatIsAlreadyBack()
    {
        var gw = Gateway(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1));
        gw.CreateThenDieByName.Add("Alpha");
        var before = Publish(gw, "a");

        await Assert.ThrowsAsync<DirectorDied>(() => NewRestore(gw).RunAsync(Order()));
        Assert.Empty(gw.PassRequests);
        gw.CreateThenDieByName.Clear();
        gw.Revive();

        await NewRestore(gw).RunAsync(Order());

        Assert.Contains(gw.PassRequests, p => p.SeatSessionId == "a");
        var after = Publish(gw, "new-1");
        Assert.False(after.Created);
        Assert.Equal(before.Id, after.Id);
    }
}
