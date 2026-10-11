using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Stats.Data;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// PROOF ROW 10: THE GATEWAY STARTS AND SERVES A ROSTER WITH THE STATISTICS DATABASE UNREACHABLE.
///
/// This is the row the whole step is judged on. On 2026-07-30 the hosted Gateway answered HTTP 500 to every
/// client for thirty-two minutes because a statistics fault propagated out of the roster handler. Removing
/// SQLite from the hosted Gateway must not replace that with a NEW way for statistics to take the Gateway
/// down - and a statistics migration that ran inside the startup gate would be a worse version of it, since
/// the process would never bind its port at all and there would be no roster to serve.
///
/// WHAT MAKES THIS FIXTURE ABLE TO SHOW THE FAILURE, rather than merely able to pass. Three things, and
/// without them a green here would prove nothing:
///
///  1. The statistics connection points at a database that is genuinely NOT THERE, and the same connection
///     is proven to THROW when it is not contained -
///     <see cref="GatewayStatsStoreContainmentTests.TheSameFault_IsFatal_WhenItIsNotContained"/>. So a
///     Gateway that let the fault propagate would fail to construct, and this test would error rather than
///     pass.
///  2. The store is asserted to have ATTEMPTED the connection - reason UNREACHABLE, failure count one. A
///     Gateway that quietly skipped statistics entirely would report NOT CONFIGURED instead, and these
///     assertions would fail. "It started" is not the claim; "it started having tried and failed" is.
///  3. The roster response is asserted to be a real roster body, not merely a 200. A catch-all, an error
///     page or an empty body would all carry a 200.
///
/// PROOF ROW 15 rides in this file too, because it is the same fixture one assertion over: nothing writes
/// gateway-concurrency-stats.json on the hosted path. It has its own control - the identical run with the
/// hosted flag off DOES write that file - so the absence is a refusal rather than a path that was never
/// going to be taken in a test.
/// </summary>
public sealed class GatewayStartsWithStatisticsUnreachableTests : IDisposable
{
    private const string SharedToken = "test-token";

    /// <summary>
    /// A PostgreSQL endpoint that is not there. Port 1 on the loopback interface: nothing listens on it, the
    /// connection is refused immediately, and the short timeouts bound it further. See
    /// <see cref="GatewayStatsStoreContainmentTests"/> for the arm that watches this same connection kill an
    /// uncontained migration.
    /// </summary>
    private const string DeadPostgres =
        "Host=127.0.0.1;Port=1;Database=gateway_live;Username=gateway_app;Password=s3cret;" +
        "Timeout=2;Command Timeout=2";

    private const string ConcurrencyFileName = "gateway-concurrency-stats.json";

    private readonly ITestOutputHelper _out;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cc-stats-unreachable-" + Guid.NewGuid().ToString("N"));

    private readonly string? _priorRoot;
    private readonly string? _priorStatsConnection;

