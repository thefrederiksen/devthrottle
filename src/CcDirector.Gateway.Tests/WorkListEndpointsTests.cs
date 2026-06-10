using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the named work-list endpoints (issue #273). Boots only
/// <see cref="WorkListEndpoints"/> on an ephemeral loopback port with a fresh in-memory store
/// (no Tailscale, no registry, no auth) and drives the full CRUD + ordering + single-consumer
/// claim contract, asserting the JSON shapes the Cockpit board and the queue runner depend on.
/// </summary>
public sealed class WorkListEndpointsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        WorkListEndpoints.Map(_app, new WorkListStore());
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task CreateList(string name)
    {
        var resp = await _http.PostAsJsonAsync("/lists", new { name });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<WorkListDto> GetList(string name)
    {
        var resp = await _http.GetAsync($"/lists/{name}");
        resp.EnsureSuccessStatusCode();
        var dto = JsonSerializer.Deserialize<WorkListDto>(await resp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(dto);
        return dto;
    }

    [Fact]
    public async Task Post_CreatesList_ThenGetAllReturnsIt()
    {
        await CreateList("backlog");

        var resp = await _http.GetAsync("/lists");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("lists").EnumerateArray()
            .Select(l => l.GetProperty("name").GetString()).ToList();
        Assert.Contains("backlog", names);
    }

    [Fact]
    public async Task Post_DuplicateName_Returns409()
    {
        await CreateList("backlog");
        var resp = await _http.PostAsJsonAsync("/lists", new { name = "backlog" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task PostItem_AppendsAndRoundTripsSourceIdArea_InOrder()
    {
        await CreateList("backlog");

        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "262", area = "Gateway" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "263", area = "Core" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "264" });

        var list = await GetList("backlog");
        Assert.Equal(new[] { "262", "263", "264" }, list.Items.Select(i => i.Id).ToArray());
        Assert.Equal("github", list.Items[0].Source);
        Assert.Equal("Gateway", list.Items[0].Area);
        Assert.Null(list.Items[2].Area);
    }

    [Fact]
    public async Task PostItem_MixedSources_AllAcceptedAndOrdered()
    {
        await CreateList("backlog");

        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "262" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "devops", id = "1203" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "jira", id = "CCD-44" });

        var list = await GetList("backlog");
        Assert.Equal(new[] { "github", "devops", "jira" }, list.Items.Select(i => i.Source).ToArray());
        Assert.Equal(new[] { "262", "1203", "CCD-44" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task PatchItems_Reorders()
    {
        await CreateList("backlog");
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "1" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "2" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "3" });

        var reordered = new[]
        {
            new { source = "github", id = "3" },
            new { source = "github", id = "1" },
            new { source = "github", id = "2" },
        };
        var patch = await _http.PatchAsync("/lists/backlog/items",
            new StringContent(JsonSerializer.Serialize(reordered), Encoding.UTF8, "application/json"));
        patch.EnsureSuccessStatusCode();

        var list = await GetList("backlog");
        Assert.Equal(new[] { "3", "1", "2" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task DeleteItem_BySourceAndId_RemovesOnlyThatItem()
    {
        await CreateList("backlog");
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "1" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "devops", id = "2" });
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "3" });

        var del = await _http.DeleteAsync("/lists/backlog/items/devops/2");
        del.EnsureSuccessStatusCode();

        var list = await GetList("backlog");
        Assert.Equal(new[] { "1", "3" }, list.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Consumer_Claim_DoubleClaimRefused_ReleaseThenReclaim()
    {
        await CreateList("backlog");

        var first = await _http.PostAsync("/lists/backlog/consumer", null);
        first.EnsureSuccessStatusCode();

        var second = await _http.PostAsync("/lists/backlog/consumer", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var release = await _http.DeleteAsync("/lists/backlog/consumer");
        release.EnsureSuccessStatusCode();

        var third = await _http.PostAsync("/lists/backlog/consumer", null);
        third.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetPayload_HasNoStatusField()
    {
        await CreateList("backlog");
        await _http.PostAsJsonAsync("/lists/backlog/items", new { source = "github", id = "262" });

        var resp = await _http.GetAsync("/lists/backlog");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain("status", fields);
        Assert.DoesNotContain("flow", fields);
        // and no per-item status either
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var itemFields = item.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain("status", itemFields);
            Assert.DoesNotContain("flow", itemFields);
        }
    }

    [Fact]
    public async Task GetUnknownList_Returns404()
    {
        var resp = await _http.GetAsync("/lists/ghost");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
