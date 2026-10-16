using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Registry;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CcDirector.Gateway.Tests.Factory.Triggers;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// ONE SOURCE OF TRUTH, THROUGH THE ROUTES AS THEY ARE MAPPED (Factory Definition mission, phase 4; design section 8).
/// Once a factory has a published definition, every route that would define it another way - register it, set its
/// purpose, add, edit, delete, link or unlink one of its schedules, add, edit or delete one of its triggers - answers
/// 409 with the one sentence and the outcome <c>DefinedByDefinition</c>, and writes nothing. Every route that OPERATES
/// it still works: a schedule switched on and off, a trigger paused and resumed, a schedule run now, the factory
/// archived and restored, a goal number posted.
///
/// These go through the real Map functions of the schedule, run, trigger, registry and owner-action routes, in memory
/// (the endpoint is found by matching verb and path against its own template, and run with a JSON body; no port is
/// opened), on real stores over one SQLite database, with the real <see cref="FactoryDefinitionStore.IsDefined"/>
/// wired exactly as the host wires it. A factory is made defined the one way it can be: a definition published
/// through the definition store.
///
/// Goal 14: the tests named <c>..._UNDEFINED_FACTORY_...</c> run every refused route against a factory with no
/// definition and against one with only a DRAFT, and each behaves as on main.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class DefinedByDefinitionRoutesThroughThePipelineTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Defined = DefinitionFixture.FactoryId;   // devthrottle
    private const string Other = "warmforward";
    private const string Sentence = "This factory is defined by its definition; edit the definition.";

    /// <summary>How the factory under test stands with its definition.</summary>
    public enum Standing { NoDefinition, DraftOnly, Published }

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly CronJobStore _schedules;
    private readonly FactoryRegistryStore _registry;
    private readonly FactoryDefinitionStore _definitions;
    private readonly TriggerStore _triggers;
    private int _starts;

    public DefinedByDefinitionRoutesThroughThePipelineTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        _schedules = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        _registry = new FactoryRegistryStore(_db);
        _definitions = new FactoryDefinitionStore(_db);
        _triggers = new TriggerStore(_db);
        var record = new FactoryActivityRecord(_db);
        var settings = new TenantSettingsStore(_db);
        var runs = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var history = new SessionHistoryStore(_db);
        var runRecords = new CronRunRecordReader(runs, history.EndingsOf);

        // The host's wiring of the one answer, verbatim in shape: the account, then the definition store.
        CronJobEndpoints.Map(_routes, _schedules,
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown,
            findFactory: (_, id) => _registry.Find(T, id),
            runRecords: runRecords,
            accountZoneOf: _ => TimeZoneInfo.Utc,
            isDefined: (_, factory) => _definitions.IsDefined(T, factory));
        var engine = new CronEngine(_schedules, runs, new CountingStarter(() => _starts++), new UnusedWorkListRunner(),
            new NullCronNotifier(), new FixedClock(Now));
        CronRunEndpoints.Map(_routes, engine, runRecords, jobById: id => _schedules.Get(id),
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown, accountZoneOf: _ => TimeZoneInfo.Utc, runLifetime: CancellationToken.None);

        var service = new TriggerService(_triggers, record,
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no machine in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true, definitionOf: (_, _) => null);
        TriggerEndpoints.Map(_routes, _ => T, service, directorMachine: (_, _) => "SOREN_NORTH", nowUtc: () => Now,
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown,
            isDefined: _definitions.IsDefined,
            dueChecks: DueCheckTestParts.NoDueChecks(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".due.json")));

        FactoryRegistryEndpoints.Map(_routes, _ => T, _registry, sessionStart: (_, _) => null, nowUtc: () => Now,
            isDefined: _definitions.IsDefined);

        var sources = new FactoriesScreenSources(
            new FactoryAgentsSources(
                Query: (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
                Append: (tenant, request, actor) => record.Append(tenant, request, actor),
                Triggers: _ => Array.Empty<FactoryTriggerFacts>(),
                SetTriggerPaused: (_, _, _, _, _) => Task.FromResult(false),
                LiveSessionIds: _ => new HashSet<string>(),
                TimeZone: _ => TimeZoneInfo.Utc,
                NowUtc: () => Now,
                Reports: new FactoryReportStore(settings),
                Maps: new FactoryMapStore(settings)),
            _registry,
            tenant => _schedules.ListAll(tenant),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f });
        FactoryOwnerActionEndpoints.Map(_routes, new FactoryAgentsSwitch(true, settings), _ => T, sources,
            appendRows: (tenant, rows, actor) => record.Append(tenant, rows, actor),
            setScheduleEnabled: (tenant, id, enabled) => _schedules.SetEnabled(tenant, id, enabled),
            placeWindows: (_, _) => Array.Empty<string>());
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    // ---------- the in-memory pipeline ----------

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    /// <summary>The owner in their own browser: a person, so every factory-naming gate lets the call through.</summary>
    private static void AsTheOwner(HttpContext ctx)
    {
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-1", T.Value, "browser", "active");
    }

    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
        // A literal segment beats a parameter, as the real router ranks them: "/cron/jobs/{id}/run" and the like.
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                     .OrderBy(e => e.RoutePattern.Parameters.Count))
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (matcher.TryMatch(path, values)) return (endpoint, values);
        }
        return null;
    }

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, string? json = null)
    {
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.RouteValues = matched!.Value.Values;
        var bytes = Encoding.UTF8.GetBytes(json ?? "");
        ctx.Request.Body = new MemoryStream(bytes);
        if (json is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();
        AsTheOwner(ctx);

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }

    private static string Json(object body) => JsonSerializer.Serialize(body, Web);

    private static void AssertRefused((int Status, JsonElement Body) answer)
    {
        Assert.Equal(409, answer.Status);
        Assert.Equal(Sentence, answer.Body.GetProperty("error").GetString());
        Assert.Equal("DefinedByDefinition", answer.Body.GetProperty("outcome").GetString());
    }

    private static void AssertNotRefusedByTheDefinition((int Status, JsonElement Body) answer, int expected)
    {
        Assert.True(answer.Status == expected, $"expected {expected}, got {answer.Status}: {answer.Body}");
        Assert.False(answer.Body.ValueKind == JsonValueKind.Object && answer.Body.TryGetProperty("outcome", out var o)
                     && o.GetString() == "DefinedByDefinition");
    }

    // ---------- the world each test starts from ----------

    private static RegisterFactoryRequest Manifest(string factory, string title) => new()
    {
        Factory = factory,
        Title = title,
        Folder = $@"D:\factories\{factory}",
        Computer = "SOREN_NORTH",
        BossSeat = "boss",
        GoalText = "Ship the product.",
        Seats =
        {
            new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "seats/boss.yaml" },
            new FactorySeatManifest { Id = "outreach", Name = "Outreach", Role = "Sales", BriefFile = "seats/outreach.yaml" },
        },
    };

    /// <summary>
    /// Both factories registered, each with an enabled schedule on its boss seat (<c>cj_dt</c>, <c>cj_wf</c>) and a
    /// trigger (<c>dt-inbox</c>, <c>wf-inbox</c>), one plain schedule in no factory (<c>cj_plain</c>), and the
    /// definition of <see cref="Defined"/> standing as <paramref name="standing"/> says. The live rows come first,
    /// as on a real Gateway: the factory ran before it had a definition.
    /// </summary>
    private void Seed(Standing standing)
    {
        ScheduleLinkSeed.Link(_db, T, Defined, "boss", "cj_dt", name: "DevThrottle - Boss");
        ScheduleLinkSeed.Link(_db, T, Other, "boss", "cj_wf", name: "WarmForward - Boss");
        ScheduleLinkSeed.Link(_db, T, factory: null, seat: null, "cj_plain", name: "Daily job search");
        _registry.Register(T, Manifest(Defined, "DevThrottle"), "the owner (test)", Now.AddDays(-5));
        _registry.Register(T, Manifest(Other, "WarmForward"), "the owner (test)", Now.AddDays(-5));
        Trigger("dt-inbox", Defined);
        Trigger("wf-inbox", Other);

        if (standing == Standing.NoDefinition) return;
        Assert.True(_definitions.SaveDraft(T, Defined, DefinitionFixture.Valid(), 0, FactoryMemoryAuthorKinds.Person,
            "owner", "first", Now).Ok);
        if (standing == Standing.Published)
            Assert.True(_definitions.Publish(T, Defined, 1, FactoryMemoryAuthorKinds.Person, "owner", Now).Ok);
        Assert.Equal(standing == Standing.Published, _definitions.IsDefined(T, Defined));
    }

    private void Trigger(string name, string factory)
    {
        var (created, error) = _triggers.Create(T, new TriggerDefinitionRequest
        {
            Name = name, Factory = factory, FactoryAgent = "outreach", Machine = "SOREN_NORTH", RepoPath = $@"D:\factories\{factory}",
            CheckCommand = "python check.py", IntervalSeconds = 300, Prompt = "There are {count} new.",
        }, "the owner (test)", Now.AddDays(-5));
        Assert.True(created is not null, error);
    }

    /// <summary>The schedule as `cc-devthrottle schedule get` reads it - the JSON the command line re-sends.</summary>
    private async Task<JsonObject> ScheduleJson(string id)
    {
        var got = await Send("GET", $"/cron/jobs/{id}");
        Assert.Equal(200, got.Status);
        return JsonNode.Parse(got.Body.GetRawText())!.AsObject();
    }

    private static string NewSchedule(string factory) => Json(new
    {
        name = "Another start",
        scheduleKind = "recurring",
        cronExpression = "0 9 * * *",
        timeZoneId = "UTC",
        factory,
        seat = "outreach",
        target = new { machine = "SOREN_NORTH" },
        action = new { repoPath = $@"D:\factories\{factory}", seed = "Go." },
    });

    private static string NewTrigger(string factory) => Json(new
    {
        name = "another-trigger", factory, factoryAgent = "outreach", machine = "SOREN_NORTH",
        repoPath = $@"D:\factories\{factory}", checkCommand = "python check.py", intervalSeconds = 300, prompt = "{count}",
    });

    private CronJobDto Job(string id) => _schedules.Get(id) ?? throw new InvalidOperationException($"{id} is gone");

    // ---------- refused, for a defined factory ----------

    [Fact]
    public async Task Register_ADefinedFactory_IsRefused_AndTheRegistrationIsUnchanged()
    {
        Seed(Standing.Published);
        var manifest = Manifest(Defined, "Renamed By Hand");

        var answer = await Send("PUT", "/gateway/factory/registry", Json(manifest));

        AssertRefused(answer);
        Assert.Equal("DevThrottle", _registry.Find(T, Defined)!.Title);
    }

    [Fact]
    public async Task Register_ADefinedFactorySpelledAnotherWay_IsRefused()
    {
        Seed(Standing.Published);

        var answer = await Send("PUT", "/gateway/factory/registry", Json(Manifest("DevThrottle", "Renamed By Hand")));

        AssertRefused(answer);
        Assert.Equal("DevThrottle", _registry.Find(T, Defined)!.Title);
    }

    [Fact]
    public async Task Purpose_OfADefinedFactory_IsRefused_AndThePurposeIsUnchanged()
    {
        Seed(Standing.Published);

        var answer = await Send("PUT", $"/gateway/factory/registry/{Defined}/purpose", Json(new { purpose = "By hand" }));

        AssertRefused(answer);
        Assert.Null(_registry.Find(T, Defined)!.Purpose);
    }

    [Fact]
    public async Task CreateSchedule_NamingADefinedFactory_IsRefused_AndNothingIsCreated()
    {
        Seed(Standing.Published);
        var before = _schedules.ListAll(T).Count;

        var answer = await Send("POST", "/cron/jobs", NewSchedule(Defined));

        AssertRefused(answer);
        Assert.Equal(before, _schedules.ListAll(T).Count);
    }

    /// <summary>Every field of a definition the command line or the Cockpit could change in a PUT, one at a time,
    /// each to a value the schedule's own validation accepts - so what answers is this rule, not a 400.</summary>
    public static IEnumerable<object[]> EveryEditableField() => new (string Field, Action<JsonObject> Change)[]
    {
        ("name", j => j["name"] = "Renamed"),
        ("cronExpression", j => j["cronExpression"] = "0 9 * * *"),
        ("timeZoneId", j => j["timeZoneId"] = "Europe/Copenhagen"),
        ("factory", j => j["factory"] = Other),
        ("seat", j => j["seat"] = "outreach"),
        ("target.machine", j => j["target"]!["machine"] = "SOREN_SOUTH"),
        ("action.repoPath", j => j["action"]!["repoPath"] = @"D:\elsewhere"),
        ("action.seed", j => j["action"]!["seed"] = "Another prompt."),
        ("action.workListName", j => j["action"]!["workListName"] = "inbox"),
        ("action.autoDismiss", j => j["action"]!["autoDismiss"] = false),
        ("preventOverlap", j => j["preventOverlap"] = false),
        ("notifyOn", j => j["notifyOn"] = "failure"),
        ("notifyWebhookUrl", j => j["notifyWebhookUrl"] = "https://example.com/hook"),
        ("scheduleKind and runAt", j => { j["scheduleKind"] = "oneOff"; j["runAt"] = "2026-11-01T08:00"; j["cronExpression"] = null; }),
    }.Select(f => new object[] { f.Field, f.Change });

    [Fact]
    public void EveryEditableField_IsEveryDefinitionFieldOfTheDto()
    {
        // The same guard as the switch rule's own list: a field added to the DTO is a definition field, and this
        // theory must change it through the route before it is proven refused.
        var touched = EveryEditableField()
            .SelectMany(row => ((string)row[0]).Split(" and "))
            .Select(field => string.Join('.', field.Split('.').Select(part => char.ToUpperInvariant(part[0]) + part[1..])))
            .OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(DefinedByDefinitionTests.DefinitionFieldsOfTheDto(), touched);
    }

    [Theory]
    [MemberData(nameof(EveryEditableField))]
    public async Task EditSchedule_OfADefinedFactory_AnyDefinitionFieldChanged_IsRefused(string field, Action<JsonObject> change)
    {
        Seed(Standing.Published);
        var before = Job("cj_dt");
        var body = await ScheduleJson("cj_dt");
        change(body);

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        AssertRefused(answer);
        var after = Job("cj_dt");
        Assert.True(DefinedByDefinition.DiffersOnlyInEnabled(before, after) && before.Enabled == after.Enabled, field);
    }

    [Theory]
    [MemberData(nameof(EveryEditableField))]
    public async Task EditSchedule_OfADefinedFactory_AFieldChangedAlongsideASwitch_IsStillRefused(string field, Action<JsonObject> change)
    {
        Seed(Standing.Published);
        var body = await ScheduleJson("cj_dt");
        change(body);
        body["enabled"] = false;

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        AssertRefused(answer);
        Assert.True(Job("cj_dt").Enabled, field);
    }

    [Fact]
    public async Task LinkSchedule_APlainScheduleOntoADefinedFactory_IsRefused_AndItStaysInNoFactory()
    {
        Seed(Standing.Published);
        // What `schedule link` sends: the schedule as read, with the factory and the seat set.
        var body = await ScheduleJson("cj_plain");
        body["factory"] = Defined;
        body["seat"] = "outreach";

        var answer = await Send("PUT", "/cron/jobs/cj_plain", body.ToJsonString());

        AssertRefused(answer);
        Assert.Null(Job("cj_plain").Factory);
    }

    [Fact]
    public async Task LinkSchedule_AnotherFactorysScheduleOntoADefinedFactory_IsRefused()
    {
        Seed(Standing.Published);
        var body = await ScheduleJson("cj_wf");
        body["factory"] = Defined;
        body["seat"] = "boss";

        var answer = await Send("PUT", "/cron/jobs/cj_wf", body.ToJsonString());

        AssertRefused(answer);
        Assert.Equal(Other, Job("cj_wf").Factory);
    }

    [Fact]
    public async Task DeleteSchedule_OfADefinedFactory_IsRefused_AndItIsKept()
    {
        Seed(Standing.Published);

        var answer = await Send("DELETE", "/cron/jobs/cj_dt");

        AssertRefused(answer);
        Assert.NotNull(_schedules.Get("cj_dt"));
    }

    [Fact]
    public async Task UnlinkSchedule_OfADefinedFactory_IsRefused_AndItStaysOnItsSeat()
    {
        Seed(Standing.Published);

        var answer = await Send("POST", "/cron/jobs/cj_dt/unlink");

        AssertRefused(answer);
        Assert.Equal((Defined, "boss"), (Job("cj_dt").Factory, Job("cj_dt").Seat));
    }

    [Fact]
    public async Task CreateTrigger_NamingADefinedFactory_IsRefused_AndNothingIsCreated()
    {
        Seed(Standing.Published);

        var answer = await Send("POST", "/triggers", NewTrigger(Defined));

        AssertRefused(answer);
        Assert.Null(_triggers.Find(T, "another-trigger"));
    }

    [Fact]
    public async Task EditTrigger_OfADefinedFactory_IsRefused_AndItIsUnchanged()
    {
        Seed(Standing.Published);

        var answer = await Send("PUT", "/triggers/dt-inbox", Json(new { prompt = "Edited by hand." }));

        AssertRefused(answer);
        Assert.Equal("There are {count} new.", _triggers.Find(T, "dt-inbox")!.Prompt);
    }

    [Fact]
    public async Task EditTrigger_MovingAnotherFactorysTriggerOntoADefinedFactory_IsRefused()
    {
        Seed(Standing.Published);

        var answer = await Send("PUT", "/triggers/wf-inbox", Json(new { factory = Defined }));

        AssertRefused(answer);
        Assert.Equal(Other, _triggers.Find(T, "wf-inbox")!.Factory);
    }

    [Fact]
    public async Task DeleteTrigger_OfADefinedFactory_IsRefused_AndItIsKept()
    {
        Seed(Standing.Published);

        var answer = await Send("DELETE", "/triggers/dt-inbox");

        AssertRefused(answer);
        Assert.NotNull(_triggers.Find(T, "dt-inbox"));
    }

    // ---------- still allowed, for a defined factory ----------

    [Fact]
    public async Task DisableThenEnable_ASchedulesOfADefinedFactory_BothWork_TheWayTheCommandLineSendsThem()
    {
        Seed(Standing.Published);

        // `schedule disable`: read the job, set enabled, send it all back.
        var off = await ScheduleJson("cj_dt");
        off["enabled"] = false;
        var disabled = await Send("PUT", "/cron/jobs/cj_dt", off.ToJsonString());
        AssertNotRefusedByTheDefinition(disabled, 200);
        Assert.False(Job("cj_dt").Enabled);

        var on = await ScheduleJson("cj_dt");
        on["enabled"] = true;
        var enabled = await Send("PUT", "/cron/jobs/cj_dt", on.ToJsonString());
        AssertNotRefusedByTheDefinition(enabled, 200);
        Assert.True(Job("cj_dt").Enabled);
    }

    // ---------- a defined factory's schedule has no folder (Factory Definition, phase 5) ----------

    /// <summary>Make cj_dt as a phase 5 publish writes it: with no folder.</summary>
    private void WithNoFolder(string id)
    {
        using var ctx = _db.CreateContext(T);
        var row = ctx.CronJobs.Single(j => j.Id == id);
        row.Action = new CronJobAction { RepoPath = "", Seed = row.Action.Seed, AutoDismiss = row.Action.AutoDismiss };
        ctx.SaveChanges();
    }

    [Fact]
    public async Task DisableThenEnable_AScheduleOfADefinedFactoryWithNoFolder_BothWork()
    {
        Seed(Standing.Published);
        WithNoFolder("cj_dt");

        var off = await ScheduleJson("cj_dt");
        off["enabled"] = false;
        AssertNotRefusedByTheDefinition(await Send("PUT", "/cron/jobs/cj_dt", off.ToJsonString()), 200);
        Assert.False(Job("cj_dt").Enabled);

        var on = await ScheduleJson("cj_dt");
        on["enabled"] = true;
        AssertNotRefusedByTheDefinition(await Send("PUT", "/cron/jobs/cj_dt", on.ToJsonString()), 200);
        Assert.True(Job("cj_dt").Enabled);
        Assert.Equal("", Job("cj_dt").Action.RepoPath);
        // When it fires does not depend on where: it still has its next run.
        Assert.NotNull(Job("cj_dt").NextRunUtc);
    }

    [Fact]
    public async Task CreateSchedule_NamingADefinedFactory_WithNoFolder_IsRefusedInTheDefinitionsWords()
    {
        Seed(Standing.Published);
        var body = JsonNode.Parse(NewSchedule(Defined))!.AsObject();
        body["action"]!["repoPath"] = "";

        AssertRefused(await Send("POST", "/cron/jobs", body.ToJsonString()));
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task CreateSchedule_UNDEFINED_FACTORY_WithNoFolder_IsRefusedAsOnMain(Standing standing)
    {
        Seed(standing);
        var body = JsonNode.Parse(NewSchedule(Defined))!.AsObject();
        body["action"]!["repoPath"] = "";

        var answer = await Send("POST", "/cron/jobs", body.ToJsonString());

        Assert.Equal(400, answer.Status);
        Assert.Equal("action.repoPath is required", answer.Body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task EditSchedule_UNDEFINED_FACTORY_ToNoFolder_IsRefusedAsOnMain(Standing standing)
    {
        Seed(standing);
        var body = await ScheduleJson("cj_dt");
        body["action"]!["repoPath"] = "";

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        Assert.Equal(400, answer.Status);
        Assert.Equal("action.repoPath is required", answer.Body.GetProperty("error").GetString());
        Assert.NotEqual("", Job("cj_dt").Action.RepoPath);
    }

    // ---------- what the answer tells a client it may offer (phase 5b, carried from phase 4's Finding 4) ----------

    [Fact]
    public async Task Disable_ASchedulesOfADefinedFactory_AnswerSaysTheDefinitionWritesIt()
    {
        Seed(Standing.Published);
        var off = await ScheduleJson("cj_dt");
        off["enabled"] = false;

        var disabled = await Send("PUT", "/cron/jobs/cj_dt", off.ToJsonString());

        AssertNotRefusedByTheDefinition(disabled, 200);
        // `schedule disable` offers its next steps from this: the switch back on, never the delete that is refused.
        Assert.True(disabled.Body.GetProperty("definedByDefinition").GetBoolean());
        Assert.True((await Send("GET", "/cron/jobs/cj_dt")).Body.GetProperty("definedByDefinition").GetBoolean());
        AssertRefused(await Send("DELETE", "/cron/jobs/cj_dt"));
    }

    [Fact]
    public async Task ListSchedules_ADefinedFactoryBesideOthers_OnlyItsScheduleSaysTheDefinitionWritesIt()
    {
        Seed(Standing.Published);

        var list = await Send("GET", "/cron/jobs");

        Assert.Equal(200, list.Status);
        var defined = list.Body.GetProperty("jobs").EnumerateArray()
            .ToDictionary(j => j.GetProperty("id").GetString()!, j => j.GetProperty("definedByDefinition").GetBoolean());
        Assert.True(defined["cj_dt"]);
        Assert.False(defined["cj_wf"]);
        Assert.False(defined["cj_plain"]);
    }

    private static string? PathTextOf(JsonElement job)
        => job.GetProperty("pathText").ValueKind == JsonValueKind.Null ? null : job.GetProperty("pathText").GetString();

    [Fact]
    public async Task ListSchedules_ADefinedFactorysScheduleWithNoFolder_SaysItsRunIsBuiltFromTheDefinition()
    {
        // QA finding 5: the command line refused the list on this schedule's empty folder. The Gateway now says, in its
        // own words, what goes where the folder goes, on the list and on the schedule's own read.
        Seed(Standing.Published);
        WithNoFolder("cj_dt");

        var list = await Send("GET", "/cron/jobs");

        Assert.Equal(200, list.Status);
        var text = list.Body.GetProperty("jobs").EnumerateArray()
            .ToDictionary(j => j.GetProperty("id").GetString()!, PathTextOf);
        Assert.Equal("built from the factory's definition", text["cj_dt"]);
        Assert.Null(text["cj_wf"]);
        Assert.Null(text["cj_plain"]);
        Assert.Equal("built from the factory's definition", PathTextOf((await Send("GET", "/cron/jobs/cj_dt")).Body));
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task ListSchedules_UNDEFINED_FACTORY_NoScheduleCarriesPathText(Standing standing)
    {
        Seed(standing);

        var list = await Send("GET", "/cron/jobs");

        Assert.Equal(200, list.Status);
        Assert.All(list.Body.GetProperty("jobs").EnumerateArray(), j => Assert.Null(PathTextOf(j)));
    }

    [Fact]
    public async Task Disable_ASchedulesOfADefinedFactory_WithTheFactoryAndSeatLeftBlank_Works()
    {
        Seed(Standing.Published);
        var off = await ScheduleJson("cj_dt");
        off["enabled"] = false;
        off["factory"] = null;
        off["seat"] = "";

        var disabled = await Send("PUT", "/cron/jobs/cj_dt", off.ToJsonString());

        AssertNotRefusedByTheDefinition(disabled, 200);
        Assert.Equal((false, Defined, "boss"), (Job("cj_dt").Enabled, Job("cj_dt").Factory, Job("cj_dt").Seat));
    }

    /// <summary>The Cockpit's pause, held once for this suite and client-core's (cronJobPauseBody.fixture.json).</summary>
    private static JsonElement CockpitPauseFixture([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Factory/Definition/<this>.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", ".."));
        var path = Path.Combine(root, "packages", "client-core", "src", "schedule", "cronJobPauseBody.fixture.json");
        Assert.True(File.Exists(path), $"Resolved the repository root to {root}, but it has no {path}. Run the suite from a checkout.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Disable_ASchedulesOfADefinedFactory_WithTheBodyTheCockpitSends_Works()
    {
        Seed(Standing.Published);
        // The schedule stored as the fixture says it was read, then the very body client-core's test proves the
        // Cockpit sends for its pause.
        var fixture = CockpitPauseFixture();
        var stored = fixture.GetProperty("stored").Deserialize<CronJobDto>(Web)!;
        Assert.NotNull(_schedules.Update("cj_dt", stored));

        var answer = await Send("PUT", "/cron/jobs/cj_dt", fixture.GetProperty("pauseBody").GetRawText());

        AssertNotRefusedByTheDefinition(answer, 200);
        var after = Job("cj_dt");
        Assert.Equal((false, Defined, "boss", false), (after.Enabled, after.Factory, after.Seat, after.Action.AutoDismiss));
    }

    [Fact]
    public async Task PauseThenResume_ATriggerOfADefinedFactory_BothWork()
    {
        Seed(Standing.Published);

        AssertNotRefusedByTheDefinition(await Send("POST", "/triggers/dt-inbox/pause"), 200);
        Assert.True(_triggers.Find(T, "dt-inbox")!.Paused);
        AssertNotRefusedByTheDefinition(await Send("POST", "/triggers/dt-inbox/resume"), 200);
        Assert.False(_triggers.Find(T, "dt-inbox")!.Paused);
    }

    [Fact]
    public async Task RunNow_AScheduleOfADefinedFactory_StartsIt()
    {
        Seed(Standing.Published);

        var answer = await Send("POST", "/cron/jobs/cj_dt/run");

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal(1, _starts);
    }

    [Fact]
    public async Task RunNow_AScheduleWithACheck_AnswersTaskStatusChecking_NotUnknown()
    {
        // Arrange: cj_dt as a publish writes a timed start with a check. The quality assurance report's finding 3:
        // `schedule run` printed Task "unknown" for a run that is waiting on its check.
        Seed(Standing.Published);
        using (var ctx = _db.CreateContext(T))
        {
            ctx.CronJobs.Single(j => j.Id == "cj_dt").CheckPath = "checks/new_mail.py";
            ctx.SaveChanges();
        }

        // Act
        var answer = await Send("POST", "/cron/jobs/cj_dt/run");

        // Assert: the fire is a due check, so no session started, and the answer says what the run is doing.
        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal(0, _starts);
        Assert.Equal((CronRunResults.Checking, "checking"),
            (answer.Body.GetProperty("result").GetString(), answer.Body.GetProperty("taskStatus").GetString()));
    }

    [Fact]
    public async Task ArchiveThenRestore_ADefinedFactory_BothWork()
    {
        Seed(Standing.Published);
        var confirm = Json(new { schedules = new[] { "cj_dt" } });

        var archived = await Send("POST", $"/gateway/factories/{Defined}/archive", confirm);
        AssertNotRefusedByTheDefinition(archived, 200);
        Assert.NotNull(_registry.Find(T, Defined)!.ArchivedAtUtc);
        Assert.False(Job("cj_dt").Enabled);

        var restored = await Send("POST", $"/gateway/factories/{Defined}/restore", confirm);
        AssertNotRefusedByTheDefinition(restored, 200);
        Assert.Null(_registry.Find(T, Defined)!.ArchivedAtUtc);
        Assert.True(Job("cj_dt").Enabled);
    }

    [Fact]
    public async Task GoalNumber_ForADefinedFactory_IsPosted()
    {
        Seed(Standing.Published);

        var answer = await Send("POST", "/gateway/factory/goal-numbers", Json(new
        {
            factory = Defined, value = "3", unit = "customers", asOf = "2026-10-14", link = "https://example.com/report", postedBy = "boss",
        }));

        AssertNotRefusedByTheDefinition(answer, 201);
        Assert.Single(_registry.GoalNumbers(T, Defined, 10).Posts);
    }

    // ---------- another factory is unaffected by a defined one existing ----------

    [Fact]
    public async Task AnotherUndefinedFactory_ItsSchedulesAndTriggers_AreWrittenAsOnMain_BesideADefinedFactory()
    {
        Seed(Standing.Published);

        var body = await ScheduleJson("cj_wf");
        body["action"]!["seed"] = "Edited by hand.";
        AssertNotRefusedByTheDefinition(await Send("PUT", "/cron/jobs/cj_wf", body.ToJsonString()), 200);
        Assert.Equal("Edited by hand.", Job("cj_wf").Action.Seed);
        AssertNotRefusedByTheDefinition(await Send("POST", "/cron/jobs", NewSchedule(Other)), 201);
        AssertNotRefusedByTheDefinition(await Send("POST", "/cron/jobs/cj_wf/unlink"), 200);
        AssertNotRefusedByTheDefinition(await Send("DELETE", "/cron/jobs/cj_wf"), 200);
        AssertNotRefusedByTheDefinition(await Send("PUT", "/triggers/wf-inbox", Json(new { prompt = "Edited." })), 200);
        AssertNotRefusedByTheDefinition(await Send("DELETE", "/triggers/wf-inbox"), 200);
        AssertNotRefusedByTheDefinition(await Send("PUT", $"/gateway/factory/registry/{Other}/purpose", Json(new { purpose = "Warm homes" })), 200);
        AssertNotRefusedByTheDefinition(await Send("PUT", "/gateway/factory/registry", Json(Manifest(Other, "WarmForward Two"))), 200);
        Assert.Equal("WarmForward Two", _registry.Find(T, Other)!.Title);
    }

    // ---------- goal 14: an undefined factory, and one with only a draft, take today's paths ----------

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task Register_UNDEFINED_FACTORY_IsRegisteredAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("PUT", "/gateway/factory/registry", Json(Manifest(Defined, "Renamed By Hand")));

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal("Renamed By Hand", _registry.Find(T, Defined)!.Title);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task Purpose_UNDEFINED_FACTORY_IsSetAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("PUT", $"/gateway/factory/registry/{Defined}/purpose", Json(new { purpose = "By hand" }));

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal("By hand", _registry.Find(T, Defined)!.Purpose);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task CreateSchedule_UNDEFINED_FACTORY_IsCreatedAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("POST", "/cron/jobs", NewSchedule(Defined));

        AssertNotRefusedByTheDefinition(answer, 201);
        Assert.Contains(_schedules.ListAll(T), j => j.Name == "Another start" && j.Factory == Defined);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task EditSchedule_UNDEFINED_FACTORY_IsEditedAsOnMain(Standing standing)
    {
        Seed(standing);
        var body = await ScheduleJson("cj_dt");
        body["action"]!["seed"] = "Edited by hand.";

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal("Edited by hand.", Job("cj_dt").Action.Seed);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task DisableSchedule_UNDEFINED_FACTORY_IsSwitchedAsOnMain(Standing standing)
    {
        Seed(standing);
        var body = await ScheduleJson("cj_dt");
        body["enabled"] = false;

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.False(Job("cj_dt").Enabled);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task DisableSchedule_UNDEFINED_FACTORY_AnswerDoesNotSayTheDefinitionWritesIt(Standing standing)
    {
        Seed(standing);
        var body = await ScheduleJson("cj_dt");
        body["enabled"] = false;

        var answer = await Send("PUT", "/cron/jobs/cj_dt", body.ToJsonString());

        AssertNotRefusedByTheDefinition(answer, 200);
        // So `schedule disable` keeps offering the delete, which is allowed here, as on main.
        Assert.False(answer.Body.GetProperty("definedByDefinition").GetBoolean());
        Assert.False((await Send("GET", "/cron/jobs/cj_plain")).Body.GetProperty("definedByDefinition").GetBoolean());
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task LinkSchedule_UNDEFINED_FACTORY_IsLinkedAsOnMain(Standing standing)
    {
        Seed(standing);
        var body = await ScheduleJson("cj_plain");
        body["factory"] = Defined;
        body["seat"] = "outreach";

        var answer = await Send("PUT", "/cron/jobs/cj_plain", body.ToJsonString());

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal((Defined, "outreach"), (Job("cj_plain").Factory, Job("cj_plain").Seat));
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task UnlinkSchedule_UNDEFINED_FACTORY_IsTakenOffAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("POST", "/cron/jobs/cj_dt/unlink");

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Null(Job("cj_dt").Factory);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task DeleteSchedule_UNDEFINED_FACTORY_IsDeletedAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("DELETE", "/cron/jobs/cj_dt");

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Null(_schedules.Get("cj_dt"));
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task CreateTrigger_UNDEFINED_FACTORY_IsCreatedAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("POST", "/triggers", NewTrigger(Defined));

        AssertNotRefusedByTheDefinition(answer, 201);
        Assert.NotNull(_triggers.Find(T, "another-trigger"));
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task EditTrigger_UNDEFINED_FACTORY_IsEditedAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("PUT", "/triggers/dt-inbox", Json(new { prompt = "Edited by hand." }));

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Equal("Edited by hand.", _triggers.Find(T, "dt-inbox")!.Prompt);
    }

    [Theory]
    [InlineData(Standing.NoDefinition)]
    [InlineData(Standing.DraftOnly)]
    public async Task DeleteTrigger_UNDEFINED_FACTORY_IsDeletedAsOnMain(Standing standing)
    {
        Seed(standing);

        var answer = await Send("DELETE", "/triggers/dt-inbox");

        AssertNotRefusedByTheDefinition(answer, 200);
        Assert.Null(_triggers.Find(T, "dt-inbox"));
    }

    // ---------- fakes ----------

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class CountingStarter(Action onStart) : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
        {
            onStart();
            return Task.FromResult<(string?, string?, string?)>(("sid-run-now", "dir-1", null));
        }
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("these tests never run a work list");
    }
}
