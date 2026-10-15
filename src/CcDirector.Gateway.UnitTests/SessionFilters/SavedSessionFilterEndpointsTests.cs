using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.SessionFilters;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.SessionFilters;

/// <summary>
/// The saved session filter routes (issue #4009), with real JSON bodies in and out: the statuses and the field names
/// the Cockpit reads, every refusal as a sentence, the 403 when no account resolves, and that a session key may not
/// reach the routes at all.
/// </summary>
public sealed class SavedSessionFilterEndpointsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly SavedSessionFilterStore _store;
    private readonly HostedTenantBoundary _boundary;

    public SavedSessionFilterEndpointsTests()
    {
        var tenants = new SingleTenantContext();
        _store = new SavedSessionFilterStore(_h.Open(tenants));
        _boundary = new HostedTenantBoundary(tenants, new DeviceRegistry(), hosted: false);
    }

    public void Dispose() => _h.Dispose();

    private static DefaultHttpContext Request(string? body = null)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, JsonElement Body)> RunAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (ctx.Response.StatusCode, json);
    }

    private const string ProductBody = """{ "name": "Product", "colour": "teal", "repos": ["devthrottle", "website"], "machines": [] }""";

    private async Task<string> CreateProductAsync()
    {
        var ctx = Request(ProductBody);
        var (status, body) = await RunAsync(await SavedSessionFilterEndpoints.HandleCreateAsync(ctx, _store, _boundary, Now), ctx);
        Assert.Equal(201, status);
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_ThenList_ReturnsTheFilterAndThePalette()
    {
        var id = await CreateProductAsync();

        var ctx = Request();
        var (status, body) = await RunAsync(SavedSessionFilterEndpoints.HandleList(ctx, _store, _boundary), ctx);

        Assert.Equal(200, status);
        var filter = Assert.Single(body.GetProperty("filters").EnumerateArray());
        Assert.Equal(id, filter.GetProperty("id").GetString());
        Assert.Equal("Product", filter.GetProperty("name").GetString());
        Assert.Equal("teal", filter.GetProperty("colour").GetString());
        Assert.Equal("#14B8A6", filter.GetProperty("colourHex").GetString());
        Assert.Equal(new[] { "devthrottle", "website" }, filter.GetProperty("repos").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(filter.GetProperty("machines").EnumerateArray());
        Assert.Equal(SavedSessionFilterStore.Colours.Count, body.GetProperty("colours").GetArrayLength());
        Assert.Equal("blue", body.GetProperty("colours")[0].GetProperty("key").GetString());
        Assert.Equal("#3B82F6", body.GetProperty("colours")[0].GetProperty("hex").GetString());
    }

    [Fact]
    public async Task Create_DuplicateName_Answers409WithASentence()
    {
        await CreateProductAsync();

        var ctx = Request(ProductBody);
        var (status, body) = await RunAsync(await SavedSessionFilterEndpoints.HandleCreateAsync(ctx, _store, _boundary, Now), ctx);

        Assert.Equal(409, status);
        Assert.Contains("already exists", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_InvalidColour_Answers400WithASentence()
    {
        var ctx = Request("""{ "name": "Product", "colour": "magenta", "repos": ["devthrottle"] }""");
        var (status, body) = await RunAsync(await SavedSessionFilterEndpoints.HandleCreateAsync(ctx, _store, _boundary, Now), ctx);

        Assert.Equal(400, status);
        Assert.Contains("is not a filter colour", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_MalformedJson_Answers400()
    {
        var ctx = Request("{ not json");
        var (status, body) = await RunAsync(await SavedSessionFilterEndpoints.HandleCreateAsync(ctx, _store, _boundary, Now), ctx);

        Assert.Equal(400, status);
        Assert.Equal("invalid JSON", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_RenamesTheFilter()
    {
        var id = await CreateProductAsync();

        var ctx = Request("""{ "name": "Products", "colour": "red", "repos": ["devthrottle"], "machines": ["DEVLINUX"] }""");
        var (status, body) = await RunAsync(await SavedSessionFilterEndpoints.HandleUpdateAsync(ctx, _store, _boundary, id, Now), ctx);

        Assert.Equal(200, status);
        Assert.Equal(id, body.GetProperty("id").GetString());
        Assert.Equal("Products", body.GetProperty("name").GetString());
        Assert.Equal("#EF4444", body.GetProperty("colourHex").GetString());
        Assert.Equal("DEVLINUX", body.GetProperty("machines")[0].GetString());
    }

    [Fact]
    public async Task Update_UnknownId_Answers404()
    {
        var ctx = Request(ProductBody);
        var (status, _) = await RunAsync(await SavedSessionFilterEndpoints.HandleUpdateAsync(ctx, _store, _boundary, "nope", Now), ctx);

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Delete_Answers204_ThenA404()
    {
        var id = await CreateProductAsync();

        var first = Request();
        Assert.Equal(204, (await RunAsync(SavedSessionFilterEndpoints.HandleDelete(first, _store, _boundary, id), first)).Status);
        var second = Request();
        Assert.Equal(404, (await RunAsync(SavedSessionFilterEndpoints.HandleDelete(second, _store, _boundary, id), second)).Status);
    }

    [Fact]
    public async Task EveryRoute_WithNoBoundary_Answers403()
    {
        var list = Request();
        Assert.Equal(403, (await RunAsync(SavedSessionFilterEndpoints.HandleList(list, _store, null), list)).Status);
        var create = Request(ProductBody);
        Assert.Equal(403, (await RunAsync(await SavedSessionFilterEndpoints.HandleCreateAsync(create, _store, null, Now), create)).Status);
        var update = Request(ProductBody);
        Assert.Equal(403, (await RunAsync(await SavedSessionFilterEndpoints.HandleUpdateAsync(update, _store, null, "x", Now), update)).Status);
        var delete = Request();
        Assert.Equal(403, (await RunAsync(SavedSessionFilterEndpoints.HandleDelete(delete, _store, null, "x"), delete)).Status);
    }

    [Theory]
    [InlineData("GET", "/gateway/session-filters")]
    [InlineData("POST", "/gateway/session-filters")]
    [InlineData("PUT", "/gateway/session-filters/0123456789abcdef0123456789abcdef")]
    [InlineData("DELETE", "/gateway/session-filters/0123456789abcdef0123456789abcdef")]
    public void SessionKey_IsRefusedEveryRoute(string method, string path)
    {
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
    }
}
