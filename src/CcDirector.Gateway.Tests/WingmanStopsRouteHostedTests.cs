using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// <c>GET /sessions/{sid}/wingman-stops</c> on a REAL hosted host (the Wingman inspector, phase 2).
///
/// WHAT ONLY A BOOTED HOST CAN PROVE:
///
///  - THE GUARD REFUSES A SESSION KEY on the path the route is really mapped on. The unit tests prove the guard's
///    decision about a string and the handler's own refusal; only a host proves those are the same path.
///  - THE PRODUCTION STAMP. The host hands the trace writer the display push's own fold. A stop the production seat
///    expires, written by the production writer, carries the colour the row wore - and keeps it after the row has moved.
///  - THE ACCOUNT. A session of another account answers exactly what an unknown session answers.
/// </summary>
public sealed class WingmanStopsRouteHostedTests : IAsyncLifetime
{
    private const string SharedToken = "wingman-stops-route-token";

    private readonly ITestOutputHelper _out;

    private GatewayHost _gateway = null!;
    private HttpClient _deviceA = null!;
    private HttpClient _deviceB = null!;
    private HttpClient _sessionKeyInA = null!;
    private TenantId _tenantA;
    private TenantId _tenantB;

    private readonly string _sessionId = Guid.NewGuid().ToString();
    // A fresh pair of account subjects per test: a fixed subject mints the same tenant in every test, and a switch one
    // test turns on would still be on for the next (see TurnVerdictRouteTests for the run that found it).
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wingman-stops-route-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private long _pushSequence;

    public WingmanStopsRouteHostedTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var a = HostedTestEnrollment.Enroll(
            _gateway, $"sub-stops-a-{_runId}", $"stops-a-{_runId}@example.com", $"dev-sa-{_runId}", "MSA");
        var b = HostedTestEnrollment.Enroll(
            _gateway, $"sub-stops-b-{_runId}", $"stops-b-{_runId}@example.com", $"dev-sb-{_runId}", "MSB");
        _tenantA = a.Tenant;
        _tenantB = b.Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");

        _deviceA = Client(a.DeviceKey);
        _deviceB = Client(b.DeviceKey);

        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, "director-stops-a", _sessionId,
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        _sessionKeyInA = Client(sessionKey);

