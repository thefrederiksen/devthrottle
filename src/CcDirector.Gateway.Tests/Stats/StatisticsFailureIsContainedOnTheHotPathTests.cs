using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// A RUNTIME statistics write failure does not fault the roster (failure review finding M2).
///
/// WHAT WAS WRONG. The startup boundary already contained store selection, opening and migration - a
/// statistics database that could not be reached could not stop the Gateway starting. Nothing contained what
/// happened AFTER startup. The writer opens a context and a transaction with no catch; both aggregator
/// observation methods call it with no catch; and <c>GET /sessions</c> and the Director hub call those
/// observation methods INLINE, on the request thread, with no catch. So a lock timeout, a lost connection or
/// an unwritable file turned a background concern into HTTP 500 on the path every client polls - the exact
/// shape of the incident the startup containment was written for, one layer further in.
///
/// WHY THIS FIXTURE CAN SHOW THE FAILURE RATHER THAN MERELY PASS. Three things, and without all three a
/// green here would be worth nothing:
///
///  1. THE FOLD MUST ACTUALLY RUN. An empty roster folds nothing and writes nothing, so a Gateway with no
///     sessions would answer 200 whether or not anything were contained. A session with a real tally is
///     pushed, and the first roster read is asserted to have folded it successfully - so the second read is
///     known to be doing the work that fails.
///  2. THE FAILURE MUST ACTUALLY BE COUNTED. A 200 alone is also what a fold that silently never ran looks
///     like. The aggregator's failure count is asserted to move from zero to one, and its last error to be
///     recorded - that is the containment SHOUTING, and it is what distinguishes "contained" from "skipped".
///  3. THE FAULT MUST BE REAL. <see cref="TheSameFault_IsFatal_WhenItIsNotContained"/> calls the SAME broken
///     aggregator the way the route used to and asserts it throws. Without that control, a fixture in which
///     the write happened to succeed would produce the same green.
/// </summary>
public sealed class StatisticsFailureIsContainedOnTheHotPathTests : IDisposable
{
    private const string Token = "test-token-contained-stats";
    private const string DirectorId = "dir-contained";
    private const string Machine = "MACHINE-CONTAINED";

    private readonly ITestOutputHelper _out;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-stats-contained-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;

