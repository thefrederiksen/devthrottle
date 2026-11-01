using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Registry;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A BOSS STARTS A SEAT OF ITS OWN FACTORY, driven over HTTP and read at what LEAVES the Gateway: the create the Director
/// is sent. The caller's seat is read from a stand-in for its history row, so each test states who is asking.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactorySeatStartByTheBossTests : IAsyncDisposable
{
    private const string North = "dir-north";
    private const string Elsewhere = "dir-elsewhere";
    private const string BossSession = "71000000-0000-4000-8000-000000000009";
    private const string StartedSession = "7d2c0e4e-0000-4000-8000-00000000c0de";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime At = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SessionFactoryLookup AsTheBoss => SessionFactoryLookup.In("warmforward", "boss", 1) with { Kit = FactoryKits.Seat };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-seat-start-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayDbTestHarness _h = new();
    private readonly List<(string DirectorId, NewSessionRequest Request)> _sent = new();
    private readonly List<(AppendFactoryActivityRequest Row, string Actor)> _rows = new();
    private readonly CcDirector.Gateway.Streaming.FactoryKitCapabilityRegistry _kit = new();
    private GatewayDatabase? _db;
    private WebApplication? _app;
    private HttpClient? _http;
    private DirectorRegistry? _directors;
    private FactoryRegistryStore? _registry;

    private GatewayDatabase Db => _db ??= _h.Open();

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
        Folder = @"D:\factories\warmforward",
        Computer = "SOREN_NORTH",
        BossSeat = "boss",
        GoalText = "A cash engine.",
        GoalFile = "GOAL.md",
        GoalApprovedOn = "2026-10-04",
        Seats =
        {
            new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
            new FactorySeatManifest { Id = "ceo", Name = "Chief", Role = "Boss", BriefFile = "agents/ceo.yaml" },
            new FactorySeatManifest { Id = "scout", Name = "Scout", Role = "Finder", BriefFile = "agents/scout.yaml" },
            new FactorySeatManifest { Id = "far", Name = "Far Desk", Role = "Sender", BriefFile = "agents/far.yaml", Computer = "OTHER-PC" },
        },
    };

    private void Publish()
    {
        var store = new FactoryDefinitionStore(Db);
        var content = new Dictionary<string, string>
        {
            ["factory.yaml"] = "factory: warmforward\ntitle: WarmForward\ngoalText: A cash engine.\nbossSeat: boss\n"
                               + "defaults:\n  machine: SOREN_NORTH\n  agent: ClaudeCode\n  model: claude-opus-5-5\n",
            ["seats/boss.yaml"] = "seat: boss\nname: Boss\nrole: Boss\nbrief: Run it.\n",
            ["seats/ceo.yaml"] = "seat: ceo\nname: Chief\nrole: Boss\nbrief: Run the mornings.\n",
            ["seats/scout.yaml"] = "seat: scout\nname: Scout\nrole: Finder\nbrief: SCOUT BRIEF find the good ones.\nmodel: claude-haiku-5-5\n",
            ["seats/far.yaml"] = "seat: far\nname: Far Desk\nrole: Sender\nbrief: Send.\nmachine: OTHER-PC\n",
        };
        Assert.True(store.SaveDraft(TenantId.Local, "warmforward", content, 0, FactoryMemoryAuthorKinds.Person, "device-1", null, At).Ok);
        Assert.True(store.Publish(TenantId.Local, "warmforward", 1, FactoryMemoryAuthorKinds.Person, "device-1", At).Ok);
    }

    private async Task StartAsync(SessionFactoryLookup? caller = null, bool published = true, bool sessionKey = true,
        bool archived = false, bool switchOn = true, bool farRunning = true)
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
            if (sessionKey)
            {
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(BossSession), TenantId.Local, North);
            }
            else
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", TenantId.Local.Value, "browser", "active");
            }
            await next();
        });

        _directors = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _directors.RegisterFromStream(North, "SOREN_NORTH", "test", "2.32.0", pid: 1, startedAt: At, tenant: TenantId.Local);
        if (farRunning)
            _directors.RegisterFromStream(Elsewhere, "OTHER-PC", "test", "2.32.0", pid: 2, startedAt: At, tenant: TenantId.Local);

        _registry = new FactoryRegistryStore(Db);
        _registry.Register(TenantId.Local, WarmForward(), "the owner (test)", At);
        if (published) Publish();
        if (archived)
        {
            using var ctx = Db.CreateContext(TenantId.Local);
            ctx.FactoryRegistry.Single(f => f.Factory == "warmforward").ArchivedAtUtc = At;
            ctx.SaveChanges();
        }

        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            if (command.Verb != "create") return Task.FromResult<DirectorCommandResult?>(null);
            _sent.Add((directorId, JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web)!));
            var reply = new SessionDto { SessionId = StartedSession };
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(reply, Web)));
        };

        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false);
        _kit.Record(TenantId.Local, North, buildsFactoryKit: true);
        _kit.Record(TenantId.Local, Elsewhere, buildsFactoryKit: true);
        var who = caller ?? AsTheBoss;
        var door = new DirectorSpawnDoor(boundary, _directors, _ => who, send,
            Missions: null, WorkflowRuns: null, Workspaces: null, KitCapabilities: _kit);

        Func<Microsoft.AspNetCore.Http.HttpContext, TenantId?> resolveTenant = ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        var gate = FactoryAgentsGate.Group(app, new FactoryAgentsSwitch(switchOn, new TenantSettingsStore(Db)), resolveTenant);
        var directors = _directors;
        FactorySeatStartEndpoints.Map(gate, resolveTenant, _ => who, _registry,
            (tenant, factory) => FactorySeatStart.ReadPublished(new FactoryDefinitionStore(Db), tenant, factory),
            tenant => directors.ListDirectors(tenant),
            (ctx, directorId, request) => GatewayEndpoints.StartSessionOnDirectorAsync(ctx, directorId, request, door),
            (_, row, actor) => _rows.Add((row, actor)));

        await app.StartAsync();
        _app = app;
        _http = app.GetTestServer().CreateClient();
    }

    private Task<HttpResponseMessage> Start(string seat, object? body = null) =>
        _http!.PostAsJsonAsync($"gateway/factory/seats/{seat}/start", body ?? new { name = "find ten", prompt = "THE JOB find ten leads." });

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    [Fact]
    public async Task Start_ByTheBoss_SendsAFullSeatStartFromThePublishedDefinition_OwnedByTheBoss()
    {
        await StartAsync();

        var response = await Start("scout");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(North, directorId);
        Assert.Equal("warmforward", sent.Factory);
        Assert.Equal("scout", sent.FactorySeat);
        Assert.Equal(1, sent.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, sent.FactoryKit);
        Assert.Equal("", sent.RepoPath);
        Assert.Equal("claude-haiku-5-5", sent.Model);
        Assert.Equal(BossSession, sent.ControllerSessionId);
        Assert.Equal(BossSession, sent.ParentSessionId);
        Assert.Equal("WarmForward - Scout - find ten", sent.Name);
    }

    [Fact]
    public async Task Start_TheFirstPrompt_CarriesTheBriefTheWayBackAndTheJob_AndNotTheBossConduct()
    {
        await StartAsync();

        await Start("scout");

        var prompt = Assert.Single(_sent).Request.PrePrompt!;
        Assert.StartsWith("You are Scout, Finder of WarmForward", prompt);
        Assert.Contains("SCOUT BRIEF find the good ones.", prompt);
        Assert.Contains(FactorySeatOpening.KitSentence, prompt);
        Assert.Contains(FactorySeatDelegation.StartedByTheBoss, prompt);
        Assert.Contains("cc-devthrottle session handback", prompt);
        Assert.Contains("Then run cc-devthrottle session done", prompt);
        Assert.Contains("THE JOB find ten leads.", prompt);
        Assert.Contains(FactorySeatOpening.OwnerWordIsFinal, prompt);
        Assert.DoesNotContain(FactorySeatOpening.BossNeverDoesTheWork, prompt);
    }

    [Fact]
    public async Task Start_ABodyThatNamesAnAgentAModelOrAKit_ChangesNothing()
    {
        await StartAsync();

        var response = await Start("scout", new
        {
            name = "find ten",
            prompt = "the job",
            agent = "Codex",
            model = "x",
            factoryKit = FactoryKits.RulesOnly,
            factory = "someone-else",
            machine = "OTHER-PC",
            controllerSessionId = StartedSession,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(North, directorId);
        Assert.Equal("ClaudeCode", sent.Agent);
        Assert.Equal("claude-haiku-5-5", sent.Model);
        Assert.Equal(FactoryKits.Seat, sent.FactoryKit);
        Assert.Equal("warmforward", sent.Factory);
        Assert.Equal(BossSession, sent.ControllerSessionId);
    }

    [Fact]
    public async Task Start_IsRecorded_OnTheFactoryActivityRecord()
    {
        await StartAsync();

        await Start("scout");

        var (row, actor) = Assert.Single(_rows);
        Assert.Equal("warmforward", row.Factory);
        Assert.Equal("boss", row.FactoryAgent);
        Assert.Equal(StartedSession, row.SessionId);
        Assert.Equal(FactoryActivityOutcome.Started, row.Outcome);
        Assert.Equal("scout", row.Subject);
        Assert.Contains(BossSession, row.What);
        Assert.Equal("session:" + BossSession, actor);
    }

    [Fact]
    public async Task Start_ASeatWithTheRoleBossThatRunsTheSchedule_MayDelegateToo()
    {
        await StartAsync(SessionFactoryLookup.In("warmforward", "ceo", 1) with { Kit = FactoryKits.Seat });

        Assert.Equal(HttpStatusCode.Created, (await Start("scout")).StatusCode);
    }

    [Fact]
    public async Task Start_ASeatOnAnotherComputer_GoesToADirectorThere()
    {
        await StartAsync();

        var response = await Start("far");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Elsewhere, Assert.Single(_sent).DirectorId);
    }

    [Fact]
    public async Task Start_NoDirectorOnTheSeatsComputer_IsRefused_AndNeverStartedSomewhereElse()
    {
        await StartAsync(farRunning: false);

        var response = await Start("far");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No Director is running on OTHER-PC", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    public static TheoryData<string, string?, string?> NotABoss => new()
    {
        { "a Boss's child, which inherited the Boss seat", "boss", FactoryKits.RulesOnly },
        { "a seat that is no Boss", "scout", FactoryKits.Seat },
        { "a child of a seat", "scout", FactoryKits.RulesOnly },
        { "a session of the factory with no seat", null, null },
    };

    [Theory]
    [MemberData(nameof(NotABoss))]
    public async Task Start_ByACallerThatIsNotABoss_IsRefused(string who, string? seat, string? kit)
    {
        await StartAsync(SessionFactoryLookup.In("warmforward", seat, seat is null ? null : 1) with { Kit = kit });

        var response = await Start("scout");

        Assert.True(HttpStatusCode.Forbidden == response.StatusCode, who);
        Assert.Empty(_sent);
        Assert.Empty(_rows);
    }

    [Fact]
    public async Task Start_ByASessionInNoFactory_IsRefused()
    {
        await StartAsync(SessionFactoryLookup.InNoFactory);

        Assert.Equal(HttpStatusCode.Forbidden, (await Start("scout")).StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_ByASessionTheGatewayDoesNotKnowYet_IsARetry()
    {
        await StartAsync(SessionFactoryLookup.NotKnown);

        var response = await Start("scout");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SpawnFactory.NotYetKnown, await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_ByAPersonsDevice_IsRefused()
    {
        await StartAsync(sessionKey: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await Start("scout")).StatusCode);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("boss")]
    [InlineData("ceo")]
    public async Task Start_OfABossSeat_IsRefused_SoNoBossMakesASecondBoss(string seat)
    {
        await StartAsync();

        var response = await Start(seat);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("is a Boss", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_OfASeatTheVersionDoesNotHold_IsRefused_WithItsSeats()
    {
        await StartAsync();

        var response = await Start("ghost");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("is not in version 1", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_InAFactoryWithNoPublishedDefinition_IsRefused()
    {
        await StartAsync(published: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await Start("scout")).StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_InAnArchivedFactory_IsRefused()
    {
        await StartAsync(archived: true);

        var response = await Start("scout");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("archived", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Start_WhileTheSwitchIsOff_AnswersAsIfUnmapped()
    {
        await StartAsync(switchOn: false);

        Assert.Equal(HttpStatusCode.NotFound, (await Start("scout")).StatusCode);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("{\"prompt\":\"the job\"}")]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("not json")]
    public async Task Start_WithoutANameAndAJob_IsRefused(string body)
    {
        await StartAsync();

        var response = await _http!.PostAsync("gateway/factory/seats/scout/start", new StringContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Guard_ASessionKeyMayReachTheSeatStart_ByPostOnly()
    {
        Assert.True(SessionKeyGuard.Check("POST", "/gateway/factory/seats/scout/start").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", "/gateway/factory/seats/scout/start").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/factory/seats/scout/stop").Allowed);
    }
}