        Push(_tenantA, "director-stops-a", _sessionId);
    }

    public async Task DisposeAsync()
    {
        _deviceA.Dispose();
        _deviceB.Dispose();
        _sessionKeyInA.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private void Push(TenantId tenant, string directorId, params string[] sessionIds)
    {
        _gateway.Registry.RegisterFromStream(directorId, "MACHINE-" + directorId, "soren", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: tenant);
        _gateway.PushedSessions.RegisterConnection(tenant, directorId, "conn-" + directorId);
        Assert.True(_gateway.PushedSessions.ApplySnapshot(tenant, directorId, "conn-" + directorId, ++_pushSequence,
            sessionIds.Select(id => new SessionDto
            {
                SessionId = id,
                Name = id,
                ActivityState = "WaitingForInput",
                LastActivityAt = DateTime.UtcNow,
            }).ToList()));
    }

    private async Task<(HttpStatusCode Status, string Body)> Get(HttpClient http, string path)
    {
        var resp = await http.GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"GET {path} -> {(int)resp.StatusCode} {resp.StatusCode}");
        _out.WriteLine("    " + (body.Length > 600 ? body[..600] + " ...(truncated)" : body));
        return (resp.StatusCode, body);
    }

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private static TurnVerdictDto Verdict(string id, string word, string label, DateTime judgedAt) => new()
    {
        VerdictId = id,
        JudgedAtUtc = judgedAt,
        TurnEndObservedAtUtc = judgedAt.AddSeconds(-10),
        ScreenHash = "screen-hash-" + id,
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = word,
        Confidence = "high",
        Evidence = "I am watching the test run and will push the tag when it is green.",
        Label = label,
        Summary = "It is watching the test run by itself.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "It is watching the test run.",
    };

    [Fact]
    public async Task A_session_key_is_refused_on_the_mapped_path_and_the_accounts_device_reads_it()
    {
        var refused = await Get(_sessionKeyInA, $"sessions/{_sessionId}/wingman-stops");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

        // THE POSITIVE CONTROL: the same path, the account's own device, served - so the 403 above is not a route that
        // refuses everybody.
        var (status, body) = await Get(_deviceA, $"sessions/{_sessionId}/wingman-stops");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_sessionId, Root(body).GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task A_stop_the_production_seat_expires_is_served_red_and_stays_red_after_the_row_goes_cyan()
    {
        _gateway.TenantSettingsResolver.SetTurnVerdictJudgeEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenantA, true, DateTime.UtcNow);
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId,
            Verdict("v-carrying-on", Core.Wingman.TurnVerdictVocabulary.ContinuesAlone, "Watching the test run", DateTime.UtcNow.AddMinutes(-30)));

        // The PRODUCTION seat, the PRODUCTION environment and the PRODUCTION trace writer with the host's stamp.
        Assert.Equal(1, _gateway.EnsureTurnVerdictServiceForTest().ExpireCarryingOn(_tenantA));

        // The writer is off the verdict path, so the stop arrives a moment later. Wait for it, with a ceiling.
        JsonElement stop = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var (s, b) = await Get(_deviceA, $"sessions/{_sessionId}/wingman-stops");
            Assert.Equal(HttpStatusCode.OK, s);
            var stops = Root(b).GetProperty("stops");
            if (stops.GetArrayLength() == 1) { stop = stops[0]; break; }
            await Task.Delay(100);
        }
        var writer = _gateway.TurnVerdictTraceWriterForTest;
        Assert.True(stop.ValueKind == JsonValueKind.Object,
            $"No stop arrived. The trace writer says: written={writer.Written} failed={writer.Failed} colourFoldFailed={writer.ColourFoldFailed} "
            + $"dropped={writer.Dropped} lost={writer.Lost} abandoned={writer.Abandoned} lastFailure={writer.LastFailure ?? "none"}.");
        Assert.Equal("expired", stop.GetProperty("outcome").GetString());
        Assert.True(stop.GetProperty("rowRecorded").GetBoolean());
        Assert.Equal("red", stop.GetProperty("rowColour").GetString());
        Assert.Equal(Wingman.TurnVerdictWatchdog.ExpiredLabel, stop.GetProperty("rowLabel").GetString());

        // The row moves on: a later verdict calls the stop finished.
        _gateway.TurnVerdicts.Store(_tenantA, _sessionId,
            Verdict("v-finished", Core.Wingman.TurnVerdictVocabulary.Finished, "Pushed the tag", DateTime.UtcNow.AddSeconds(1)));
        var (rowStatus, rowBody) = await Get(_deviceA, $"sessions/{_sessionId}");
        Assert.Equal(HttpStatusCode.OK, rowStatus);
        // THE CONTROL: the row really is cyan now.
        Assert.Equal("cyan", Root(rowBody).GetProperty("effectiveColor").GetString());

        var (_, after) = await Get(_deviceA, $"sessions/{_sessionId}/wingman-stops");
        Assert.Equal("red", Root(after).GetProperty("stops")[0].GetProperty("rowColour").GetString());
    }

    [Fact]
    public async Task Another_accounts_session_answers_exactly_what_an_unknown_session_answers()
    {
        var foreign = await Get(_deviceB, $"sessions/{_sessionId}/wingman-stops");
        var unknown = await Get(_deviceB, $"sessions/{Guid.NewGuid()}/wingman-stops");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("session_not_found", Root(foreign.Body).GetProperty("code").GetString());
        Assert.Equal(unknown.Status, foreign.Status);
        Assert.Equal(unknown.Body, foreign.Body);
    }
}
