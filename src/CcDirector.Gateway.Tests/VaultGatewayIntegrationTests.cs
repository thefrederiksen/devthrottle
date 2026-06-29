using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core;
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
            instancesDirectory: _instancesDir, cockpitProxyPort: 1, keyVaultPath: _keyVaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
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

        // The real resolver a gateway-attached Director uses pulls it over HTTP. Pin BYO transcription
        // mode: since issue #541 the default mode is Local (in-process, no key), so an unpinned
        // resolver would short-circuit ResolveAsync to null; this test covers the OpenAI key path.
        var resolver = new OpenAiKeyResolver(
            () => new GatewayConfig { Url = _gatewayBase, Token = Token },
            () => TranscriptionMode.Byo);
        Assert.True(resolver.UsesGateway);
        Assert.Equal("sk-integration-123", await resolver.ResolveAsync());
    }

    [Fact]
    public async Task Delete_then_resolver_returns_null_with_gateway_message()
    {
        await _http.PutAsJsonAsync("vault/keys/OPENAI_API_KEY", new { value = "sk-temp" });
        var del = await _http.DeleteAsync("vault/keys/OPENAI_API_KEY");
        del.EnsureSuccessStatusCode();

        // Pin BYO mode (default is Local since #541, which has no key) so the deleted-key path is
        // what makes ResolveAsync return null - not the mode having no key in the first place.
        var resolver = new OpenAiKeyResolver(
            () => new GatewayConfig { Url = _gatewayBase, Token = Token },
            () => TranscriptionMode.Byo);
        Assert.Null(await resolver.ResolveAsync());
        Assert.Contains("Cockpit", resolver.UnavailableMessage);
    }

    [Fact]
    public async Task Standalone_resolver_uses_local_vault_key_no_gateway()
    {
        // No gateway configured: the resolver reads the LOCAL key vault (issue #839: the vault is the
        // single key store; the config.json Voice.OpenAiKey copy is gone). Pin BYO mode (default is
        // Local since #541, which has no key) so the local OpenAI key path is exercised.
        var localVaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-it-local-" + Guid.NewGuid().ToString("N") + ".json");
        var emptyVaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-it-empty-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var localVault = new KeyVault(localVaultPath);
            localVault.Set(TranscriptionEndpointResolver.OpenAiKeyName, "sk-local-standalone");
            var withKey = new OpenAiKeyResolver(() => new GatewayConfig(), () => TranscriptionMode.Byo, localVault: localVault);
            Assert.False(withKey.UsesGateway);
            Assert.Equal("sk-local-standalone", await withKey.ResolveAsync());

            // Empty local vault: unavailable, pointed at Settings > Transcription (not the Cockpit).
            var noKey = new OpenAiKeyResolver(() => new GatewayConfig(), () => TranscriptionMode.Byo, localVault: new KeyVault(emptyVaultPath));
            Assert.Null(await noKey.ResolveAsync());
            Assert.Contains("Settings > Transcription", noKey.UnavailableMessage);
        }
        finally
        {
            try { if (File.Exists(localVaultPath)) File.Delete(localVaultPath); } catch { /* best effort */ }
            try { if (File.Exists(emptyVaultPath)) File.Delete(emptyVaultPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Resolver_re_reads_gateway_config_so_a_late_added_gateway_self_heals()
    {
        // The exact production bug: a Director boots STANDALONE (config.json has no gateway.url
        // yet), then the user adds a gateway block + sets the key in the Cockpit. Dictation must
        // start working WITHOUT restarting the Director - the resolver re-reads the mode live
        // rather than snapshotting it at construction.
        await _http.PutAsJsonAsync("vault/keys/OPENAI_API_KEY", new { value = "sk-late-gateway" });

        var mode = new GatewayConfig();                 // standalone at boot
        // Pin BYO transcription mode (default is Local since #541, which has no key); this test is
        // about the gateway-config live re-read (standalone -> attached), not the transcription mode.
        // Standalone reads an EMPTY local vault (issue #839) so the boot state is "no key" - distinct
        // from the gateway vault that holds sk-late-gateway once attached below.
        var emptyLocalVaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-it-boot-" + Guid.NewGuid().ToString("N") + ".json");
        var resolver = new OpenAiKeyResolver(() => mode, () => TranscriptionMode.Byo, localVault: new KeyVault(emptyLocalVaultPath));
        Assert.False(resolver.UsesGateway);
        Assert.Null(await resolver.ResolveAsync());
        Assert.Contains("Settings > Transcription", resolver.UnavailableMessage);

        // config.json gains a gateway block (what writing it later did) - same resolver instance.
        mode = new GatewayConfig { Url = _gatewayBase, Token = Token };
        Assert.True(resolver.UsesGateway);
        Assert.Contains("Cockpit", resolver.UnavailableMessage);
        Assert.Equal("sk-late-gateway", await resolver.ResolveAsync());
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
