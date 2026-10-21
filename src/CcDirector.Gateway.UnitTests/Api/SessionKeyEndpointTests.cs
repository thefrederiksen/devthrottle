using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Enter and Escape pressed in a session (the owner's ruling, 21 October 2026): "the fleet manager can do it for any
/// session, and other agents can press escape and [enter] in the session they started, or the sessions they own."
///
/// Driven through <c>GatewayEndpoints.PressSessionKeyAsync</c>, the handler both routes map to in one line, over the
/// real self-host tenant boundary, a real Director registry and a real pushed-session store. A fake command sender plays
/// the Director and records every command it was sent, which is what makes "nothing was sent" a measured fact.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class SessionKeyEndpointTests : IDisposable
{
    private const string DirectorId = "director-1";
    private const string Machine = "DEVLINUX";
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StreamStale = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-keys-" + Guid.NewGuid().ToString("N"));
    private readonly CcDirector.Gateway.Tests.Data.GatewayDbTestHarness _harness = new();
    private HostedTenantBoundary? _boundary;
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new(() => Now);
    private readonly List<(string DirectorId, string Verb, string SessionId)> _sent = new();
    private Func<DirectorCommand, DirectorCommandResult?> _director = _ => DirectorCommandResult.Success(null);

    public SessionKeyEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234, startedAt: Now, tenant: TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { /* test cleanup */ }
    }

    [Fact]
    public void KeyCommands_AreTheNamesTheDirectorDispatchesOn()
    {
        Assert.Equal("enter", GatewayEndpoints.EnterCommand);
        Assert.Equal("escape", GatewayEndpoints.EscapeCommand);
    }

    [Theory]
    [InlineData(GatewayEndpoints.EnterCommand)]
    [InlineData(GatewayEndpoints.EscapeCommand)]
    public async Task PressSessionKeyAsync_OwnersDevice_PressesInAnySession(string key)
    {
        var sid = Seat(controller: null);

        var result = await PressAsync(sid, key, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, key, sid), Assert.Single(_sent));
    }

    [Theory]
    [InlineData(GatewayEndpoints.EnterCommand)]
    [InlineData(GatewayEndpoints.EscapeCommand)]
    public async Task PressSessionKeyAsync_SessionKeyOnASessionItOwns_Presses(string key)
    {
        var caller = Guid.NewGuid();
        var sid = Seat(controller: caller.ToString());

        var result = await PressAsync(sid, key, AsSession(caller));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, key, sid), Assert.Single(_sent));
    }

    [Theory]
    [InlineData(GatewayEndpoints.EnterCommand)]
    [InlineData(GatewayEndpoints.EscapeCommand)]
    public async Task PressSessionKeyAsync_SessionKeyOnASessionAnotherSessionOwns_IsRefusedAndNothingIsSent(string key)
    {
        var sid = Seat(controller: Guid.NewGuid().ToString());

        var result = await PressAsync(sid, key, AsSession(Guid.NewGuid()));

        AssertRefused(result);
    }

    [Theory]
    [InlineData(GatewayEndpoints.EnterCommand)]
    [InlineData(GatewayEndpoints.EscapeCommand)]
    public async Task PressSessionKeyAsync_SessionKeyOnASessionTheOwnerRuns_IsRefusedAndNothingIsSent(string key)
    {
        var sid = Seat(controller: null);

        var result = await PressAsync(sid, key, AsSession(Guid.NewGuid()));

        AssertRefused(result);
    }

    [Fact]
    public async Task PressSessionKeyAsync_SessionKeyOnItself_IsRefused()
    {
        var sid = Seat(controller: null);

        var result = await PressAsync(sid, GatewayEndpoints.EscapeCommand, AsSession(Guid.Parse(sid)));

        AssertRefused(result);
    }

    [Theory]
    [InlineData(GatewayEndpoints.EnterCommand)]
    [InlineData(GatewayEndpoints.EscapeCommand)]
    public async Task PressSessionKeyAsync_RaisedSession_PressesInASessionItDoesNotOwn(string key)
    {
        // The Fleet Manager: a raised session presses the key in any session of the account.
        var sid = Seat(controller: Guid.NewGuid().ToString());

        var result = await PressAsync(sid, key, AsSession(Guid.NewGuid(), raised: true));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, key, sid), Assert.Single(_sent));
    }

    [Fact]
    public async Task PressSessionKeyAsync_DirectorRefusesEnterOnTheUsageLimitDialog_AnswersConflictInTheDirectorsWords()
    {
        var caller = Guid.NewGuid();
        var sid = Seat(controller: caller.ToString());
        _director = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "Nothing was pressed: the usage-limit dialog");

        var result = await PressAsync(sid, GatewayEndpoints.EnterCommand, AsSession(caller));

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("Nothing was pressed: the usage-limit dialog", Body(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task PressSessionKeyAsync_DirectorNotConnected_AnswersBadGateway()
    {
        var sid = Seat(controller: null);
        _director = _ => null;

        var result = await PressAsync(sid, GatewayEndpoints.EnterCommand, new DefaultHttpContext());

        Assert.Equal(StatusCodes.Status502BadGateway, Status(result));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private string Seat(string? controller)
    {
        var sid = Guid.NewGuid().ToString();
        var json = new Dictionary<string, object>
        {
            ["sessionId"] = sid,
            ["directorId"] = DirectorId,
            ["agent"] = "ClaudeCode",
            ["repoPath"] = "D:/ReposFred/devthrottle",
            ["status"] = "Running",
            ["activityState"] = "WaitingForInput",
            ["lastActivityAt"] = Now,
        };
        if (controller is not null) json["controllerSessionId"] = controller;
        var row = JsonSerializer.Deserialize<SessionDto>(JsonSerializer.Serialize(json), WebJson)!;
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { row }));
        return sid;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        lock (_sent) _sent.Add((directorId, command.Verb, command.SessionId));
        return Task.FromResult(_director(command));
    }

    private HostedTenantBoundary SelfHostBoundary() =>
        _boundary ??= new(new SingleTenantContext(), new DeviceRegistry(_harness.Open(), _harness.LegacyPath("devices.json")), hosted: false);

    private Task<IResult> PressAsync(string sid, string key, HttpContext ctx) =>
        GatewayEndpoints.PressSessionKeyAsync(ctx, sid, key, SelfHostBoundary(), _registry, _pushed, StreamStale,
            owners: null, sendCommand: Send);

    private static HttpContext AsSession(Guid caller, bool raised = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(caller, TenantId.Local, DirectorId);
        if (raised) ctx.Items[AuthMiddleware.RaisedGrantItemKey] = RaisedGrant.AgentInput;
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static JsonElement Body(IResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private void AssertRefused(IResult result)
    {
        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        var body = Body(result);
        Assert.Equal(GatewayEndpoints.WheelRefusalCodes.NotYourSession, body.GetProperty("code").GetString());
        Assert.Equal(AgentInputRefusal.KeysNotYourSession, body.GetProperty("error").GetString());
        Assert.Empty(_sent);
    }
}
