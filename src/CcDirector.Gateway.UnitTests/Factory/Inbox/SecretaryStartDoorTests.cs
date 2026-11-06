using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// A SECRETARY'S KEY STARTS NOTHING (Factory Inbox mission, phase 2, task D), proved through the REAL authentication
/// middleware and the REAL session key registry on an in-memory test server. Every start door is mapped as a route that
/// answers "reached" - so a refusal here is the middleware's, made before any route runs, which is the point: no door can
/// forget to ask.
///
/// The caller is told apart only by the history row of the session its own key proves (the reader stands in for the
/// session history): a Secretary - started as one, a child that inherited the seat, or one moved by a handover - is refused
/// at every door with a sentence; a session with no row yet is told to try again; a Boss and an ordinary session reach
/// every door. A Secretary still reaches its own inbox routes and its reads.
/// </summary>
public sealed class SecretaryStartDoorTests : IAsyncLifetime
{
    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>One request per start door, written as a session's command line would send it.</summary>
    public static readonly TheoryData<string, string> Doors = new()
    {
        { "POST", "/machines/SOREN_NORTH/sessions" },
        { "POST", "/directors/dir-1/sessions" },
        { "POST", "/machines/SOREN_NORTH/launch" },
        { "POST", "/gateway/factory/seats/writer/start" },
        { "POST", "/directors/dir-1/handovers" },
        { "POST", "/gateway/workspaces/ws-1/restore" },
        { "POST", "/cron/jobs" },
        { "PUT", "/cron/jobs/job-1" },
        { "POST", "/cron/jobs/job-1/run" },
        { "POST", "/triggers" },
        { "PUT", "/triggers/trig-1" },
        { "POST", "/triggers/trig-1/resume" },
        { "POST", "/directors/dir-1/browsers/b-1/start" },
        { "POST", "/directors/dir-1/browsers/b-1/signin" },
        { "POST", "/gateway/fleet-manager/start" },
        { "POST", "/gateway/fleet-manager/restart" },
        { "POST", "/gateway/fleet-manager/move" },
    };

    private readonly GatewayDbTestHarness _h = new();
    private readonly Dictionary<Guid, SessionFactoryLookup> _rows = new();
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
    private int _seatReads;
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var sessions = new SessionKeyRegistry(_h.Open(), isHosted: false, clock: () => Now);
        Seat("secretary", SessionFactoryLookup.In("invoices", FactoryInboxSeats.Secretary) with { Kit = FactoryKits.Seat });
        Seat("secretary-child", SessionFactoryLookup.In("invoices", FactoryInboxSeats.Secretary, 3) with { Kit = FactoryKits.RulesOnly });
        Seat("secretary-capitals", SessionFactoryLookup.In("invoices", "Secretary") with { Kit = FactoryKits.Seat });
        Seat("boss", SessionFactoryLookup.In("invoices", "boss", 3) with { Kit = FactoryKits.Seat });
        Seat("worker", SessionFactoryLookup.In("invoices", "writer", 3) with { Kit = FactoryKits.Seat });
        Seat("ordinary", SessionFactoryLookup.InNoFactory);
        Seat("not-yet-known", SessionFactoryLookup.NotKnown);
        foreach (var (name, _) in _keys.ToList())
        {
            var key = GatewaySessionKey.Mint();
            var id = Guid.Parse(_keys[name]);
            Assert.True(sessions.Register(A, "dir-1", id.ToString("D"), GatewaySessionKey.Hash(key), Now.AddHours(1)));
            _keys[name] = key;
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var config = new AuthMiddleware.RequireToken
        {
            Hosted = false, Token = "shared-machine-token", Devices = null, Sessions = sessions,
            // Every key here is raised, so the Fleet Manager placement doors - a raised key's only - are reachable too.
            IsRaised = _ => true,
            RecordRaisedAction = (_, _, _, _) => { },
            SessionSeatOf = identity =>
            {
                _seatReads++;
                Assert.Equal(A, identity.Tenant);
                return _rows[identity.SessionId];
            },
        };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, config, next));
        foreach (var door in Doors)
            app.MapMethods((string)door[1], [(string)door[0]], () => Results.Ok(new { reached = true }));
        app.MapGet("/sessions/{sid}/factory-inbox", () => Results.Ok(new { reached = true }));
        app.MapPost("/sessions/{sid}/factory-inbox/bundle", () => Results.Ok(new { reached = true }));
        app.MapGet("/sessions", () => Results.Ok(new { reached = true }));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private void Seat(string name, SessionFactoryLookup row)
    {
        var id = Guid.NewGuid();
        _rows[id] = row;
        _keys[name] = id.ToString("D");
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string who)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _keys[who]);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public async Task StartDoor_SecretaryKey_IsRefusedWithASentence_BeforeTheRouteRuns(string method, string path)
    {
        var (status, body) = await Call(method, path, "secretary");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("secretary_starts_nothing", body.GetProperty("code").GetString());
        Assert.Equal(SecretaryStartLimit.Refusal, body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("reached", out _));
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public async Task StartDoor_SessionCarryingTheSecretarySeatAnyOtherWay_IsRefused(string method, string path)
    {
        // A child that inherited the seat with the rules-only kit, and the seat spelled in capitals: both are the Secretary.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(method, path, "secretary-child")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(method, path, "secretary-capitals")).Status);
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public async Task StartDoor_BossWorkerAndOrdinarySession_ReachTheRoute(string method, string path)
    {
        foreach (var who in new[] { "boss", "worker", "ordinary" })
        {
            var (status, body) = await Call(method, path, who);
            Assert.True(status == HttpStatusCode.OK, $"{who} at {method} {path} answered {(int)status}: {body}");
            Assert.True(body.GetProperty("reached").GetBoolean());
        }
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public async Task StartDoor_SessionWithNoHistoryRowYet_IsToldToTryAgain(string method, string path)
    {
        var (status, body) = await Call(method, path, "not-yet-known");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("session_not_yet_known", body.GetProperty("code").GetString());
        Assert.Equal(SecretaryStartLimit.NotYetKnown, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotAStartDoor_SecretaryReachesItsInboxAndItsReads_AndNoHistoryRowIsRead()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call("GET", $"/sessions/{Guid.NewGuid():D}/factory-inbox", "secretary")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call("POST", $"/sessions/{Guid.NewGuid():D}/factory-inbox/bundle", "secretary")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call("GET", "/sessions", "secretary")).Status);
        // An ordinary request costs no history read: only a start door asks who the caller is.
        Assert.Equal(0, _seatReads);
    }
}
