using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Registry;
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

namespace CcDirector.Gateway.Tests.Factory.Briefs;

/// <summary>
/// THE BRIEF ROUTES AS THEY ARE MAPPED (Factory Seats mission, phase 2a). <see cref="FactoryBriefEndpointsTests"/>
/// calls the handlers, which proves the rules and proves nothing about a request REACHING them: the route
/// templates, the reading of the JSON body, and the names of the fields in the answer are all outside a handler.
/// The command line depends on every one of those - it sends <c>expectedVersion</c> and reads <c>outcome</c>,
/// <c>current</c> and <c>newestVersion</c> by name.
///
/// So these go through <see cref="FactoryBriefEndpoints.Map"/> itself, in memory: the mapped endpoint is found by
/// matching the request's verb and path against its own template, and run with a request body and a response body.
/// No port is opened.
/// </summary>
[Trait("Category", "FactoryBrief")]
public sealed class FactoryBriefRoutesThroughThePipelineTests : IDisposable
{
    private const string TheFactory = "warmforward";
    private const string Boss = "boss";
    private const string FactorySession = "73000000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly ServiceProvider _services;
    private readonly Routes _routes;

    public FactoryBriefRoutesThroughThePipelineTests()
    {
        var db = _h.Open();
        var registry = new FactoryRegistryStore(db);
        registry.Register(A, new RegisterFactoryRequest
        {
            Factory = TheFactory,
            Title = "WarmForward",
            Folder = "/home/soren/warmforward-factory",
            Computer = "DEVLINUX",
            Seats = { new FactorySeatManifest { Id = Boss, Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", Now);

        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        FactoryBriefEndpoints.Map(_routes,
            resolveTenant: _ => A,
            store: new FactoryBriefStore(db, registry),
            sessionFactoryOf: _ => SessionFactoryLookup.In(TheFactory),
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
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
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

    private const string SeatPath = "/factory-briefs/warmforward/boss";

    [Fact]
    public async Task A_DRAFT_SENT_AS_JSON_IS_SAVED_PUBLISHED_AND_READ_BACK_UNDER_THE_NAMES_THE_COMMAND_LINE_USES()
    {
        // The body is written by hand, in the exact spelling the command line sends.
        var saved = await Send("PUT", SeatPath + "/draft", AsASessionOfTheFactory,
            """{"text":"You are the boss of WarmForward.","expectedVersion":0}""");

        Assert.Equal(200, saved.Status);
        Assert.Equal(1, saved.Body.GetProperty("version").GetInt32());
        Assert.Equal("draft", saved.Body.GetProperty("state").GetString());
        Assert.Equal("session", saved.Body.GetProperty("authorKind").GetString());
        Assert.Equal(FactorySession, saved.Body.GetProperty("authorId").GetString());
        Assert.Equal(1, saved.Body.GetProperty("newestVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved.Body.GetProperty("publishedVersion").ValueKind);

        var notYet = await Send("GET", SeatPath, AsAPerson);
        Assert.Equal(404, notYet.Status);
        Assert.Contains("no published brief yet", notYet.Body.GetProperty("error").GetString());
        Assert.Equal(1, notYet.Body.GetProperty("newestVersion").GetInt32());

        var published = await Send("POST", SeatPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.Equal(200, published.Status);
        Assert.Equal("published", published.Body.GetProperty("state").GetString());
        Assert.Equal(1, published.Body.GetProperty("publishedVersion").GetInt32());

        var read = await Send("GET", SeatPath, AsASessionOfTheFactory);
        Assert.Equal(200, read.Status);
        Assert.Equal("You are the boss of WarmForward.", read.Body.GetProperty("text").GetString());
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
        Assert.Equal(Boss, read.Body.GetProperty("seat").GetString());

        var newest = await Send("GET", SeatPath + "/draft", AsAPerson);
        Assert.Equal(1, newest.Body.GetProperty("version").GetInt32());

        var history = await Send("GET", SeatPath + "/history", AsAPerson);
        Assert.Equal(1, history.Body.GetProperty("publishedVersion").GetInt32());
        Assert.Equal(1, history.Body.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task A_SESSIONS_PUBLISH_COMES_BACK_AS_403_WITH_THE_SENTENCE_AND_THE_OUTCOME_BY_NAME()
    {
        await Send("PUT", SeatPath + "/draft", AsASessionOfTheFactory, """{"text":"a session's draft","expectedVersion":0}""");

        var refused = await Send("POST", SeatPath + "/publish", AsASessionOfTheFactory, """{"version":1}""");

        Assert.Equal(403, refused.Status);
        Assert.Equal(FactoryBriefStore.OnlyAPersonPublishes, refused.Body.GetProperty("error").GetString());
        Assert.Equal("OnlyAPersonPublishes", refused.Body.GetProperty("outcome").GetString());

        var restore = await Send("POST", SeatPath + "/restore", AsASessionOfTheFactory, """{"version":1,"expectedVersion":1}""");
        Assert.Equal(403, restore.Status);
        Assert.Equal(FactoryBriefStore.OnlyAPersonBringsBack, restore.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_STALE_SAVE_COMES_BACK_AS_409_WITH_THE_CURRENT_VERSION_UNDER_current()
    {
        await Send("PUT", SeatPath + "/draft", AsAPerson, """{"text":"start","expectedVersion":0}""");
        await Send("PUT", SeatPath + "/draft", AsAPerson, """{"text":"start, and the owner's change","expectedVersion":1}""");

        var refused = await Send("PUT", SeatPath + "/draft", AsASessionOfTheFactory,
            """{"text":"start, and a session's change","expectedVersion":1}""");

        Assert.Equal(409, refused.Status);
        Assert.Equal("Stale", refused.Body.GetProperty("outcome").GetString());
        var current = refused.Body.GetProperty("current");
        Assert.Equal(2, current.GetProperty("version").GetInt32());
        Assert.Equal("start, and the owner's change", current.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_BRING_BACK_SENT_AS_JSON_PUBLISHES_A_COPY_AS_THE_NEWEST_VERSION()
    {
        await Send("PUT", SeatPath + "/draft", AsAPerson, """{"text":"the first brief","expectedVersion":0}""");
        await Send("POST", SeatPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", SeatPath + "/draft", AsAPerson, """{"text":"the second brief","expectedVersion":1}""");
        await Send("POST", SeatPath + "/publish", AsAPerson, """{"version":2}""");

        var back = await Send("POST", SeatPath + "/restore", AsAPerson, """{"version":1,"expectedVersion":2}""");

        Assert.Equal(200, back.Status);
        Assert.Equal(3, back.Body.GetProperty("version").GetInt32());
        Assert.Equal("the first brief", back.Body.GetProperty("text").GetString());
        Assert.Equal("published", back.Body.GetProperty("state").GetString());
        Assert.Equal(3, back.Body.GetProperty("publishedVersion").GetInt32());
    }

    [Fact]
    public async Task The_factory_and_the_seat_in_the_path_are_folded_to_their_one_spelling()
    {
        await Send("PUT", SeatPath + "/draft", AsAPerson, """{"text":"v1","expectedVersion":0}""");

        var read = await Send("GET", "/factory-briefs/WarmForward/BOSS/draft", AsAPerson);

        Assert.Equal(200, read.Status);
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
        Assert.Equal(Boss, read.Body.GetProperty("seat").GetString());
    }

    [Fact]
    public async Task A_write_that_carries_no_body_is_a_400_in_words()
    {
        var refused = await Send("PUT", SeatPath + "/draft", AsAPerson);

        Assert.Equal(400, refused.Status);
        Assert.Equal("a body is required", refused.Body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "/factory-briefs/warmforward/boss")]
    [InlineData("GET", "/factory-briefs/warmforward/boss/draft")]
    [InlineData("GET", "/factory-briefs/warmforward/boss/history")]
    [InlineData("PUT", "/factory-briefs/warmforward/boss/draft")]
    [InlineData("POST", "/factory-briefs/warmforward/boss/publish")]
    [InlineData("POST", "/factory-briefs/warmforward/boss/restore")]
    public void EACH_OF_THE_SIX_ROUTES_IS_MAPPED_and_the_session_key_guard_lets_a_session_reach_it(string method, string path)
    {
        Assert.NotNull(Match(method, path));
        // The two lists must agree: a route the guard does not know is one no session can reach in production.
        Assert.True(SessionKeyGuard.Check(method, path).Allowed);
    }

    [Theory]
    [InlineData("DELETE", "/factory-briefs/warmforward/boss")]
    [InlineData("PUT", "/factory-briefs/warmforward/boss")]
    [InlineData("POST", "/factory-briefs/warmforward/boss/draft")]
    [InlineData("GET", "/factory-briefs/warmforward/boss/publish")]
    [InlineData("GET", "/factory-briefs/warmforward")]
    [InlineData("GET", "/factory-briefs")]
    public void NOTHING_ELSE_IS_MAPPED_UNDER_THE_PREFIX_and_the_guard_agrees(string method, string path)
    {
        Assert.Null(Match(method, path));
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
    }
}
