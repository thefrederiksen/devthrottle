using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Throttle;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the DevThrottle Stats routes (<c>GET /stats</c> and <c>/stats/data</c>) on a
/// SELF-HOSTED Gateway. Boots ONLY <see cref="StatsPageEndpoint"/> on an ephemeral port, including under the
/// same host-wide <see cref="AuthMiddleware"/> the real Gateway applies.
///
/// WHAT THIS FILE USED TO PROVE, AND WHY IT CHANGED. It used to seed an aggregator with a few sessions and
/// assert the feed served their buckets, repos and agents from the <c>stat_delta</c> tally. Two rulings of
/// the "Clean up Your Throttle" mission (2026-09-05) retire both halves of that: Your Throttle is a
/// HOSTED-GATEWAY-ONLY feature (owner's ruling R1), so a self-hosted Gateway answers the data route with one
/// plain sentence and no figure (ruling R6); and on hosted every count of turns comes from the submission
/// ledger, never the tally (ruling R9) - proven end to end in <c>Throttle/ThrottleFeedReadsTheLedgerTests</c>.
/// What is left to prove here is the self-host answer itself: it is a 200, it is the sentence, it carries no
/// number, and the redirect and the auth gate in front of it are unchanged.
/// </summary>
public sealed class StatsPageEndpointTests : IDisposable
{
    private const string GatewayToken = "test-gateway-token-for-stats";
    private readonly string _dir;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string? _priorHosted;

    public StatsPageEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Declared, not assumed: this class is the self-host contract, so the process must actually be in
        // self-host mode while it runs - a leaked hosted toggle from another fixture would turn these into
        // tests of something else.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private async Task<(WebApplication app, HttpClient http)> StartAsync(bool authEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        if (authEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken
            {
                Hosted = false,
                Token = GatewayToken,
                Devices = new DeviceRegistry(Path.Combine(_dir, "devices-" + Guid.NewGuid().ToString("N") + ".json")),
            };
            app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        // The REAL self-host boundary (built over the SingleTenantContext) and a real ledger reader over a
        // throwaway database, exactly as the Gateway wires them; the point is that neither is consulted on
        // self-host, because the route answers before it gets to them.
        StatsPageEndpoint.Map(app, false, new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db")),
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new CcDirector.Core.Tenancy.SingleTenantContext(), new DeviceRegistry(), hosted: false),
            new ThrottleLedgerReader(_harness.Open()));
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    /// <summary>
    /// The standalone embedded dashboard is RETIRED (issue #587): GET /stats answers a redirect to the
    /// Cockpit /your-throttle route, on self-host too - the page is where the sentence gets shown.
    /// </summary>
    [Fact]
    public async Task StatsPage_RedirectsToYourThrottle()
    {
        var (app, http) = await StartAsync(authEnabled: false);
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
    /// Rulings R1 and R6: a self-hosted Gateway has no Your Throttle figure, and says so in one sentence on a
    /// 200 the page renders - not a 404 (reads as a broken build), not a 503 (reads as an outage), and never
    /// a number computed from a store the mentor report does not read.
    /// </summary>
    [Fact]
    public async Task StatsData_OnSelfHost_AnswersTheOneSentence_AndNoFigure()
    {
        var (app, http) = await StartAsync(authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("available").GetBoolean());
            Assert.Equal(StatsPageEndpoint.SelfHostReason, root.GetProperty("reason").GetString());
            Assert.Contains("hosted DevThrottle Gateway", StatsPageEndpoint.SelfHostReason);
            Assert.Contains("self-hosted", StatsPageEndpoint.SelfHostReason);

            // No figure, no partial figure, nothing a client could mistake for one.
            var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "available", "reason" }, properties);
            Assert.DoesNotContain("\"turns\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"buckets\"", body, StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>The host-wide auth gate is unchanged in front of the route: no token is refused, the token is
    /// admitted - and what it is admitted to on self-host is the sentence.</summary>
    [Fact]
    public async Task AuthEnabled_NoToken_Returns401_AndWithToken_Returns200()
    {
        var (app, http) = await StartAsync(authEnabled: true);
        try
        {
            var noToken = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
            var withToken = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
            using var doc = JsonDocument.Parse(await withToken.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
