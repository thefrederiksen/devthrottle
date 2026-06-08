using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire test for the key-vault endpoints. Boots only <see cref="VaultEndpoints"/> on an
/// ephemeral port with a temp-file vault (no Tailscale, no registry, no auth), then drives the
/// full set/get/list/delete contract. Proves the routes and JSON shapes the Cockpit Keys page
/// and the Director's resolver depend on.
/// </summary>
public sealed class VaultEndpointsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _vaultPath = null!;

    public async Task InitializeAsync()
    {
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-ep-" + Guid.NewGuid().ToString("N") + ".json");

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        VaultEndpoints.Map(_app, new KeyVault(_vaultPath));
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Get_UnknownKey_Returns404()
    {
        var resp = await _http.GetAsync("/vault/keys/OPENAI_API_KEY");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_ThenGet_ReturnsValue()
    {
        var put = await _http.PutAsJsonAsync("/vault/keys/OPENAI_API_KEY", new { value = "sk-live-xyz" });
        put.EnsureSuccessStatusCode();

        var get = await _http.GetAsync("/vault/keys/OPENAI_API_KEY");
        get.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("sk-live-xyz", doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Put_NoValueField_Returns400()
    {
        var resp = await _http.PutAsJsonAsync("/vault/keys/OPENAI_API_KEY", new { notvalue = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsNamesOnly_NeverValues()
    {
        await _http.PutAsJsonAsync("/vault/keys/OPENAI_API_KEY", new { value = "sk-secret" });

        var resp = await _http.GetAsync("/vault/keys");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("OPENAI_API_KEY", names);
        Assert.DoesNotContain("sk-secret", body); // the list must not leak any value
    }

    [Fact]
    public async Task Delete_RemovesKey()
    {
        await _http.PutAsJsonAsync("/vault/keys/OPENAI_API_KEY", new { value = "sk-gone" });

        var del = await _http.DeleteAsync("/vault/keys/OPENAI_API_KEY");
        del.EnsureSuccessStatusCode();

        var get = await _http.GetAsync("/vault/keys/OPENAI_API_KEY");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
