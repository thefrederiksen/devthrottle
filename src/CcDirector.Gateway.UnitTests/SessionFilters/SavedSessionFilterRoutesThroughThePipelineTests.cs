using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.SessionFilters;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.SessionFilters;

/// <summary>
/// The saved session filter routes (issue #4009) driven AS THEY ARE MAPPED: JSON in, through the endpoint the route
/// table really holds, JSON out. The handler tests call the handlers directly, so they could not see what went wrong
/// live - the save was mapped as `async (HttpContext ctx) =&gt; await ...`, which ASP.NET binds as a plain request
/// delegate that DISCARDS the result: the filter was stored and the Cockpit got a 200 with an empty body
/// ("Unexpected end of JSON input"). Every route here must answer with the body the Cockpit reads.
/// </summary>
public sealed class SavedSessionFilterRoutesThroughThePipelineTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private readonly ServiceProvider _services;
    private readonly Routes _routes;

    public SavedSessionFilterRoutesThroughThePipelineTests()
    {
        var tenants = new SingleTenantContext();
        var store = new SavedSessionFilterStore(_h.Open(tenants));
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        SavedSessionFilterEndpoints.Map(_routes, store, new HostedTenantBoundary(tenants, new DeviceRegistry(), hosted: false));
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

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
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

    private async Task<(int Status, string Body)> Send(string method, string path, string? json = null)
    {
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.RouteValues = matched!.Value.Values;
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    private const string Route = "/gateway/session-filters";

    [Fact]
    public async Task Save_AsMapped_Answers201WithTheSavedFilter_NeverAnEmptyBody()
    {
        var (status, body) = await Send("POST", Route,
            """{ "name": "DevThrottle", "colour": "blue", "repos": ["devthrottle", "devthrottle_internal"], "machines": [] }""");

        Assert.True(body.Length > 0, $"the save answered {status} with an EMPTY body - the Cockpit cannot read it");
        Assert.Equal(201, status);
        var saved = JsonDocument.Parse(body).RootElement;
        Assert.Equal("DevThrottle", saved.GetProperty("name").GetString());
        Assert.Equal("#3B82F6", saved.GetProperty("colourHex").GetString());
        Assert.Equal(32, saved.GetProperty("id").GetString()!.Length);
    }

    [Fact]
    public async Task Save_AsMapped_ARefusalIsASentence()
    {
        await Send("POST", Route, """{ "name": "Product", "colour": "blue", "repos": ["a"] }""");

        var (status, body) = await Send("POST", Route, """{ "name": "product", "colour": "blue", "repos": ["a"] }""");

        Assert.Equal(409, status);
        Assert.Contains("already exists", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_Change_AndDelete_AsMapped_AnswerAsTheCockpitReadsThem()
    {
        var (_, created) = await Send("POST", Route, """{ "name": "Client", "colour": "green", "repos": ["acme"] }""");
        var id = JsonDocument.Parse(created).RootElement.GetProperty("id").GetString();

        var (listStatus, list) = await Send("GET", Route);
        Assert.Equal(200, listStatus);
        Assert.Equal("Client", JsonDocument.Parse(list).RootElement.GetProperty("filters")[0].GetProperty("name").GetString());

        var (putStatus, put) = await Send("PUT", $"{Route}/{id}", """{ "name": "Client X", "colour": "red", "repos": ["acme"] }""");
        Assert.Equal(200, putStatus);
        Assert.Equal("Client X", JsonDocument.Parse(put).RootElement.GetProperty("name").GetString());

        var (deleteStatus, _) = await Send("DELETE", $"{Route}/{id}");
        Assert.Equal(204, deleteStatus);
        var (missingStatus, missing) = await Send("DELETE", $"{Route}/{id}");
        Assert.Equal(404, missingStatus);
        Assert.Contains("no longer exists", missing);
    }
}
