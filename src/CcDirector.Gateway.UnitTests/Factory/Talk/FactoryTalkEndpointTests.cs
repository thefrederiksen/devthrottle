using CcDirector.Gateway.Tests.Factory.Registry;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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

    /// <summary>Which Directors build a seat's kit (Factory Definition mission, phase 5): the north one does.</summary>
    private readonly CcDirector.Gateway.Streaming.FactoryKitCapabilityRegistry _kit = new();
    private FactoryRegistryStore? _registry;
    private FactoryBriefStore? _briefs;
    private FactoryBriefStore Briefs => _briefs ??= new FactoryBriefStore(Db, _registry ?? throw new InvalidOperationException("start the route first"));

    /// <summary>Every create the route dispatched to a Director, with the Director it went to.</summary>
    private readonly List<(string DirectorId, NewSessionRequest Request)> _sent = new();

    /// <summary>The Factory Manager facts (Factory Control, step 5), in memory: the binding, the placement, and what
    /// the Directors last pushed for a session.</summary>
    private string? _factoryManager;
    private (string Machine, string Folder)? _placement;
    private readonly Dictionary<string, SessionDto> _pushed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The problems the one problems fold answers with, for the account; Handle the issue reads these.</summary>
    private readonly List<CronRunProblemDto> _problems = new();

    /// <summary>One open problem of the boss's schedule, as the problems fold lists it.</summary>
    private static CronRunProblemDto ScoutProblem(string runId = "3f1c2b7a-0000-4000-8000-0000000000a1", string factory = "warmforward") => new()
    {
        RunId = runId,
        JobId = "cj_a721e6",
        JobName = "WarmForward Factory - Boss - morning run",
        Factory = factory,
        Seat = "nora-hale",
        ScheduledUtc = new DateTime(2026, 10, 11, 10, 15, 0, DateTimeKind.Utc),
        StartedUtc = new DateTime(2026, 10, 11, 10, 16, 0, DateTimeKind.Utc),
        Shift = "morning",
        ShiftText = "morning shift, Sun 11 Oct, 08:00-16:00",
        Kind = CronRunProblems.DidNotReport,
        KindText = "did not report",
        Reason = "its session ended without reporting how the run went (stopped by you after 12 min)",
        Sentence = "Did not report: its session ended without reporting how the run went (stopped by you after 12 min).",
        RaisedAtUtc = new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
        SessionId = "5b0e9d1c-0000-4000-8000-0000000000b2",
        State = CronRunProblemStates.Open,
    };

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
        BossSeat = "nora-hale",
        GoalText = "A cash engine that runs without your time.",
        GoalFile = "GOAL.md",
        GoalApprovedOn = "2026-10-04",
        Seats =
        {
            new FactorySeatManifest { Id = "nora-hale", Name = "Boss", Role = "Boss", BriefFile = "agents/ceo.yaml", Schedules = { "cj_a721e6" } },
            new FactorySeatManifest { Id = "savings-engineer", Name = "Savings Engineer", Role = "Savings Engineer", BriefFile = "agents/savings.yaml", Computer = "DEVLINUX" },
        },
    };

    /// <summary>Who is calling, as the auth middleware stamps it.</summary>
    public enum Caller { OwnerBrowser, OwnerPhone, DirectorDeviceKey, MachineToken, SessionKey }

    private const string CallingSessionId = "71000000-0000-4000-8000-000000000009";
    private const string Folder = @"D:\ReposFred\factories";

    private async Task StartAsync(bool switchOn = true, bool northStopped = false, Caller caller = Caller.OwnerBrowser,
        string northVersion = "2.16.0", bool bossless = false)
    {
        Directory.CreateDirectory(_dir);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // An in-memory host: the routes are driven over HTTP with no listener and no port.
        builder.WebHost.UseTestServer();
        var app = builder.Build();

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
            if (caller == Caller.SessionKey)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(CallingSessionId), TenantId.Local, NorthDirector);
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
        var manifest = WarmForward();
        if (bossless) manifest.BossSeat = null;
        _registry.Register(TenantId.Local, manifest, "the owner (test)", DateTime.UtcNow);

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

        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false);
        _kit.Record(TenantId.Local, NorthDirector, buildsFactoryKit: true);
        var door = new DirectorSpawnDoor(boundary, _directors, _ => SessionFactoryLookup.InNoFactory, send,
            Missions: null, WorkflowRuns: null, Workspaces: null, KitCapabilities: _kit);

        var factorySwitch = new FactoryAgentsSwitch(switchOn, new TenantSettingsStore(Db));
        var directors = _directors;
        var factoryManager = new FactoryManagerSources(
            Bound: _ => _factoryManager,
            Bind: (_, sid) => _factoryManager = sid,
            Placement: _ => _placement,
            SavePlacement: (_, machine, folder) => _placement = (machine, folder),
            Factories: tenant => _registry!.List(tenant),
            ListDirectors: tenant => directors.ListDirectors(tenant),
            StartOnDirector: (ctx, directorId, request) => GatewayEndpoints.StartSessionOnDirectorAsync(ctx, directorId, request, door),
            LastKnownSession: (_, sid) => _pushed.TryGetValue(sid, out var s) ? s : null,
            TimeZone: _ => TimeZoneInfo.Utc);
        Func<Microsoft.AspNetCore.Http.HttpContext, TenantId?> resolveTenant = ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        FactoryManagerEndpoints.Map(app, FactoryAgentsGate.Group(app, factorySwitch, resolveTenant), factorySwitch, resolveTenant, factoryManager);
        FactoryTalkEndpoints.Map(app, factorySwitch,
            resolveTenant: ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary),
            registry: _registry,
            listDirectors: tenant => directors.ListDirectors(tenant),
            schedule: (_, id) => schedules.TryGetValue(id, out var job) ? job : null,
            startOnDirector: (ctx, directorId, request) => GatewayEndpoints.StartSessionOnDirectorAsync(ctx, directorId, request, door),
            problems: (_, factory) => new CronRunProblemsDto
            {
                Factory = factory,
                Problems = _problems.Where(p => string.Equals(p.Factory, factory, StringComparison.OrdinalIgnoreCase)).ToList(),
            },
            timeZone: _ => TimeZoneInfo.Utc,
            factoryManager: factoryManager,
            // The real brief store, read the way the host reads it.
            // As the host wires it: from the definition for a defined factory, else from the brief store.
            publishedBrief: (tenant, factory, seat) => new FactoryDefinitionBriefs(new FactoryDefinitionStore(Db), Briefs).GivenTo(tenant, factory, seat),
            // As the host wires it: a defined factory's talk is built from its published definition.
            definitionOf: (tenant, factory) => FactorySeatStart.ReadPublished(new FactoryDefinitionStore(Db), tenant, factory));

        await app.StartAsync();
        _app = app;
        _http = app.GetTestServer().CreateClient();
    }

    private Task<HttpResponseMessage> Talk(string factory, string seat) =>
        _http!.PostAsync($"gateway/factory-agents/factories/{factory}/seats/{seat}/talk", content: null);

    private Task<HttpResponseMessage> Handle(string factory, string runId) =>
        _http!.PostAsync($"gateway/factory-agents/factories/{factory}/problems/{runId}/handle", content: null);

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
        Assert.Equal("WarmForward - Boss - talk with the owner", sent.Name);
        Assert.Equal(@"D:\ReposFred\cc-consult\ideas\warmforward-factory", sent.RepoPath);
        Assert.Equal("ClaudeCode", sent.Agent);
        Assert.NotNull(sent.PrePrompt);
        Assert.StartsWith("You are the boss of WarmForward", sent.PrePrompt);
        Assert.Contains(MorningRun, sent.PrePrompt);
        Assert.Contains("--outcome talked", sent.PrePrompt);
    }

    // ---- the brief on the Gateway (Factory Seats mission, phase 2c) --------------------------------------------

    /// <summary>When a brief is saved or published here: a fixed time, because when it happened is stored, not judged.</summary>
    private static readonly DateTime BriefAt = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private void PublishABrief(string seat, string text)
    {
        var saved = Briefs.SaveDraft(TenantId.Local, "warmforward", seat, text, Briefs.Standing(TenantId.Local, "warmforward", seat).Newest?.Version ?? 0,
            FactoryMemoryAuthorKinds.Session, CallingSessionId, BriefAt);
        Assert.True(saved.Ok);
        Assert.True(Briefs.Publish(TenantId.Local, "warmforward", seat, saved.Brief!.Version, FactoryMemoryAuthorKinds.Person, "device-1", BriefAt).Ok);
    }

    [Fact]
    public async Task Talk_ToASeatWithAPublishedBrief_IsGivenItsTextAndVersion_AndTheAnswerSaysWhichVersion()
    {
        await StartAsync();
        PublishABrief("nora-hale", "FIRST: be careful with money.");
        PublishABrief("nora-hale", "SECOND: be careful with money, and say so.");
        // A newer draft nobody has approved: it must not reach the talk.
        Assert.True(Briefs.SaveDraft(TenantId.Local, "warmforward", "nora-hale", "DRAFT: spend freely.", 2,
            FactoryMemoryAuthorKinds.Session, CallingSessionId, BriefAt).Ok);

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, sent) = Assert.Single(_sent);
        Assert.Contains("--- your brief, version 2 ---", sent.PrePrompt);
        Assert.Contains("SECOND: be careful with money, and say so.", sent.PrePrompt);
        Assert.DoesNotContain("FIRST", sent.PrePrompt);
        Assert.DoesNotContain("DRAFT", sent.PrePrompt);
        Assert.DoesNotContain("ceo.yaml", sent.PrePrompt);
        Assert.Contains("--agent nora-hale --version 2 --outcome talked", sent.PrePrompt);
        var started = await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web);
        Assert.Equal(2, started!.BriefVersion);
    }

    /// <summary>WarmForward's definition, published as version 1: the boss seat's file holds this brief, and the
    /// savings engineer has a file only when told so. A whole definition: since phase 5 a defined factory's talk is built
    /// from it. <paramref name="bossExtra"/> is added to the boss seat's file, and <paramref name="extraFiles"/> to the
    /// definition (a rule set, a skill).</summary>
    private void PublishADefinition(string bossBrief, bool withEngineer = false, string bossExtra = "",
        IReadOnlyDictionary<string, string>? extraFiles = null)
    {
        var definitions = new FactoryDefinitionStore(Db);
        var content = new Dictionary<string, string>
        {
            ["factory.yaml"] = "factory: warmforward\ntitle: WarmForward\ngoalText: A cash engine that runs without your time.\n"
                               + "bossSeat: nora-hale\ndefaults:\n  machine: SOREN_NORTH\n  agent: ClaudeCode\n  model: claude-opus-5-5\n",
            ["seats/nora-hale.yaml"] = $"seat: nora-hale\nname: Boss\nrole: Boss\nbrief: |\n  {bossBrief}\n{bossExtra}",
        };
        foreach (var (path, text) in extraFiles ?? new Dictionary<string, string>()) content[path] = text;
        if (withEngineer) content["seats/savings-engineer.yaml"] = "seat: savings-engineer\nname: Savings Engineer\nrole: Savings Engineer\nbrief: Save.\n";
        Assert.True(definitions.SaveDraft(TenantId.Local, "warmforward", content, 0, FactoryMemoryAuthorKinds.Person, "device-1", null, BriefAt).Ok);
        Assert.True(definitions.Publish(TenantId.Local, "warmforward", 1, FactoryMemoryAuthorKinds.Person, "device-1", BriefAt).Ok);
    }

    [Fact]
    public async Task Talk_ToASeatOfADefinedFactory_IsGivenTheBriefInThePublishedDefinition_AndRecordsADefinitionVersion()
    {
        await StartAsync();
        PublishABrief("nora-hale", "BRIEF STORE: not read for a defined factory.");
        PublishADefinition("DEFINED: be careful with money.");

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, sent) = Assert.Single(_sent);
        Assert.Contains("- your brief: definition version 1, given to you in full below this list. It is part of the factory's definition", sent.PrePrompt);
        Assert.Contains("--- your brief, definition version 1 ---", sent.PrePrompt);
        Assert.Contains("DEFINED: be careful with money.", sent.PrePrompt);
        Assert.DoesNotContain("BRIEF STORE", sent.PrePrompt);
        Assert.DoesNotContain("ceo.yaml", sent.PrePrompt);
        Assert.Contains("--agent nora-hale --version \"definition v1\" --outcome talked", sent.PrePrompt);
        var started = await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web);
        Assert.Equal(1, started!.BriefVersion);
        Assert.Equal(FactoryBriefVersionKinds.Definition, started.BriefVersionKind);
    }

    [Fact]
    public async Task Talk_ToASeatOfADefinedFactoryWithNoFileForIt_IsRefused_NeverToldAFilePath()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.", withEngineer: false);

        var response = await Talk("warmforward", "savings-engineer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The talk was not started: the seat 'savings-engineer' is not in version 1 of the definition of 'warmforward', "
                     + "the published one, so no session was started. Publish a definition that holds it, or use one of its seats: nora-hale.",
            await ErrorOf(response));
        Assert.Empty(_sent);
    }

    // ---- a defined factory's talk is built from its definition (Factory Definition mission, phase 5) -------------

    private Task<HttpResponseMessage> TalkWith(string factory, string seat, string json) =>
        _http!.PostAsync($"gateway/factory-agents/factories/{factory}/seats/{seat}/talk",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    /// <summary>A rule set for the boss seat, so an agent that cannot take rules is refused it.</summary>
    private static readonly IReadOnlyDictionary<string, string> BaseRules = new Dictionary<string, string>
    {
        ["rules/base.json"] = "{\n  \"permissions\": {\n    \"deny\": [\"Bash(git push:*)\"]\n  }\n}\n",
    };

    [Fact]
    public async Task Talk_DefinedFactory_TheCreateCarriesTheSeatFromTheDefinition_AndNoFolder()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.");

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        Assert.Equal("", sent.RepoPath);
        Assert.True(sent.FactoryHome);
        Assert.Null(sent.FactoryWorktree);
        Assert.Equal("nora-hale", sent.FactorySeat);
        Assert.Equal(1, sent.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.Seat, sent.FactoryKit);
        Assert.Equal("ClaudeCode", sent.Agent);
        Assert.Equal("claude-opus-5-5", sent.Model);
        Assert.Equal("warmforward", sent.Factory);
        Assert.Null(sent.FactoryStartOverride);
        Assert.Equal("WarmForward - Boss - talk with the owner", sent.Name);
        // Still the person's own session: no controller, no parent, a person's origin.
        Assert.Null(sent.ControllerSessionId);
        Assert.Null(sent.ParentSessionId);
        Assert.Equal(SessionOriginKinds.Human, sent.Origin);
        var started = await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web);
        Assert.Equal("SOREN_NORTH", started!.Computer);
    }

    [Fact]
    public async Task Talk_DefinedFactory_TheFirstPromptSaysWhereTheKitIs_AndAGoalChangeIsADraftOfTheDefinition()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.");

        await Talk("warmforward", "nora-hale");

        var prompt = Assert.Single(_sent).Request.PrePrompt!;
        Assert.Contains("- " + FactorySeatOpening.KitSentence, prompt);
        Assert.Contains("- save a draft of the definition with the new goal (cc-devthrottle factory definition save); the owner publishes it.", prompt);
        Assert.Contains("--- your brief, definition version 1 ---", prompt);
        // Never a goal file, never a registration, never a folder.
        Assert.DoesNotContain("GOAL.md", prompt);
        Assert.DoesNotContain("factory register", prompt);
        Assert.DoesNotContain(@"D:\ReposFred", prompt);
    }

    [Fact]
    public async Task Handle_DefinedFactory_TheTalkAboutAProblem_IsASeatStartFromTheDefinitionToo()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.");
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, sent) = Assert.Single(_sent);
        Assert.Equal("nora-hale", sent.FactorySeat);
        Assert.Equal(FactoryKits.Seat, sent.FactoryKit);
        Assert.Equal("", sent.RepoPath);
        Assert.Contains("THIS TALK IS ABOUT ONE PROBLEM", sent.PrePrompt);
        Assert.Contains(FactorySeatOpening.KitSentence, sent.PrePrompt);
    }

    [Fact]
    public async Task Talk_DefinedFactory_WithAnOverride_StartsWhatThePersonChose_AndRecordsItInWords()
    {
        await StartAsync();
        _directors!.RegisterFromStream("dir-south", "SOREN_SOUTH", "test", "2.16.0", pid: 3, startedAt: BriefAt, tenant: TenantId.Local);
        _kit.Record(TenantId.Local, "dir-south", buildsFactoryKit: true);
        PublishADefinition("DEFINED: be careful with money.");

        var response = await TalkWith("warmforward", "nora-hale", """{"agent":"codex","model":"gpt-6","machine":"SOREN_SOUTH"}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal("dir-south", directorId);
        Assert.Equal("Codex", sent.Agent);
        Assert.Equal("gpt-6", sent.Model);
        Assert.Equal("agent Codex, model gpt-6, machine SOREN_SOUTH, chosen by the person", sent.FactoryStartOverride);
        Assert.Equal("nora-hale", sent.FactorySeat);
        Assert.Equal("SOREN_SOUTH", (await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web))!.Computer);
    }

    [Fact]
    public async Task Talk_DefinedFactory_AnOverrideAgentThatCannotTakeTheSeatsRules_IsRefusedWithItsReason_AndNothingIsSent()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.", bossExtra: "rules: [base]\n", extraFiles: BaseRules);

        var response = await TalkWith("warmforward", "nora-hale", """{"agent":"Codex"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The talk was not started: " + FactoryRulesSupport.CodexReason, await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_DefinedFactory_AnOverrideAgentNoSeatCanRun_IsRefusedByName()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.");

        var response = await TalkWith("warmforward", "nora-hale", """{"agent":"Clippy"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The talk was not started: 'Clippy' is not an agent a seat can run. Choose one of: "
                     + string.Join(", ", FactorySkillSupport.SupportedAgents) + ".", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_DefinedFactory_AnOverrideMachineWithNoDirector_Is409NamingIt_AndNothingIsSent()
    {
        await StartAsync();
        PublishADefinition("DEFINED: be careful with money.");

        var response = await TalkWith("warmforward", "nora-hale", """{"machine":"NOWHERE"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("No Director is running on NOWHERE, the computer chosen for this talk, so the talk was not started. Start the Director on NOWHERE and press Talk again.",
            await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_DefinedFactory_ToADirectorThatDoesNotBuildTheKit_IsRefusedWithTheReason()
    {
        await StartAsync();
        _kit.Record(TenantId.Local, NorthDirector, buildsFactoryKit: false);
        PublishADefinition("DEFINED: be careful with money.");

        var response = await Talk("warmforward", "nora-hale");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The Director on SOREN_NORTH is version 2.16.0, which cannot start a seat of a defined factory (it cannot build the seat's kit), so no session was started. Update this Director and try again.",
            await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_UNDEFINED_FACTORY_TheCreateIsAsItWas_InTheFactorysFolder_WithNoSeatField()
    {
        await StartAsync();

        await Talk("warmforward", "nora-hale");

        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        Assert.Equal(@"D:\ReposFred\cc-consult\ideas\warmforward-factory", sent.RepoPath);
        Assert.Equal("ClaudeCode", sent.Agent);
        Assert.False(sent.CarriesSeatStartFields());
        Assert.Null(sent.FactoryStartOverride);
        Assert.Equal(FactoryTalkSeed.Compose(_registry!.Find(TenantId.Local, "warmforward")!,
            _registry.Find(TenantId.Local, "warmforward")!.Seats[0], "cj_a721e6", MorningRun), sent.PrePrompt);
    }

    [Fact]
    public async Task Talk_UNDEFINED_FACTORY_WithAnOverride_IsRefused_AndNothingIsSent()
    {
        await StartAsync();

        var response = await TalkWith("warmforward", "nora-hale", """{"agent":"Codex"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WarmForward has no published definition, so a talk with it runs as it is registered: an agent, a model or a machine can be chosen only for a seat of a defined factory. Press Talk without choosing them.",
            await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_UNDEFINED_FACTORY_AnEmptyBody_StartsAsBefore()
    {
        await StartAsync();

        var response = await TalkWith("warmforward", "nora-hale", "{}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(Assert.Single(_sent).Request.CarriesSeatStartFields());
    }

    [Fact]
    public async Task Talk_ABodyThatIsNotJson_Is400InWords_AndNothingIsSent()
    {
        await StartAsync();

        var response = await TalkWith("warmforward", "nora-hale", "agent=Codex");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The talk's body is not JSON the talk reads. Send nothing, or an object with agent, model and machine to choose them for this talk.",
            await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Talk_ToASeatWithNoBriefOnTheGateway_IsToldThePathOfItsFile_AndTheAnswerCarriesNoVersion()
    {
        await StartAsync();
        // Another seat of the same factory has one; this seat is not given it.
        PublishABrief("savings-engineer", "The engineer's brief.");

        var response = await Talk("warmforward", "nora-hale");

        var (_, sent) = Assert.Single(_sent);
        Assert.Equal(FactoryTalkSeed.Compose(_registry!.Find(TenantId.Local, "warmforward")!,
            _registry.Find(TenantId.Local, "warmforward")!.Seats[0], "cj_a721e6", MorningRun), sent.PrePrompt);
        Assert.Contains(@"- your brief: D:\ReposFred\cc-consult\ideas\warmforward-factory\agents/ceo.yaml", sent.PrePrompt);
        Assert.DoesNotContain("--- your brief", sent.PrePrompt);
        var started = await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web);
        Assert.Null(started!.BriefVersion);
    }

    [Fact]
    public async Task Handle_ATalkAboutAProblem_IsGivenTheBossesPublishedBriefToo()
    {
        await StartAsync();
        PublishABrief("nora-hale", "FIRST: be careful with money.");
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (_, sent) = Assert.Single(_sent);
        Assert.Contains("--- your brief, version 1 ---", sent.PrePrompt);
        Assert.Contains("THIS TALK IS ABOUT ONE PROBLEM", sent.PrePrompt);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web))!.BriefVersion);
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
        Assert.Equal("WarmForward - Boss - talk with the owner", talk.SessionName);
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

    // ---- Handle the issue (Factory Control, step 1b) ----------------------------------------------------------

    [Fact]
    public async Task Handle_AnOpenProblem_OpensATalkWithTheBoss_BriefedOnTheProblem()
    {
        await StartAsync();
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        // The person's own top-level session, in the factory, seated as the boss, like any talk.
        Assert.Null(sent.ControllerSessionId);
        Assert.Null(sent.ParentSessionId);
        Assert.Equal("warmforward", sent.Factory);
        Assert.Equal("WarmForward - Boss - handle the issue (WarmForward Factory - Boss - morning run)", sent.Name);
        Assert.StartsWith("You are the boss of WarmForward", sent.PrePrompt);
        // Under the same rules as the boss's scheduled runs.
        Assert.Contains(MorningRun, sent.PrePrompt);
        // Told which run, what went wrong, when, and which session to read - and how to resolve it.
        Assert.Contains(problem.RunId, sent.PrePrompt);
        Assert.Contains("- what went wrong: Did not report: its session ended without reporting how the run went (stopped by you after 12 min).", sent.PrePrompt);
        Assert.Contains("morning shift, Sun 11 Oct, 08:00-16:00", sent.PrePrompt);
        Assert.Contains("cc-devthrottle session buffer " + problem.SessionId, sent.PrePrompt);
        Assert.Contains($"cc-devthrottle run resolve {problem.RunId} \"<one line: what fixed it>\"", sent.PrePrompt);
    }

    [Fact]
    public async Task Handle_AFactoryWithNoBoss_WithAnOverride_IsRefusedInTheSeatArmsWords_AndNothingIsStartedOrBound()
    {
        // Review finding 3: the Factory Manager arm answers the same body the seat arm does, never dropping it silently.
        await StartAsync(bossless: true);
        _placement = ("SOREN_NORTH", Folder);
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await _http!.PostAsync($"gateway/factory-agents/factories/warmforward/problems/{problem.RunId}/handle",
            new StringContent("""{"agent":"Codex"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WarmForward has no published definition, so a talk with it runs as it is registered: an agent, a model or a machine can be chosen only for a seat of a defined factory. Press Talk without choosing them.",
            await ErrorOf(response));
        Assert.Empty(_sent);
        Assert.Null(_factoryManager);
    }

    [Fact]
    public async Task Handle_AFactoryWithNoBoss_StartsAFactoryManagerTalk_BriefedOnTheProblem_AndBindsIt()
    {
        await StartAsync(bossless: true);
        _placement = ("SOREN_NORTH", Folder);
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        // The person's own top-level session, in the Factory Manager's folder, in no factory.
        Assert.Null(sent.ControllerSessionId);
        Assert.Null(sent.ParentSessionId);
        Assert.Null(sent.Factory);
        Assert.Equal(Folder, sent.RepoPath);
        Assert.Equal("Factory Manager - WarmForward - handle the issue (WarmForward Factory - Boss - morning run)", sent.Name);
        Assert.StartsWith("You are this account's Factory Manager", sent.PrePrompt);
        // Briefed in the boss's words: which run, what went wrong, which session to read, how to resolve it.
        Assert.Contains("THIS TALK IS ABOUT ONE PROBLEM OF WarmForward", sent.PrePrompt);
        Assert.Contains(problem.RunId, sent.PrePrompt);
        Assert.Contains("cc-devthrottle session buffer " + problem.SessionId, sent.PrePrompt);
        Assert.Contains($"cc-devthrottle run resolve {problem.RunId} \"<one line: what fixed it>\"", sent.PrePrompt);
        // The new session is now the account's Factory Manager.
        Assert.Equal("7d2c0e4e-0000-4000-8000-00000000c0de", _factoryManager);
    }

    [Fact]
    public async Task Handle_AFactoryWithNoBoss_AndNoPlacementSaved_Is409WithASentence_AndNothingIsStarted()
    {
        await StartAsync(bossless: true);
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("The Factory Manager has no computer and folder yet", await ErrorOf(response));
        Assert.Empty(_sent);
        Assert.Null(_factoryManager);
    }

    // ---- the Factory Manager's own talk and binding (Factory Control, step 5) -------------------------------------

    private Task<HttpResponseMessage> TalkToFactoryManager(object? body = null) =>
        _http!.PostAsync("gateway/factory-agents/factory-manager/talk", body is null ? null : JsonContent.Create(body));

    private Task<HttpResponseMessage> PutBinding(string json) =>
        _http!.PutAsync("gateway/factory-manager", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    [Fact]
    public async Task TalkToFactoryManager_WithAPlacement_StartsATopLevelSession_SavesThePlacement_AndBindsIt()
    {
        await StartAsync();

        var response = await TalkToFactoryManager(new { computer = "SOREN_NORTH", folder = Folder });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var (directorId, sent) = Assert.Single(_sent);
        Assert.Equal(NorthDirector, directorId);
        Assert.Null(sent.ControllerSessionId);
        Assert.Null(sent.ParentSessionId);
        Assert.Equal(SessionOriginKinds.Human, sent.Origin);
        Assert.Equal("Factory Manager - talk with the owner", sent.Name);
        Assert.Equal(Folder, sent.RepoPath);
        Assert.StartsWith("You are this account's Factory Manager", sent.PrePrompt);
        // It is told about every factory.
        Assert.Contains("WarmForward", sent.PrePrompt);
        Assert.Equal(("SOREN_NORTH", Folder), _placement);
        Assert.Equal("7d2c0e4e-0000-4000-8000-00000000c0de", _factoryManager);
        var started = (await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web))!;
        Assert.Equal("/session/7d2c0e4e-0000-4000-8000-00000000c0de", started.Href);
    }

    [Fact]
    public async Task TalkToFactoryManager_WhileTheBoundOneStillRuns_Is200WithThatSession_AndNothingIsStarted()
    {
        await StartAsync();
        _placement = ("SOREN_NORTH", Folder);
        _factoryManager = "11111111-0000-4000-8000-000000000001";
        _pushed[_factoryManager] = new SessionDto
        {
            SessionId = _factoryManager, Name = "Factory Manager - talk with the owner", MachineName = "SOREN_NORTH",
            DirectorId = NorthDirector, ActivityState = "Idle",
        };

        var response = await TalkToFactoryManager();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/session/" + _factoryManager, (await response.Content.ReadFromJsonAsync<FactoryTalkStartedDto>(Web))!.Href);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task TalkToFactoryManager_WhenTheBoundOneHasExited_StartsANewOne()
    {
        await StartAsync();
        _placement = ("SOREN_NORTH", Folder);
        _factoryManager = "11111111-0000-4000-8000-000000000001";
        _pushed[_factoryManager] = new SessionDto { SessionId = _factoryManager, ActivityState = "Exited" };

        var response = await TalkToFactoryManager();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(_sent);
        Assert.Equal("7d2c0e4e-0000-4000-8000-00000000c0de", _factoryManager);
    }

    [Fact]
    public async Task TalkToFactoryManager_OnlyAComputerAndNoFolder_Is400_AndNothingIsStarted()
    {
        await StartAsync();

        var response = await TalkToFactoryManager(new { computer = "SOREN_NORTH" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sent);
        Assert.Null(_placement);
    }

    [Fact]
    public async Task TalkToFactoryManager_NoDirectorRunsOnTheComputer_Is409_AndNothingIsSavedOrStarted()
    {
        await StartAsync(northStopped: true);

        var response = await TalkToFactoryManager(new { computer = "SOREN_NORTH", folder = Folder });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No Director is running on SOREN_NORTH", await ErrorOf(response));
        Assert.Empty(_sent);
        Assert.Null(_placement);
        Assert.Null(_factoryManager);
    }

    [Theory]
    [InlineData(Caller.SessionKey)]
    [InlineData(Caller.DirectorDeviceKey)]
    [InlineData(Caller.MachineToken)]
    public async Task TalkToFactoryManager_ACredentialThatIsNotTheOwnersBrowserOrPhone_Is403_AndNothingIsStarted(Caller caller)
    {
        await StartAsync(caller: caller);

        var response = await TalkToFactoryManager(new { computer = "SOREN_NORTH", folder = Folder });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task TalkToFactoryManager_TheFactorySwitchOff_Is404_AndNothingIsStarted()
    {
        await StartAsync(switchOn: false);

        var response = await TalkToFactoryManager(new { computer = "SOREN_NORTH", folder = Folder });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public void TalkToFactoryManager_ASessionsOwnKey_NeverReachesTheRoute()
        => Assert.False(SessionKeyGuard.Check("POST", "/gateway/factory-agents/factory-manager/talk").Allowed,
            "a talk is the owner's own session; a session must not be able to start one");

    [Theory]
    [InlineData(Caller.OwnerBrowser)]
    [InlineData(Caller.OwnerPhone)]
    public async Task Binding_SetByTheOwnersOwnDevice_ThenShowThenClear_RoundTrips(Caller owner)
    {
        await StartAsync(caller: owner);
        const string id = "22222222-0000-4000-8000-000000000002";

        var set = await PutBinding("{\"sessionId\":\"" + id + "\"}");
        var shown = await _http!.GetFromJsonAsync<FactoryManagerBindingDto>("gateway/factory-manager", Web);
        var cleared = await PutBinding("{\"sessionId\":null}");

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(id, shown!.SessionId);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<FactoryManagerBindingDto>(Web))!.SessionId);
        Assert.Null(_factoryManager);
    }

    [Theory]
    [InlineData(Caller.SessionKey)]
    [InlineData(Caller.DirectorDeviceKey)]
    [InlineData(Caller.MachineToken)]
    public async Task Binding_SetByACredentialThatIsNotTheOwnersBrowserOrPhone_Is403_AndTheMarkStaysWhereItWas(Caller caller)
    {
        await StartAsync(caller: caller);
        _factoryManager = "11111111-0000-4000-8000-000000000001";

        var response = await PutBinding("{\"sessionId\":\"22222222-0000-4000-8000-000000000002\"}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("11111111-0000-4000-8000-000000000001", _factoryManager);
    }

    [Fact]
    public async Task Binding_ASessionMarkingItself_Is403_InASentenceThatSaysWhoSetsItAndWhatASessionMayStillDo()
    {
        await StartAsync(caller: Caller.SessionKey);

        var response = await PutBinding("{\"sessionId\":\"" + CallingSessionId + "\"}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(_factoryManager);
        var error = await ErrorOf(response);
        Assert.Equal(FactoryManagerEndpoints.SessionMaySetSentence, error);
        Assert.Contains("only the owner sets it", error);
        Assert.Contains("may still read the mark and clear it", error);
    }

    [Fact]
    public async Task Binding_ASessionKey_MayStillReadTheMarkAndClearIt()
    {
        await StartAsync(caller: Caller.SessionKey);
        _factoryManager = "11111111-0000-4000-8000-000000000001";

        var shown = await _http!.GetFromJsonAsync<FactoryManagerBindingDto>("gateway/factory-manager", Web);
        var cleared = await PutBinding("{\"sessionId\":null}");

        Assert.Equal("11111111-0000-4000-8000-000000000001", shown!.SessionId);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null(_factoryManager);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessionId\":\"not-a-session\"}")]
    [InlineData("{\"sessionId\":42}")]
    public async Task Binding_WithNoOrABadSessionId_Is400_AndNothingChanges(string json)
    {
        await StartAsync(caller: Caller.SessionKey);
        _factoryManager = "22222222-0000-4000-8000-000000000002";

        var response = await PutBinding(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("22222222-0000-4000-8000-000000000002", _factoryManager);
    }

    [Fact]
    public async Task Binding_TheFactorySwitchOff_Is404_AndNothingChanges()
    {
        await StartAsync(switchOn: false, caller: Caller.SessionKey);

        var response = await PutBinding("{\"sessionId\":\"22222222-0000-4000-8000-000000000002\"}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(_factoryManager);
    }

    [Fact]
    public async Task Card_NotStartedAndNoPlacement_AsksForTheComputerAndFolder_ListingRunningComputers()
    {
        await StartAsync();

        var card = (await _http!.GetFromJsonAsync<FactoryManagerCardDto>("gateway/factory-agents/factory-manager", Web))!;

        Assert.Equal("Factory Manager", card.Title);
        Assert.Equal("Not started yet", card.StateText);
        Assert.Equal("Talk to the Factory Manager", card.ActionLabel);
        Assert.Null(card.SessionHref);
        Assert.True(card.NeedsPlacement);
        Assert.Equal(new[] { "OTHER-PC", "SOREN_NORTH" }, card.Computers);
        // The form's labels are the Gateway's, as the contract says; the Cockpit writes none of its own.
        Assert.Equal("Computer", card.ComputerLabel);
        Assert.Equal("Folder", card.FolderLabel);
    }

    [Fact]
    public async Task Card_WhileTheBoundOneRuns_OffersToOpenIt()
    {
        await StartAsync();
        _placement = ("SOREN_NORTH", Folder);
        _factoryManager = "11111111-0000-4000-8000-000000000001";
        _pushed[_factoryManager] = new SessionDto { SessionId = _factoryManager, MachineName = "SOREN_NORTH", ActivityState = "Idle" };

        var card = (await _http!.GetFromJsonAsync<FactoryManagerCardDto>("gateway/factory-agents/factory-manager", Web))!;

        Assert.Equal("Running on SOREN_NORTH", card.StateText);
        Assert.Equal("Open the Factory Manager", card.ActionLabel);
        Assert.Equal("/session/" + _factoryManager, card.SessionHref);
        Assert.False(card.NeedsPlacement);
        Assert.Null(card.ComputerLabel);
        Assert.Null(card.FolderLabel);
        Assert.Equal("Runs on SOREN_NORTH in " + Folder, card.PlacementSavedText);
    }

    [Fact]
    public async Task NameCheck_OverHttp_ChecksTheRegisteredFactory()
    {
        await StartAsync(caller: Caller.SessionKey);

        var check = (await _http!.GetFromJsonAsync<FactoryNameCheckDto>("gateway/factory/name-check", Web))!;

        // WarmForward's seats are named Boss and Savings Engineer, both role words, so nothing is flagged.
        Assert.Equal(1, check.FactoriesChecked);
        Assert.Equal(0, check.Count);
    }

    [Fact]
    public async Task Handle_ARunThatIsNoOpenProblemOfThisFactory_Is404_AndNothingIsStarted()
    {
        await StartAsync();
        var resolved = ScoutProblem();
        resolved.State = CronRunProblemStates.Resolved;
        _problems.Add(resolved);
        var elsewhere = ScoutProblem(runId: "3f1c2b7a-0000-4000-8000-0000000000a2", factory: "tallyhand");
        _problems.Add(elsewhere);

        var notOpen = await Handle("warmforward", resolved.RunId);
        var notThisFactory = await Handle("warmforward", elsewhere.RunId);

        Assert.Equal(HttpStatusCode.NotFound, notOpen.StatusCode);
        Assert.Contains("has no open problem on run", await ErrorOf(notOpen));
        Assert.Equal(HttpStatusCode.NotFound, notThisFactory.StatusCode);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(Caller.DirectorDeviceKey)]
    [InlineData(Caller.MachineToken)]
    public async Task Handle_ACredentialThatIsNotTheOwnersBrowserOrPhone_Is403_AndNothingIsStarted(Caller caller)
    {
        await StartAsync(caller: caller);
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Handle_TheFactorySwitchOff_Is404WithASentence_AndNothingIsStarted()
    {
        await StartAsync(switchOn: false);
        var problem = ScoutProblem();
        _problems.Add(problem);

        var response = await Handle("warmforward", problem.RunId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("switched off", await ErrorOf(response));
        Assert.Empty(_sent);
    }

    [Fact]
    public void Handle_ASessionsOwnKey_NeverReachesTheRoute()
        => Assert.False(SessionKeyGuard.Check("POST", "/gateway/factory-agents/factories/warmforward/problems/x/handle").Allowed,
            "Handle the issue opens the owner's own talk; a session must not be able to start one");
}
