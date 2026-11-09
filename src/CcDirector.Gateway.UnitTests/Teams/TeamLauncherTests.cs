using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A TEAM DIRECTOR'S OWN LAUNCHER (devthrottle_internal#2829). Over a real database, a hosted device registry with Teams
/// released, a real Director registry and the real team gate: Alice and Bob (Developers) each set a Director up in the
/// team on their own computer, the way the hosted enrollment writes the row (<see cref="HostedEnrollmentEndpoint"/>);
/// Carol is a Contributor. Before the fix the gate answered Unknown for every launcher route in a team, so a launcher
/// holding its Director's team key was refused on <c>POST /launchers/register</c> and on <c>/launcher-stream</c> with
/// 403 team_action_refused, and the Director could never be restarted.
/// </summary>
public sealed class TeamLauncherTests : IAsyncLifetime
{
    private const string Owner = "sub-launcher-owner";
    private const string Alice = "sub-launcher-alice";
    private const string Bob = "sub-launcher-bob";
    private const string Carol = "sub-launcher-carol";
    private const string AlicePc = "ALICE-PC";
    private const string BobPc = "BOB-PC";

    /// <summary>When the Directors said Hello: a fixed time, because it is stored, not judged.</summary>
    private static readonly DateTime HelloAt = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly HostedTenantBoundary _boundary;
    private readonly TeamCallerOwnership _ownership;
    private readonly TeamEndpointGate _gate;
    private readonly TeamLauncherCheck _check;
    private readonly LauncherRegistry _launchers = new();
    private readonly LauncherConnectionRegistry _connections = new();
    private readonly TenantId _teamA;
    private readonly TenantId _teamB;
    private readonly string _aliceKey;
    private readonly string _bobKey;
    private readonly string _aliceTeamBKey;
    private readonly string _alicePersonalKey;
    private readonly TenantId _alicePersonal;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _connectionCount;

