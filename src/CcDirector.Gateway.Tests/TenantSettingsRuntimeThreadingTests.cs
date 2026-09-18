using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hostile two-tenant runtime proofs for issue #2017. These drive the production selection and synthesis
/// seams, not the resolver in isolation: tenant A and tenant B deliberately choose different values and the
/// outbound model or speech request must carry only the value belonging to the tenant on that call.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TenantSettingsRuntimeThreadingTests : IAsyncLifetime
{
    private static readonly TenantId TenantA = new("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly TenantId TenantB = new("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-runtime-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _instances = Path.Combine(
        Path.GetTempPath(), "cc-runtime-settings-instances-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot;
    private GatewayHost _gateway = null!;

    public TenantSettingsRuntimeThreadingTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public Task InitializeAsync()
    {
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: "runtime-settings-test-token",
            authEnabled: false,
            instancesDirectory: _instances,
            workListsPath: Path.Combine(_root, "worklists.json"));
        // These tests use the host WITHOUT starting it, and the database is no longer opened by the
        // constructor - StartAsync opens it immediately after the listener binds, so that a slow database
        // cannot delay the bind and make the platform stop the site (#2383, #2585). Run the same named
        // startup step here; binding a port is what these tests do not want, not the stores being loaded.
        _gateway.EnsureStoresReady();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _gateway.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, recursive: true); } catch { }
    }

    [Fact]
    public void WingmanModel_TwoTenantsAndBothRoles_SelectOnlyTheirOwnRuntimeValues()
    {
        // Overrides must be DevThrottle internal included ids (issue #1360) or resolution falls
        // forward to the default - so the four distinct values are crossed role assignments of the
        // two included ids, which still proves each (tenant, role) cell reads only its own value.
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantA, WingmanModelRole.Thinking, "devthrottle/wingman", Now);
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantA, WingmanModelRole.Fast, "devthrottle/wingman-fast", Now);
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantB, WingmanModelRole.Thinking, "devthrottle/wingman-fast", Now);
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantB, WingmanModelRole.Fast, "devthrottle/wingman", Now);

        Assert.Equal("devthrottle/wingman", _gateway.ResolveWingmanModel(TenantA, WingmanModelRole.Thinking));
        Assert.Equal("devthrottle/wingman-fast", _gateway.ResolveWingmanModel(TenantA, WingmanModelRole.Fast));
        Assert.Equal("devthrottle/wingman-fast", _gateway.ResolveWingmanModel(TenantB, WingmanModelRole.Thinking));
        Assert.Equal("devthrottle/wingman", _gateway.ResolveWingmanModel(TenantB, WingmanModelRole.Fast));
        Assert.Throws<ArgumentException>(() => _gateway.ResolveWingmanModel(default, WingmanModelRole.Thinking));
    }

    [Fact]
    public void WingmanModel_CatalogIdOverride_FallsForwardToTheIncludedDefault()
    {
        // The Included AI revert-proof on the per-tenant path (issue #1360): a catalog-id override
        // saved by an older release must NOT reach the proxy - it would bill credits on an internal
        // feature. Put the old honor-any-override read back and this goes red.
        _gateway.TenantSettingsResolver.SetWingmanModel(TenantA, WingmanModelRole.Thinking, "zai-org/GLM-5.2", Now);

        Assert.Equal("devthrottle/wingman", _gateway.ResolveWingmanModel(TenantA, WingmanModelRole.Thinking));
    }

    [Fact]
    public async Task NarrationSynthesis_TwoTenants_SendDistinctVoiceAndModelValues()
    {
        using var data = new GatewayDbTestHarness();
        var settings = new TenantSettingsResolver(new TenantSettingsStore(data.Open()));
        settings.SetTtsVoice(TenantA, "voice-a", Now);
        settings.SetTtsModel(TenantA, "speech-a", Now);
        settings.SetTtsVoice(TenantB, "voice-b", Now);
        settings.SetTtsModel(TenantB, "speech-b", Now);

        var handler = new RecordingSpeechHandler();
        var vault = new KeyVault(Path.Combine(_root, "narration.vault"));
        vault.Set("OPENAI_API_KEY", "test-key");
        vault.Set("DEVTHROTTLE_API_KEY", "test-key");
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> unusedBrain =
            (_, _, _) => throw new InvalidOperationException("StoreSpokenAsync must not call the model.");
        var service = new WingmanVoiceService(
            unusedBrain,
            vault,
            settings,
            Path.Combine(_root, "voice-sessions.json"),
            ttsHttpClient: new HttpClient(handler));

        await service.StoreSpokenAsync(TenantA, "session-a", "spoken A", "reply A");
        await service.StoreSpokenAsync(TenantB, "session-b", "spoken B", "reply B");

        Assert.Collection(handler.Requests,
            request => AssertSpeechRequest(request, "speech-a", "voice-a", "spoken A"),
            request => AssertSpeechRequest(request, "speech-b", "voice-b", "spoken B"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StoreSpokenAsync(default, "unresolved", "must not synthesize", "reply"));
    }

    [Fact]
    public void SelfHostedRuntimeSelectors_UseExplicitLocalAndKeepGlobalDefaults()
    {
        var mode = TranscriptionModeConfig.Get();

        Assert.Equal(
            WingmanModelConfig.Resolve(mode, WingmanModelRole.Thinking).Value,
            _gateway.ResolveWingmanModel(TenantId.Local, WingmanModelRole.Thinking));
        Assert.Equal(
            WingmanModelConfig.Resolve(mode, WingmanModelRole.Fast).Value,
            _gateway.ResolveWingmanModel(TenantId.Local, WingmanModelRole.Fast));
    }

    private static void AssertSpeechRequest(string body, string model, string voice, string input)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Equal(model, json.RootElement.GetProperty("model").GetString());
        Assert.Equal(voice, json.RootElement.GetProperty("voice").GetString());
        Assert.Equal(input, json.RootElement.GetProperty("input").GetString());
    }


    private sealed class RecordingSpeechHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            };
        }
    }
}
