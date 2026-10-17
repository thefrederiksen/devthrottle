using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeDisplay;

/// <summary>
/// The API-mode verdict on what the clients read (mission #4070, phase 2): the REAL <c>GET /sessions</c> and
/// <c>GET /sessions/{sid}</c> routes on an in-memory test server (no port), over the REAL pushed roster, the REAL ingest,
/// receipt store (the harness's SQLite file) and event buffer. An API-mode row carries <c>apiMode</c>; a terminal-mode
/// row is byte-for-byte what the same Gateway serves without API mode wired at all. And <c>GET /sessions/{sid}/receipts</c>
/// carries each receipt's finished words.
/// </summary>
public sealed class ApiModeVerdictOnTheRosterTests : IDisposable
{
    private const string DirectorId = "director-api-mode-verdict";
    private const string ConnectionId = "connection-1";
    private const string TerminalSid = "0f0e0d0c-0b0a-4908-8706-050403020100";
    private static readonly DateTime Pushed = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-api-mode-verdict-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayDbTestHarness _harness = new();
    // Self-host, as production runs it there: ONE single-tenant context under the stores and the boundary, so every
    // read resolves to the Local tenant the pushes and the ingest below went into.
    private readonly SingleTenantContext _single = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly GatewayDatabase _db;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();
    private readonly DirectorRegistry _registry;
    // An injected clock, so the clocks the roster recomputes on every read (idle seconds) are the same on every read.
    private readonly PushedSessionStore _pushed = new(() => Pushed.AddMinutes(5));
    private readonly List<WebApplication> _apps = new();

    public ApiModeVerdictOnTheRosterTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_single, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _db = _harness.Open(_single);
        _receipts = new ApiModeReceiptStore(_db, () => T0.UtcDateTime);
    }

    public void Dispose()
    {
        foreach (var app in _apps)
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task GetSessions_AnApiModeSessionThatFailedMidTurn_CarriesTheVerdict()
    {
        // Arrange
        PushRoster();
        Ingest(DeliveredThenFailedMidTurn());
        var http = await StartAsync(withApiMode: true);

        // Act
        var row = await RowAsync(http, Sid);

        // Assert: read off the wire by the camel-case names the clients read.
        var verdict = row.GetProperty("apiMode");
        Assert.Equal("API mode", verdict.GetProperty("connectionLabel").GetString());
        Assert.Equal("Idle after turn 1", verdict.GetProperty("activityLabel").GetString());
        Assert.Equal(1, verdict.GetProperty("turnNumber").GetInt32());
        Assert.False(verdict.GetProperty("working").GetBoolean());
        Assert.Equal("The last turn failed: Pi exited during the turn (exit code 137)", verdict.GetProperty("lastTurnEnding").GetString());
        Assert.False(verdict.GetProperty("agentConnected").GetBoolean());
        Assert.Equal("Agent disconnected: Pi exited during the turn (exit code 137)", verdict.GetProperty("agentLabel").GetString());
    }

    [Fact]
    public async Task GetSessions_TheUserHasTheWheel_SaysYouHaveTheWheel()
    {
        // Arrange
        PushRoster(apiWheel: WheelHolders.User);
        Ingest(Queued(1, "p1", PromptQueuedReason.UserHasTheWheel));
        var http = await StartAsync(withApiMode: true);

        // Act
        var verdict = (await RowAsync(http, Sid)).GetProperty("apiMode");

        // Assert
        Assert.Equal("API mode - You have the wheel", verdict.GetProperty("connectionLabel").GetString());
        Assert.True(verdict.GetProperty("userHasTheWheel").GetBoolean());
        Assert.Equal("1 prompt waiting: you have the wheel", verdict.GetProperty("queueLabel").GetString());
    }

    [Fact]
    public async Task GetSessions_TheWheelButton_ReachesTheWireByItsCamelCaseNames()
    {
        // Arrange: the protocol process holds the wheel and a turn is running, so the button is there and disabled.
        PushRoster();
        Ingest(Ready(1), Delivered(2, "p1", PlainAck), Started(3, "t1", "p1"));
        var http = await StartAsync(withApiMode: true);

        // Act
        var wheel = (await RowAsync(http, Sid)).GetProperty("apiMode").GetProperty("wheelAction");

        // Assert
        Assert.Equal("takeTheWheel", wheel.GetProperty("verb").GetString());
        Assert.Equal("Take the wheel", wheel.GetProperty("label").GetString());
        Assert.False(wheel.GetProperty("enabled").GetBoolean());
        Assert.Equal("A turn is running. Stop it, or wait for it to end, before taking the wheel.", wheel.GetProperty("disabledReason").GetString());
    }

    [Fact]
    public async Task GetSessions_ATerminalModeSession_IsByteForByteTheRowWithoutApiMode()
    {
        // Arrange: the same roster served by a Gateway with API mode wired and by one without it.
        PushRoster();
        Ingest(DeliveredThenDone());
        var withApiMode = await StartAsync(withApiMode: true);
        var without = await StartAsync(withApiMode: false);

        // Act
        var rowWith = (await RowAsync(withApiMode, TerminalSid)).GetRawText();
        var rowWithout = (await RowAsync(without, TerminalSid)).GetRawText();

        // Assert
        Assert.Equal(rowWithout, rowWith);
        Assert.DoesNotContain("apiMode", rowWith, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSession_ById_CarriesTheVerdictForAnApiModeSessionAndNoneForATerminalOne()
    {
        // Arrange
        PushRoster();
        Ingest(DeliveredThenCancelled());
        var http = await StartAsync(withApiMode: true);

        // Act
        using var api = JsonDocument.Parse(await http.GetStringAsync("/sessions/" + Sid));
        using var terminal = JsonDocument.Parse(await http.GetStringAsync("/sessions/" + TerminalSid));

        // Assert
        Assert.Equal("The last turn was stopped: stopped by the user", api.RootElement.GetProperty("apiMode").GetProperty("lastTurnEnding").GetString());
        Assert.False(terminal.RootElement.TryGetProperty("apiMode", out _));
    }

    [Fact]
    public void GetReceipts_EachReceipt_CarriesItsFinishedWords()
    {
        // Arrange
        PushRoster();
        Ingest(DeliveredThenFailedMidTurn());
        Ingest(Refused(7, "p2", RefusalBusy, PromptRefusedBy.Agent), Queued(8, "p3", PromptQueuedReason.TurnRunning));

        // Act
        var result = SessionReceiptsEndpoint.Read(new DefaultHttpContext(), Sid, _receipts, _pushed, TimeSpan.FromDays(3650), _boundary);

        // Assert
        var body = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<SessionReceiptsDto>>(result).Value!;
        var words = body.Receipts.ToDictionary(r => r.PromptId, r => (r.Words, r.Tone));
        Assert.Equal(("Delivered - the turn failed: Pi exited during the turn (exit code 137)", "red"), words["p1"]);
        Assert.Equal(("Refused: " + RefusalBusy, "red"), words["p2"]);
        Assert.Equal(("Queued: a turn is running", "neutral"), words["p3"]);
    }

    // ----- helpers -----

    private void PushRoster(string apiWheel = WheelHolders.Protocol)
    {
        _registry.RegisterFromStream(DirectorId, "TEST-PC", "tester", "1.0", 1, Pushed, TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, DirectorId, ConnectionId);
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, ConnectionId, 1, new List<SessionDto>
        {
            new() { SessionId = Sid, Name = "Pi in API mode", Agent = "Pi", Status = "Running", ActivityState = "Idle", LastActivityAt = Pushed, ConnectionMode = ConnectionModes.Api, WheelHolder = apiWheel },
            new() { SessionId = TerminalSid, Name = "Pi in a terminal", Agent = "Pi", Status = "Running", ActivityState = "Idle", LastActivityAt = Pushed },
        }));
    }

    private void Ingest(params SessionEvent[] events)
        => Assert.NotNull(new ApiModeEventIngest(_buffer, _receipts, _boundary).Accept(TenantId.Local, DirectorId, A(events)));

    private static async Task<JsonElement> RowAsync(HttpClient http, string sid)
    {
        using var response = await http.GetAsync("/sessions");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Single(r => r.GetProperty("sessionId").GetString() == sid).Clone();
    }

    /// <summary>The real Gateway routes on an in-memory test server: no port.</summary>
    private async Task<HttpClient> StartAsync(bool withApiMode)
    {
        // No configuration reloading, so the host opens no file watchers: whether a test passes must not depend on how
        // many the machine has left.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = new[] { "--hostBuilder:reloadConfigOnChange=false" } });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseRouting();
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: _boundary,
            aiGate: TenantAiGate.SelfHost, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            pushedSessions: _pushed,
            apiModeFacts: withApiMode ? new ApiModeDisplayFacts(_buffer, new ApiModeVerdictReceipts(_db)) : null);
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestClient();
    }
}
