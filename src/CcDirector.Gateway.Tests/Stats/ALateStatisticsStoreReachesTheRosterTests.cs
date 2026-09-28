using System.Net;
using System.Net.Http.Headers;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// A STATISTICS STORE THAT ARRIVES AFTER THE ROUTES ARE MAPPED STILL REACHES THE ROSTER.
///
/// WHY THIS FILE EXISTS, AND WHY ITS ABSENCE LET A DEFECT SURVIVE A WHOLE INSPECTION ROUND. The hosted
/// statistics store is allowed to publish its context factory LATE - it keeps a slow open running past
/// the startup deadline on purpose, so a merely slow PostgreSQL costs the first seconds of one boot
/// instead of everything after it. The first fix for that made <c>/stats</c> and <c>/stats/data</c>
/// resolve the aggregator per request, and stopped there. The ROSTER did not: <c>GatewayHost</c>
/// evaluated the two observer properties ONCE while mapping the endpoints, and the <c>GET /sessions</c>
/// closure used those captured values for the life of the process.
///
/// The result of that half-fix is the worst of both states - a tenant served a perfectly working
/// statistics feed over a roster that records nothing into it. Statistics served, statistics never
/// written.
///
/// AND NO EXISTING TEST COULD HAVE CAUGHT IT, which is the part worth keeping. Every fixture in this
/// suite starts against an already-healthy store and asserts the observers exist immediately, so the
/// captured value and the resolved value are identical at the only moment anything looks. A defect that
/// only appears when the answer CHANGES cannot be seen by a test where the answer never changes. That is
/// why the fix moved one place further out and would have moved again unnoticed.
///
/// So this fixture makes the answer change: the resolver returns NOTHING while the routes are mapped and
/// the first roster is served, then starts returning an aggregator, and the next roster read must record.
/// It drives the REAL <see cref="GatewayEndpoints.Map"/> and the real route, not a hand-wired stand-in.
/// </summary>
public sealed class ALateStatisticsStoreReachesTheRosterTests : IDisposable
{
    private const string Token = "test-token-late-stats";
    private const string DirectorId = "dir-late";

    private readonly ITestOutputHelper _out;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-late-stats-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot;

    public ALateStatisticsStoreReachesTheRosterTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

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

    [Fact]
    public async Task AnAggregatorThatArrivesAfterRouteMapping_StillRecordsFromTheRoster()
    {
        // The switchable answer. NULL now - exactly what a hosted store that is still opening reports.
        GatewayInputStatsAggregator? aggregator = null;
        ISessionConcurrencyRecorder? concurrency = null;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
        var app = builder.Build();

        var registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        var pushed = new PushedSessionStore();

        // MAPPED WHILE THERE IS NOTHING. If these were captured rather than resolved, the roster would be
        // wired to null for the life of the process and the assertion below could never pass.
        GatewayEndpoints.Map(app, registry, version: "test", token: Token,
            tenantBoundary: new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry()),
            pushedSessions: pushed,
            inputStats: () => aggregator,
            concurrency: () => concurrency, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);

        await app.StartAsync();
        try
        {
            var port = BoundPort.Of(app);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            registry.RegisterFromStream(DirectorId, "MACHINE-LATE", "soren", "1.0", pid: 99,
                startedAt: DateTime.UtcNow, tenant: TenantId.Local);
            pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
            Assert.True(pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1,
                new[] { SessionWithATally("s-late", turns: 4) }));

            // FIRST READ, with no store. It must serve - a roster does not depend on statistics - and it
            // must record nothing, because there is nowhere to record to.
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("sessions")).StatusCode);

            // THE STORE ARRIVES. This is the late publish the statistics store's own contract permits.
            aggregator = new GatewayInputStatsAggregator(Path.Combine(_root, "gateway-stats.db"));
            concurrency = new GatewaySessionConcurrencyStats(Path.Combine(_root, "concurrency.json"));
            _out.WriteLine("statistics store published AFTER the routes were mapped and one roster served");

            // A fresh tally, so there is something new to fold.
            Assert.True(pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 2,
                new[] { SessionWithATally("s-late", turns: 11) }));

            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("sessions")).StatusCode);

            // THE CLAIM: the roster picked the store up without a restart, and actually WROTE.
            var totals = aggregator.CurrentTotals(TenantId.Local);
            var bucket = Assert.Single(totals.Buckets);
            Assert.Equal(11, bucket.Turns);
            _out.WriteLine($"roster recorded {bucket.Turns} turns into the late-arriving store");

            // And the concurrency half, which was captured by the same line and would fail the same way.
            Assert.True(concurrency.Snapshot(DateTime.UtcNow, TenantId.Local).Live.AllTimeMax >= 1,
                "the late-arriving concurrency recorder saw no roster, so it was captured rather than resolved");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            aggregator?.Dispose();
        }
    }
}