    public TeamLauncherTests()
    {
        var db = _harness.Open();
        _tenants = new TenantRegistry(db);
        _teams = new TeamRegistry(db, _tenants);
        _devices = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false);
        _ownership = new TeamCallerOwnership(_directors, new PushedSessionStore(), _devices, new SessionTurnStore(db), _boundary,
            new SessionKeyRegistry(db, isHosted: true));
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants, _boundary, _ownership);
        _check = new TeamLauncherCheck(_teams.IsTeam, _ownership);

        _teamA = new TenantId(_teams.CreateTeam(Owner, "A").Team!.TeamId);
        _teamB = new TenantId(_teams.CreateTeam(Owner, "B").Team!.TeamId);
        Assert.True(_teams.AddMember(_teamA.Value, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_teamA.Value, Bob, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_teamA.Value, Carol, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_teamB.Value, Alice, TeamRole.Developer).IsDone);

        _aliceKey = TeamDirector(_teamA, Alice, "director-alice", AlicePc);
        _bobKey = TeamDirector(_teamA, Bob, "director-bob", BobPc);
        TeamDirector(_teamA, Carol, "director-carol", "CAROL-PC");
        _aliceTeamBKey = TeamDirector(_teamB, Alice, "director-alice-b", AlicePc);

        // Alice's own account, with a Director on the same computer: the personal launcher is unchanged.
        _alicePersonal = _tenants.MintOrLookupBySubject(Alice, null);
        _alicePersonalKey = _devices.RegisterForTenant(_alicePersonal, Alice, "personal|director-alice-own", AlicePc).DeviceKey;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        _app = builder.Build();
        _app.UseRouting();
        // What the auth middleware leaves on a request made with a device key: the authenticated identity.
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Device-Key", out var key))
            {
                var resolution = _devices.ResolveCredential(key.ToString());
                Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = resolution.Identity;
            }
            await next();
        });
        _app.Use((ctx, next) => _gate.RunAsync(ctx, () => next()));
        MachineEndpoints.Map(_app, false, _launchers,
            new MachineSessionSpawner(new NoDirectors(),
                (DirectorCommandRouter.SendDirectorCommandAsync)((_, _, _) => Task.FromResult<DirectorCommandResult?>(null))),
            boundary: _boundary,
            teamLaunchers: _check,
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown);
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _devices.Dispose();
        _harness.Dispose();
    }

    /// <summary>A member sets a Director up in a team on their own computer: the row the hosted enrollment writes, and
    /// the Director's Hello on its key.</summary>
    private string TeamDirector(TenantId team, string subject, string directorId, string machine)
    {
        var deviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(team.Value, subject, directorId);
        var key = _devices.RegisterForTenant(team, subject, deviceId, machine).DeviceKey;
        _directors.RegisterFromStream(directorId, machine, "u", "1.0", 1, HelloAt, team, directorId, "device:" + deviceId);
        return key;
    }

    private DeviceCredentialIdentity Identity(string key) => _devices.ResolveCredential(key).Identity!;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string key, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Device-Key", key);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> RegisterAsync(string key, string machine) =>
        SendAsync(HttpMethod.Post, "/launchers/register", key, new LauncherRegistrationRequest { MachineName = machine, Pid = 7, Version = "1.0" });

    private static void AssertTeamRefusal((HttpStatusCode Status, JsonElement Body) answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(TeamEndpointGate.RefusalCode, answer.Body.GetProperty("code").GetString());
    }

    // ---- the team launcher registers ---------------------------------------------------------------------------------

    // Shows: the launcher holding Alice's team Director's key registers for the computer that Director was set up on,
    // into the team's tenant, and its heartbeat and goodbye are accepted. This is the request that was refused with
    // 403 team_action_refused before the fix.
    [Fact]
    public async Task Register_TheTeamDirectorsOwnLauncher_ForItsOwnComputer_IsRegisteredInTheTeam()
    {
        var registered = await RegisterAsync(_aliceKey, AlicePc);

        Assert.Equal(HttpStatusCode.Created, registered.Status);
        Assert.NotNull(_launchers.Get(_teamA, AlicePc));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/launchers/{AlicePc}/heartbeat", _aliceKey)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/launchers/{AlicePc}", _aliceKey)).Status);
        Assert.Null(_launchers.Get(_teamA, AlicePc));
    }

    // Shows: the computer name is matched as the rest of the Gateway matches it - ignoring letter case and spaces.
    [Fact]
    public async Task Register_TheOwnComputerWrittenInAnotherCase_IsRegistered()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(_aliceKey, " alice-pc ")).Status);
    }

    // ---- refused -----------------------------------------------------------------------------------------------------

    // Shows: Alice's launcher cannot register for Bob's computer - a launcher row is one per computer in a team, so
    // it would be the one asked to restart Bob's Director - nor for a computer no Director of hers was set up on.
    [Theory]
    [InlineData(BobPc)]
    [InlineData("NOBODYS-PC")]
    public async Task Register_AnotherMembersComputerOrAnUnknownComputer_IsRefusedAndNothingIsRegistered(string machine)
    {
        AssertTeamRefusal(await RegisterAsync(_aliceKey, machine));
        Assert.Null(_launchers.Get(_teamA, machine));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, $"/launchers/{machine}/heartbeat", _aliceKey));
    }

    // Shows: Alice's launcher cannot say goodbye for Bob's launcher either, so it cannot unregister it.
    [Fact]
    public async Task Delete_AnotherMembersLauncher_IsRefusedAndTheirLauncherStays()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(_bobKey, BobPc)).Status);

        AssertTeamRefusal(await SendAsync(HttpMethod.Delete, $"/launchers/{BobPc}", _aliceKey));
        Assert.NotNull(_launchers.Get(_teamA, BobPc));
    }

    // Shows: a Contributor runs no sessions on their own computers, so the gate refuses their launcher's register and
    // stream before the endpoint is reached, even for the computer a key of theirs was set up on.
    [Theory]
    [InlineData("POST", "/launchers/register")]
    [InlineData("GET", "/launcher-stream")]
    public void Check_AContributorsLauncher_IsRefused(string method, string pattern)
    {
        var verdict = _gate.Check(method, pattern, _ => null, _teamA, () => Carol,
            _ => _ownership.Whose(_teamA, Carol, pattern, _ => null, method));

        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamGateOutcome.Allowed, _gate.Check(method, pattern, _ => null, _teamA, () => Alice,
            _ => _ownership.Whose(_teamA, Alice, pattern, _ => null, method)).Outcome);
    }

    // Shows: the list of the whole team's launchers is not one launcher's own, so it stays refused.
    [Fact]
    public async Task List_TheWholeTeamsLaunchers_StaysRefused()
    {
        AssertTeamRefusal(await SendAsync(HttpMethod.Get, "/launchers", _aliceKey));
    }

    // Shows: when a colleague's Director also reports the computer, the computer is not the caller's alone, and the
    // launcher is refused - even though the caller's key was set up on that computer name.
    [Fact]
    public async Task Register_AComputerAColleaguesDirectorAlsoReports_IsRefused()
    {
        _directors.RegisterFromStream("director-bob-2", AlicePc, "u", "1.0", 1, HelloAt, _teamA, "director-bob-2",
            "device:" + HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA.Value, Bob, "director-bob"));

        AssertTeamRefusal(await RegisterAsync(_aliceKey, AlicePc));
    }

    // Shows: the ownership rule refuses a session key (a session's, never a launcher's) and a key bound to another
    // team, for the very computer Alice's own team Director is on.
    [Fact]
    public void LauncherMachineRefusal_ASessionKeyOrAKeyBoundToAnotherTeam_IsRefused()
    {
        var session = new SessionCredentialIdentity(Guid.NewGuid(), _teamA, "director-alice");
        Assert.Equal(TeamLauncherCheck.Refusal, _check.Refuse(_teamA, null, session, AlicePc, "test"));
        Assert.Equal(TeamLauncherCheck.Refusal, _check.Refuse(_teamA, Identity(_aliceKey), session, AlicePc, "test"));
        Assert.Equal(TeamLauncherCheck.Refusal, _check.Refuse(_teamA, Identity(_aliceTeamBKey), null, AlicePc, "test"));
        Assert.Equal(TeamLauncherCheck.Refusal, _check.Refuse(_teamA, null, null, AlicePc, "test"));
        Assert.Equal(TeamLauncherCheck.Refusal, _check.Refuse(_teamA, Identity(_aliceKey), null, "  ", "test"));

        Assert.Null(_check.Refuse(_teamA, Identity(_aliceKey), null, AlicePc, "test"));
        // And in team B, Alice's team B key is hers on the same computer.
        Assert.Null(_check.Refuse(_teamB, Identity(_aliceTeamBKey), null, AlicePc, "test"));
    }

    // ---- personal accounts unchanged ---------------------------------------------------------------------------------

    // Shows: a person's own account is not a team, so the check never refuses there - a personal launcher registers
    // for any computer name, exactly as before - and the host with no teams answers the same.
    [Fact]
    public async Task Register_APersonalLauncher_IsUnchanged()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(_alicePersonalKey, "ANY-PC")).Status);
        Assert.NotNull(_launchers.Get(_alicePersonal, "ANY-PC"));
        Assert.Null(_check.Refuse(_alicePersonal, Identity(_alicePersonalKey), null, "ANY-PC", "test"));
        Assert.Null(TeamLauncherCheck.PersonalAccountsOnly.Refuse(_teamA, null, null, BobPc, "test"));
    }

    // ---- the gate ----------------------------------------------------------------------------------------------------

    // Shows: the gate answers the launcher's own routes as the caller's, for the endpoint to check the machine, and
    // leaves every other launcher route - the team list - Unknown.
    [Theory]
    [InlineData("POST", "/launchers/register", true)]
    [InlineData("POST", "/launchers/{machine}/heartbeat", true)]
    [InlineData("DELETE", "/launchers/{machine}", true)]
    [InlineData("GET", "/launcher-stream", true)]
    [InlineData("POST", "/launcher-stream/negotiate", true)]
    [InlineData("GET", "/launchers", false)]
    [InlineData("GET", "/launchers/register", false)]
    public void Whose_ALaunchersOwnRoute_IsTheCallersForTheEndpointToCheck(string method, string pattern, bool callers)
    {
        var whose = _ownership.Whose(_teamA, Alice, pattern, _ => AlicePc, method);
        Assert.Equal(callers ? TeamOwnership.Callers : TeamOwnership.Unknown, whose);
    }

    // ---- the stream --------------------------------------------------------------------------------------------------

    // Shows: the team launcher's stream Hello binds for its own computer in the team's tenant - the command stream a
    // restart travels on.
    [Fact]
    public void Hello_TheTeamDirectorsOwnLauncher_ForItsOwnComputer_HoldsTheStream()
    {
        var ctx = StreamHello(_aliceKey, AlicePc);

        Assert.False(ctx.Aborted);
        Assert.NotNull(_connections.GetActiveConnection(_teamA, AlicePc));
    }

    // Shows: a launcher stream Hello for another member's computer, or for an unknown one, is aborted before anything
    // is bound, and a personal launcher's stream is unchanged.
    [Theory]
    [InlineData(BobPc)]
    [InlineData("NOBODYS-PC")]
    public void Hello_AnotherMembersComputerOrAnUnknownComputer_IsAbortedAndBindsNothing(string machine)
    {
        var ctx = StreamHello(_aliceKey, machine);

        Assert.True(ctx.Aborted);
        Assert.Null(_connections.GetActiveConnection(_teamA, machine));
    }

    [Fact]
    public void Hello_APersonalLauncher_IsUnchanged()
    {
        var ctx = StreamHello(_alicePersonalKey, "ANY-PC");

        Assert.False(ctx.Aborted);
        Assert.NotNull(_connections.GetActiveConnection(_alicePersonal, "ANY-PC"));
    }

    // Shows: what Smart Restart asks before it closes anything now says yes for a team Director: once its own launcher
    // has registered and holds a stream declaring the guarded restart, the restart check for its computer, in the
    // team's tenant, answers that it can be restarted - the answer that was "no launcher is registered" before.
    [Fact]
    public async Task RestartCapability_ATeamDirectorWhoseLauncherRegisteredAndStreams_CanBeRestarted()
    {
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(_aliceKey, AlicePc)).Status);
        var ctx = StreamHello(_aliceKey, AlicePc, new LauncherCapabilityDeclaration
        {
            Commands = new List<string>
            {
                LauncherCapabilities.DirectorStart, LauncherCapabilities.DirectorStop,
                LauncherCapabilities.DirectorRestart, LauncherCapabilities.DirectorRestartOnlyIfEmpty,
            },
            RestartSignalArmed = true,
            ServingRootIsInstanceHome = false,
            ServingRootKey = "b1706c7af60c",
        });
        Assert.False(ctx.Aborted);

        var answer = MachineRestartCapability.Judge(AlicePc, _launchers.Get(_teamA, AlicePc),
            _connections.GetActiveConnection(_teamA, AlicePc), _launchers.UtcNow);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Null(DirectorRestartGate.Refusal(answer));
    }

    private FakeHubCtx StreamHello(string key, string machine, LauncherCapabilityDeclaration? capability = null)
    {
        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.DeviceKeyItemKey] = key;
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = Identity(key);
        var ctx = new FakeHubCtx("conn-launcher-" + Interlocked.Increment(ref _connectionCount), http);
        var hub = new LauncherHub(_connections, _boundary, _check) { Context = ctx };
        hub.Hello(new LauncherStreamHello { MachineName = machine, Version = "1.0", Capability = capability });
        return ctx;
    }

    private sealed class NoDirectors : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct) =>
            throw new InvalidOperationException("no Director is resolved by these tests");
    }

    private sealed class FakeHubCtx : HubCallerContext
    {
        public FakeHubCtx(string connectionId, HttpContext http)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeatureImpl { HttpContext = http });
        }

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
