using System;
using System.Net.Http;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>/healthz</c> REPORTS EACH SUBSYSTEM, so a deploy can tell "the process answers" apart from "the
/// pages work".
///
/// WHY THIS EXISTS. On 2 September 2026 a deploy went green and the release was broken. Production served
/// a sustained 200 on this endpoint carrying the commit that had just shipped - which is everything the
/// pipeline knew how to ask - while Your Throttle answered 503 to every request and the owner's turns went
/// unrecorded for hours. The container's statistics store had lost a race for a database connection during
/// the four minutes production and staging both ran, and nothing retried it.
///
/// NOTHING IN THE PIPELINE WAS WRONG, and that is the point worth keeping. It checked what it checked. The
/// Gateway reported ONE status for the whole of itself, so there was no question the deploy could have
/// asked that would have caught this. A subsystem that is designed to fail on its own without stopping the
/// process has to be able to SAY so somewhere the deploy reads, or it is by construction invisible to
/// every check that will ever be written.
///
/// THE TESTS ARE PAIRED, because "the block says available" proves nothing on its own - it is exactly what
/// a hard-coded string would say. So the same probe is run against a Gateway whose statistics store CANNOT
/// open, and the block has to say the opposite. A build that stopped computing this and answered
/// "available" always would pass the first test and fail the second.
///
/// AND THE REASON IS NOT ON IT. This endpoint is public and unauthenticated; the reason a subsystem is
/// down is a full operator sentence that names the database host. Status words only.
///
/// The assembly runs sequentially (TestParallelization), so setting environment variables here is safe.
/// </summary>
public sealed class HealthzSubsystemReadinessTests
{
    private const string Token = "test-token-subsystems";

    private readonly ITestOutputHelper _out;

    public HealthzSubsystemReadinessTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task A_healthy_statistics_store_reports_available()
    {
        var dir = TempDir();
        var (health, raw) = await ProbeHealthz(Path.Combine(dir, "gateway-stats.db"));

        Assert.NotNull(health.Subsystems);
        Assert.True(health.Subsystems!.ContainsKey("statistics"),
            "the readiness block must name the statistics subsystem - the deploy step keys off it");
        Assert.Equal("available", health.Subsystems["statistics"]);
        // The AI-call credential is reported beside it, so a hosted deploy that lost the setting fails the same
        // step. Its hosted/unset branch is proven in AiCallTagTests.
        Assert.Equal("available", health.Subsystems["ai-attribution"]);
        _out.WriteLine(raw);
    }

    /// <summary>
    /// THE OTHER HALF. A statistics store that cannot be opened - a directory sits where its file belongs,
    /// which is a real provider failure rather than a fabricated one - and the Gateway still serves. That
    /// is deliberate and unchanged: statistics are a failure domain of their own. What changed is that the
    /// probe now SAYS so, which is the difference between the deploy on 2 September passing and failing.
    /// </summary>
    [Fact]
    public async Task A_statistics_store_that_cannot_open_reports_unavailable_while_the_Gateway_still_serves()
    {
        var dir = TempDir();
        var obstructed = Path.Combine(dir, "gateway-stats.db");
        Directory.CreateDirectory(obstructed);   // a directory where the database file belongs

        var (health, raw) = await ProbeHealthz(obstructed);

        // The Gateway is up. That is the whole trap: this is a 200, and it always was.
        Assert.Equal("ok", health.Status);

        Assert.NotNull(health.Subsystems);
        Assert.Equal("unavailable", health.Subsystems!["statistics"]);
        _out.WriteLine(raw);
    }

    /// <summary>
    /// PUBLIC ENDPOINT, SO STATUS WORDS ONLY. The store's own reason is a sentence naming the database host
    /// and the connection's target; it belongs in the Gateway log and on the authenticated feed. An
    /// anonymous caller learns that our statistics are down - a fact about our service - and nothing about
    /// where our data lives.
    /// </summary>
    [Fact]
    public async Task The_readiness_block_never_carries_the_reason_or_the_database_target()
    {
        var dir = TempDir();
        var obstructed = Path.Combine(dir, "gateway-stats.db");
        Directory.CreateDirectory(obstructed);

        var (_, raw) = await ProbeHealthz(obstructed);

        foreach (var leak in new[] { "gateway-stats.db", "Host=", "postgres", "Sqlite", "Exception", dir })
            Assert.DoesNotContain(leak, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hz-subsys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<(HealthDto Health, string RawJson)> ProbeHealthz(string inputStatsPath)
    {
        var instancesDir = Path.Combine(Path.GetTempPath(), "cc-hz-subsys-i-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token,
            authEnabled: true,
            instancesDirectory: instancesDir,
            workListsPath: Path.Combine(instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(instancesDir, "snooze", "snooze.json"),
            inputStatsPath: inputStatsPath,
            streamMode: true);
        try
        {
            await gateway.StartAsync();

            // NO credential: /healthz is public, and that is exactly why the reason is not on it.
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            var resp = await http.GetAsync("healthz");
            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadAsStringAsync();
            var dto = System.Text.Json.JsonSerializer.Deserialize<HealthDto>(raw,
                          new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                      ?? throw new InvalidOperationException("healthz returned no body");
            return (dto, raw);
        }
        finally
        {
            await gateway.StopAsync();
            // Deliberately NOT deleting instancesDir - see HealthzTenantLeakTests for why a delete here can
            // crash the whole test process via a late FileSystemWatcher event. The OS reclaims the temp dir.
        }
    }
}
