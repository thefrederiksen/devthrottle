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
/// HTTP wire test for the Gateway's <c>POST /transcription/test</c> smoke-test endpoint. Boots only
/// <see cref="TranscriptionTestEndpoint"/> on an ephemeral port with a temp-file vault and a temp
/// CC_DIRECTOR_ROOT (so the test owns the transcription_mode config). Covers the branches that do
/// not call the live provider: 409 when no key is set for the current mode, and 400 when the request
/// carries no audio. The success path is not exercised here because it requires a live provider key.
/// In the "DirectorRoot" collection because it sets CC_DIRECTOR_ROOT.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TranscriptionTestEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    // Initialized in InitializeAsync (xUnit's async fixture setup), not the constructor - the
    // compiler cannot see that, so null! suppresses the false nullable warning.
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _vaultPath = null!;

    public TranscriptionTestEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-txtest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CcStorage.ConfigJson())!);
        _vaultPath = Path.Combine(Path.GetTempPath(), "cc-vault-txtest-" + Guid.NewGuid().ToString("N") + ".json");

        var port = AllocateFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add(baseUrl);
        TranscriptionTestEndpoint.Map(_app, new KeyVault(_vaultPath));
        await _app.StartAsync();

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
    public async Task Test_NoKeyForMode_Returns409_WithMode()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        // No key seeded for the BYO mode.
        var resp = await _http.PostAsync("/transcription/test", Audio(new byte[] { 1, 2, 3 }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("byo", doc.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Test_KeySetButNoAudio_Returns400()
    {
        TranscriptionModeConfig.Set(TranscriptionMode.Byo);
        SeedVault(TranscriptionEndpointResolver.OpenAiKeyName, "sk-byo-123");

        // Key is present, so we get past the 409, but the body is empty -> 400 before any provider call.
        var resp = await _http.PostAsync("/transcription/test", Audio(Array.Empty<byte>()));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