    public StatisticsFailureIsContainedOnTheHotPathTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // Self-host, stated rather than inherited: this fixture needs a WORKING statistics store to break,
        // and the only one a test can stand up without a database server is the local SQLite file - which a
        // hosted Gateway refuses to open. A run that silently inherited hosted mode would find no aggregator
        // at all and would pass having exercised nothing.
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>A session carrying a real tally, so the fold has something to write. A session with no
    /// buckets would produce an empty batch, which the writer returns from without opening a transaction -
    /// and a write that never happens cannot fail.</summary>
    private static SessionDto SessionWithATally(string id, long turns) => new()
    {
        SessionId = id,
        Name = id,
        ActivityState = "Working",
        StatusColor = "blue",
        LastActivityAt = DateTime.UtcNow,
        RepoPath = @"D:\ReposFred\devthrottle",
        InputStats = new InputStatsDto
        {
            Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = turns, Characters = turns * 100 } },
        },
    };

    private GatewayHost NewGateway() => new(
        port: GatewayHost.OperatingSystemAssignedPort,
        token: Token,
        authEnabled: true,
        instancesDirectory: Path.Combine(_root, "instances"),
        workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
        snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
        inputStatsPath: StatsDbPath);

    /// <summary>Put one live, freshly-served session on the roster, so the statistics fold has a subject.
    /// Registering the connection AND applying the snapshot is what makes the serve FRESH - the roster only
    /// folds sessions from a serve it confirmed current, so a session without a live connection would be
    /// excluded and the fold would run over nothing.</summary>
    private static void PushOneLiveSession(GatewayHost gateway, string sessionId, long turns)
    {
        gateway.Registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(gateway.PushedSessions.ApplySnapshot(
            TenantId.Local, DirectorId, "conn-1", 1, new[] { SessionWithATally(sessionId, turns) }));
    }

    /// <summary>
    /// Break the statistics store the way a runtime failure breaks it: it was fine at startup and is not
    /// fine now. Every later write raises, which is the class of fault - lock timeout, dropped connection,
    /// full disk - the failure review says escapes into the roster and the hub.
    ///
    /// WHY NOT JUST DISPOSE THE AGGREGATOR, which is what this method did first and which DOES NOT WORK.
    /// Disposing it closes the SQLite connection it owns, and a closed connection is not a broken one:
    /// Entity Framework simply opens it again on the next context and the write SUCCEEDS. The fixture
    /// injected nothing, the roster answered 200 because there was no fault to contain, and the failure
    /// count stayed at zero. The three containment facts would have looked like a pass on a green run had
    /// the control not been there - `TheSameFault_IsFatal_WhenItIsNotContained` asserted the injected fault
    /// throws, and it correctly reported that it did not. That is the control doing its whole job: without
    /// it this file would have claimed to prove containment while proving nothing at all.
    ///
    /// So the fault is now injected in the database rather than in the connection: a SECOND connection to
    /// the same file drops the tables the write path needs. The aggregator's own connection stays open and
    /// perfectly healthy, and every statement it issues fails with "no such table" - a real runtime error
    /// arriving after startup, which is exactly the shape being contained.
    /// </summary>
    private void BreakTheStatisticsStoreAfterStartup(GatewayHost gateway)
    {
        Assert.NotNull(gateway.InputStats);

        using var connection = new SqliteConnection($"Data Source={StatsDbPath}");
        connection.Open();

        var tables = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = read.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        // The fixture must know it actually broke something. An empty list would mean the file was not the
        // one under test, and every assertion after this would be measuring nothing.
        Assert.NotEmpty(tables);
        foreach (var table in tables)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS \"{table}\"";
            drop.ExecuteNonQuery();
        }
        _out.WriteLine($"FAULT INJECTED: dropped {tables.Count} table(s) from {StatsDbPath}");
    }

    private string StatsDbPath => Path.Combine(_root, "gateway-stats.db");

    [Fact]
    public async Task AStatisticsWriteFailure_DoesNotFaultTheRoster_AndIsCountedAndLogged()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();

        // The store really is there and really is usable - otherwise there is nothing to break.
        Assert.NotNull(gateway.InputStats);
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);

        PushOneLiveSession(gateway, "s-contained", turns: 7);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // ---- FIRST READ: the healthy path. This is what proves the fold RUNS in this fixture.
        var healthy = await http.GetAsync("sessions");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        using (var parsed = JsonDocument.Parse(await healthy.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
            Assert.NotEmpty(parsed.RootElement.EnumerateArray());
        }
        // Folded, and folded WITHOUT failing: the numbers reached the store.
        Assert.Equal(0, gateway.InputStats.Health.FailureCount);
        var totalsBefore = gateway.InputStats.CurrentTotals(TenantId.Local);
        Assert.Contains(totalsBefore.Buckets, b => b.Turns == 7);

        // ---- BREAK IT, then drive the same path again with MORE to fold.
        BreakTheStatisticsStoreAfterStartup(gateway);
        Assert.True(gateway.PushedSessions.ApplySnapshot(
            TenantId.Local, DirectorId, "conn-1", 2, new[] { SessionWithATally("s-contained", turns: 19) }));

        var broken = await http.GetAsync("sessions");
        var body = await broken.Content.ReadAsStringAsync();
        _out.WriteLine($"GET /sessions with the statistics store broken -> {(int)broken.StatusCode} {broken.StatusCode}");

        // THE CLAIM: the roster is unaffected by a statistics failure.
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        Assert.Equal("application/json", broken.Content.Headers.ContentType?.MediaType);
        using (var parsed = JsonDocument.Parse(body))
        {
            Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
            Assert.NotEmpty(parsed.RootElement.EnumerateArray());
        }

        // AND IT SHOUTED. Without this the test would also pass against a Gateway that had quietly stopped
        // folding altogether, which is the failure mode a silent catch produces and the one that took
        // thirty-two minutes to see the last time it happened.
        Assert.Equal(1, gateway.InputStats.Health.FailureCount);
        Assert.False(string.IsNullOrWhiteSpace(gateway.InputStats.Health.LastError));
        _out.WriteLine($"CONTAINED: observer={gateway.InputStats.Health.Observer} " +
                       $"failures={gateway.InputStats.Health.FailureCount} error={gateway.InputStats.Health.LastError}");
    }

    /// <summary>
    /// SHOUTED WHERE AN OPERATOR CAN HEAR IT - asserted against the REAL logging seam, not against the
    /// counter.
    ///
    /// The counter and the log are two different claims and only one of them was being checked. An earlier
    /// version of the fact above was called "AndLogged" while asserting nothing about any log, so deleting
    /// the production `FileLog.Write` inside the containment left it green - the test named the property it
    /// did not test, which is worse than not claiming it. `FileLog.RedirectForTests` swaps in an isolated
    /// writer and drains it synchronously, so this reads the line the operator would read.
    /// </summary>
    [Fact]
    public async Task TheContainedFailure_IsWrittenToTheRealLog_NamingTheObserverAndTheCallSite()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        PushOneLiveSession(gateway, "s-logged", turns: 3);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("sessions")).StatusCode);

        BreakTheStatisticsStoreAfterStartup(gateway);
        Assert.True(gateway.PushedSessions.ApplySnapshot(
            TenantId.Local, DirectorId, "conn-1", 2, new[] { SessionWithATally("s-logged", turns: 11) }));

        IReadOnlyList<string> lines;
        HttpStatusCode status;
        using (var log = FileLog.RedirectForTests())
        {
            status = (await http.GetAsync("sessions")).StatusCode;
            lines = log.DrainAndReadLines();
        }

        Assert.Equal(HttpStatusCode.OK, status);

        // The line itself, and the three things it has to carry for an operator to act on it: that a
        // statistics failure was contained, WHICH observer, and WHICH hot path was protected.
        var contained = lines.Where(l => l.Contains("[StatsObservation] CONTAINED", StringComparison.Ordinal)).ToList();
        foreach (var l in contained) _out.WriteLine(l);
        var line = Assert.Single(contained);
        Assert.Contains("observer=input-stats", line, StringComparison.Ordinal);
        Assert.Contains("callSite=GET /sessions roster fold", line, StringComparison.Ordinal);
        Assert.Contains("error=", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HUB'S REMOVE PATH, which an inspection found uncontained after the snapshot and delta paths were
    /// covered - so the claim "a statistics failure can no longer break the Director hub" was false as
    /// written, and the fixture that was supposed to prove it only ever drove HTTP.
    ///
    /// `Forget` reads like in-memory cleanup and is not: it clears the mirror and then goes to the database
    /// writer to delete the stored high-water rows, so it fails for exactly the reasons the other two
    /// observations fail. It also runs AFTER `ApplyRemove` has already committed the authoritative removal,
    /// which is the partial-success shape - the Director told its push failed when the session had in fact
    /// been removed.
    /// </summary>
    [Fact]
    public async Task AStatisticsDeleteFailure_DoesNotFaultTheHubsRemovePath()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        var ctx = new FakeHubCallerContext("conn-hub");
        var hub = new DirectorHub(gateway.PushedSessions, gateway.Registry, gateway.InputStatsHandle,
            new GatewayStreamRegistry(),
            new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false)) { Context = ctx };

        hub.Hello(new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        // A push first, so the session HAS a high-water row. Forget on a session the store never recorded
        // writes nothing, and a delete that never happens cannot fail - the fixture would prove nothing.
        hub.PushSnapshot(1, new[] { SessionWithATally("s-hub", turns: 5) });
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);

        BreakTheStatisticsStoreAfterStartup(gateway);

        IReadOnlyList<string> lines;
        Exception? thrown;
        using (var log = FileLog.RedirectForTests())
        {
            thrown = Record.Exception(() => hub.RemoveSession(2, "s-hub"));
            lines = log.DrainAndReadLines();
        }

        // THE CLAIM: the hub invocation completes. Before the containment this threw out of RemoveSession,
        // after the removal had already been applied.
        Assert.Null(thrown);

        // The removal itself still happened - containment must not have swallowed the actual work.
        Assert.DoesNotContain(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions,
            s => s.SessionId == "s-hub");

        // And it shouted, naming this call site rather than one of the two that were already covered.
        Assert.Equal(1, gateway.InputStats.Health.FailureCount);
        var line = Assert.Single(lines, l => l.Contains("[StatsObservation] CONTAINED", StringComparison.Ordinal));
        _out.WriteLine(line);
        Assert.Contains("callSite=DirectorHub.RemoveSession", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE HUB'S PUSH PATH, which had containment but no failing-path proof - the same gap that produced
    /// finding 1 one method over. A containment nothing exercises is a claim, and this file has already
    /// demonstrated that facts riding an unproven fault can be green over nothing.
    ///
    /// PushDelta applies the session to the store BEFORE folding statistics, so an escaping failure reports
    /// the push as failed after it has in fact landed.
    /// </summary>
    [Fact]
    public async Task AStatisticsWriteFailure_DoesNotFaultTheHubsPushPath()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        var ctx = new FakeHubCallerContext("conn-push");
        var hub = new DirectorHub(gateway.PushedSessions, gateway.Registry, gateway.InputStatsHandle,
            new GatewayStreamRegistry(),
            new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false)) { Context = ctx };

        hub.Hello(new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        hub.PushDelta(1, SessionWithATally("s-push", turns: 2));
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);

        BreakTheStatisticsStoreAfterStartup(gateway);

        IReadOnlyList<string> lines;
        Exception? thrown;
        using (var log = FileLog.RedirectForTests())
        {
            thrown = Record.Exception(() => hub.PushDelta(2, SessionWithATally("s-push", turns: 9)));
            lines = log.DrainAndReadLines();
        }

        Assert.Null(thrown);

        // The push itself still landed - containment must not have swallowed the actual work.
        Assert.Contains(gateway.PushedSessions.GetLastKnown(TenantId.Local, DirectorId).Sessions,
            s => s.SessionId == "s-push");

        Assert.Equal(1, gateway.InputStats.Health.FailureCount);
        var line = Assert.Single(lines, l => l.Contains("[StatsObservation] CONTAINED", StringComparison.Ordinal));
        _out.WriteLine(line);
        Assert.Contains("callSite=DirectorHub.PushDelta", line, StringComparison.Ordinal);
    }

    /// <summary>The control for the hub push path: the same broken store, called the way PushDelta called it
    /// before the containment, throws.</summary>
    [Fact]
    public async Task TheSamePushFault_IsFatal_WhenItIsNotContained()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        BreakTheStatisticsStoreAfterStartup(gateway);

        var thrown = Record.Exception(() =>
            gateway.InputStats!.Observe(SessionWithATally("s-push-uncontained", turns: 6), DateTime.UtcNow, TenantId.Local));
        Assert.NotNull(thrown);
        _out.WriteLine($"UNCONTAINED PushDelta fold: {thrown!.GetType().Name}: {thrown.Message}");
    }

    /// <summary>The control for the hub path, the same shape as the roster one: the same broken store,
    /// called the way RemoveSession called it before the containment, throws.</summary>
    [Fact]
    public async Task TheSameDeleteFault_IsFatal_WhenItIsNotContained()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        gateway.InputStats!.ObserveSnapshot(
            new[] { SessionWithATally("s-forget", turns: 4) }, DateTime.UtcNow, TenantId.Local);

        BreakTheStatisticsStoreAfterStartup(gateway);

        var thrown = Record.Exception(() => gateway.InputStats!.Forget("s-forget", TenantId.Local));
        Assert.NotNull(thrown);
        _out.WriteLine($"UNCONTAINED Forget: {thrown!.GetType().Name}: {thrown.Message}");
    }

    /// <summary>The minimum a hub invocation needs: a connection id and an HttpContext, because the tenant
    /// boundary resolves a connection's tenant through it exactly as a real SignalR negotiate does.</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    /// <summary>
    /// THE CONTROL. The same broken aggregator, called the way the roster route called it BEFORE the
    /// containment, throws. So the 200 above is the containment working, not a fixture in which the write
    /// would have succeeded anyway.
    /// </summary>
    [Fact]
    public async Task TheSameFault_IsFatal_WhenItIsNotContained()
    {
        await using var gateway = NewGateway();
        await gateway.StartAsync();
        Assert.NotNull(gateway.InputStats);

        BreakTheStatisticsStoreAfterStartup(gateway);

        var thrown = Record.Exception(() =>
            gateway.InputStats!.ObserveSnapshot(
                new[] { SessionWithATally("s-uncontained", turns: 5) }, DateTime.UtcNow, TenantId.Local));

        Assert.NotNull(thrown);
        _out.WriteLine($"UNCONTAINED: {thrown!.GetType().Name}: {thrown.Message}");

        // The aggregator does NOT contain its own failures, deliberately: containment belongs to the caller
        // whose fate is being protected, and the write-path tests rely on a broken store throwing.
        Assert.Equal(0, gateway.InputStats!.Health.FailureCount);
    }
}
