using System.Text.Json;
using CcDirector.ControlApi.Drain;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The restore as a Director act (the Message Load mission, slice 6): the REAL <see cref="DirectorRestore"/>
/// against a fake Gateway that stores the workspace by serialising it, as the real store does, and records every
/// spawn. The route test (<c>WorkspaceRestoreRouteTests</c>) proves the same spawn on a real host; these prove the
/// order, the owner resolution and the per-seat reporting.
/// </summary>
public class DirectorRestoreTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeGateway : IRestoreGateway
    {
        private string _stored;
        private int _next;

        public FakeGateway(WorkspaceDocument doc) => _stored = JsonSerializer.Serialize(doc, Json);

        public List<NewSessionRequest> Spawns { get; } = new();

        /// <summary>Spawns whose name is here are refused by the "Gateway" with this reason.</summary>
        public Dictionary<string, string> RefuseByName { get; } = new();

        /// <summary>Spawns whose name is here time out on the client side.</summary>
        public HashSet<string> TimeOutByName { get; } = new();

        public WorkspaceDocument Stored => JsonSerializer.Deserialize<WorkspaceDocument>(_stored, Json)!;

        public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
            => Task.FromResult<WorkspaceDocument?>(id == Stored.Id ? Stored : null);

        public Task<WorkspaceDocument> SaveWorkspaceAsync(WorkspaceDocument doc, CancellationToken ct)
        {
            _stored = JsonSerializer.Serialize(doc, Json);
            return Task.FromResult(Stored);
        }

        public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
        {
            Spawns.Add(JsonSerializer.Deserialize<NewSessionRequest>(JsonSerializer.Serialize(request, Json), Json)!);
            if (request.Name is { } t && TimeOutByName.Contains(t))
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
            if (request.Name is { } n && RefuseByName.TryGetValue(n, out var why))
                throw new InvalidOperationException(why);
            _next++;
            return Task.FromResult(new SessionDto { SessionId = $"new-{_next}", Name = request.Name });
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
            DrainState = "drained",
            Restore = new WorkspaceSeatRestore { Decision = decision, Why = "test", Command = "cc-devthrottle director restore ..." },
        };

    private static WorkspaceDocument Doc(params WorkspaceSeat[] seats) => new()
    {
        Id = "drain-1",
        Name = "Drain",
        Origin = WorkspaceOrigins.Captured,
        DirectorId = "old-director",
        Machine = "MAC",
        Seats = seats.ToList(),
        RestoreAfterRestart = seats.Where(s => s.Restore!.Decision == WorkspaceRestoreDecisions.Restore).Select(s => s.SessionId!).ToList(),
    };

    private static DirectorRestore NewRestore(FakeGateway gw) => new(gw, "new-director", () => Now);

    private static WorkspaceRestoreOrder Order(string? askedBy = "restoring-session", List<string>? seats = null, Dictionary<string, string>? seeds = null)
        => new() { WorkspaceId = "drain-1", RequestedBySessionId = askedBy, Seats = seats, Seeds = seeds };

    // ================= the placeholder, resolved in order =================

    [Fact]
    public async Task RunAsync_AWorkerListedBeforeItsManager_ComesBackAfterIt_OwnedByTheManagersNewId()
    {
        // The worker is FIRST in the record. Restoring in record order would start it before its owner had an id.
        var gw = new FakeGateway(Doc(
            Seat("worker", "M - Worker", reportsTo: "manager", order: 0),
            Seat("manager", "M - Manager", order: 1)));

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
    }

    [Fact]
    public async Task RunAsync_AThreeLevelChain_ResolvesEachPlaceholderToTheLevelAbove()
    {
        var gw = new FakeGateway(Doc(
            Seat("w", "Worker", reportsTo: "m"),
            Seat("m", "Manager", reportsTo: "a"),
            Seat("a", "Architect")));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "Architect", "Manager", "Worker" }, gw.Spawns.Select(s => s.Name));
        Assert.Equal("new-1", gw.Spawns[1].ControllerSessionId);
        Assert.Equal("new-2", gw.Spawns[2].ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerRestoredInAnEarlierRun_IsUsedByItsNewId()
    {
        var manager = Seat("manager", "Manager");
        manager.RestoredSessionId = "restored-earlier";
        var gw = new FakeGateway(Doc(manager, Seat("worker", "Worker", reportsTo: "manager")));

        await NewRestore(gw).RunAsync(Order());

        var spawn = Assert.Single(gw.Spawns);
        Assert.Equal("Worker", spawn.Name);
        Assert.Equal("restored-earlier", spawn.ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerOnAnotherDirector_KeepsItsRealId()
    {
        var gw = new FakeGateway(Doc(Seat("worker", "Worker", reportsTo: "11111111-2222-3333-4444-555555555555")));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal("11111111-2222-3333-4444-555555555555", Assert.Single(gw.Spawns).ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerThatBlockedTheDrainAndWasNeverClosed_IsStillRunning_AndKeepsItsId()
    {
        // The restore after a blocked drain: no restart happened, the owner would not stop, its worker did.
        var owner = Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Undecided);
        owner.DrainState = WorkspaceDrainStates.Blocked;
        owner.ClosedAtUtc = null;
        var gw = new FakeGateway(Doc(owner, Seat("w", "Worker", reportsTo: "m")));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal("m", Assert.Single(gw.Spawns).ControllerSessionId);
    }

    [Fact]
    public async Task RunAsync_ABlockedOwnerThatWasClosedAfterAll_IsNotTakenForRunning()
    {
        var owner = Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Undecided);
        owner.DrainState = WorkspaceDrainStates.Blocked;
        owner.ClosedAtUtc = Now;
        var gw = new FakeGateway(Doc(owner, Seat("w", "Worker", reportsTo: "m")));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.NotNull(Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_ASeatWithNoOwner_IsTheUsers_AndTheRequestSaysWhoAsked()
    {
        var gw = new FakeGateway(Doc(Seat("solo", "Solo")));

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
        var gw = new FakeGateway(Doc(Seat("solo", "Solo")));

        await NewRestore(gw).RunAsync(Order(askedBy: null));

        var spawn = Assert.Single(gw.Spawns);
        Assert.Equal("human", spawn.Origin);
        Assert.Null(spawn.ParentSessionId);
    }

    [Fact]
    public async Task RunAsync_ASeedFileGiven_IsTheSeed_AndIsRecorded()
    {
        var gw = new FakeGateway(Doc(Seat("solo", "Solo")));

        await NewRestore(gw).RunAsync(Order(seeds: new() { ["solo"] = "/index/SEED-solo.md" }));

        Assert.Equal("Read /index/SEED-solo.md - it is your whole mandate. Follow it.", Assert.Single(gw.Spawns).PrePrompt);
        Assert.Equal("/index/SEED-solo.md", gw.Stored.Seats.Single().RestoredSeedFile);
    }

    // ================= a failure per seat =================

    [Fact]
    public async Task RunAsync_OneSeatRefused_IsReportedOnThatSeat_AndTheRestStillComeBack()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1), Seat("c", "Charlie", order: 2)));
        gw.RefuseByName["Bravo"] = "repository not found";

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, gw.Spawns.Select(s => s.Name));
        var bravo = result.Seats.Single(s => s.SessionId == "b");
        Assert.Null(bravo.RestoredSessionId);
        Assert.Contains("repository not found", bravo.Failure);

        var stored = gw.Stored.Seats.ToDictionary(s => s.SessionId!);
        Assert.Null(stored["b"].RestoredSessionId);
        Assert.Contains("repository not found", stored["b"].Restore!.Failure);
        Assert.Equal(Now, stored["b"].Restore!.AttemptedAtUtc);
        Assert.NotNull(stored["a"].RestoredSessionId);
        Assert.NotNull(stored["c"].RestoredSessionId);
    }

    [Fact]
    public async Task RunAsync_ASpawnThatTimesOut_IsReportedOnThatSeatAsMaybeStarted_AndTheRestCarryOn()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha", order: 0), Seat("b", "Bravo", order: 1)));
        gw.TimeOutByName.Add("Alpha");

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Equal(DirectorRestore.TimedOut, result.Seats.Single(s => s.SessionId == "a").Failure);
        Assert.Contains("MAY have been started", gw.Stored.Seats.Single(s => s.SessionId == "a").Restore!.Failure);
        Assert.NotNull(gw.Stored.Seats.Single(s => s.SessionId == "b").RestoredSessionId);
    }

    [Fact]
    public async Task RunAsync_AnOwnerThatFailed_FailsItsWorkers_WithTheReason_AndNeverStartsThem()
    {
        var gw = new FakeGateway(Doc(
            Seat("m", "Manager"),
            Seat("w", "Worker", reportsTo: "m"),
            Seat("solo", "Solo")));
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
        var gw = new FakeGateway(Doc(
            Seat("m", "Manager", decision: WorkspaceRestoreDecisions.Close),
            Seat("w", "Worker", reportsTo: "m")));

        var result = await NewRestore(gw).RunAsync(Order());

        Assert.Empty(gw.Spawns);
        Assert.Contains("\"close\"", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_OnlyTheWorkerAsked_AndItsOwnerNotBackYet_FailsSayingRestoreTheOwnerFirst()
    {
        var gw = new FakeGateway(Doc(Seat("m", "Manager"), Seat("w", "Worker", reportsTo: "m")));

        var result = await NewRestore(gw).RunAsync(Order(seats: new() { "w" }));

        Assert.Empty(gw.Spawns);
        Assert.Contains("Restore the owner first", Assert.Single(result.Seats).Failure);
    }

    [Fact]
    public async Task RunAsync_AFailedSeatAskedAgain_ComesBack_AndItsFailureIsCleared()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha")));
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
        var back = Seat("a", "Alpha");
        back.RestoredSessionId = "already";
        var gw = new FakeGateway(Doc(back, Seat("b", "Bravo")));

        await NewRestore(gw).RunAsync(Order());

        Assert.Equal("Bravo", Assert.Single(gw.Spawns).Name);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).RunAsync(Order(seats: new() { "a" })));
        Assert.Contains("restored once", ex.Message);
    }

    [Fact]
    public async Task PrepareAsync_AnAuthoredWorkspace_IsRefused_BecauseItsOwnersAreTyped()
    {
        var doc = Doc(Seat("w", "Worker", reportsTo: "someone"));
        doc.Origin = WorkspaceOrigins.Authored;
        var gw = new FakeGateway(doc);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order()));

        Assert.Contains("not captured", ex.Message);
        Assert.Empty(gw.Spawns);
    }

    [Fact]
    public async Task PrepareAsync_NothingLeftToBringBack_IsRefused_AndNamesTheSeatsOtherwise()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha", decision: WorkspaceRestoreDecisions.Close)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order()));
        Assert.Contains("no seat left", ex.Message);

        var gw2 = new FakeGateway(Doc(Seat("a", "Alpha"), Seat("b", "Bravo")));
        Assert.Equal(new[] { "a", "b" }, await NewRestore(gw2).PrepareAsync(Order()));
    }

    [Fact]
    public async Task PrepareAsync_ASeatNotDecidedRestore_OrUnknown_IsRefusedByName()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha", decision: WorkspaceRestoreDecisions.Close)));

        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order(seats: new() { "a" })));
        Assert.Contains("\"close\"", closed.Message);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRestore(gw).PrepareAsync(Order(seats: new() { "zzz" })));
        Assert.Contains("no seat 'zzz'", unknown.Message);
    }

    [Fact]
    public async Task RunAsync_ASecondRestoreWhileOneIsClaimed_IsRefused()
    {
        var gw = new FakeGateway(Doc(Seat("a", "Alpha")));
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
}