    public GatewayStartsWithStatisticsUnreachableTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);

        // Its OWN storage root, not the assembly-wide shared one. The claim in row 15 is about a file NOT
        // existing, and a shared root that another test had already written that file into would make this
        // fail for a reason that has nothing to do with the hosted path.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _priorStatsConnection = Environment.GetEnvironmentVariable(
            StatsConnectionSelection.StatsConnectionEnvVar);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable(
            StatsConnectionSelection.StatsConnectionEnvVar, _priorStatsConnection);
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    // ==================================================================== row 10

    [Fact]
    public async Task HostedGateway_StartsAndServesARoster_WithTheStatisticsDatabaseUnreachable()
    {
        Environment.SetEnvironmentVariable(
            StatsConnectionSelection.StatsConnectionEnvVar, DeadPostgres);

        // THE START. If the statistics migration were fatal, this line throws and there is no roster at all.
        await using var gateway = NewGateway(hosted: true);
        await gateway.StartAsync();

        // THE STATISTICS STORE TRIED, AND FAILED, AND SAID SO. Without these the test would also pass
        // against a Gateway that had simply never looked at a statistics database.
        Assert.False(gateway.StatsStore.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.Unreachable, gateway.StatsStore.Availability.Reason);
        Assert.Equal("unreachable", gateway.StatsStore.Availability.ReasonCode);
        Assert.Equal(1, gateway.StatsStore.Health.FailureCount);
        Assert.NotNull(gateway.StatsStore.Health.LastError);
        Assert.Null(gateway.StatsStore.Factory);

        _out.WriteLine(
            $"STATISTICS: available={gateway.StatsStore.IsAvailable} " +
            $"reason={gateway.StatsStore.Availability.ReasonCode} " +
            $"source={gateway.StatsStore.Availability.Source}");
        _out.WriteLine($"STATISTICS DETAIL: {gateway.StatsStore.Availability.Detail}");

        // THE ROSTER. A real hosted caller, through the real auth middleware.
        var device = HostedTestEnrollment.Enroll(gateway, "sub-roster", "roster@example.com", "dev-r", "MR");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceKey);

        var response = await http.GetAsync("sessions");
        var body = await response.Content.ReadAsStringAsync();

        _out.WriteLine($"GET /sessions -> {(int)response.StatusCode} {response.StatusCode}");
        _out.WriteLine($"BODY: {body}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Status and media type BEFORE any parse: parsing is itself an assertion about format, and
        // parse-first would turn a wrong response into a parser crash, which proves nothing.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // A real roster body, not merely a 200. The roster route answers with a JSON array of sessions; an
        // error page, a catch-all or an empty body would all have carried a 200 and would fail here.
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
    }

    // ==================================================================== row 15

    /// <summary>
    /// PROOF ROW 15: gateway-concurrency-stats.json is not written on the hosted path.
    ///
    /// The CONTROL is the second half of this test and it is what makes the first half mean anything: the
    /// identical run with the hosted flag off writes that file. So the absence on the hosted path is the
    /// hosted path refusing to write it, not a fixture in which nobody would have written it anyway.
    /// </summary>
    [Fact]
    public async Task ConcurrencyStatisticsFile_IsNeverWrittenOnTheHostedPath_AndIsWrittenOnSelfHost()
    {
        var file = Path.Combine(_root, ConcurrencyFileName);

        // ---- HOSTED: no recorder is constructed at all, so the file is never written.
        Environment.SetEnvironmentVariable(
            StatsConnectionSelection.StatsConnectionEnvVar, DeadPostgres);

        await using (var hosted = NewGateway(hosted: true))
        {
            await hosted.StartAsync();
            Assert.Null(hosted.SessionConcurrency);

            var device = HostedTestEnrollment.Enroll(hosted, "sub-conc", "conc@example.com", "dev-c", "MC");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{hosted.Port}/") };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", device.DeviceKey);

            // Drive the roster - the path that writes this file - more than once.
            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("sessions")).StatusCode);
        }

        var afterHosted = Directory.GetFiles(_root, "*.json").Select(Path.GetFileName).ToList();
        _out.WriteLine("HOSTED root contents: " + string.Join(", ", afterHosted));
        Assert.False(File.Exists(file), $"{ConcurrencyFileName} was written on the HOSTED path.");

        // ---- CONTROL, SELF-HOST: the same roster calls DO write it. One value different.
        Environment.SetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar, null);

        await using (var selfHost = NewGateway(hosted: false))
        {
            await selfHost.StartAsync();
            Assert.NotNull(selfHost.SessionConcurrency);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{selfHost.Port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SharedToken);

            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("sessions")).StatusCode);
        }

        var afterSelfHost = Directory.GetFiles(_root, "*.json").Select(Path.GetFileName).ToList();
        _out.WriteLine("SELF-HOST root contents: " + string.Join(", ", afterSelfHost));
        Assert.True(
            File.Exists(file),
            $"CONTROL FAILED: {ConcurrencyFileName} was not written on the SELF-HOST path either, so the " +
            "hosted assertion above proves nothing about the hosted path.");
    }

    private GatewayHost NewGateway(bool hosted) =>
        new(port: GatewayHost.OperatingSystemAssignedPort,
            token: SharedToken,
            authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            cronJobsPath: Path.Combine(_root, "cron", "cronjobs.json"),
            snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_root, "missions", "missions.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = hosted });
}
