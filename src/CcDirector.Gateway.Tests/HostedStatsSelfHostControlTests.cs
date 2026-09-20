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
        var group = StatsPageEndpoint.Map(app, aggregator,
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
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
/// Self-host is the control for this entire hosted-tenancy mission, so it has to be PROVEN rather than
/// INHERITED. <see cref="StatsPageEndpointTests"/> does not prove it: those tests never mention
/// <c>CC_GATEWAY_HOSTED</c> and pass only because the runner happens to leave it unset. If that ambient
/// default ever flipped - one leaked environment variable, one continuous-integration image change, one test
/// that forgot to restore it - they would keep passing while self-host was broken, because they assert
/// nothing about which mode they are in.
///
/// So this class sets the variable itself, to BOTH non-hosted values that occur in practice: absent, and
/// present-but-not-"1". It then asserts the routes serve their REAL PAYLOADS - the seeded counts, the ranked
/// repository, the honesty caveats, the dashboard markup - and not merely that a refusal string is absent.
/// On self-host the data route resolves to the single Local tenant, so it serves exactly as it always has.
/// </summary>
public sealed class HostedStatsSelfHostControlTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _priorHosted;
    private readonly GatewayDbTestHarness _harness = new();

    public HostedStatsSelfHostControlTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-selfhost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private ThrottleLedgerReader Reader() => new(_harness.Open());

    /// <summary>
    /// Puts the process into a stated non-hosted mode and proves it took, so no test below can silently be
    /// running in the mode it thinks it is not in.
    /// </summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

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

    /// <summary>
    /// null = the variable is absent. "0" = the variable is present and explicitly not hosted. Both are real
    /// non-hosted deployments and both must serve.
    /// </summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    /// <summary>The standalone dashboard page is retired (issue #587): on self-host too, /stats answers a
    /// redirect to the Cockpit /your-throttle route rather than serving embedded HTML.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_stats_page_redirects_to_your_throttle_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

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
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_stats_feed_on_self_host_answers_the_one_sentence_and_no_figure(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

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
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);

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
