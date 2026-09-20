using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Throttle;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;
using CcDirector.Gateway.Data;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE HOSTED GATEWAY SERVES A TENANT ITS OWN STATISTICS, FROM POSTGRESQL (issue #1174).
///
/// WHAT THIS FILE USED TO SAY, AND WHY IT CHANGED. These tests used to PIN the degraded state: on hosted,
/// <c>/stats</c> and <c>/stats/data</c> answered a named 503 and served nothing, and the comments explained
/// that the database-backed read path "has not been wired yet". They were honest about the shipped contract
/// and they were right to be. That wiring has now landed - the read AND the write path - so the pins come
/// out and the routes are held to what they are for.
///
/// IT RUNS AGAINST A REAL POSTGRESQL, NOT A FAKE, and that is the point rather than thoroughness for its own
/// sake. What is being proved is that a hosted Gateway serves one account its own numbers and never
/// another's; a fake store would prove that the code passes a tenant around, which is a different and much
/// weaker claim. The whole class is gated on <c>CC_GATEWAY_TEST_PG_STATS_CONNECTION</c> - the restricted-role
/// connection <c>scripts/pg-stats-proof-rig.ps1</c> hands out, whose grants mirror the hosted role's measured
/// grants and no more - and reports SKIPPED when it is unset, so the ordinary run and continuous integration
/// touch no database. Stand it up with your OWN instance and port:
///
///     powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance &lt;yours&gt; -Port &lt;yours&gt; -Verb up
///
/// SINCE THE "CLEAN UP YOUR THROTTLE" MISSION (2026-09-05, ruling R9) the feed's counts of TURNS come from the
/// submission ledger in the Gateway database, not from the statistics store this file proves. What the
/// statistics store still feeds is concurrency and token spend, and what this file still proves about the
/// tally is that the production ingress WRITES it to PostgreSQL and a cold reader recovers it - the tally is
/// still kept, it just no longer feeds the page. The served-feed assertions read the ledger figure, fed
/// through the ledger's own production ingress alongside the roster push.
///
/// The three facts, on a real HOSTED GatewayHost with TWO fully enrolled tenants and one unbound device:
///   1. SERVE - the data route answers 200 with the caller's OWN figure, read from the caller's own partition.
///   2. ISOLATED - turns fed for tenant A are visible on A's read and INVISIBLE on B's read of the same feed.
///   3. FAIL CLOSED - a caller who cannot be attributed to a tenant is REFUSED, never served the Local
///      partition.
///
/// And the law the wiring must not have broken on its way in: a hosted Gateway still opens NO local
/// statistics file. That is asserted here too, because "serve on hosted" would be an easy thing to deliver
/// by quietly re-enabling the file that caused the 2026-07-30 outage.
///
/// Self-host is unchanged and is the control in HostedStatsSelfHostControlTests, which stayed in
/// CcDirector.Gateway.Tests when this class moved into the PostgreSQL proofs project: that control needs no
/// database, so it belongs with the suite that runs on every change rather than with the proofs that run only
/// when database-facing code moves.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedStatsServeTests : IAsyncLifetime
{
    private const string Token = "test-token-stats-serve";
    private const string ConnectionEnvVar = "CC_GATEWAY_TEST_PG_STATS_CONNECTION";

    /// <summary>Tenant A's repository. Distinctive on purpose: a leak check that greps for a common word
    /// would pass on a body that never mentioned tenant A at all.</summary>
    private const string TenantARepo = "thefrederiksen/alpha-only-repo";

    /// <summary>A Fact that skips itself when the rig is not up, so the ordinary SQLite run is unaffected.
    /// Skip rather than a silent pass: a green that touched no database would be a green that proves
    /// nothing, and this is the class where that matters most.</summary>
    private sealed class RequiresPostgresStatsFactAttribute : FactAttribute
    {
        public RequiresPostgresStatsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvVar)))
                Skip = $"Set {ConnectionEnvVar} (scripts\\pg-stats-proof-rig.ps1 -Verb up) to prove the hosted " +
                       "statistics serve against real PostgreSQL.";
        }
    }

    private static string? ConfiguredConnection => Environment.GetEnvironmentVariable(ConnectionEnvVar);

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string? _priorStatsConnection;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-stats-serve-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;        // fully enrolled tenant A
    private HttpClient _httpB = null!;        // fully enrolled tenant B
    private HttpClient _httpUnbound = null!;  // enrolled device, NO tenant binding
    private TenantId _tenantA;

    public HostedStatsServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-stats-serve-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        // Point the SHIPPED selector at the rig, by the same environment variable a hosted deployment sets.
        // Nothing here constructs a store by hand: the Gateway resolves, opens and migrates it on its own
        // startup path, which is the path under test.
        _priorStatsConnection = Environment.GetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar);
        Environment.SetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar, ConfiguredConnection);
    }

    public async Task InitializeAsync()
    {
        ResetSchema();

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com", out _tenantA);
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com", out _);

        // An enrolled device key bound to NO account: the strongest UNRESOLVED caller. It must be refused,
        // never served the Local partition. MTR-14B: an unbound device on hosted is an invalid credential
        // (invalidHostedBinding -> Revoked), so it is refused at the auth gate with 401 before reaching the
        // stats route's tenant-boundary 403. Isolation unchanged (no bound tenant -> no access); only the
        // denial layer moved.
        var unboundKey = _gateway.Devices.Register("dev-unbound", "MA").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
    }

    /// <summary>
    /// Drop the statistics schema so this run starts from nothing and the Gateway's own startup path
    /// recreates it. The rig's database is long-lived on purpose, so without this a fact could pass on rows
    /// a previous run left behind - and "tenant A sees its own numbers" is exactly the claim that inherited
    /// rows would make look true.
    /// </summary>
    private static void ResetSchema()
    {
        var connection = ConfiguredConnection!;
        var database = new NpgsqlConnectionStringBuilder(connection).Database ?? "";
        if (!database.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop the statistics schema in '{database}': these tests DROP a schema, so the " +
                $"database must be a throwaway one whose name begins with 'ccpg'. Point {ConnectionEnvVar} at " +
                "a rig database (scripts\\pg-stats-proof-rig.ps1).");

        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>().UseNpgsql(connection).Options;
        using var ctx = new GatewayStatsDbContext(options);
        ctx.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS {GatewayStatsDbContext.PostgresSchema} CASCADE");
    }

    private HttpClient Enrolled(string deviceId, string subject, string email, out TenantId tenant)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var minted = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, minted.Value);
        tenant = minted;
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public async Task DisposeAsync()
    {
        _httpA?.Dispose();
        _httpB?.Dispose();
        _httpUnbound?.Dispose();
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar, _priorStatsConnection);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Put a distinctive tally into a tenant's statistics THROUGH THE PRODUCTION INGRESS - never by calling
    /// the aggregator.
    ///
    /// WHY THIS MATTERS MORE THAN IT LOOKS. The first version of these tests called
    /// `_gateway.InputStats.ObserveSnapshot(...)` directly and then read the answer back from the endpoint
    /// backed by that same in-process object. An inspection pointed out what that leaves green: disconnect
    /// the production write call sites in `GatewayEndpoints` and `DirectorHub` entirely, and the test still
    /// passes, because the test was the writer. It also never established that a single byte reached
    /// PostgreSQL - an aggregator that kept the value in memory satisfied every assertion.
    ///
    /// So the numbers now travel the route a real Director's numbers travel: a registered Director with a
    /// live connection pushes a roster carrying the tally, and `GET /sessions` - the production fold, on the
    /// request thread, under the caller's resolved tenant - is what writes. That one call also drives the
    /// concurrency recorder, so both halves of the surface are fed by the same production path.
    /// </summary>
    private async Task FeedThroughProductionIngress(
        HttpClient http, TenantId tenant, string directorId, string sessionId, string repo, long turns)
    {
        _gateway.Registry.RegisterFromStream(directorId, "MACHINE-" + directorId, "soren", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(tenant, directorId, "conn-" + directorId, 1, new[]
        {
            new SessionDto
            {
                SessionId = sessionId,
                Name = sessionId,
                ActivityState = "Working",
                StatusColor = "blue",
                LastActivityAt = DateTime.UtcNow,
                RepoPath = repo,
                InputStats = new InputStatsDto
                {
                    Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = turns, Characters = turns * 100 } },
                },
            },
        }));

        // THE PRODUCTION FOLD. A roster read by this tenant, through the real auth gate and the real route.
        var roster = await http.GetAsync("sessions");
        Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
        using var body = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.EnumerateArray(),
            s => s.GetProperty("sessionId").GetString() == sessionId);

        // THE LEDGER (ruling R9): the same turns, as the Director's activity producer records them at the
        // submission choke point, pushed through the ledger's own production ingress. The feed's turn
        // figures read THIS, so the served numbers below are the ledger's, not the tally's.
        var at = DateTime.UtcNow.AddMinutes(-30);
        var events = Enumerable.Range(0, (int)turns).Select(i => new ActivityEventRecord
        {
            EventId = Guid.NewGuid(), DirectorSequence = i + 1, OccurredUtc = at.AddSeconds(i), DirectorId = directorId,
            SessionId = sessionId, Machine = "MACHINE-" + directorId, AgentKind = "ClaudeCode",
            EventType = ActivityEventTypes.TurnSubmitted, Cause = ActivityCauses.OwnerSubmit,
            InputOrigin = "voice/phone", SendSource = "Delivery",
        }).ToList();
        var ingest = await http.PostAsJsonAsync("activity-events/batch", new ActivityEventIngestRequest { Events = events });
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        // And the session's repository, in the tenant's own session history, which the repository split joins
        // on. The history recorder itself runs on the SignalR push path, which this HTTP-level test does not
        // drive, so the row is written through the real store over the Gateway's own database file.
        new SessionHistoryStore(new GatewayDatabase(new FixedTenantContext(tenant))).UpsertLive(directorId, new SessionDto
        {
            SessionId = sessionId, Name = sessionId, RepoPath = repo, RepoName = repo, Agent = "ClaudeCode",
            CreatedAt = at, ActivityState = "Working", Status = "Running",
        }, DateTime.UtcNow);
    }

    /// <summary>A reader that shares NOTHING with the Gateway's own aggregator except the database: its own
    /// pooled connection factory, its own mirror, loaded from the rows on disk. A value that only ever lived
    /// in the Gateway's memory is invisible to it.</summary>
    private static IDbContextFactory<GatewayStatsDbContext> FreshFactory()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<GatewayStatsDbContext>(o => o.UseNpgsql(ConfiguredConnection!));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
    }

    /// <summary>The turns actually stored in PostgreSQL for one tenant, summed straight off the append-only
    /// delta ledger every all-time total is derived from. Raw SQL on purpose: it is the one reading in this
    /// file that no object under test can influence.</summary>
    private static long StoredTurns(TenantId tenant)
    {
        using var connection = new NpgsqlConnection(ConfiguredConnection);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT COALESCE(SUM(turns), 0) FROM {GatewayStatsDbContext.PostgresSchema}.stat_delta WHERE tenant = @t";
        cmd.Parameters.AddWithValue("t", tenant.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// THE WIRING ITSELF: a hosted Gateway with a healthy PostgreSQL statistics store HAS both observers -
    /// and still opens no local statistics file.
    ///
    /// Asserted before anything is served, because every fact below is meaningless if the Gateway quietly
    /// fell back to having no statistics: it would answer the old 503 and the isolation facts would pass by
    /// serving nobody anything.
    /// </summary>
    [RequiresPostgresStatsFact]
    public void The_hosted_gateway_has_a_database_backed_statistics_store_and_no_local_file()
    {
        Assert.True(_gateway.StatsStore.IsAvailable);
        Assert.NotNull(_gateway.StatsStore.Factory);

        // Both halves. The READ path (the aggregator) and the WRITE path's concurrency recorder - the review
        // found both unwired, and wiring only the read path would serve a tenant an empty page for ever.
        // The TYPE is asserted here only as a wiring fact; a no-op of the right type would satisfy it, which
        // is why the facts below assert real numbers out of the database instead.
        Assert.NotNull(_gateway.InputStats);
        Assert.NotNull(_gateway.SessionConcurrency);
        Assert.IsType<GatewaySessionConcurrencyStore>(_gateway.SessionConcurrency);

        // THE LAW THE WIRING MUST NOT HAVE BROKEN. No gateway-stats.db, and no concurrency JSON document,
        // anywhere under this Gateway's storage root.
        var files = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList()
            : new List<string?>();
        Assert.DoesNotContain("gateway-stats.db", files);
        Assert.DoesNotContain("gateway-concurrency-stats.json", files);
    }

    /// <summary>
    /// THE ROUND TRIP: numbers pushed through the production ingress are READ BACK OUT OF POSTGRESQL, by a
    /// reader that shares nothing with the Gateway but the database.
    ///
    /// This is the fact the headline claim rests on, and it is asserted three ways so that no single
    /// substitution satisfies it: the raw delta ledger (no object under test can influence it), a FRESH
    /// aggregator over its own connection pool (proves a cold reader recovers the value, which is what a
    /// restarted container does), and the served feed itself.
    /// </summary>
    [RequiresPostgresStatsFact]
    public async Task Numbers_pushed_through_production_ingress_come_back_out_of_postgresql()
    {
        Assert.Equal(0, StoredTurns(_tenantA));

        await FeedThroughProductionIngress(_httpA, _tenantA, "dir-a", "s-alpha", TenantARepo, turns: 7);

        // 1. THE ROWS. Straight off the append-only ledger, by raw SQL over a separate connection.
        Assert.Equal(7, StoredTurns(_tenantA));

        // 2. A COLD READER. Its own pooled factory, its own mirror loaded from those rows - it has never
        //    seen the Gateway's in-memory state, so a value that never left memory is invisible to it.
        using var coldReader = new GatewayInputStatsAggregator(FreshFactory());
        var recovered = coldReader.CurrentTotals(_tenantA);
        var recoveredBucket = Assert.Single(recovered.Buckets);
        Assert.Equal(7, recoveredBucket.Turns);
        Assert.Equal(700, recoveredBucket.Characters);
        Assert.Equal("voice", recoveredBucket.Modality);

        // 3. AND THE SERVED FEED agrees with both - from the ledger the same ingress fed (ruling R9), not
        //    from the tally the two readings above proved. The two substrates carry the same seven turns
        //    here because one Director wrote both at one choke point, which is the whole design.
        using var doc = JsonDocument.Parse(await (await _httpA.GetAsync("stats/data")).Content.ReadAsStringAsync());
        Assert.Equal(7, Assert.Single(doc.RootElement.GetProperty("throttle").GetProperty("buckets").EnumerateArray())
            .GetProperty("turns").GetInt64());
    }

    /// <summary>
    /// The concurrency half, given a REAL NUMBER rather than a type check. The same production roster read
    /// that folds the input tally also drives the concurrency recorder, so one live session must show up as
    /// one live session on the feed - and a no-op recorder of the correct type fails here.
    /// </summary>
    [RequiresPostgresStatsFact]
    public async Task The_hosted_concurrency_recorder_serves_a_real_number_from_the_database()
    {
        await FeedThroughProductionIngress(_httpA, _tenantA, "dir-a", "s-alpha", TenantARepo, turns: 7);

        using var doc = JsonDocument.Parse(await (await _httpA.GetAsync("stats/data")).Content.ReadAsStringAsync());
        var concurrency = doc.RootElement.GetProperty("concurrency");
        Assert.Equal(JsonValueKind.Object, concurrency.ValueKind);

        // One session was live when the roster was folded, so the live series must say so - current and
        // all-time peak both at least one. A recorder that stored nothing reports zero here.
        Assert.True(concurrency.GetProperty("live").GetProperty("allTimeMax").GetInt32() >= 1,
            "the hosted concurrency recorder reported no all-time peak after a live roster was folded");

        // And it is in the DATABASE, not only in the recorder's shadow: a fresh store over its own factory
        // rehydrates the same peak.
        var coldPeak = new GatewaySessionConcurrencyStore(FreshFactory()).Snapshot(DateTime.UtcNow, _tenantA);
        Assert.True(coldPeak.Live.AllTimeMax >= 1,
            "a cold concurrency reader saw no peak, so the fold never reached PostgreSQL");
    }

    /// <summary>
    /// SERVE: an enrolled tenant gets 200 and its OWN totals, read back out of PostgreSQL.
    ///
    /// The seeded numbers themselves are asserted, not merely the shape: an empty-but-well-formed payload is
    /// what a wired-but-not-working read path produces, and it would satisfy a shape check exactly.
    /// </summary>
    [RequiresPostgresStatsFact]
    public async Task The_stats_feed_serves_an_enrolled_tenant_its_own_totals_from_postgresql()
    {
        await FeedThroughProductionIngress(_httpA, _tenantA, "dir-a", "s-alpha", TenantARepo, turns: 7);

        var resp = await _httpA.GetAsync("stats/data");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        var figure = root.GetProperty("throttle");
        var bucket = Assert.Single(figure.GetProperty("buckets").EnumerateArray());
        Assert.Equal("voice", bucket.GetProperty("modality").GetString());
        Assert.Equal("phone", bucket.GetProperty("surface").GetString());
        Assert.Equal(7, bucket.GetProperty("turns").GetInt64());

        var repo = Assert.Single(figure.GetProperty("repos").EnumerateArray());
        Assert.Equal("alpha-only-repo", repo.GetProperty("repoName").GetString());
        Assert.Equal(7, repo.GetProperty("turns").GetInt64());

        // The statistics store is up on this Gateway, so the blocks it feeds are served, not null.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("statisticsUnavailableReason").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("tokenSpend").ValueKind);
    }

    /// <summary>
    /// ISOLATED: the numbers fed for tenant A are invisible to tenant B on the same feed. This is the fact
    /// the hosted serve is only safe because of, so it is asserted in its STRONG form - A's distinctive
    /// repository name must not appear anywhere in B's response body, and B's own totals must be empty.
    /// </summary>
    [RequiresPostgresStatsFact]
    public async Task One_tenants_numbers_are_invisible_to_another_on_the_same_feed()
    {
        await FeedThroughProductionIngress(_httpA, _tenantA, "dir-a", "s-alpha", TenantARepo, turns: 7);

        // A sees them.
        using (var a = JsonDocument.Parse(await (await _httpA.GetAsync("stats/data")).Content.ReadAsStringAsync()))
            Assert.NotEmpty(a.RootElement.GetProperty("throttle").GetProperty("buckets").EnumerateArray());

        // B does not - and this is a 200 that serves B's OWN (empty) partition, never a 200 carrying A's.
        var respB = await _httpB.GetAsync("stats/data");
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var bodyB = await respB.Content.ReadAsStringAsync();

        Assert.DoesNotContain("alpha-only-repo", bodyB, StringComparison.Ordinal);
        using var b = JsonDocument.Parse(bodyB);
        Assert.Empty(b.RootElement.GetProperty("throttle").GetProperty("buckets").EnumerateArray());
        Assert.Empty(b.RootElement.GetProperty("throttle").GetProperty("repos").EnumerateArray());
    }

    /// <summary>
    /// FAIL CLOSED: a caller who cannot be attributed to a tenant is REFUSED, never served the Local
    /// partition. The denial happens at the auth gate on hosted (MTR-14B: an unbound device key is an invalid
    /// credential there, so 401 arrives before the route's tenant-boundary 403), and the body carries no
    /// statistics either way.
    /// </summary>
    [RequiresPostgresStatsFact]
    public async Task The_stats_feed_refuses_a_caller_who_resolves_to_no_tenant()
    {
        await FeedThroughProductionIngress(_httpA, _tenantA, "dir-a", "s-alpha", TenantARepo, turns: 7);

        var resp = await _httpUnbound.GetAsync("stats/data");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var text = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"turns\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-only-repo", text, StringComparison.Ordinal);
    }

    /// <summary>Control: with no key the host-wide auth gate still refuses first, so serving on hosted did
    /// not open the route to anonymous callers.</summary>
    [RequiresPostgresStatsFact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var noAuth = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("stats/data")).StatusCode);
    }

    /// <summary>The page route no longer answers the named 503 either: with the feed serving, <c>/stats</c>
    /// redirects to the Cockpit page that reads it, exactly as it does on self-host. A 503 here would send an
    /// account holder to a dead end on a Gateway whose statistics are working.</summary>
    [RequiresPostgresStatsFact]
    public async Task The_stats_page_redirects_to_the_cockpit_page_that_reads_the_feed()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var raw = new HttpClient(handler) { BaseAddress = _httpA.BaseAddress };
        raw.DefaultRequestHeaders.Authorization = _httpA.DefaultRequestHeaders.Authorization;

        var resp = await raw.GetAsync("stats");
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal("/your-throttle", resp.Headers.Location!.ToString());
    }
}
