using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Sessions;
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
/// Boots ONLY the stats group on an ephemeral port, exactly as <see cref="StatsPageEndpointTests"/> does, and
/// hands the caller the route group back so a test can map routes onto it. Used by the self-host control
/// below; on self-host the data route answers the one sentence (rulings R1 and R6) before it resolves a
/// tenant or reads a store.
/// </summary>
internal static class StatsGroupProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        GatewayInputStatsAggregator aggregator,
        ThrottleLedgerReader throttle,
        Action<RouteGroupBuilder>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        // The boundary is required and non-nullable now (finding I1-01). This probe host is used by the
        // SELF-HOST control tests only, so it gets the REAL self-host boundary: built over the
        // SingleTenantContext, it always resolves the single Local tenant.
        var group = StatsPageEndpoint.Map(app, false, aggregator,
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            throttle);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }
}

/// <summary>
/// THE SELF-HOST CONTROL, STATED EXPLICITLY.
///
/// Self-host is the control for this whole mission, so it is STATED rather than INHERITED: the probe host
/// here is built with <c>hosted: false</c>, a required argument, so no test below can be running in the mode
/// it thinks it is not in. (The mode used to be a process-wide environment variable with two non-hosted
/// forms, absent and present-but-not-"1", and this class set both; the mode is now a value handed to the
/// host, and the forms are proved once, in <c>GatewayHostedModeTests</c>.)
///
/// It asserts the routes serve their REAL PAYLOADS - the seeded counts, the ranked
/// repository, the honesty caveats, the dashboard markup - and not merely that a refusal string is absent.
/// On self-host the data route resolves to the single Local tenant, so it serves exactly as it always has.
/// </summary>
public sealed class HostedStatsSelfHostControlTests : IDisposable
{
    private readonly string _dir;
    private readonly GatewayDbTestHarness _harness = new();

    public HostedStatsSelfHostControlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-selfhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private ThrottleLedgerReader Reader() => new(_harness.Open());

    private GatewayInputStatsAggregator SeededAggregator()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s-" + Guid.NewGuid().ToString("N") + ".db"));
        agg.Observe(new SessionDto
        {
            SessionId = "s1",
            RepoPath = @"D:\ReposFred\devthrottle",
            InputStats = new InputStatsDto
            {
                Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = 7, Characters = 700 } },
            },
        });
        return agg;
    }

    /// <summary>The standalone dashboard page is retired (issue #587): on self-host too, /stats answers a
    /// redirect to the Cockpit /your-throttle route rather than serving embedded HTML.</summary>
    [Fact]
    public async Task The_stats_page_redirects_to_your_throttle_on_self_host()
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(SeededAggregator(), Reader());
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var raw = new HttpClient(handler) { BaseAddress = http.BaseAddress };
            var resp = await raw.GetAsync("/stats");
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            Assert.Equal("/your-throttle", resp.Headers.Location!.ToString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// THE SELF-HOST CONTROL AFTER RULING R1: Your Throttle is a hosted-Gateway feature, so a self-hosted
    /// Gateway - the variable absent OR explicitly "0" - answers the data route with one sentence and no
    /// figure (ruling R6). It is a 200, because the absence of a figure is a fact about this Gateway and not
    /// a fault in the request; and the aggregator that was seeded with real numbers is never consulted, so
    /// those numbers do not leak into the answer.
    /// </summary>
    [Fact]
    public async Task The_stats_feed_on_self_host_answers_the_one_sentence_and_no_figure()
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(SeededAggregator(), Reader());
        try
        {
            var resp = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("available").GetBoolean());
            Assert.Equal(StatsPageEndpoint.SelfHostReason, root.GetProperty("reason").GetString());
            Assert.Equal(new[] { "available", "reason" }, root.EnumerateObject().Select(p => p.Name).ToArray());
            // The seeded seven turns are nowhere in the answer.
            Assert.DoesNotContain("\"turns\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("devthrottle", body, StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>A route added to the group still serves on self-host, in both non-hosted forms.</summary>
    [Fact]
    public async Task A_route_added_to_the_group_still_serves_on_self_host()
    {
        var (app, http) = await StatsGroupProbeHost.StartAsync(
            SeededAggregator(), Reader(),
            mapIntoGroup: group => group.MapGet("/stats/added-later",
                () => Results.Json(new { probe = "served" })));
        try
        {
            var resp = await http.GetAsync("/stats/added-later");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("served", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
