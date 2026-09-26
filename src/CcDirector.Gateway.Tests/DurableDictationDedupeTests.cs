using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the durable per-upload-id dictation record (issue #1183): once an upload id is
/// DELIVERED or ABANDONED, its on-disk terminal tombstone makes every later register/complete return the
/// cached outcome and NEVER inject a second turn - past any age and across a Gateway restart - and the
/// tombstone is retired only by the client ack.
///
/// The complete/register de-dupe SHORT-CIRCUITS on the tombstone before it ever locates or injects into a
/// session, so this drives a real GatewayHost over the HTTP front door with NO Director present: a delivery
/// is simulated by writing the tombstone directly to the same on-disk staging the Gateway reads (a real
/// first delivery through the pipeline needs a live transcription provider; the tombstone WRITE on delivery
/// is proven by the store unit tests). The correctness this file pins is the NEW behavior: a delivered or
/// abandoned upload id returns its cached outcome with zero re-injection, even from a FRESH Gateway
/// instance, and ack retires it.
/// </summary>
public sealed class DurableDictationDedupeTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-dedupe-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-dedupe-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Isolate the cc-director storage root BEFORE the Gateway starts so its dictation upload store binds
        // the temp root, never the developer's real %LOCALAPPDATA%\cc-director.
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

    // ===== the four required scenarios =============================================================

    [Fact]
    public async Task Delivered_ReComplete_ReturnsCachedOutcome_AndSurvivesAFreshGatewayInstance()
    {
        // A prior successful delivery: the durable DELIVERED tombstone holds the submitted outcome.
        var uploadId = Guid.NewGuid().ToString();
        Store().MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "hello there");

        // Re-complete the SAME id: it returns the cached submitted outcome (never a second injection). Under
        // no chunks on disk the only way to get a 200 { submitted:true } is the tombstone short-circuit - the
        // live path would 409-incomplete or error, never fabricate a submitted turn.
        var first = await CompleteAsync(_http, uploadId);
        Assert.Equal(HttpStatusCode.OK, first.status);
        Assert.True(first.body.GetProperty("submitted").GetBoolean());
        Assert.Equal("hello there", first.body.GetProperty("transcript").GetString());

        // Restart: a FRESH GatewayHost over the SAME on-disk root re-reads the tombstone and de-dupes just
        // the same - the "already delivered" marker is as durable as the audio it guards.
        var restarted = NewGateway();
        await restarted.StartAsync();
        try
        {
            using var http2 = NewClient(restarted.Port);
            var afterRestart = await CompleteAsync(http2, uploadId);
            Assert.Equal(HttpStatusCode.OK, afterRestart.status);
            Assert.True(afterRestart.body.GetProperty("submitted").GetBoolean());
            Assert.Equal("hello there", afterRestart.body.GetProperty("transcript").GetString());
        }
        finally
        {
            await restarted.StopAsync();
        }
    }

    [Fact]
    public async Task Delivered_Ack_RetiresTheTombstone_AndReCompleteAfterAckDoesNotReinject()
    {
        var uploadId = Guid.NewGuid().ToString();
        Store().MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "acknowledged turn");

        // The client received the delivered outcome and acknowledges it: the tombstone is retired.
        var ack = await _http.PostAsync($"/dictation/{uploadId}/ack", content: null);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var ackBody = await ack.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ackBody.GetProperty("retired").GetBoolean());
        Assert.Null(Store().ReadRecord(uploadId)); // gone from disk

        // A re-complete after ack must NOT return the cached submitted outcome (the record is gone, so there
        // is nothing to re-inject and the client no longer holds a copy). It resolves to a non-OK outcome
        // (unknown upload / no transcription configured), never a 200 { submitted:true }.
        var afterAck = await CompleteAsync(_http, uploadId);
        Assert.NotEqual(HttpStatusCode.OK, afterAck.status);

        // Ack is idempotent: a re-ack (a lost first ack) is a harmless no-op.
        var reAck = await _http.PostAsync($"/dictation/{uploadId}/ack", content: null);
        Assert.Equal(HttpStatusCode.OK, reAck.StatusCode);
        Assert.False((await reAck.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retired").GetBoolean());
    }

    [Fact]
    public async Task Abandoned_ReComplete_ReturnsAClearDroppedOutcome_WithNoInjection()
    {
        var uploadId = Guid.NewGuid().ToString();
        Store().MarkAbandoned(uploadId, "user cancelled");

        var result = await CompleteAsync(_http, uploadId);
        Assert.Equal(HttpStatusCode.OK, result.status);
        Assert.True(result.body.GetProperty("dropped").GetBoolean());
        Assert.False(result.body.TryGetProperty("submitted", out var s) && s.GetBoolean());
        Assert.Equal("user cancelled", result.body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ReRegister_OfATerminalUploadId_ReturnsTheCachedOutcome()
    {
        // Acceptance criterion 6 read side at register: a re-register of a delivered id returns the cached
        // outcome so the client drops its copy and acknowledges instead of re-uploading.
        var uploadId = Guid.NewGuid().ToString();
        Store().MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "already done");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = Guid.NewGuid().ToString() }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("terminal").GetBoolean());
        Assert.True(body.GetProperty("submitted").GetBoolean());
        Assert.Equal("already done", body.GetProperty("transcript").GetString());
    }

    // ===== FAILED retry re-entry (issue #1185) ====================================================

    [Fact]
    public async Task Failed_ReComplete_ClearsBackToPending_AndRetainsChunks()
    {
        // A parked FAILED record is user-retryable, NOT a terminal short-circuit: a fresh complete clears it
        // back to PENDING (keeping the staged chunks) and re-drives. With no transcription key in this
        // harness the re-drive resolves to a non-OK error, but the FAILED park is cleared and the chunk kept.
        var uploadId = Guid.NewGuid().ToString();
        var store = Store();
        store.Register(uploadId);
        await store.StoreChunkAsync(uploadId, 0, Encoding.UTF8.GetBytes("AAA"), null);
        store.MarkFailed(uploadId, "audio_too_large");
        Assert.False(store.IsPending(uploadId));

        var resp = await CompleteAsync(_http, uploadId);
        Assert.NotEqual(HttpStatusCode.OK, resp.status); // not a cached terminal - it re-drove the real work

        // Under the explicit-PENDING model the FAILED marker is cleared BACK to a PENDING marker (issue #1188).
        Assert.Equal(DictationDeliveryState.Pending, store.ReadRecord(uploadId)!.State);
        Assert.True(store.IsPending(uploadId)); // back to PENDING
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(CcStorage.DictationUploads(), Guid.Parse(uploadId).ToString("N")), "*.part"));
    }

    [Fact]
    public async Task Failed_ReRegister_ClearsBackToPending_NoTerminalShortCircuit()
    {
        var uploadId = Guid.NewGuid().ToString();
        var store = Store();
        store.Register(uploadId);
        await store.StoreChunkAsync(uploadId, 0, Encoding.UTF8.GetBytes("AAA"), null);
        store.MarkFailed(uploadId, "unsupported_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = Guid.NewGuid().ToString() }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // A cleared FAILED re-register is a normal register (no terminal fields), not a short-circuit.
        Assert.False(body.TryGetProperty("terminal", out var terminal) && terminal.GetBoolean());
        // The FAILED marker is cleared back to a fresh PENDING marker carrying the new register's session id.
        Assert.Equal(DictationDeliveryState.Pending, store.ReadRecord(uploadId)!.State);
        Assert.True(store.IsPending(uploadId));
    }

    // ===== helpers =================================================================================

    // A store bound to the SAME on-disk dictation root the running Gateway reads (both resolve
    // CcStorage.DictationUploads() under the isolated CC_DIRECTOR_ROOT), so a tombstone written here is the
    // one the Gateway's complete/register handlers see.
    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

    private static async Task<(HttpStatusCode status, JsonElement body)> CompleteAsync(HttpClient http, string uploadId)
    {
        var resp = await http.PostAsJsonAsync($"/dictation/{uploadId}/complete",
            new { sessionId = Guid.NewGuid().ToString(), totalChunks = 1, sentAtUtc = DateTime.UtcNow });
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
