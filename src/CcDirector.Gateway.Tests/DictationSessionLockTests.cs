using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway front door never refuses human input because a dictation is inbound. The old issue
/// #1188 "session lock" (423 Locked on /prompt, /upload-image and /recap while a PENDING dictation
/// record existed) was removed deliberately (issue #1308): this is a single-operator tool, so a
/// collision between the operator's own phone dictation and their own typed send is theirs to make -
/// and a wedged PENDING marker used to falsely block every send for its whole lifetime. These tests
/// drive a real <see cref="GatewayHost"/> over the HTTP front door with no Director present: a request
/// that proceeds past any lock reaches the session lookup and returns 404, which is exactly the
/// "was not refused" signal.
/// </summary>
public sealed class DictationSessionLockTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-lock-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-lock-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        _gateway = NewGateway();
        await _gateway.StartAsync();
        _http = NewClient(_gateway.Port);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task PromptWhileDictationInbound_IsNotRefused()
    {
        var sessionId = Guid.NewGuid().ToString();

        // Register a dictation for the session through the real front door: this writes the PENDING marker.
        using var reg = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId }),
        };
        reg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var regResp = await _http.SendAsync(reg);
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        // The typed prompt proceeds past any lock to the session lookup (404: no Director is running).
        var (status, _) = await PromptAsync(sessionId);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task UploadImageAndRecap_AreNotRefusedWhileDictationInbound()
    {
        var sessionId = Guid.NewGuid().ToString();
        Store().MarkPending(Guid.NewGuid().ToString(), sessionId);

        var image = await _http.PostAsync($"/sessions/{sessionId}/upload-image", content: null);
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);

        var recap = await _http.PostAsync($"/sessions/{sessionId}/recap", content: null);
        Assert.Equal(HttpStatusCode.NotFound, recap.StatusCode);
    }

    // ===== helpers =================================================================================

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

    private async Task<(HttpStatusCode status, JsonElement body)> PromptAsync(string sessionId, HttpClient? http = null)
    {
        var resp = await (http ?? _http).PostAsJsonAsync($"/sessions/{sessionId}/prompt", new { text = "hello" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, body);
    }

    private GatewayHost NewGateway() => new(
        port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: false,
        instancesDirectory: _instancesDir,
        workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

    private static HttpClient NewClient(int port)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        return http;
    }

}
