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
/// THE DEFINITION TAB'S ROUTES AS THEY ARE MAPPED (Factory Definition mission, phase 6). The fold's sentences are proven
/// in <see cref="FactoryDefinitionTabFoldTests"/>; these prove a request REACHES them: the Cockpit's whole loop - import a
/// zip, read the draft and what changes, publish it through the definition's own route, read it published, export it -
/// through <see cref="FactoryDefinitionTabEndpoints.Map"/> and <see cref="FactoryDefinitionEndpoints.Map"/> in memory,
/// on real stores over one SQLite database. No port is opened.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionTabRoutesThroughThePipelineTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private const string F = DefinitionFixture.FactoryId;
    private const string Tab = "/gateway/factories/devthrottle/definition";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;

    public FactoryDefinitionTabRoutesThroughThePipelineTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        var registry = new FactoryRegistryStore(_db);
        registry.Register(T, DefinitionFixture.Registration(), "test", Now);
        var store = new FactoryDefinitionStore(_db);
        var apply = new FactoryPublishApply(_db);
        var record = new FactoryActivityRecord(_db);
        var settings = new TenantSettingsStore(_db);
        var account = new FactoryDefinitionEndpoints.Account(_ => DefinitionFixture.FleetSkills, _ => DefinitionFixture.Machines, DefinitionFixture.AskNobody);

        var triggers = new CcDirector.Gateway.Factory.Triggers.TriggerStore(_db);
        FactoryDefinitionEndpoints.Map(_routes, _ => T, store, _ => SessionFactoryLookup.NotKnown,
            account.FleetSkillsOf, account.ConnectedMachinesOf, (_, _) => null, apply, () => Now, DefinitionFixture.AskNobody,
            new FactoryDefinitionEndpoints.CheckStateParts(triggers.CheckStates, triggers.List));
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
            registry,
            _ => Array.Empty<CronJobDto>(),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f });
        FactoryDefinitionTabEndpoints.Map(_routes, _ => T, sources, store, apply, (_, _) => null,
            _ => SessionFactoryLookup.NotKnown, account, () => Now);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    [Fact]
    public async Task THE_COCKPITS_LOOP_import_a_zip_read_the_draft_publish_it_read_it_published_and_export_the_same_files()
    {
        var files = DefinitionFixture.Publishable();

        var before = await Json("GET", Tab);
        Assert.Equal(200, before.Status);
        Assert.Equal("DevThrottle has no definition on the Gateway.", before.Body.GetProperty("stateText").GetString());

        var imported = await Json("POST", Tab + "/import", FactoryDefinitionTabEndpoints.Zip(files), "application/zip");
        Assert.True(imported.Status == 200, imported.Body.ToString());
        Assert.Equal(1, imported.Body.GetProperty("version").GetInt32());
        Assert.StartsWith($"Imported {files.Count} files as version 1, a draft.", imported.Body.GetProperty("doneText").GetString());

        var drafted = await Json("GET", Tab);
        Assert.Equal("Nothing is published yet. Version 1, a draft, is the only version.", drafted.Body.GetProperty("stateText").GetString());
        Assert.True(drafted.Body.GetProperty("changes").GetProperty("changes").GetProperty("firstPublish").GetBoolean());
        var publish = drafted.Body.GetProperty("publish");
        Assert.Equal(1, publish.GetProperty("version").GetInt32());

        // The tab sends the publish to the definition's own route, with the versions its confirm named.
        var published = await Json("POST", "/factory-definitions/devthrottle/publish", Encoding.UTF8.GetBytes("""{"version":1,"replaces":null}"""), "application/json");
        Assert.True(published.Status == 200, published.Body.ToString());
        Assert.Contains("Definition: version 1 published.", published.Body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()));

        var after = await Json("GET", Tab);
        Assert.Equal("Version 1 is published, and it is the newest version.", after.Body.GetProperty("stateText").GetString());
        Assert.Equal(JsonValueKind.Null, after.Body.GetProperty("publish").ValueKind);

        var history = await Json("GET", Tab + "/history");
        Assert.Equal("Published", history.Body.GetProperty("rows")[0].GetProperty("stateWord").GetString());

        var exported = await Send("GET", Tab + "/export?version=1");
        Assert.Equal(200, exported.Status);
        Assert.Equal("application/zip", exported.ContentType);
        Assert.Contains("devthrottle-definition-v1.zip", exported.Disposition);
        Assert.Equal(FactoryDefinitionTabEndpoints.Zip(files), exported.Bytes);
    }

    [Fact]
    public async Task AN_IMPORT_OF_A_FOLDER_FOR_ANOTHER_FACTORY_IS_REFUSED_ON_THIS_FACTORYS_PAGE_and_nothing_is_stored()
    {
        var files = DefinitionFixture.Publishable();
        files["factory.yaml"] = files["factory.yaml"].Replace("factory: devthrottle", "factory: warmforward", StringComparison.Ordinal);

        var refused = await Json("POST", Tab + "/import", FactoryDefinitionTabEndpoints.Zip(files), "application/zip");

        Assert.Equal(400, refused.Status);
        Assert.StartsWith("This folder's factory.yaml names the factory 'warmforward', not 'devthrottle'.", refused.Body.GetProperty("error").GetString());
        Assert.Equal("DevThrottle has no definition on the Gateway.", (await Json("GET", Tab)).Body.GetProperty("stateText").GetString());
    }

    [Fact]
    public async Task AN_IMPORT_THE_CHECK_REFUSES_answers_422_with_every_finding_and_stores_nothing()
    {
        var files = DefinitionFixture.Publishable();
        files["seats/writer.yaml"] += "colour: red\n";

        var refused = await Json("POST", Tab + "/import", FactoryDefinitionTabEndpoints.Zip(files), "application/zip");

        Assert.Equal(422, refused.Status);
        Assert.Equal("CheckFailed", refused.Body.GetProperty("outcome").GetString());
        Assert.Contains(refused.Body.GetProperty("findings").EnumerateArray(), f => f.GetProperty("file").GetString() == "seats/writer.yaml");
    }

    // ---------- every refusal of the tab's routes, each one a test that fails if the refusal is removed ----------

    [Fact]
    public async Task AN_IMPORT_LARGER_THAN_THE_CAP_is_refused_in_words_before_it_is_unzipped_and_nothing_is_stored()
    {
        var body = new byte[FactoryDefinitionTabEndpoints.MaxZipBytes + 1];

        var refused = await Json("POST", Tab + "/import", body, "application/zip");

        Assert.Equal(400, refused.Status);
        Assert.Equal("NotImported", refused.Body.GetProperty("outcome").GetString());
        var error = refused.Body.GetProperty("error").GetString()!;
        Assert.StartsWith("The file is larger than ", error);
        Assert.EndsWith(", more than a definition can hold. Nothing was imported.", error);
        Assert.Equal("DevThrottle has no definition on the Gateway.", (await Json("GET", Tab)).Body.GetProperty("stateText").GetString());
    }

    [Fact]
    public async Task AN_IMPORT_WHOSE_FILES_COME_TO_MORE_THAN_A_VERSION_HOLDS_is_refused_from_the_sizes_the_zip_declares()
    {
        // Zeros compress to almost nothing, so the zip is far under the body cap while its files are over a version's.
        var files = new Dictionary<string, string>(DefinitionFixture.Publishable(), StringComparer.Ordinal)
        {
            ["files/big.md"] = new string('0', DefinitionPaths.MaxVersionBytes),
        };
        var zip = FactoryDefinitionTabEndpoints.Zip(files);
        Assert.True(zip.Length < FactoryDefinitionTabEndpoints.MaxZipBytes);

        var refused = await Json("POST", Tab + "/import", zip, "application/zip");

        Assert.Equal(400, refused.Status);
        Assert.StartsWith("The files in the zip come to more than ", refused.Body.GetProperty("error").GetString());
        Assert.EndsWith(", the most a definition holds. Nothing was imported.", refused.Body.GetProperty("error").GetString());
        Assert.Equal("DevThrottle has no definition on the Gateway.", (await Json("GET", Tab)).Body.GetProperty("stateText").GetString());
    }

    [Theory]
    [InlineData("/history?before=0", "before is the version a page of the history starts below: a whole number from 1, not '0'")]
    [InlineData("/history?before=seven", "before is the version a page of the history starts below: a whole number from 1, not 'seven'")]
    [InlineData("/versions/0", "a version is a whole number from 1, not '0'")]
    [InlineData("/versions/x", "a version is a whole number from 1, not 'x'")]
    [InlineData("/export?version=-1", "a version is a whole number from 1, not '-1'")]
    public async Task A_PAGE_OR_VERSION_THAT_IS_NOT_A_WHOLE_NUMBER_FROM_1_is_refused_in_words(string rest, string sentence)
    {
        await Json("POST", Tab + "/import", FactoryDefinitionTabEndpoints.Zip(DefinitionFixture.Publishable()), "application/zip");

        var refused = await Json("GET", Tab + rest);

        Assert.Equal(400, refused.Status);
        Assert.Equal(sentence, refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_VERSION_THAT_DOES_NOT_EXIST_answers_404_in_words()
    {
        await Json("POST", Tab + "/import", FactoryDefinitionTabEndpoints.Zip(DefinitionFixture.Publishable()), "application/zip");

        var missing = await Json("GET", Tab + "/versions/9");

        Assert.Equal(404, missing.Status);
        Assert.Equal("version 9 of the definition of devthrottle does not exist", missing.Body.GetProperty("error").GetString());
    }

    [Fact]
    public void THE_TABS_ROUTES_ARE_THE_OWNERS_a_session_key_is_refused_by_the_guard()
    {
        Assert.False(SessionKeyGuard.Check("GET", Tab).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", Tab + "/history").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", Tab + "/versions/3").Allowed);
        Assert.False(SessionKeyGuard.Check("GET", Tab + "/export").Allowed);
        Assert.False(SessionKeyGuard.Check("POST", Tab + "/import").Allowed);
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

    private static void AsTheOwner(HttpContext ctx)
    {
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-1", T.Value, "browser", "active");
    }

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

    private async Task<(int Status, string ContentType, string Disposition, byte[] Bytes)> Send(string method, string path,
        byte[]? body = null, string? contentType = null)
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

        return (ctx.Response.StatusCode, (ctx.Response.ContentType ?? "").Split(';')[0],
            ctx.Response.Headers.ContentDisposition.ToString(), ((MemoryStream)ctx.Response.Body).ToArray());
    }

    private async Task<(int Status, JsonElement Body)> Json(string method, string path, byte[]? body = null, string? contentType = null)
    {
        var answer = await Send(method, path, body, contentType);
        using var document = JsonDocument.Parse(answer.Bytes);
        return (answer.Status, document.RootElement.Clone());
    }
}
