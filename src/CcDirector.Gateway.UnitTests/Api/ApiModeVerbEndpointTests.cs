using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Take the wheel and hand it back on an API-mode session (mission "API mode for agent wrappers", issue #4070, phase 2):
/// the Gateway's verb, its routing to the owning Director, and its refusals.
///
/// Driven through <c>GatewayEndpoints.RunApiModeVerbAsync</c>, the handler both routes map to in one line, over the
/// real self-host tenant boundary, a real Director registry and a real pushed-session store on an injected clock. A
/// fake command sender plays the Director and records every command it was sent, which is what makes "nothing was
/// sent" a measured fact. The last tests boot the real route map on an in-memory test server, to prove the two paths
/// are wired and that a disconnected Director is answered exactly as interrupt answers it.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class ApiModeVerbEndpointTests : IDisposable
{
    private const string DirectorId = "director-1";
    private const string Machine = "DEVLINUX";
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StreamStale = TimeSpan.FromSeconds(30);
    private static readonly TenantId OtherAccount = new("tenant-b");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-wheel-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new(() => Now);
    private readonly List<(string DirectorId, string Verb, string SessionId)> _sent = new();
    private Func<DirectorCommand, DirectorCommandResult?> _director = _ => DirectorCommandResult.Success(null);

    public ApiModeVerbEndpointTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234, startedAt: Now, tenant: TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { /* test cleanup */ }
    }

    // ---- success: the command reaches the owning Director ---------------------------------------------------------

    [Theory]
    [InlineData(WheelHolders.Protocol)]
    // No process holds it (the agent is starting, or between a hand back and the protocol resuming). The Gateway
    // cannot see a reason to refuse, so the Director - which holds the ownership lock - decides.
    [InlineData(WheelHolders.None)]
    public async Task RunApiModeVerbAsync_TakeOnApiSessionTheUserDoesNotHold_SendsWheelTakeToTheOwningDirector(string holder)
    {
        var sid = Seat(ConnectionModes.Api, holder);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.True(Body(result).GetProperty("accepted").GetBoolean());
        var sent = Assert.Single(_sent);
        Assert.Equal((DirectorId, "wheel-take", sid), sent);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_HandBackWhileTheUserHoldsTheWheel_SendsWheelHandBackToTheOwningDirector()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.User);

        var result = await MoveAsync(sid, ApiModeVerb.HandBackWheel);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.True(Body(result).GetProperty("accepted").GetBoolean());
        Assert.Equal((DirectorId, "wheel-hand-back", sid), Assert.Single(_sent));
    }

    [Fact]
    public void WheelCommands_AreTheNamesAgreedWithTheDirector()
    {
        // The Director side (phase 4) dispatches on these two strings. A rename here without the Director breaks
        // the verb silently, so the names are pinned.
        Assert.Equal("wheel-take", GatewayEndpoints.WheelTakeCommand);
        Assert.Equal("wheel-hand-back", GatewayEndpoints.WheelHandBackCommand);
    }

    // ---- the Gateway's own refusals: nothing is sent --------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunApiModeVerbAsync_TerminalModeSession_IsRefusedAsNotApiMode(bool take)
    {
        var sid = Seat(ConnectionModes.Terminal, holder: null);

        var result = await MoveAsync(sid, take ? ApiModeVerb.TakeWheel : ApiModeVerb.HandBackWheel);

        AssertRefused(result, StatusCodes.Status409Conflict, GatewayEndpoints.WheelRefusalCodes.NotApiMode, GatewayEndpoints.WheelNotApiModeReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunApiModeVerbAsync_SessionFromAnOlderDirectorWithNeitherField_IsTreatedAsTerminalMode(bool take)
    {
        // A Director that predates API mode pushes no ConnectionMode and no WheelHolder. Every session it runs is a
        // terminal session, and that is what the Gateway must answer - never a guess, never a send.
        var sid = Seat(mode: null, holder: null);
        Assert.True(_pushed.TryLocate(TenantId.Local, sid, StreamStale) is { } located
            && located.Session.ConnectionMode == ConnectionModes.Terminal && located.Session.WheelHolder == WheelHolders.None,
            "a row pushed without either field must read as terminal mode with nobody holding the wheel");

        var result = await MoveAsync(sid, take ? ApiModeVerb.TakeWheel : ApiModeVerb.HandBackWheel);

        AssertRefused(result, StatusCodes.Status409Conflict, GatewayEndpoints.WheelRefusalCodes.NotApiMode, GatewayEndpoints.WheelNotApiModeReason);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_TakeWhileTheUserAlreadyHoldsTheWheel_IsRefused()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.User);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        AssertRefused(result, StatusCodes.Status409Conflict, GatewayEndpoints.WheelRefusalCodes.AlreadyHeld, GatewayEndpoints.WheelAlreadyHeldReason);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_HandBackWhileTheProtocolHoldsTheWheel_IsRefused()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.HandBackWheel);

        AssertRefused(result, StatusCodes.Status409Conflict, GatewayEndpoints.WheelRefusalCodes.NotHeld, GatewayEndpoints.WheelNotHeldReason);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_HandBackWhileNobodyHoldsTheWheel_IsSentToTheDirectorToRule()
    {
        // Review finding 2. No process holds the conversation - for example the terminal the user took ended on its
        // own. The Gateway cannot say the session "is still running in API mode", and hand back may be exactly the
        // verb that resumes it, so the Director, which knows, rules - as it does for a take on none.
        var sid = Seat(ConnectionModes.Api, WheelHolders.None);

        var result = await MoveAsync(sid, ApiModeVerb.HandBackWheel);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, "wheel-hand-back", sid), Assert.Single(_sent));
    }

    [Theory]
    [InlineData("acp", WheelHolders.Protocol)]
    [InlineData(ConnectionModes.Api, "")]
    [InlineData(ConnectionModes.Api, "somebody")]
    public async Task RunApiModeVerbAsync_StateTheGatewayCannotRead_IsRefusedWithTheReasonAndNothingIsSent(string mode, string holder)
    {
        var sid = Seat(mode, holder);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        Assert.Equal(StatusCodes.Status502BadGateway, Status(result));
        var body = Body(result);
        Assert.Equal(GatewayEndpoints.WheelRefusalCodes.UnreadableState, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_AnotherAccountsSession_IsNotFoundAndNothingIsSent()
    {
        // The same session id, pushed by a Director of ANOTHER account. The caller (this Gateway's own account) must
        // not locate it, must not learn it exists, and must not reach that Director.
        _registry.RegisterFromStream("director-b", "THEIR-MACHINE", "them", "1.0", pid: 99, startedAt: Now, tenant: OtherAccount);
        _pushed.RegisterConnection(OtherAccount, "director-b", "conn-b");
        var sid = Guid.NewGuid().ToString();
        Assert.True(_pushed.ApplySnapshot(OtherAccount, "director-b", "conn-b", 1, new[] { Row(sid, "director-b", ConnectionModes.Api, WheelHolders.Protocol) }));

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal("session_not_found", Body(result).GetProperty("code").GetString());
        Assert.Empty(_sent);
    }

    // ---- the Director's answers -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunApiModeVerbAsync_DirectorRefusesHandBackMidTurn_PassesItsWordsAndStatusThroughVerbatim()
    {
        const string directorWords = "The terminal is in the middle of a turn. Hand back once Pi has finished answering.";
        _director = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, directorWords);
        var sid = Seat(ConnectionModes.Api, WheelHolders.User);

        var result = await MoveAsync(sid, ApiModeVerb.HandBackWheel);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal(directorWords, Body(result).GetProperty("error").GetString());
        Assert.Equal("wheel-hand-back", Assert.Single(_sent).Verb);
    }

    [Theory]
    [InlineData(DirectorCommandStatus.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(DirectorCommandStatus.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(DirectorCommandStatus.Locked, StatusCodes.Status423Locked)]
    [InlineData(DirectorCommandStatus.Error, StatusCodes.Status502BadGateway)]
    public async Task RunApiModeVerbAsync_DirectorRefusesTake_PassesItsWordsThroughWithItsStatus(DirectorCommandStatus directorStatus, int expected)
    {
        const string directorWords = "Pi's executable was not found on this machine, so its terminal could not be opened.";
        _director = _ => DirectorCommandResult.Fail(directorStatus, directorWords);
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        Assert.Equal(expected, Status(result));
        Assert.Equal(directorWords, Body(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunApiModeVerbAsync_DirectorNotConnected_IsA502ThatSaysSo()
    {
        _director = _ => null;
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel);

        Assert.Equal(StatusCodes.Status502BadGateway, Status(result));
        Assert.Equal("The Director is not connected right now, so the command was not delivered.", Body(result).GetProperty("error").GetString());
    }

    // ---- the one reader of the pushed state -----------------------------------------------------------------------

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("terminal", false)]
    [InlineData("Terminal", false)]
    [InlineData("api", true)]
    [InlineData("API", true)]
    public void SessionWheelStateRead_ConnectionMode_IsReadCaseInsensitivelyAndMissingIsTerminal(string? mode, bool isApi)
    {
        // A null here is an explicit JSON null, which the default cannot catch; it is still terminal mode.
        var state = SessionWheelState.Read(new SessionDto { ConnectionMode = mode!, WheelHolder = "User" });

        Assert.Null(state.Problem);
        Assert.Equal(isApi, state.IsApiMode);
        Assert.Equal(isApi ? WheelHolders.User : "", state.Holder);
    }

    // ---- the routes, on the real route map ------------------------------------------------------------------------

    [Theory]
    [InlineData("take", "wheel-take", WheelHolders.Protocol)]
    [InlineData("hand-back", "wheel-hand-back", WheelHolders.User)]
    public async Task WheelRoutes_OnTheRealRouteMap_ReachTheOwningDirectorWithTheirCommand(string path, string verb, string holder)
    {
        var sid = Seat(ConnectionModes.Api, holder);
        await using var host = await StartRouteMapAsync();

        var response = await host.Client.PostAsync($"sessions/{sid}/wheel/{path}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((DirectorId, verb, sid), Assert.Single(_sent));
    }

    [Fact]
    public async Task WheelRoutes_DirectorNotConnected_AnswerExactlyAsInterruptAnswers()
    {
        _director = _ => null;
        var sid = Seat(ConnectionModes.Api, WheelHolders.User);
        await using var host = await StartRouteMapAsync();

        var interrupt = await host.Client.PostAsync($"sessions/{sid}/interrupt", content: null);
        var handBack = await host.Client.PostAsync($"sessions/{sid}/wheel/hand-back", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, interrupt.StatusCode);
        Assert.Equal(interrupt.StatusCode, handBack.StatusCode);
        Assert.Equal(await interrupt.Content.ReadAsStringAsync(), await handBack.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WheelRoutes_OnTheRealRouteMap_RefuseATerminalSessionWithTheReasonInTheBody()
    {
        var sid = Seat(mode: null, holder: null);
        await using var host = await StartRouteMapAsync();

        var response = await host.Client.PostAsync($"sessions/{sid}/wheel/take", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GatewayEndpoints.WheelNotApiModeReason, body.GetProperty("error").GetString());
        Assert.Empty(_sent);
    }

    // ---- stop the turn -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WheelHolders.Protocol)]
    [InlineData(WheelHolders.None)]
    [InlineData(WheelHolders.User)]
    public async Task RunApiModeVerbAsync_StopOnAnApiSession_SendsTurnStopToTheOwningDirector(string holder)
    {
        // Stop does not depend on who holds the wheel: the Director rules what stopping means in each state.
        var sid = Seat(ConnectionModes.Api, holder);

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, "turn-stop", sid), Assert.Single(_sent));
    }

    [Theory]
    [InlineData(ConnectionModes.Terminal)]
    [InlineData(null)]
    public async Task RunApiModeVerbAsync_StopOnATerminalSession_IsRefusedPointingAtInterrupt(string? mode)
    {
        var sid = Seat(mode, holder: null);

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn);

        AssertRefused(result, StatusCodes.Status409Conflict, GatewayEndpoints.WheelRefusalCodes.NotApiMode, GatewayEndpoints.StopNotApiModeReason);
        Assert.Contains("interrupt", GatewayEndpoints.StopNotApiModeReason);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_StopWhenTheHolderCannotBeRead_IsStillSent()
    {
        // A holder the Gateway does not know must never keep a user from stopping a turn: Stop needs only the mode.
        var sid = Seat(ConnectionModes.Api, "somebody");

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal("turn-stop", Assert.Single(_sent).Verb);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_StopOnAnUnknownMode_IsRefusedAndNothingIsSent()
    {
        var sid = Seat("acp", WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn);

        Assert.Equal(StatusCodes.Status502BadGateway, Status(result));
        Assert.Equal(GatewayEndpoints.WheelRefusalCodes.UnreadableState, Body(result).GetProperty("code").GetString());
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_DirectorRefusesStop_PassesItsWordsAndStatusThroughVerbatim()
    {
        const string directorWords = "Nothing is running in this session, so there is no turn to stop.";
        _director = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, directorWords);
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal(directorWords, Body(result).GetProperty("error").GetString());
    }

    [Fact]
    public void TurnStopCommand_IsTheNameSentToTheDirector()
        => Assert.Equal("turn-stop", GatewayEndpoints.TurnStopCommand);

    // ---- a session key: only on a session it controls (the owner's ruling on review finding 1) -------------------

    [Theory]
    [InlineData(nameof(ApiModeVerb.TakeWheel), WheelHolders.Protocol, "wheel-take")]
    [InlineData(nameof(ApiModeVerb.HandBackWheel), WheelHolders.User, "wheel-hand-back")]
    [InlineData(nameof(ApiModeVerb.StopTurn), WheelHolders.Protocol, "turn-stop")]
    public async Task RunApiModeVerbAsync_SessionKeyOnASessionItControls_IsAllowed(string verb, string holder, string command)
    {
        var caller = Guid.NewGuid();
        var sid = Seat(ConnectionModes.Api, holder, controller: caller.ToString());

        var result = await MoveAsync(sid, VerbNamed(verb), AsSession(caller));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((DirectorId, command, sid), Assert.Single(_sent));
    }

    [Theory]
    [InlineData(nameof(ApiModeVerb.TakeWheel), WheelHolders.Protocol)]
    [InlineData(nameof(ApiModeVerb.HandBackWheel), WheelHolders.User)]
    [InlineData(nameof(ApiModeVerb.StopTurn), WheelHolders.Protocol)]
    public async Task RunApiModeVerbAsync_SessionKeyOnASessionAnotherSessionControls_IsRefusedAndNothingIsSent(string verb, string holder)
    {
        var sid = Seat(ConnectionModes.Api, holder, controller: Guid.NewGuid().ToString());

        var result = await MoveAsync(sid, VerbNamed(verb), AsSession(Guid.NewGuid()));

        AssertRefused(result, StatusCodes.Status403Forbidden, GatewayEndpoints.WheelRefusalCodes.NotYourSession, AgentInputRefusal.ApiModeNotYourSession);
    }

    [Theory]
    [InlineData(nameof(ApiModeVerb.TakeWheel))]
    [InlineData(nameof(ApiModeVerb.HandBackWheel))]
    [InlineData(nameof(ApiModeVerb.StopTurn))]
    public async Task RunApiModeVerbAsync_SessionKeyOnASessionTheOwnerRuns_IsRefusedAndNothingIsSent(string verb)
    {
        // No controller: the owner runs it directly. Not the caller's.
        var sid = Seat(ConnectionModes.Api, WheelHolders.User);

        var result = await MoveAsync(sid, VerbNamed(verb), AsSession(Guid.NewGuid()));

        AssertRefused(result, StatusCodes.Status403Forbidden, GatewayEndpoints.WheelRefusalCodes.NotYourSession, AgentInputRefusal.ApiModeNotYourSession);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_SessionKeyOnItself_IsRefused()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.StopTurn, AsSession(Guid.Parse(sid)));

        AssertRefused(result, StatusCodes.Status403Forbidden, GatewayEndpoints.WheelRefusalCodes.NotYourSession, AgentInputRefusal.ApiModeNotYourSession);
    }

    [Fact]
    public async Task RunApiModeVerbAsync_NotControlled_IsRefusedBeforeTheSessionsStateIsSaid()
    {
        // A terminal session the caller does not control: the answer is "not yours", never "terminal mode", so a caller
        // learns nothing about a session it does not control.
        var sid = Seat(ConnectionModes.Terminal, holder: null);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel, AsSession(Guid.NewGuid()));

        Assert.Equal(GatewayEndpoints.WheelRefusalCodes.NotYourSession, Body(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RunApiModeVerbAsync_RaisedSessionOnASessionItDoesNotControl_IsAllowedAsOnThePromptRoute()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);

        var result = await MoveAsync(sid, ApiModeVerb.TakeWheel, AsSession(Guid.NewGuid(), raised: true));

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal("wheel-take", Assert.Single(_sent).Verb);
    }

    [Fact]
    public async Task TurnStopRoute_OnTheRealRouteMap_ReachesTheOwningDirector()
    {
        var sid = Seat(ConnectionModes.Api, WheelHolders.Protocol);
        await using var host = await StartRouteMapAsync();

        var response = await host.Client.PostAsync($"sessions/{sid}/turn/stop", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((DirectorId, "turn-stop", sid), Assert.Single(_sent));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private string Seat(string? mode, string? holder, string? controller = null)
    {
        var sid = Guid.NewGuid().ToString();
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { Row(sid, DirectorId, mode, holder, controller) }));
        return sid;
    }

    /// <summary>A session row as the owning Director pushes it, through the same JSON a push carries. A null
    /// <paramref name="mode"/> or <paramref name="holder"/> leaves that field OUT of the JSON, which is exactly what a
    /// Director that predates API mode sends.</summary>
    private static SessionDto Row(string sid, string directorId, string? mode, string? holder, string? controller = null)
    {
        var json = new Dictionary<string, object>
        {
            ["sessionId"] = sid,
            ["directorId"] = directorId,
            ["agent"] = "Pi",
            ["repoPath"] = "/home/soren/ReposFred/devthrottle",
            ["status"] = "Running",
            ["activityState"] = "WaitingForInput",
            ["lastActivityAt"] = Now,
        };
        if (mode is not null) json["connectionMode"] = mode;
        if (holder is not null) json["wheelHolder"] = holder;
        if (controller is not null) json["controllerSessionId"] = controller;
        return JsonSerializer.Deserialize<SessionDto>(JsonSerializer.Serialize(json), WebJson)!;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        lock (_sent) _sent.Add((directorId, command.Verb, command.SessionId));
        return Task.FromResult(_director(command));
    }

    private static HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new DeviceRegistry(), hosted: false);

    private Task<IResult> MoveAsync(string sid, ApiModeVerb verb, HttpContext? ctx = null) =>
        GatewayEndpoints.RunApiModeVerbAsync(ctx ?? new DefaultHttpContext(), sid, verb, SelfHostBoundary(), _registry, _pushed,
            StreamStale, owners: null, sendCommand: Send);

    /// <summary>A request made with a SESSION key, as AuthMiddleware leaves it once it has verified the key: the calling
    /// session's identity, and the raised grant when the owner has raised that session.</summary>
    private static HttpContext AsSession(Guid caller, bool raised = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(caller, TenantId.Local, DirectorId);
        if (raised) ctx.Items[AuthMiddleware.RaisedGrantItemKey] = RaisedGrant.AgentInput;
        return ctx;
    }

    private static ApiModeVerb VerbNamed(string name) => Enum.Parse<ApiModeVerb>(name);

    /// <summary>The status the result writes. A JSON result built with no status code writes 200, which is what a null means.</summary>
    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static JsonElement Body(IResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private void AssertRefused(IResult result, int status, string code, string reason)
    {
        Assert.Equal(status, Status(result));
        var body = Body(result);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(reason, body.GetProperty("error").GetString());
        Assert.Empty(_sent);
    }

    private sealed class RouteMapHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private async Task<RouteMapHost> StartRouteMapAsync()
    {
        // An EMPTY builder: no configuration files, so no file watchers. A default builder watches its settings files,
        // and on a machine already running other suites that can run out of watchers and fail on correct code.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: SelfHostBoundary(), aiGate: TenantAiGate.SelfHost,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            pushedSessions: _pushed, streamStaleAfter: StreamStale, sendCommand: Send);
        await app.StartAsync();
        return new RouteMapHost(app, app.GetTestClient());
    }
}
