using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Factory.Records;
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
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Factory.Records;

/// <summary>
/// THE RECORD ROUTES AS THEY ARE MAPPED (Factory Definition mission, phase 2). <see cref="FactoryRecordEndpointsTests"/>
/// calls the handlers, which proves the rules and nothing about a request REACHING them: the route templates, the
/// reading of the JSON body and the names of the fields in the answer are all outside a handler, and the command line
/// depends on every one - it sends <c>expectedVersion</c>, <c>contentBase64</c> and <c>importKey</c> and reads
/// <c>outcome</c> and <c>current</c> by name.
///
/// So these go through <see cref="FactoryRecordEndpoints.Map"/> itself, in memory. No port is opened.
/// </summary>
[Trait("Category", "FactoryRecords")]
public sealed class FactoryRecordRoutesThroughThePipelineTests : IDisposable
{
    private const string TheFactory = "devthrottle";
    private const string FactorySession = "7c000000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _h = new();
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly ITestOutputHelper _output;

    public FactoryRecordRoutesThroughThePipelineTests(ITestOutputHelper output)
    {
        _output = output;
        var db = _h.Open();
        var attachments = new FactoryAttachmentStore(db);
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        FactoryRecordEndpoints.Map(_routes,
            resolveTenant: _ => A,
            journal: new FactoryJournalStore(db, attachments),
            documents: new FactoryDocumentStore(db),
            attachments: attachments,
            sessionFactoryOf: _ => SessionFactoryLookup.In(TheFactory),
            nowUtc: () => Now);
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static void AsAPerson(HttpContext ctx) => ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";

    private static void AsASessionOfTheFactory(HttpContext ctx) =>
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(FactorySession), A, "dir-a");

    private static void AsTheMachineToken(HttpContext ctx)
    {
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
        ctx.Request.Headers[FactoryRecordEndpoints.UploadMachineHeader] = "SOREN_NORTH";
    }

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

    private const string Root = "/factory-records/devthrottle";

    [Fact]
    public async Task A_LEDGER_WRITTEN_AS_JSON_IS_READ_BACK_AND_A_STALE_WRITE_CARRIES_THE_CURRENT_TEXT_UNDER_current()
    {
        var first = await Send("PUT", Root + "/documents", AsASessionOfTheFactory,
            """{"name":"outreach/ledger.json","text":"row-a","expectedVersion":0}""");
        Assert.Equal(200, first.Status);
        Assert.Equal("Written", first.Body.GetProperty("outcome").GetString());
        Assert.Equal(1, first.Body.GetProperty("document").GetProperty("version").GetInt32());

        await Send("PUT", Root + "/documents", AsAPerson, """{"name":"outreach/ledger.json","text":"row-a\nrow-b","expectedVersion":1}""");
        var stale = await Send("PUT", Root + "/documents", AsASessionOfTheFactory,
            """{"name":"outreach/ledger.json","text":"row-a\nrow-c","expectedVersion":1}""");

        Assert.Equal(409, stale.Status);
        Assert.Equal("Stale", stale.Body.GetProperty("outcome").GetString());
        Assert.Equal("'outreach/ledger.json' is at version 2, not 1; read it again, merge, and write once more",
            stale.Body.GetProperty("error").GetString());
        Assert.Equal("row-a\nrow-b", stale.Body.GetProperty("current").GetProperty("text").GetString());
        Assert.Equal(2, stale.Body.GetProperty("current").GetProperty("version").GetInt32());
        // The answer exactly as the wire carries it; the phase 2 QA report quotes this line.
        _output.WriteLine($"second writer: {stale.Status} {stale.Body.GetRawText()}");

        // The one-time upload naming the same document is refused in the same words, but is never handed its text:
        // the machine token reads no record, and a refusal that carried one would be a read.
        var upload = await Send("PUT", Root + "/documents", AsTheMachineToken,
            """{"name":"outreach/ledger.json","text":"anything","expectedVersion":0,"importKey":"devthrottle/outreach/ledger.json"}""");
        Assert.Equal(409, upload.Status);
        Assert.Equal("'outreach/ledger.json' is at version 2, not 0; read it again, merge, and write once more",
            upload.Body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, upload.Body.GetProperty("current").ValueKind);
        _output.WriteLine($"the upload: {upload.Status} {upload.Body.GetRawText()}");

        var read = await Send("GET", Root + "/documents/read?name=outreach/ledger.json", AsASessionOfTheFactory);
        Assert.Equal("row-a\nrow-b", read.Body.GetProperty("text").GetString());
        var list = await Send("GET", Root + "/documents", AsAPerson);
        Assert.Equal(1, list.Body.GetProperty("documents").GetArrayLength());
        Assert.DoesNotContain("row-a", list.Body.GetRawText());
        var history = await Send("GET", Root + "/documents/history?name=outreach/ledger.json&limit=1", AsAPerson);
        Assert.Equal(2, history.Body.GetProperty("total").GetInt32());
        Assert.Equal(2, history.Body.GetProperty("nextBefore").GetInt32());
    }

