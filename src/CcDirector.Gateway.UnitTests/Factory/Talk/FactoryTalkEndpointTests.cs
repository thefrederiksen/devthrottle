using CcDirector.Gateway.Tests.Factory.Registry;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// THE TALK BUTTON'S GATEWAY SIDE (Factories screen mission, phase C), driven over HTTP the way the Cockpit calls it,
/// reading what LEAVES the Gateway: the create the Director is sent. The create goes through
/// <see cref="GatewayEndpoints.StartSessionOnDirectorAsync"/>, the person's New Session path, with the same door shape
/// the host builds - so what is proven here is that a talk is a person's top-level session: no controller, no parent,
/// the factory and the name set, in the factory's folder, on the seat's computer, and nowhere else.
///
/// The Director is a capture, not a real one. The device type is stamped by a stand-in for the auth middleware, as
/// <c>CockpitSpawnIntoAFactoryTests</c> does, because what is under test is the route, not the key registry.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactoryTalkEndpointTests : IAsyncDisposable
{
    private const string NorthDirector = "dir-north";
    private const string MorningRun = "You are Nora Hale, CEO of WarmForward. Do the morning run. When done, run cc-devthrottle session done.";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-factory-talk-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private WebApplication? _app;
    private HttpClient? _http;
    private DirectorRegistry? _directors;
    private FactoryRegistryStore? _registry;

    /// <summary>Every create the route dispatched to a Director, with the Director it went to.</summary>
    private readonly List<(string DirectorId, NewSessionRequest Request)> _sent = new();

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        _directors?.Dispose();
        _h.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static RegisterFactoryRequest WarmForward() => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        Computer = "SOREN_NORTH",
        CeoSeat = "nora-hale",
        GoalText = "A cash engine that runs without your time.",
        GoalFile = "GOAL.md",
        GoalApprovedOn = "2026-10-04",
        Seats =
        {
            new FactorySeatManifest { Id = "nora-hale", Name = "Nora Hale", Role = "CEO", BriefFile = "agents/ceo.yaml", Schedules = { "cj_a721e6" } },
            new FactorySeatManifest { Id = "savings-engineer", Name = "Savings Engineer", Role = "Savings Engineer", BriefFile = "agents/savings.yaml", Computer = "DEVLINUX" },
        },
    };

    /// <summary>Who is calling, as the auth middleware stamps it.</summary>
    public enum Caller { OwnerBrowser, OwnerPhone, DirectorDeviceKey, MachineToken }

    private async Task StartAsync(bool switchOn = true, bool northStopped = false, Caller caller = Caller.OwnerBrowser,
        string northVersion = "2.16.0")
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        // The credential the auth middleware would have verified, stamped the way it stamps it.
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
            var deviceType = caller switch
            {
                Caller.OwnerBrowser => "browser",
                Caller.OwnerPhone => "phone",
                Caller.DirectorDeviceKey => "workstation",
                _ => null,
            };
            if (deviceType is not null)
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", TenantId.Local.Value, deviceType, "active");
            }
            await next();
        });

        _directors = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _directors.RegisterFromStream(NorthDirector, "SOREN_NORTH", "test", northVersion, pid: 1,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        if (northStopped) Assert.True(_directors.MarkStopped(TenantId.Local, NorthDirector));
        _directors.RegisterFromStream("dir-elsewhere", "OTHER-PC", "test", "0.0.0-test", pid: 2,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);

        _registry = new FactoryRegistryStore(Db);
        // The CEO is run by cj_a721e6, linked first: a seat's schedules are the ones that point at it (issue #3650).
        ScheduleLinkSeed.Link(Db, TenantId.Local, "warmforward", "nora-hale", "cj_a721e6");
        _registry.Register(TenantId.Local, WarmForward(), "the owner (test)", DateTime.UtcNow);

        var schedules = new Dictionary<string, CronJobDto>
        {
            ["cj_a721e6"] = new() { Id = "cj_a721e6", Name = "WarmForward Factory - Nora Hale - morning run", Action = new CronJobAction { Seed = MorningRun } },
        };

        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            if (command.Verb == "create")
            {
                _sent.Add((directorId, JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web)!));
                var reply = new SessionDto { SessionId = "7d2c0e4e-0000-4000-8000-00000000c0de" };
                return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(reply, Web)));
            }
            return Task.FromResult<DirectorCommandResult?>(null);
        };

        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry());
        var door = new DirectorSpawnDoor(boundary, _directors, _ => SessionFactoryLookup.InNoFactory, send,
            Missions: null, WorkflowRuns: null, Workspaces: null);

        var factorySwitch = new FactoryAgentsSwitch(switchOn, new TenantSettingsStore(Db));
        var directors = _directors;
        FactoryTalkEndpoints.Map(app, factorySwitch,
            resolveTenant: ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary),
            registry: _registry,
            listDirectors: tenant => directors.ListDirectors(tenant),
            schedule: (_, id) => schedules.TryGetValue(id, out var job) ? job : null,
            startOnDirector: (ctx, directorId, request) => GatewayEndpoints.StartSessionOnDirectorAsync(ctx, directorId, request, door));

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
    }

    private Task<HttpResponseMessage> Talk(string factory, string seat) =>
        _http!.PostAsync($"gateway/factory-agents/factories/{factory}/seats/{seat}/talk", content: null);

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    [Fact]
    public async Task Talk_ToTheCeo_StartsATopLevelSessionOwnedByThePerson_WithNoControllerAndNoParent()
    {
        await StartAsync();

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        Assert.Null(sent.ControllerSessionId);
        Assert.Null(sent.ParentSessionId);
        Assert.Equal(SessionOriginKinds.Human, sent.Origin);
    }

    [Fact]
    public async Task Talk_TheCreate_CarriesTheFactoryTheNameTheFolderAndTheSeed()
    {
        await StartAsync();

        await Talk("warmforward", "nora-hale");

        var (_, sent) = Assert.Single(_sent);
        Assert.Equal("warmforward", sent.Factory);
        Assert.Equal("WarmForward - Nora Hale - talk with the owner", sent.Name);
        Assert.Equal(@"D:\ReposFred\cc-consult\ideas\warmforward-factory", sent.RepoPath);
        Assert.Equal("ClaudeCode", sent.Agent);
        Assert.NotNull(sent.PrePrompt);
        Assert.StartsWith("You are Nora Hale, CEO of WarmForward", sent.PrePrompt);
        Assert.Contains(MorningRun, sent.PrePrompt);
        Assert.Contains("--outcome talked", sent.PrePrompt);
    }

    [Fact]
    public async Task Talk_Answers_TheNewSessionAndTheCockpitHrefThatOpensIt()
    {
        await StartAsync();

        var response = await Talk("warmforward", "nora-hale");

        var talk = await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web);
        Assert.NotNull(talk);
        Assert.Equal("7d2c0e4e-0000-4000-8000-00000000c0de", talk!.SessionId);
        Assert.Equal("/session/7d2c0e4e-0000-4000-8000-00000000c0de", talk.Href);
        Assert.Equal("WarmForward - Nora Hale - talk with the owner", talk.SessionName);
        Assert.Equal("warmforward", talk.Factory);
        Assert.Equal("nora-hale", talk.Seat);
        Assert.Equal("SOREN_NORTH", talk.Computer);
        Assert.Equal(NorthDirector, talk.DirectorId);
    }

    // ---------- a Director too old to carry a factory (live QA, 6 Oct 2026) ----------

    [Theory]
    [InlineData("2.12.0")]
    [InlineData("2.12.9+68fd9d75b")]
    [InlineData("0.0.0-test")]
    [InlineData("")]
    public async Task Talk_ToADirectorOlderThanTheFactoryField_Is409WithASentence_AndNothingIsStarted(string version)
    {
        // Live, the talk reached a v2.12.0 Director, which has no factory field: it dropped the factory and started
        // the session in no factory, and the agent could not read or write the factory's memory. Refused instead.
        await StartAsync(northVersion: version);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Contains("The Director on SOREN_NORTH is ", error);
        if (version.Length > 0) Assert.Contains($"version {version}", error);
        Assert.Contains("no session was started in 'warmforward'", error);
        Assert.Contains("Update this Director to 2.13.0 or later", error);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("2.13.0")]
    [InlineData("v2.16.0")]
    [InlineData("3.0.0-rc1")]
    public async Task Talk_ToADirectorThatCarriesAFactory_IsSentTheFactory(string version)
    {
        await StartAsync(northVersion: version);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("warmforward", Assert.Single(_sent).Request.Factory);
    }

    [Fact]
    public async Task Talk_AnUnknownFactory_Is404WithASentence_AndNothingIsStarted()
    {
        await StartAsync();

        var response = await Talk("no-such-factory", "nora-hale");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No factory 'no-such-factory' is registered", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_AnUnknownSeat_Is404NamingTheSeatsThereAre_AndNothingIsStarted()
    {
        await StartAsync();

        var response = await Talk("warmforward", "owner-session");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Contains("WarmForward has no seat 'owner-session'", error);
        Assert.Contains("nora-hale, savings-engineer", error);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_NoDirectorOnTheSeatsComputer_Is409_AndNeverStartsItOnAnotherComputer()
    {
        await StartAsync();

        // The Savings Engineer runs on DEVLINUX, where no Director is running; OTHER-PC has one.
        var response = await Talk("warmforward", "savings-engineer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No Director is running on DEVLINUX", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_TheDirectorOnTheComputerHasStopped_Is409_AndNothingIsStarted()
    {
        await StartAsync(northStopped: true);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No Director is running on SOREN_NORTH", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_TheFactorySwitchOff_Is404WithASentence_AndNothingIsStarted()
    {
        await StartAsync(switchOn: false);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Factory agents are switched off for this account, so no talk was started.", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_FromTheOwnersPhone_StartsTheSession()
    {
        await StartAsync(caller: Caller.OwnerPhone);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(_sent);
    }

    [Theory]
    [InlineData(Caller.DirectorDeviceKey, "device (workstation)")]
    [InlineData(Caller.MachineToken, "machine")]
    public async Task Talk_ACredentialThatIsNotTheOwnersBrowserOrPhone_Is403_AndNothingIsStarted(Caller caller, string named)
    {
        await StartAsync(caller: caller);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Contains("only the owner on their own signed-in phone or browser may start a talk with a factory agent", error);
        Assert.Contains(named, error);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_ANonOwnerCredentialWithTheSwitchOff_LearnsNothingAboutTheSwitch()
    {
        await StartAsync(switchOn: false, caller: Caller.DirectorDeviceKey);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("switched off", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public void Talk_ASessionsOwnKey_NeverReachesTheRoute()
        => Assert.False(SessionKeyGuard.Check("POST", "/gateway/factory-agents/factories/warmforward/seats/nora-hale/talk").Allowed,
            "a talk is the owner's own session; a session must not be able to start one");
}
