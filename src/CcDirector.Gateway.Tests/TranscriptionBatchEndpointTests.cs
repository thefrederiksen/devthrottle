using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire test for the single Gateway speech-to-text endpoint (issue #839),
/// <c>POST /transcription</c>. Boots only <see cref="TranscriptionBatchEndpoint"/> on an ephemeral
/// port with a temp-file vault and a temp CC_DIRECTOR_ROOT (so the test owns the transcription_mode
/// config). Covers the branches that do not call a live provider: 409 when no DevThrottle account key
/// is set, and 400 when the request carries no audio. The success path needs a live provider key and is not
/// exercised here. In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TranscriptionBatchEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    // Initialized in InitializeAsync (xUnit async fixture setup); the compiler cannot see that, so
    // null! suppresses the false nullable warning.
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _vaultPath = null!;

    public TranscriptionBatchEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-txbatch-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-txbatch-" + Guid.NewGuid().ToString("N") + ".json");

        // Issue #2161: bind an operating-system-assigned port, then learn the number it gave us. The
        // address cannot be built up front any more - "0" is not something a client can dial.
        var bindUrl = $"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(bindUrl);
        // The boundary is required and non-nullable now (finding I1-01). Self-host harness, so the REAL
        // self-host boundary: built over the SingleTenantContext, it always resolves Local.
        TranscriptionBatchEndpoint.Map(_app, false, new KeyVault(_vaultPath),
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false));
        await _app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{BoundPort.Of(_app)}";

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void SeedVault(string name, string value) => new KeyVault(_vaultPath).Set(name, value);

    private static HttpContent Audio(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/webm");
        return content;
    }

    [Fact]
    public async Task Transcription_NoDevThrottleKey_Returns409_WithMode()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        // Legacy BYO config migrates forward; no DevThrottle key is seeded.
        var resp = await _http.PostAsync("/transcription", Audio(new byte[] { 1, 2, 3 }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("devthrottle", doc.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Transcription_KeySetButNoAudio_Returns400()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        SeedVault(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_abc");

        // Key is present, so we get past the 409, but the body is empty -> 400 before any provider call.
        var resp = await _http.PostAsync("/transcription", Audio(Array.Empty<byte>()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

}
