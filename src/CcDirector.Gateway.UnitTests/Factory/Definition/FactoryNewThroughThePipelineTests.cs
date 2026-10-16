using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// NEW FACTORY ON THE FACTORIES SCREEN, AS MAPPED (Factory Definition mission, phase 6b): the owner's whole loop from a
/// blank screen - New factory with a zip, the draft row under the list, the draft's factory page with only its Definition
/// tab, the tab, Publish, and the factory on the list with the draft row gone - and every refusal of New factory, each a
/// test that fails if the refusal is removed. Through <see cref="FactoriesScreenEndpoints.Map"/>,
/// <see cref="FactoryDefinitionTabEndpoints.Map"/> and <see cref="FactoryDefinitionEndpoints.Map"/> in memory, on real
/// stores over one SQLite database. No port is opened.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryNewThroughThePipelineTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);
    private const string New = "/gateway/factories/new";
    private const string List = "/gateway/factories";
    private const string Page = "/gateway/factories/devthrottle";
    private const string Tab = "/gateway/factories/devthrottle/definition";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly FactoryRegistryStore _registry;
    private readonly FactoryDefinitionStore _store;

    public FactoryNewThroughThePipelineTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        _registry = new FactoryRegistryStore(_db);
        _store = new FactoryDefinitionStore(_db);
        var apply = new FactoryPublishApply(_db);
        var record = new FactoryActivityRecord(_db);
        var settings = new TenantSettingsStore(_db);
        var account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => DefinitionFixture.Machines, DefinitionFixture.AskNobody);
        var triggers = new CcDirector.Gateway.Factory.Triggers.TriggerStore(_db);
        FactoryDefinitionEndpoints.Map(_routes, _ => T, _store, _ => SessionFactoryLookup.NotKnown,
            account.FleetSkillsOf, account.ConnectedMachinesOf, (_, _) => null, apply, () => Now, DefinitionFixture.AskNobody,
            new FactoryDefinitionEndpoints.CheckStateParts(triggers.CheckStates, triggers.List, _ => Array.Empty<CronJobDto>()));
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
            _ => Array.Empty<CronJobDto>(),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f },
            FactoriesScreenEndpoints.UnpublishedFrom(_store));
        FactoriesScreenEndpoints.Map(_routes, _ => T, sources, isDefined: _store.IsDefined);
        FactoryDefinitionTabEndpoints.Map(_routes, _ => T, sources, _store, apply, (_, _) => null,
            _ => SessionFactoryLookup.NotKnown, account, () => Now);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    [Fact]
    public async Task THE_OWNERS_LOOP_new_factory_the_draft_row_its_page_the_tab_publish_and_the_factory_on_the_list()
    {
        var blank = await Json("GET", List);
        Assert.Equal(0, blank.Body.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, blank.Body.GetProperty("draftRows").GetArrayLength());
        Assert.Equal("New factory", blank.Body.GetProperty("newFactory").GetProperty("label").GetString());
        Assert.Equal(New, blank.Body.GetProperty("newFactory").GetProperty("href").GetString());
        Assert.Equal(404, (await Json("GET", Page)).Status);

        var imported = await Json("POST", New, Zip(DefinitionFixture.Publishable()));
        Assert.True(imported.Status == 200, imported.Body.ToString());
        Assert.Equal("devthrottle", imported.Body.GetProperty("factory").GetString());
        Assert.Equal(1, imported.Body.GetProperty("version").GetInt32());
        Assert.Equal("/factories/devthrottle/definition", imported.Body.GetProperty("openHref").GetString());
        Assert.Contains("It is the first version of the definition of devthrottle.", imported.Body.GetProperty("doneText").GetString());

        // The draft row under the list, and not a row of the list.
        var drafted = await Json("GET", List);
        Assert.Equal(0, drafted.Body.GetProperty("rows").GetArrayLength());
        Assert.Equal("Not published yet", drafted.Body.GetProperty("draftTitle").GetString());
        var row = Assert.Single(drafted.Body.GetProperty("draftRows").EnumerateArray());
        Assert.Equal("DevThrottle - draft, not published yet", row.GetProperty("text").GetString());
        Assert.Equal("/factories/devthrottle/definition", row.GetProperty("href").GetString());

        // Its page: every tab but Definition says it starts when published.
        var page = await Json("GET", Page);
        Assert.Equal(200, page.Status);
        Assert.Equal("DRAFT", page.Body.GetProperty("statusWord").GetString());
        Assert.Equal(JsonValueKind.Null, page.Body.GetProperty("talk").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.Body.GetProperty("archive").ValueKind);
        foreach (var tab in page.Body.GetProperty("tabs").EnumerateArray())
        {
            if (tab.GetProperty("key").GetString() == "definition")
                Assert.Equal(JsonValueKind.Null, tab.GetProperty("notYetText").ValueKind);
            else
                Assert.Equal(FactoryNewFold.NotYetText("DevThrottle"), tab.GetProperty("notYetText").GetString());
        }

        // Its Definition tab: the draft, what changes, and Publish.
        var tabView = await Json("GET", Tab);
        Assert.Equal(200, tabView.Status);
        Assert.Equal("Nothing is published yet. Version 1, a draft, is the only version.", tabView.Body.GetProperty("stateText").GetString());
        Assert.True(tabView.Body.GetProperty("changes").GetProperty("changes").GetProperty("firstPublish").GetBoolean());
        Assert.Equal(1, tabView.Body.GetProperty("publish").GetProperty("version").GetInt32());
        Assert.Equal(200, (await Json("GET", Tab + "/history")).Status);
        Assert.Equal(200, (await Send("GET", Tab + "/export")).Status);

        var published = await Json("POST", "/factory-definitions/devthrottle/publish", Encoding.UTF8.GetBytes("""{"version":1,"replaces":null}"""), "application/json");
        Assert.True(published.Status == 200, published.Body.ToString());

        // Published: on the list, the draft row gone, and its page is a factory's page.
        var after = await Json("GET", List);
        Assert.Equal(0, after.Body.GetProperty("draftRows").GetArrayLength());
        Assert.Equal("devthrottle", Assert.Single(after.Body.GetProperty("rows").EnumerateArray()).GetProperty("id").GetString());
        var live = await Json("GET", Page);
        Assert.NotEqual("DRAFT", live.Body.GetProperty("statusWord").GetString());
        Assert.All(live.Body.GetProperty("tabs").EnumerateArray(), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("notYetText").ValueKind));
    }

    [Fact]
    public async Task A_FOLDER_FOR_A_REGISTERED_FACTORY_is_refused_with_open_it_and_import_there_and_nothing_is_stored()
    {
        _registry.Register(T, DefinitionFixture.Registration(), "test", Now);

        var refused = await Json("POST", New, Zip(DefinitionFixture.Publishable()));

        Assert.Equal(409, refused.Status);
        Assert.Equal("NotImported", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal("This folder's factory.yaml names 'devthrottle', and that factory already exists: DevThrottle. Open it and " +
                     "import there, on its Definition tab, to make the folder its next draft. Nothing was imported.",
            refused.Body.GetProperty("error").GetString());
        Assert.Null(_store.Standing(T, "devthrottle").Newest);
    }

    [Fact]
    public async Task A_FOLDER_FOR_AN_ARCHIVED_FACTORY_is_refused_saying_it_is_archived_and_a_new_factory_needs_a_new_id()
    {
        _registry.Register(T, DefinitionFixture.Registration(), "test", Now);
        _registry.Archive(T, "devthrottle", "test", Array.Empty<string>(), Now);

        var refused = await Json("POST", New, Zip(DefinitionFixture.Publishable()));

        Assert.Equal(409, refused.Status);
        var error = refused.Body.GetProperty("error").GetString()!;
        Assert.StartsWith("This folder's factory.yaml names 'devthrottle', and DevThrottle is archived under that id. A new factory needs a new id", error);
        Assert.Contains("Restore it under Show archived", error);
        Assert.Null(_store.Standing(T, "devthrottle").Newest);
    }

    [Fact]
    public async Task A_SECOND_NEW_FACTORY_FOR_AN_ID_WITH_A_DRAFT_is_refused_with_open_it_under_not_published_yet()
    {
        Assert.Equal(200, (await Json("POST", New, Zip(DefinitionFixture.Publishable()))).Status);

        var refused = await Json("POST", New, Zip(DefinitionFixture.Publishable()));

        Assert.Equal(409, refused.Status);
        Assert.StartsWith("This folder's factory.yaml names 'devthrottle', and DevThrottle already has a draft that is not published yet. Open it under Not published yet",
            refused.Body.GetProperty("error").GetString());
        Assert.Equal(1, _store.Standing(T, "devthrottle").Newest!.Version);
    }

    [Fact]
    public async Task A_FOLDER_THE_CHECK_REFUSES_answers_422_with_every_finding_by_file_and_line_and_stores_nothing()
    {
        var files = DefinitionFixture.Publishable();
        files["seats/writer.yaml"] += "colour: red\n";

        var refused = await Json("POST", New, Zip(files));

        Assert.Equal(422, refused.Status);
        Assert.Equal("CheckFailed", refused.Body.GetProperty("outcome").GetString());
        var finding = Assert.Single(refused.Body.GetProperty("findings").EnumerateArray(), f => f.GetProperty("file").GetString() == "seats/writer.yaml");
        Assert.True(finding.GetProperty("line").GetInt32() > 0);
        Assert.Null(_store.Standing(T, "devthrottle").Newest);
        Assert.Equal(0, (await Json("GET", List)).Body.GetProperty("draftRows").GetArrayLength());
    }

    [Fact]
    public async Task A_FILE_THAT_IS_NOT_A_ZIP_is_refused_in_words_by_the_same_unzip_as_the_tab()
    {
        var refused = await Json("POST", New, Encoding.UTF8.GetBytes("not a zip"));

        Assert.Equal(400, refused.Status);
        Assert.StartsWith("The file is not a zip that can be read", refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_REGISTERED_FACTORY_WITH_ONLY_DRAFTS_is_on_the_list_and_never_a_draft_row()
    {
        _registry.Register(T, DefinitionFixture.Registration(), "test", Now);
        Assert.Equal(200, (await Json("POST", Tab + "/import", Zip(DefinitionFixture.Publishable()))).Status);

        var list = await Json("GET", List);

        Assert.Equal(0, list.Body.GetProperty("draftRows").GetArrayLength());
        Assert.Single(list.Body.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public void NEW_FACTORY_IS_THE_OWNERS_a_session_key_is_refused_by_the_guard()
    {
        Assert.False(SessionKeyGuard.Check("POST", New).Allowed);
    }

    // ---------- the in-memory pipeline ----------

    private static byte[] Zip(IReadOnlyDictionary<string, string> files) => FactoryDefinitionTabEndpoints.Zip(files);

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

    private static void AsTheOwner(HttpContext ctx)
    {
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-1", T.Value, "browser", "active");
    }

    /// <summary>The route ASP.NET Core would pick: literal segments before parameters, so /new is never {factory}.</summary>
    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
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

    private async Task<(int Status, byte[] Bytes)> Send(string method, string path, byte[]? body = null, string contentType = "application/zip")
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
        ctx.Request.Body = new MemoryStream(body ?? Array.Empty<byte>());
        if (body is not null)
        {
            ctx.Request.ContentType = contentType;
            ctx.Request.ContentLength = body.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();
        AsTheOwner(ctx);

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        return (ctx.Response.StatusCode, ((MemoryStream)ctx.Response.Body).ToArray());
    }

    private async Task<(int Status, JsonElement Body)> Json(string method, string path, byte[]? body = null, string contentType = "application/zip")
    {
        var answer = await Send(method, path, body, contentType);
        using var document = JsonDocument.Parse(answer.Bytes);
        return (answer.Status, document.RootElement.Clone());
    }
}
