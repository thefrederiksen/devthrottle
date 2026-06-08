using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof of the key-vault feature across the process boundary: boots a REAL
/// <see cref="GatewayHost"/> (auth on, Tailscale off via TestEnvironment, isolated dirs +
/// isolated key-vault file, ephemeral port - so it never touches a running Gateway or the
/// real %LOCALAPPDATA% store), then drives the vault over HTTP AND through the real
/// <see cref="OpenAiKeyResolver"/> a Director uses. This is the live contract the Cockpit
/// Keys page and Director dictation depend on.
/// </summary>
public sealed class VaultGatewayIntegrationTests : IAsyncLifetime
{
    private const string Token = "test-token-vault";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _gatewayBase = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-vault-it-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string _keyVaultPath =
        Path.Combine(Path.GetTempPath(), "cc-vault-it-" + Guid.NewGuid().ToString("N") + ".json");

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1, keyVaultPath: _keyVaultPath);
        await _gateway.StartAsync();

        _gatewayBase = $"http://127.0.0.1:{_gateway.Port}";
        _http = new HttpClient { BaseAddress = new Uri(_gatewayBase + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (File.Exists(_keyVaultPath)) File.Delete(_keyVaultPath); } catch { }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task Vault_endpoints_require_auth()
    {
        using var anon = new HttpClient { BaseAddress = new Uri(_gatewayBase + "/") };
        var resp = await anon.GetAsync("vault/keys");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Put_then_resolver_pulls_the_key()
    {
        // Set the key the way the Cockpit Keys page does.
        var put = await _http.PutAsJsonAsync("vault/keys/OPENAI_API_KEY", new { value = "sk-integration-123" });
        put.EnsureSuccessStatusCode();

        // The names list shows it (and never a value).
        var list = await _http.GetAsync("vault/keys");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains("OPENAI_API_KEY", listBody);
        Assert.DoesNotContain("sk-integration-123", listBody);

        // The real resolver a gateway-attached Director uses pulls it over HTTP.
        var resolver = new OpenAiKeyResolver(
            new AgentOptions(),
            new GatewayConfig { Url = _gatewayBase, Token = Token });
        Assert.True(resolver.UsesGateway);
        Assert.Equal("sk-integration-123", await resolver.ResolveAsync());
    }

    [Fact]
    public async Task Delete_then_resolver_returns_null_with_gateway_message()
    {
        await _http.PutAsJsonAsync("vault/keys/OPENAI_API_KEY", new { value = "sk-temp" });
        var del = await _http.DeleteAsync("vault/keys/OPENAI_API_KEY");
        del.EnsureSuccessStatusCode();

        var resolver = new OpenAiKeyResolver(
            new AgentOptions(),
            new GatewayConfig { Url = _gatewayBase, Token = Token });
        Assert.Null(await resolver.ResolveAsync());
        Assert.Contains("Cockpit", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task Standalone_resolver_uses_local_key_no_gateway()
    {
        // No gateway configured: the resolver uses the local Settings > Voice key.
        var withKey = new OpenAiKeyResolver(
            new AgentOptions { OpenAiKey = "sk-local-standalone" },
            new GatewayConfig());
        Assert.False(withKey.UsesGateway);
        Assert.Equal("sk-local-standalone", await withKey.ResolveAsync());

        // No local key either: unavailable, pointed at Settings > Voice (not the Cockpit).
        var noKey = new OpenAiKeyResolver(new AgentOptions(), new GatewayConfig());
        Assert.Null(await noKey.ResolveAsync());
        Assert.Contains("Settings > Voice", noKey.UnavailableMessage);
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
