using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// A SEAT'S BRIEF THROUGH THE DEFINITION (Factory Definition, phase 4, design section 9), over the routes AS THEY ARE
/// MAPPED - the brief's own routes and the seat page's views - in memory, against the real brief store, the real
/// definition store, the real registry and the real whole-definition check on one database. No port is opened.
///
/// For a DEFINED factory every route answers from the definition and says so; a save writes a new definition draft
/// with only the seat's brief changed; a publish or restore is sent to the definition. For a factory with NO published
/// definition - none at all, or only drafts - every route behaves exactly as on main (goal 14): the tests named
/// <c>..._UNDEFINED_FACTORY_...</c> prove it, each against both kinds.
///
/// Every refusal is proven by the answer that carries it: its status and its sentence.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryBriefsThroughTheDefinitionTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string FactorySession = "75000000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = TenantId.Local;

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryRegistryStore _registry;
    private readonly FactoryBriefStore _briefs;
    private readonly FactoryDefinitionStore _definitions;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;

    /// <summary>The account's fleet skills as the check is told them; a test may take one away.</summary>
    private readonly List<string> _fleetSkills = DefinitionFixture.FleetSkills.ToList();

    public FactoryBriefsThroughTheDefinitionTests()
    {
        _db = _h.Open();
        _registry = new FactoryRegistryStore(_db);
        _registry.Register(A, new RegisterFactoryRequest
        {
            Factory = TheFactory,
            Title = "DevThrottle",
            Folder = "/home/soren/devthrottle-factory",
            Computer = "SOREN_NORTH",
            BossSeat = "boss",
            Seats =
            {
                new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
                new FactorySeatManifest { Id = "writer", Name = "Writer", Role = "Writer", BriefFile = "agents/writer.yaml" },
            },
        }, "the owner (test)", Now.AddDays(-2));
        _briefs = new FactoryBriefStore(_db, _registry);
        _definitions = new FactoryDefinitionStore(_db);
        var defined = new FactoryDefinitionBriefs(_definitions, _briefs);

        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        FactoryBriefEndpoints.Map(_routes,
            resolveTenant: _ => A,
            store: _briefs,
            sessionFactoryOf: _ => SessionFactoryLookup.In(TheFactory),
            nowUtc: () => Now,
            defined: new FactoryBriefEndpoints.Defined(defined,
                new FactoryDefinitionEndpoints.Account(_ => _fleetSkills.ToList(), _ => DefinitionFixture.Machines),
                new FactoryPublishApply(_db)));

        var schedules = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var record = new FactoryActivityRecord(_db);
        var settings = new TenantSettingsStore(_db);
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
            tenant => schedules.ListAll(tenant),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f });
        FactoriesScreenEndpoints.MapSeatPage(_routes, _ => A, sources, (_, _) => 0, _briefs, defined);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    // ---------- the factory's definition ----------

    /// <summary>Version 1 of the fixture's definition as a publish takes it (no start with a check), saved by a person
    /// and, unless told otherwise, published.</summary>
    private void Define(bool publish = true)
    {
        var saved = _definitions.SaveDraft(A, TheFactory, DefinitionFixture.Publishable(), 0, FactoryMemoryAuthorKinds.Person, "owner", null, Now.AddHours(-3));
        Assert.True(saved.Ok, saved.Refusal);
        if (!publish) return;
        var published = _definitions.Publish(A, TheFactory, 1, FactoryMemoryAuthorKinds.Person, "owner", Now.AddHours(-2));
        Assert.True(published.Ok, published.Refusal);
    }

    /// <summary>Publish one definition version straight in the store, as a person: the starting state of a restore.</summary>
    private void PublishDefinition(int version)
    {
        var published = _definitions.Publish(A, TheFactory, version, FactoryMemoryAuthorKinds.Person, "owner", Now.AddMinutes(-30));
        Assert.True(published.Ok, published.Refusal);
    }

    /// <summary>Save the next definition draft with one seat file's brief replaced, as a person.</summary>
    private int SaveDefinitionDraft(string seat, string brief)
    {
        var standing = _definitions.Standing(A, TheFactory);
        var content = new Dictionary<string, string>(_definitions.Find(A, TheFactory, standing.Newest!.Version)!.Files);
        content[$"seats/{seat}.yaml"] = SeatBriefText.Replace($"seats/{seat}.yaml", content[$"seats/{seat}.yaml"], brief).Text!;
        var saved = _definitions.SaveDraft(A, TheFactory, content, standing.Newest.Version, FactoryMemoryAuthorKinds.Person, "owner", null, Now.AddHours(-1));
        Assert.True(saved.Ok, saved.Refusal);
        return saved.Version!.Version;
    }

    // ---------- the pipeline ----------

    /// <summary>The thing routes are mapped onto, with no server behind it: it only collects what was mapped.</summary>
    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static void AsAPerson(HttpContext ctx) => ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";

    private static void AsASessionOfTheFactory(HttpContext ctx) =>
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(FactorySession), A, "dir-a");

    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (matcher.TryMatch(path, values)) return (endpoint, values);
        }
        return null;
    }

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, Action<HttpContext>? who = null, object? body = null)
    {
        var query = path.Contains('?') ? path[path.IndexOf('?')..] : "";
        if (query.Length > 0) path = path[..path.IndexOf('?')];
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.RouteValues = matched!.Value.Values;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();
        (who ?? AsAPerson)(ctx);

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private const string Boss = "/factory-briefs/devthrottle/boss";
    private const string BossView = "/gateway/factories/devthrottle/seats/boss";

    private static readonly string Line = FactoryBriefPanelFold.DefinitionLine(TheFactory);
    private static readonly string ViewLine = FactoryBriefPanelFold.DefinitionLine("DevThrottle");

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // =================================================================================================================
    // A DEFINED factory: every brief route answers from the definition
    // =================================================================================================================

    [Fact]
    public async Task GetPublished_DefinedFactory_IsTheSeatsBriefInThePublishedDefinition_MarkedAsADefinitionVersion()
    {
        Define();
        SaveDefinitionDraft("boss", "A draft nobody published.\n");

        var (status, body) = await Send("GET", Boss);

        Assert.Equal(200, status);
        Assert.Equal("Run the factory.\n", Text(body, "text"));
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal("published", Text(body, "state"));
        Assert.Equal(2, body.GetProperty("newestVersion").GetInt32());
        Assert.Equal(1, body.GetProperty("publishedVersion").GetInt32());
        Assert.Equal("definition", Text(body, "versionKind"));
        Assert.Equal(Line, Text(body, "definition"));
        Assert.Null(_briefs.Standing(A, TheFactory, "boss").Newest);
    }

    [Fact]
    public async Task GetDraft_DefinedFactory_IsTheSeatsBriefInTheNewestDefinitionVersion()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it better.\n");

        var (status, body) = await Send("GET", Boss + "/draft");

        Assert.Equal(200, status);
        Assert.Equal("Run it better.\n", Text(body, "text"));
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal("draft", Text(body, "state"));
        Assert.Equal("definition", Text(body, "versionKind"));
    }

    [Fact]
    public async Task History_DefinedFactory_ListsOnlyTheDefinitionVersionsThatChangedThisSeatsBrief_NewestFirst()
    {
        Define();                                        // v1: both briefs as the fixture writes them
        SaveDefinitionDraft("writer", "Write more.\n");  // v2: only the writer's brief changed
        SaveDefinitionDraft("boss", "Run it better.\n"); // v3: the boss's brief changed

        var (status, body) = await Send("GET", Boss + "/history");
        var (_, writer) = await Send("GET", "/factory-briefs/devthrottle/writer/history");

        Assert.Equal(200, status);
        Assert.Equal(new[] { 3, 1 }, body.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(3, body.GetProperty("newestVersion").GetInt32());
        Assert.Equal(1, body.GetProperty("publishedVersion").GetInt32());
        Assert.Equal("definition", Text(body, "versionKind"));
        Assert.Equal(Line, Text(body, "definition"));
        Assert.Equal(new[] { 2, 1 }, writer.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task History_DefinedFactory_IsPagedAsTheBriefsHistoryIs()
    {
        Define();
        SaveDefinitionDraft("boss", "Two.\n");
        SaveDefinitionDraft("boss", "Three.\n");

        var (_, first) = await Send("GET", Boss + "/history?limit=2");
        var (_, second) = await Send("GET", Boss + "/history?limit=2&before=" + first.GetProperty("nextBefore").GetInt32());

        Assert.Equal(new[] { 3, 2 }, first.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(2, first.GetProperty("nextBefore").GetInt32());
        Assert.Equal(new[] { 1 }, second.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task OneVersion_DefinedFactory_IsTheSeatsBriefInThatDefinitionVersion_AndAMissingOneIs404()
    {
        Define();
        SaveDefinitionDraft("writer", "Write more.\n");

        var (status, body) = await Send("GET", Boss + "/versions/2");
        var (missingStatus, missing) = await Send("GET", Boss + "/versions/9");

        Assert.Equal(200, status);
        Assert.Equal("Run the factory.\n", Text(body, "text"));
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal("definition", Text(body, "versionKind"));
        Assert.Equal(404, missingStatus);
        Assert.Equal("version 9 of the definition of devthrottle does not exist; its newest version is 2", Text(missing, "error"));
    }

    [Fact]
    public async Task GetPublished_ASeatWithNoFileInTheDefinition_Is404_AndSaysHowASeatIsAdded()
    {
        Define();
        _registry.Register(A, new RegisterFactoryRequest
        {
            Factory = TheFactory, Title = "DevThrottle", Folder = "/home/soren/devthrottle-factory", Computer = "SOREN_NORTH", BossSeat = "boss",
            Seats =
            {
                new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
                new FactorySeatManifest { Id = "ghost", Name = "Ghost", Role = "Helper", BriefFile = "agents/ghost.yaml" },
            },
        }, "the owner (test)", Now);

        var (status, body) = await Send("GET", "/factory-briefs/devthrottle/ghost");

        Assert.Equal(404, status);
        Assert.Equal("'ghost' has no seat file in the published definition of devthrottle (version 1), so it has no brief there. " +
                     "A seat of a defined factory is added to its definition as seats/ghost.yaml.", Text(body, "error"));
    }

    // ---------- saving a brief saves a definition draft ----------

    [Fact]
    public async Task SaveDraft_DefinedFactory_SavesANewDefinitionDraft_WithOnlyThatSeatsBriefChanged_EveryOtherByteKept()
    {
        Define();
        var before = _definitions.Find(A, TheFactory, 1)!.Files;

        var (status, body) = await Send("PUT", Boss + "/draft", AsAPerson, new { text = "Run the factory.\nEmail the owner on Fridays.\n", expectedVersion = 1 });

        Assert.Equal(200, status);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal("draft", Text(body, "state"));
        Assert.Equal("Run the factory.\nEmail the owner on Fridays.\n", Text(body, "text"));
        Assert.Equal("definition", Text(body, "versionKind"));
        Assert.Equal(Line, Text(body, "definition"));
        var after = _definitions.Find(A, TheFactory, 2)!;
        Assert.Equal("draft", after.Summary.State);
        Assert.Equal("The brief of 'boss' was edited.", after.Summary.ChangeNote);
        Assert.Equal(before.Keys, after.Files.Keys);
        foreach (var (path, text) in before)
            if (path != "seats/boss.yaml") Assert.Equal(text, after.Files[path]);
        Assert.Equal("""
            seat: boss
            name: Boss
            role: Boss
            brief: |
              Run the factory.
              Email the owner on Fridays.
            starts:
              - cron: "0 8 * * 1-5"
                timeZone: Europe/Copenhagen
                prompt: Morning round.
            rules: [base]
            """.ReplaceLineEndings("\n"), after.Files["seats/boss.yaml"]);
        // The brief store is untouched for a defined factory.
        Assert.Null(_briefs.Standing(A, TheFactory, "boss").Newest);
    }

    [Fact]
    public async Task SaveDraft_DefinedFactory_BySessionOfTheFactory_IsAllowed_AndRecordsTheSession()
    {
        Define();

        var (status, body) = await Send("PUT", Boss + "/draft", AsASessionOfTheFactory, new { text = "Run it.\n", expectedVersion = 1 });

        Assert.Equal(200, status);
        Assert.Equal("session", Text(body, "authorKind"));
        Assert.Equal(FactorySession, Text(body, "authorId"));
        Assert.Equal(2, _definitions.Standing(A, TheFactory).Newest!.Version);
    }

    [Fact]
    public async Task SaveDraft_DefinedFactory_Stale_IsRefusedAsADefinitionSaveIs_WithTheNewestBriefAttached()
    {
        Define();
        SaveDefinitionDraft("boss", "Someone else's text.\n");

        var (status, body) = await Send("PUT", Boss + "/draft", AsAPerson, new { text = "Mine.\n", expectedVersion = 1 });

        Assert.Equal(409, status);
        Assert.Equal("Stale", Text(body, "outcome"));
        Assert.Equal("the definition of devthrottle is at version 2, not 1; read it again, merge, and save once more", Text(body, "error"));
        Assert.Equal("Someone else's text.\n", Text(body.GetProperty("current"), "text"));
        Assert.Equal(2, body.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal(2, _definitions.Standing(A, TheFactory).Newest!.Version);
    }

    [Fact]
    public async Task SaveDraft_DefinedFactory_TheWholeDefinitionCheckRuns_AndAnErrorSavesNothing()
    {
        Define();
        _fleetSkills.Remove("browsers"); // seats/outreach.yaml names it: the whole definition now fails the check

        var (status, body) = await Send("PUT", Boss + "/draft", AsAPerson, new { text = "Run it.\n", expectedVersion = 1 });

        Assert.Equal(422, status);
        Assert.Equal("CheckFailed", Text(body, "outcome"));
        Assert.StartsWith("The brief of 'boss' was not saved: the check found 1 error in the definition of devthrottle.", Text(body, "error"));
        var finding = Assert.Single(body.GetProperty("findings").EnumerateArray(), f => Text(f, "severity") == "error");
        Assert.Equal("seats/outreach.yaml", Text(finding, "file"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Newest!.Version);
    }

    [Fact]
    public async Task SaveDraft_DefinedFactory_EmptyText_IsRefused_AndNothingIsSaved()
    {
        Define();

        var (status, body) = await Send("PUT", Boss + "/draft", AsAPerson, new { text = "   ", expectedVersion = 1 });

        Assert.Equal(400, status);
        Assert.Equal("a brief needs text: a seat told nothing has nothing to do", Text(body, "error"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Newest!.Version);
    }

    // ---------- publishing is the whole definition's act ----------

    [Fact]
    public async Task Publish_DefinedFactory_ByASession_IsRefusedWithThePersonOnlySentence()
    {
        Define();
        await Send("PUT", Boss + "/draft", AsASessionOfTheFactory, new { text = "Run it.\n", expectedVersion = 1 });

        var (status, body) = await Send("POST", Boss + "/publish", AsASessionOfTheFactory, new { version = 2 });

        Assert.Equal(403, status);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonPublishes, Text(body, "error"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Published!.Version);
    }

    [Fact]
    public async Task Publish_DefinedFactory_ByAPerson_PublishesTheWholeDefinitionThroughTheTransaction()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it.\n");

        var (status, body) = await Send("POST", Boss + "/publish", AsAPerson, new { version = 2, replaces = 1 });

        Assert.Equal(200, status);
        Assert.Equal(2, _definitions.Standing(A, TheFactory).Published!.Version);
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.Equal("Run it.\n", Text(body, "text"));
        Assert.Equal(FactoryBriefVersionKinds.Definition, Text(body, "versionKind"));
        Assert.Equal(2, body.GetProperty("publishedVersion").GetInt32());
        // The answer says it is the whole definition that was published, and carries what changes and what was applied.
        Assert.Equal(Line, Text(body, "definition"));
        var applied = body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("Definition: version 2 published.", applied[0]);
        Assert.Contains(applied, a => a!.StartsWith("Registry: ", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, body.GetProperty("changes").ValueKind);
        // The registry now holds what the definition says, written by the same transaction.
        Assert.Contains(_registry.Find(A, TheFactory)!.Seats, s => s.Id == "outreach");
    }

    [Fact]
    public async Task Publish_DefinedFactory_AStaleReplaces_IsRefusedWithTheTransactionsSentence_AndNothingIsPublished()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it.\n");

        var (status, body) = await Send("POST", Boss + "/publish", AsAPerson, new { version = 2, replaces = 0 });

        Assert.Equal(409, status);
        Assert.Equal("Not published: version 1 is the published definition now, not 0. Read what changes again and publish once more.",
            Text(body, "error"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Published!.Version);
    }

    [Fact]
    public async Task Publish_DefinedFactory_AVersionTheCheckRefuses_Is422_AndNothingIsPublished()
    {
        Define();
        // The draft brings back the start with a check, which a publish cannot take yet.
        var content = new Dictionary<string, string>(_definitions.Find(A, TheFactory, 1)!.Files)
        {
            ["seats/outreach.yaml"] = DefinitionFixture.Valid()["seats/outreach.yaml"],
        };
        Assert.True(_definitions.SaveDraft(A, TheFactory, content, 1, FactoryMemoryAuthorKinds.Person, "owner", null, Now.AddHours(-1)).Ok);

        var (status, body) = await Send("POST", Boss + "/publish", AsAPerson, new { version = 2 });

        Assert.Equal(422, status);
        Assert.StartsWith("Version 2 was not published: the check found 1 error in the definition of devthrottle.", Text(body, "error"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Published!.Version);
    }

    [Fact]
    public async Task Publish_DefinedFactory_ASeatTheVersionHasNoFileFor_Is404_AndNothingIsPublished()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it.\n");

        var (status, body) = await Send("POST", "/factory-briefs/devthrottle/ghost/publish", AsAPerson, new { version = 2 });

        Assert.Equal(404, status);
        Assert.Equal(FactoryDefinitionBriefs.NotInVersion(TheFactory, "ghost", 2, "named"), Text(body, "error"));
        Assert.Equal(1, _definitions.Standing(A, TheFactory).Published!.Version);
    }

    [Fact]
    public async Task Restore_DefinedFactory_ByASession_IsRefused_AndByAPerson_PublishesACopyThroughTheTransaction()
    {
        Define();
        var original = _definitions.Find(A, TheFactory, 1)!.Files["seats/boss.yaml"];
        SaveDefinitionDraft("boss", "Run it.\n");
        PublishDefinition(2);

        var (sessionStatus, session) = await Send("POST", Boss + "/restore", AsASessionOfTheFactory, new { version = 1, expectedVersion = 2 });
        var (personStatus, person) = await Send("POST", Boss + "/restore", AsAPerson, new { version = 1, expectedVersion = 2, replaces = 2 });

        Assert.Equal(403, sessionStatus);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonRestores, Text(session, "error"));
        Assert.Equal(200, personStatus);
        var standing = _definitions.Standing(A, TheFactory);
        Assert.Equal(3, standing.Newest!.Version);
        Assert.Equal(3, standing.Published!.Version);
        Assert.Equal(3, person.GetProperty("version").GetInt32());
        Assert.Equal(SeatBriefText.Read("seats/boss.yaml", original), Text(person, "text"));
        Assert.Equal(Line, Text(person, "definition"));
        Assert.Equal("Definition: version 3 written, a copy of version 1, and published.",
            person.GetProperty("applied")[0].GetString());
    }

    [Fact]
    public async Task Restore_DefinedFactory_AStaleExpectedVersion_IsRefused_AndNothingIsWritten()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it.\n");
        PublishDefinition(2);

        var (status, body) = await Send("POST", Boss + "/restore", AsAPerson, new { version = 1, expectedVersion = 1 });

        Assert.Equal(409, status);
        Assert.Equal("Stale", Text(body, "outcome"));
        Assert.Equal(2, _definitions.Standing(A, TheFactory).Newest!.Version);
        Assert.Equal(2, _definitions.Standing(A, TheFactory).Published!.Version);
    }

    // ---------- the seat page's views ----------

    [Fact]
    public async Task SeatPage_DefinedFactory_ThePanelShowsTheDefinitionsBrief_NeverAFilePath_AndTheDefinitionLine()
    {
        Define();

        var (status, page) = await Send("GET", BossView);

        Assert.Equal(200, status);
        var brief = page.GetProperty("brief");
        Assert.Equal("published", Text(brief, "state"));
        Assert.Equal("Run the factory.\n", Text(brief, "text"));
        Assert.StartsWith("Definition version 1, published by a person (owner) ", Text(brief, "versionText"));
        Assert.Equal(ViewLine, Text(brief, "note"));
        Assert.Equal(JsonValueKind.Null, brief.GetProperty("pathText").ValueKind);
    }

    [Fact]
    public async Task EditorView_DefinedFactory_StartsFromTheNewestDefinitionVersion_AndSaysTheBriefIsPartOfTheDefinition()
    {
        Define();
        SaveDefinitionDraft("boss", "Run it better.\n");

        var (status, view) = await Send("GET", BossView + "/brief/editor");

        Assert.Equal(200, status);
        Assert.Equal("Run it better.\n", Text(view, "text"));
        Assert.Equal(2, view.GetProperty("expectedVersion").GetInt32());
        Assert.EndsWith(" " + ViewLine, Text(view, "startText"));
        Assert.Contains(ViewLine, view.GetProperty("publish").GetProperty("confirmLines").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task HistoryView_DefinedFactory_IsTheDefinitionVersionsThatChangedTheBrief()
    {
        Define();
        SaveDefinitionDraft("writer", "Write more.\n");
        SaveDefinitionDraft("boss", "Run it better.\n");

        var (status, view) = await Send("GET", BossView + "/brief/history");

        Assert.Equal(200, status);
        Assert.Equal(new[] { 3, 1 }, view.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("version").GetInt32()));
        Assert.Equal("2 versions of the definition changed this brief. Definition version 1 is published. " + ViewLine,
            Text(view, "summaryText"));
    }

    [Fact]
    public async Task VersionView_DefinedFactory_IsTheSeatsBriefInThatDefinitionVersion()
    {
        Define();

        var (status, view) = await Send("GET", BossView + "/brief/versions/1");
        var (missingStatus, _) = await Send("GET", BossView + "/brief/versions/5");

        Assert.Equal(200, status);
        Assert.Equal("Definition version 1", Text(view, "title"));
        Assert.Equal("Run the factory.\n", Text(view, "text"));
        Assert.EndsWith(" " + ViewLine, Text(view, "lineText"));
        Assert.Equal(404, missingStatus);
    }

    // =================================================================================================================
    // GOAL 14: a factory with no published definition - none, or only drafts - takes exactly today's paths
    // =================================================================================================================

    public static TheoryData<bool> UndefinedKinds => new() { false, true };

    /// <param name="draftOnly">False: the factory has no definition at all. True: it has a definition draft, never
    /// published - which is not a defined factory.</param>
    private void Undefined(bool draftOnly)
    {
        if (draftOnly) Define(publish: false);
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task GetPublished_UNDEFINED_FACTORY_IsTheBriefStoresAnswer(bool draftOnly)
    {
        Undefined(draftOnly);

        var (status, body) = await Send("GET", Boss);

        Assert.Equal(404, status);
        Assert.Equal("'boss' of devthrottle has no brief on the Gateway", Text(body, "error"));
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task SaveDraftAndPublish_UNDEFINED_FACTORY_WriteTheBriefStore_AndTheDefinitionIsUntouched(bool draftOnly)
    {
        Undefined(draftOnly);
        var definitionBefore = _definitions.Standing(A, TheFactory).Newest?.Version;

        var (saveStatus, saved) = await Send("PUT", Boss + "/draft", AsAPerson, new { text = "Run it.\n", expectedVersion = 0 });
        var (publishStatus, published) = await Send("POST", Boss + "/publish", AsAPerson, new { version = 1 });
        var (readStatus, read) = await Send("GET", Boss);

        Assert.Equal(200, saveStatus);
        Assert.Equal("brief", Text(saved, "versionKind"));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("definition").ValueKind);
        Assert.Equal(200, publishStatus);
        Assert.Equal("published", Text(published, "state"));
        Assert.Equal(200, readStatus);
        Assert.Equal("Run it.\n", Text(read, "text"));
        Assert.Equal(1, _briefs.Standing(A, TheFactory, "boss").Published!.Version);
        Assert.Equal(definitionBefore, _definitions.Standing(A, TheFactory).Newest?.Version);
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task SessionPublish_UNDEFINED_FACTORY_IsRefusedWithTheBriefsOwnSentence(bool draftOnly)
    {
        Undefined(draftOnly);
        await Send("PUT", Boss + "/draft", AsASessionOfTheFactory, new { text = "Run it.\n", expectedVersion = 0 });

        var (status, body) = await Send("POST", Boss + "/publish", AsASessionOfTheFactory, new { version = 1 });

        Assert.Equal(403, status);
        Assert.Equal(FactoryBriefStore.OnlyAPersonPublishes, Text(body, "error"));
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task HistoryVersionsAndRestore_UNDEFINED_FACTORY_AreTheBriefStores(bool draftOnly)
    {
        Undefined(draftOnly);
        await Send("PUT", Boss + "/draft", AsAPerson, new { text = "One.\n", expectedVersion = 0 });
        await Send("POST", Boss + "/publish", AsAPerson, new { version = 1 });
        await Send("PUT", Boss + "/draft", AsAPerson, new { text = "Two.\n", expectedVersion = 1 });
        await Send("POST", Boss + "/publish", AsAPerson, new { version = 2 });

        var (_, history) = await Send("GET", Boss + "/history");
        var (_, one) = await Send("GET", Boss + "/versions/1");
        var (_, newest) = await Send("GET", Boss + "/draft");
        var (restoreStatus, restored) = await Send("POST", Boss + "/restore", AsAPerson, new { version = 1, expectedVersion = 2 });

        Assert.Equal(new[] { 2, 1 }, history.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal("brief", Text(history, "versionKind"));
        Assert.Equal("One.\n", Text(one, "text"));
        Assert.Equal("Two.\n", Text(newest, "text"));
        Assert.Equal(200, restoreStatus);
        Assert.Equal(3, restored.GetProperty("version").GetInt32());
        Assert.Equal("One.\n", Text(restored, "text"));
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task SeatPageViews_UNDEFINED_FACTORY_ShowTheBriefFile_AndNoDefinitionLine(bool draftOnly)
    {
        Undefined(draftOnly);

        var (_, page) = await Send("GET", BossView);
        var (_, editor) = await Send("GET", BossView + "/brief/editor");
        var (_, history) = await Send("GET", BossView + "/brief/history");

        var brief = page.GetProperty("brief");
        Assert.Equal("file", Text(brief, "state"));
        Assert.Equal("/home/soren/devthrottle-factory/agents/boss.yaml", Text(brief, "pathText"));
        Assert.Equal(FactoryBriefPanelFold.FileNote("SOREN_NORTH"), Text(brief, "note"));
        Assert.Equal("Boss has no brief on the Gateway yet. Write it here. Until one is published, talks and runs of this seat " +
                     "are told the path of its brief file, /home/soren/devthrottle-factory/agents/boss.yaml on SOREN_NORTH.",
            Text(editor, "startText"));
        Assert.Equal("No version yet.", Text(history, "summaryText"));
    }

    [Theory]
    [MemberData(nameof(UndefinedKinds))]
    public async Task VersionView_UNDEFINED_FACTORY_IsTheBriefStoresVersion(bool draftOnly)
    {
        Undefined(draftOnly);
        await Send("PUT", Boss + "/draft", AsAPerson, new { text = "One.\n", expectedVersion = 0 });

        var (status, view) = await Send("GET", BossView + "/brief/versions/1");

        Assert.Equal(200, status);
        Assert.Equal("Version 1", Text(view, "title"));
        Assert.Equal("One.\n", Text(view, "text"));
        Assert.DoesNotContain("definition", Text(view, "lineText"));
    }
}
