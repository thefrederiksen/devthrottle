using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Integration tests for the Director's voice endpoints  (POST /voice/command,
/// GET /voice/status).  These spin up a real ControlApiHost on an ephemeral
/// loopback port so we exercise multipart parsing, JSON routing, and the
/// VoiceService end-to-end up to (but not including) the actual Whisper call.
///
/// We deliberately do NOT exercise Whisper itself - that requires a live OpenAI
/// key and we want these tests to run offline. Instead we verify that:
///   - /voice/status correctly reports availability based on the resolved key
///   - /voice/command returns the structured "no_key" response when no key is set
///   - /voice/command returns BadRequest when no audio file is uploaded
///
/// We force the no-key path by clearing both the AgentOptions field and the
/// OPENAI_API_KEY env var for the duration of each test.
/// </summary>
public sealed class VoiceEndpointTests : IAsyncLifetime
{
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private HttpClient _client = null!;
    private string? _originalEnv;
    private string? _originalRoot;
    private string _tempRoot = "";

    public async Task InitializeAsync()
    {
        // Stash and clear OPENAI_API_KEY so tests are deterministic regardless of the
        // developer's machine. Restored in DisposeAsync.
        _originalEnv = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        // Voice transcription now routes through the Gateway routing resolver (issue #587), which
        // reads the machine's config.json for any configured Gateway. Point CC_DIRECTOR_ROOT at an
        // empty temp dir so there is NO gateway block and the standalone no-key path is exercised
        // deterministically regardless of the developer's machine config.
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _tempRoot = Path.Combine(Path.GetTempPath(), "voice-endpoint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempRoot);

        _sm = new SessionManager(new AgentOptions { OpenAiKey = null });
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();

        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _originalEnv);

        try
        {
            var f = Path.Combine(InstanceRegistration.InstancesDirectory, $"{_host.DirectorId}.json");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* test cleanup, ignore */ }

        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* test cleanup, ignore */ }
    }

    [Fact]
    public async Task VoiceStatus_reports_unavailable_when_no_key()
    {
        var resp = await _client.GetAsync("voice/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<VoiceStatusDto>();
        Assert.NotNull(body);
        Assert.False(body!.available);
    }

    [Fact]
    public async Task VoiceStatus_reports_available_when_options_key_set()
    {
        // Rebuild the host with a key set in AgentOptions.
        // We don't actually call Whisper - just verify the availability flag flips.
        await _host.StopAsync();
        _sm.Dispose();

        var opts = new AgentOptions { OpenAiKey = "sk-test-not-a-real-key" };
        _sm = new SessionManager(opts);
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask, useEphemeralPort: true);
        var port = await _host.StartAsync();
        _client.Dispose();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _client.GetAsync("voice/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<VoiceStatusDto>();
        Assert.True(body!.available);
    }

    [Fact]
    public async Task VoiceCommand_returns_no_key_when_unconfigured()
    {
        // Upload a tiny dummy audio blob; the service should bail before reaching Whisper.
        using var form = new MultipartFormDataContent();
        var bytes = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }; // EBML magic; arbitrary
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(content, "file", "recording.webm");

        var resp = await _client.PostAsync("voice/command", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<VoiceCommandResponse>();
        Assert.NotNull(body);
        Assert.Equal("no_key", body!.Status);
        Assert.Contains("OpenAI", body.ReplyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoiceCommand_returns_400_when_no_audio()
    {
        // multipart with no files
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("not-a-file"), "model");

        var resp = await _client.PostAsync("voice/command", form);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task VoiceCommand_returns_400_for_non_multipart()
    {
        var resp = await _client.PostAsync("voice/command",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class VoiceStatusDto
    {
        public bool available { get; set; }
    }
}