    [Fact]
    public async Task A_FILE_SENT_AS_BASE64_IS_KEPT_AND_ONE_OVER_THE_CAP_IS_A_413_IN_WORDS()
    {
        var kept = await Send("POST", Root + "/attachments", AsASessionOfTheFactory,
            """{"seat":"onboarding","name":"walk/01.png","contentType":"image/png","contentBase64":"AQID"}""");
        Assert.Equal(200, kept.Status);
        var id = kept.Body.GetProperty("attachment").GetProperty("id").GetString();
        var one = await Send("GET", $"{Root}/attachments/{id}", AsAPerson);
        Assert.Equal("AQID", one.Body.GetProperty("contentBase64").GetString());

        var big = Convert.ToBase64String(new byte[FactoryAttachmentStore.MaxAttachmentBytes + 1]);
        var refused = await Send("POST", Root + "/attachments", AsASessionOfTheFactory,
            $$"""{"name":"walk/big.png","contentType":"image/png","contentBase64":"{{big}}"}""");
        Assert.Equal(413, refused.Status);
        Assert.Equal("TooLarge", refused.Body.GetProperty("outcome").GetString());
        Assert.StartsWith($"'walk/big.png' holds {FactoryAttachmentStore.MaxAttachmentBytes + 1} bytes; one attachment holds at most " +
                          $"{FactoryAttachmentStore.MaxAttachmentBytes} (4 MB). Nothing was stored.",
            refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_JOURNAL_ENTRY_SENT_AS_JSON_IS_ADDED_AND_THE_UPLOAD_SENDING_IT_AGAIN_IS_ALREADY_STORED()
    {
        const string body = """{"seat":"outreach","date":"2026-10-07","text":"the day","importKey":"devthrottle/outreach/journal/2026-10-07.md"}""";

        var first = await Send("POST", Root + "/journal", AsTheMachineToken, body);
        var again = await Send("POST", Root + "/journal", AsTheMachineToken, body);

        Assert.Equal("Written", first.Body.GetProperty("outcome").GetString());
        Assert.Equal("upload", first.Body.GetProperty("entry").GetProperty("authorKind").GetString());
        Assert.Equal("SOREN_NORTH", first.Body.GetProperty("entry").GetProperty("authorId").GetString());
        Assert.Equal("AlreadyStored", again.Body.GetProperty("outcome").GetString());
        var list = await Send("GET", Root + "/journal?seat=outreach", AsAPerson);
        Assert.Equal(1, list.Body.GetProperty("total").GetInt32());
        Assert.Equal("2026-10-07", list.Body.GetProperty("entries")[0].GetProperty("date").GetString());
    }

    [Theory]
    [InlineData("GET", "/factory-records/devthrottle/journal")]
    [InlineData("POST", "/factory-records/devthrottle/journal")]
    [InlineData("GET", "/factory-records/devthrottle/journal/0b2d7c1e-0000-4000-8000-000000000001")]
    [InlineData("GET", "/factory-records/devthrottle/documents")]
    [InlineData("PUT", "/factory-records/devthrottle/documents")]
    [InlineData("GET", "/factory-records/devthrottle/documents/read")]
    [InlineData("GET", "/factory-records/devthrottle/documents/history")]
    [InlineData("GET", "/factory-records/devthrottle/attachments")]
    [InlineData("POST", "/factory-records/devthrottle/attachments")]
    [InlineData("GET", "/factory-records/devthrottle/attachments/0b2d7c1e-0000-4000-8000-000000000001")]
    public void EACH_OF_THE_TEN_ROUTES_IS_MAPPED_and_the_session_key_guard_lets_a_session_reach_it(string method, string path)
    {
        Assert.NotNull(Match(method, path));
        // The two lists must agree: a route the guard does not know is one no session can reach in production.
        Assert.True(SessionKeyGuard.Check(method, path).Allowed);
    }

    [Theory]
    [InlineData("DELETE", "/factory-records/devthrottle/journal/0b2d7c1e-0000-4000-8000-000000000001")]
    [InlineData("PUT", "/factory-records/devthrottle/journal/0b2d7c1e-0000-4000-8000-000000000001")]
    [InlineData("DELETE", "/factory-records/devthrottle/documents")]
    [InlineData("POST", "/factory-records/devthrottle/documents")]
    [InlineData("DELETE", "/factory-records/devthrottle/attachments/0b2d7c1e-0000-4000-8000-000000000001")]
    [InlineData("PUT", "/factory-records/devthrottle/attachments")]
    [InlineData("GET", "/factory-records/devthrottle")]
    [InlineData("GET", "/factory-records")]
    public void NOTHING_ELSE_IS_MAPPED_UNDER_THE_PREFIX_and_the_guard_agrees(string method, string path)
    {
        Assert.Null(Match(method, path));
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
    }
}
