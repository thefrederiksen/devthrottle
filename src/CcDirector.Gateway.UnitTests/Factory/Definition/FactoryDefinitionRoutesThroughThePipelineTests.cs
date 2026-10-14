using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
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
/// THE DEFINITION ROUTES AS THEY ARE MAPPED (Factory Definition mission, phase 1). <see cref="FactoryDefinitionEndpointsTests"/>
/// calls the handlers, which proves the rules and nothing about a request REACHING them: the route templates, the
/// reading of the JSON body, and the names of the fields in the answer are all outside a handler. The command line
/// depends on every one of those - it sends <c>expectedVersion</c>, <c>note</c> and <c>content</c> and reads
/// <c>outcome</c>, <c>findings</c>, <c>current</c> and <c>newestVersion</c> by name.
///
/// So these go through <see cref="FactoryDefinitionEndpoints.Map"/> itself, in memory: the mapped endpoint is found by
/// matching the request's verb and path against its own template, and run with a request body and a response body.
/// No port is opened. They also hold the mapped routes and the session key guard to the same list, because adding a
/// route does not add it to the guard: a session key would be refused with 403 while every handler test stayed green.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionRoutesThroughThePipelineTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string FactorySession = "75000000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly ServiceProvider _services;
    private readonly Routes _routes;

    public FactoryDefinitionRoutesThroughThePipelineTests()
    {
        var db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        FactoryDefinitionEndpoints.Map(_routes,
            resolveTenant: _ => A,
            store: new FactoryDefinitionStore(db),
            sessionFactoryOf: _ => SessionFactoryLookup.In(TheFactory),
            fleetSkillsOf: _ => DefinitionFixture.FleetSkills,
            connectedMachinesOf: _ => DefinitionFixture.Machines,
            nowUtc: () => Now);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

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

    /// <summary>The mapped endpoint whose verb and template match this request, with the route values it matched.</summary>
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

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, Action<HttpContext> who, string? json = null)
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
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();
        who(ctx);

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private const string FactoryPath = "/factory-definitions/devthrottle";

    /// <summary>A save's body in the exact spelling the command line sends.</summary>
    private static string SaveBody(IReadOnlyDictionary<string, string> content, int expect, string? note = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["expectedVersion"] = expect, ["note"] = note, ["content"] = content });

    [Fact]
    public async Task A_DEFINITION_SENT_AS_JSON_IS_SAVED_PUBLISHED_AND_READ_BACK_UNDER_THE_NAMES_THE_COMMAND_LINE_USES()
    {
        var saved = await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Valid(), 0, "first"));

        Assert.Equal(200, saved.Status);
        Assert.Equal(1, saved.Body.GetProperty("version").GetInt32());
        Assert.Equal("draft", saved.Body.GetProperty("state").GetString());
        Assert.Equal("session", saved.Body.GetProperty("authorKind").GetString());
        Assert.Equal(FactorySession, saved.Body.GetProperty("authorId").GetString());
        Assert.Equal("first", saved.Body.GetProperty("changeNote").GetString());
        Assert.Equal(DefinitionFixture.Valid().Count, saved.Body.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, saved.Body.GetProperty("newestVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved.Body.GetProperty("publishedVersion").ValueKind);
        var notice = saved.Body.GetProperty("findings")[0];
        Assert.Equal("notice", notice.GetProperty("severity").GetString());
        Assert.True(notice.TryGetProperty("file", out _));
        Assert.True(notice.TryGetProperty("line", out _));
        Assert.True(notice.TryGetProperty("message", out _));

        var notYet = await Send("GET", FactoryPath, AsAPerson);
        Assert.Equal(404, notYet.Status);
        Assert.Equal(1, notYet.Body.GetProperty("newestVersion").GetInt32());

        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.Equal(200, published.Status);
        Assert.Equal("published", published.Body.GetProperty("state").GetString());
        Assert.Equal(1, published.Body.GetProperty("publishedVersion").GetInt32());

        var read = await Send("GET", FactoryPath, AsASessionOfTheFactory);
        Assert.Equal(200, read.Status);
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
        Assert.Equal(DefinitionFixture.Valid()["seats/boss.yaml"], read.Body.GetProperty("files").GetProperty("seats/boss.yaml").GetString());

        var newest = await Send("GET", FactoryPath + "/draft", AsAPerson);
        Assert.Equal(1, newest.Body.GetProperty("version").GetInt32());

        var one = await Send("GET", FactoryPath + "/versions/1", AsAPerson);
        Assert.Equal(DefinitionFixture.Valid().Count, one.Body.GetProperty("files").EnumerateObject().Count());

        var history = await Send("GET", FactoryPath + "/history?limit=5", AsAPerson);
        Assert.Equal(1, history.Body.GetProperty("publishedVersion").GetInt32());
        Assert.Equal(1, history.Body.GetProperty("total").GetInt32());
        var row = history.Body.GetProperty("versions")[0];
        Assert.Equal("first", row.GetProperty("changeNote").GetString());
        Assert.False(row.TryGetProperty("files", out _));
        Assert.DoesNotContain("Run the factory.", history.Body.GetRawText());
    }

    [Fact]
    public async Task THE_CHECKS_REFUSAL_COMES_BACK_AS_422_WITH_EVERY_FINDING_BY_FILE_AND_LINE()
    {
        var broken = DefinitionFixture.With("seats/writer.yaml", DefinitionFixture.Writer + "\ncolour: red");

        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(broken, 0));

        Assert.Equal(422, refused.Status);
        Assert.Equal("CheckFailed", refused.Body.GetProperty("outcome").GetString());
        Assert.StartsWith("The definition was not saved: the check found 1 error", refused.Body.GetProperty("error").GetString());
        var error = refused.Body.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("severity").GetString() == "error");
        Assert.Equal("seats/writer.yaml", error.GetProperty("file").GetString());
        Assert.Equal(5, error.GetProperty("line").GetInt32());
        Assert.Contains("'colour' is not a key", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_SESSIONS_PUBLISH_AND_RESTORE_COME_BACK_AS_403_WITH_THE_SENTENCE_AND_THE_OUTCOME_BY_NAME()
    {
        await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Valid(), 0));

        var refused = await Send("POST", FactoryPath + "/publish", AsASessionOfTheFactory, """{"version":1}""");
        Assert.Equal(403, refused.Status);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonPublishes, refused.Body.GetProperty("error").GetString());
        Assert.Equal("OnlyAPersonPublishes", refused.Body.GetProperty("outcome").GetString());

        var restore = await Send("POST", FactoryPath + "/restore", AsASessionOfTheFactory, """{"version":1,"expectedVersion":1}""");
        Assert.Equal(403, restore.Status);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonRestores, restore.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_STALE_SAVE_COMES_BACK_AS_409_WITH_THE_CURRENT_VERSION_UNDER_current()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 1));

        var refused = await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Valid(), 1));

        Assert.Equal(409, refused.Status);
        Assert.Equal("Stale", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal(2, refused.Body.GetProperty("current").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_RESTORE_SENT_AS_JSON_PUBLISHES_A_COPY_AS_THE_NEWEST_VERSION()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.Edit("seats/writer.yaml", "Write the posts.", "v2"), 1));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2}""");

        var back = await Send("POST", FactoryPath + "/restore", AsAPerson, """{"version":1,"expectedVersion":2}""");

        Assert.Equal(200, back.Status);
        Assert.Equal(3, back.Body.GetProperty("version").GetInt32());
        Assert.Equal(1, back.Body.GetProperty("restoredFrom").GetInt32());
        Assert.Equal("published", back.Body.GetProperty("state").GetString());
        Assert.Equal(3, back.Body.GetProperty("publishedVersion").GetInt32());
    }

    [Fact]
    public async Task A_PUBLISH_ANSWERS_WITH_WHAT_CHANGES_and_the_diff_route_answers_it_between_any_two_versions()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));
        var first = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.True(first.Body.GetProperty("changes").GetProperty("firstPublish").GetBoolean());

        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.Edit("seats/writer.yaml", "name: Writer", "name: Author"), 1));
        var diff = await Send("GET", FactoryPath + "/diff", AsASessionOfTheFactory);

        Assert.Equal(200, diff.Status);
        Assert.Equal(1, diff.Body.GetProperty("from").GetInt32());
        Assert.Equal(2, diff.Body.GetProperty("to").GetInt32());
        var changes = diff.Body.GetProperty("changes");
        Assert.False(changes.GetProperty("nothingChanges").GetBoolean());
        var seat = changes.GetProperty("seats")[0];
        Assert.Equal("writer", seat.GetProperty("seat").GetString());
        Assert.Equal("changed", seat.GetProperty("change").GetString());

        var second = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2}""");
        Assert.Equal(changes.GetRawText(), second.Body.GetProperty("changes").GetRawText());

        var none = await Send("GET", FactoryPath + "/diff?from=2&to=2", AsAPerson);
        Assert.True(none.Body.GetProperty("changes").GetProperty("nothingChanges").GetBoolean());
    }

    [Fact]
    public async Task The_factory_in_the_path_is_folded_to_its_one_spelling()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));

        var read = await Send("GET", "/factory-definitions/DevThrottle/draft", AsAPerson);

        Assert.Equal(200, read.Status);
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
    }

    [Fact]
    public async Task A_write_that_carries_no_body_is_a_400_in_words()
    {
        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson);

        Assert.Equal(400, refused.Status);
        Assert.Equal("a body is required", refused.Body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "/factory-definitions/devthrottle")]
    [InlineData("GET", "/factory-definitions/devthrottle/draft")]
    [InlineData("GET", "/factory-definitions/devthrottle/history")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions/3")]
    [InlineData("GET", "/factory-definitions/devthrottle/diff")]
    [InlineData("PUT", "/factory-definitions/devthrottle/draft")]
    [InlineData("POST", "/factory-definitions/devthrottle/publish")]
    [InlineData("POST", "/factory-definitions/devthrottle/restore")]
    public void EACH_ROUTE_IS_MAPPED_and_the_session_key_guard_lets_a_session_reach_it(string method, string path)
    {
        Assert.NotNull(Match(method, path));
        // The two lists must agree: a route the guard does not know is one no session can reach in production.
        Assert.True(SessionKeyGuard.Check(method, path).Allowed);
    }

    [Theory]
    [InlineData("DELETE", "/factory-definitions/devthrottle")]
    [InlineData("PUT", "/factory-definitions/devthrottle")]
    [InlineData("POST", "/factory-definitions/devthrottle/draft")]
    [InlineData("DELETE", "/factory-definitions/devthrottle/draft")]
    [InlineData("GET", "/factory-definitions/devthrottle/publish")]
    [InlineData("GET", "/factory-definitions")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions")]
    [InlineData("PUT", "/factory-definitions/devthrottle/versions/3")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions/3/files")]
    [InlineData("POST", "/factory-definitions/devthrottle/anything-else")]
    [InlineData("POST", "/factory-definitions/devthrottle/diff")]
    public void NOTHING_ELSE_IS_MAPPED_UNDER_THE_PREFIX_and_the_guard_agrees(string method, string path)
    {
        Assert.Null(Match(method, path));
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
    }
}
