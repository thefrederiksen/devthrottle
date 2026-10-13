using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE GATEWAY DRIVES A HELD DELIVERY TO ITS END, proven through the REAL host (Voice Delivery mission, phase 5).
///
/// Phase 4, case 2d: under full load the Director's tunnel dropped, the Gateway held the recording as "still
/// delivering", and nothing retried it for 7 minutes 44 seconds because the only retry lived in a frozen background tab.
/// Here the same thing happens on purpose: the first complete runs out of time and its question gets no answer; the
/// tunnel drops and comes back - with the registry entry SURVIVING, so the registry's "Director added" never fires - and
/// with no further client call the Gateway asks, hears that the Director never saw the id, sends once, and the recording
/// is delivered.
///
/// The rig: a real <see cref="GatewayHost"/> over loopback HTTP, the real routes, the real store on disk, and a real
/// tunnel-connected <see cref="FakeTunnelDirector"/> that keeps its own delivery record the way a Director does - it
/// types a delivery id once and refuses a copy. The driver's tick is an hour long, so every drive here was woken by the
/// tunnel coming back or the Gateway starting, never by the tick. Only the speech-to-text provider is a stub.
/// </summary>
[Collection("DirectorRoot")]
public sealed class GatewayDrivesHeldDeliveriesTests : IAsyncLifetime
{
    private const string Token = "test-token-held-deliveries";
    private const string DirectorId = "dir-held-deliveries";
    private const string Transcript = "quokka lantern the owner said this once";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-held-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath = Path.Combine(Path.GetTempPath(), "cc-held-vault-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _sessionId = Guid.NewGuid().ToString();

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector? _director;

    // The Director's side, kept across its reconnects the way a real Director keeps its delivery record on disk.
    private readonly object _gate = new();
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly List<string> _typed = new();
    private int _prompts;
    private int _questions;
    /// <summary>True while the Director is starved: every prompt and every question runs out of time.</summary>
    private volatile bool _starved = true;

    public GatewayDrivesHeldDeliveriesTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-held-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_test_key");
        await StartGatewayAsync();
        await ConnectDirectorAsync();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_director is not null) await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* cleanup */ }
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task A_repeated_complete_for_a_recording_the_Gateway_owns_drives_nothing()
    {
        // Proves contract section 2: once the Gateway owns a recording, a repeated complete - a lost answer, an old client
        // - answers the current state and sends nothing, asks nothing, transcribes nothing.
        var uploadId = await RegisterAndUploadAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await CompleteAsync(uploadId)).Status);
        int prompts, questions;
        lock (_gate) { prompts = _prompts; questions = _questions; }
        var linesBefore = (await DecisionsAsync(uploadId)).Count;

        var again = await CompleteAsync(uploadId, resumed: true);
        var andAgain = await CompleteAsync(uploadId, resumed: true);

        Assert.Equal(HttpStatusCode.Accepted, again.Status);
        Assert.Equal(HttpStatusCode.Accepted, andAgain.Status);
        Assert.Equal("no-answer", andAgain.Body.GetProperty("directorState").GetString());
        lock (_gate)
        {
            Assert.Equal(prompts, _prompts);
            Assert.Equal(questions, _questions);
        }
        // The only lines the repeats add are the "retried" line each resumed complete has always written.
        var added = (await DecisionsAsync(uploadId)).Skip(linesBefore).Select(d => d.Name).ToList();
        Assert.Equal(new[] { DeliveryDecisions.Retried, DeliveryDecisions.Retried }, added);
    }

    [Fact]
    public async Task The_outcome_route_reads_and_never_sends_or_asks()
    {
        // Proves GET /dictation/{id}/outcome is read-only through the real route: a held recording reads 202 with its
        // directorState however often it is read, and no prompt and no question reach the Director; an unknown id is 404.
        var uploadId = await RegisterAndUploadAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await CompleteAsync(uploadId)).Status);
        int prompts, questions;
        lock (_gate) { prompts = _prompts; questions = _questions; }
        var linesBefore = (await DecisionsAsync(uploadId)).Count;

        for (var i = 0; i < 3; i++)
        {
            var read = await _http.GetAsync($"dictation/{uploadId}/outcome");
            Assert.Equal(HttpStatusCode.Accepted, read.StatusCode);
            Assert.Equal("no-answer", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("directorState").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"dictation/{Guid.NewGuid()}/outcome")).StatusCode);

        lock (_gate)
        {
            Assert.Equal(prompts, _prompts);
            Assert.Equal(questions, _questions);
        }
        Assert.Equal(linesBefore, (await DecisionsAsync(uploadId)).Count);
    }

    // ===== rig ============================================================================================

    private int Prompts
    {
        get { lock (_gate) return _prompts; }
    }

    private async Task StartGatewayAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            dictationTranscription: StubTranscription());
        // An hour-long tick: every drive in these tests was woken by the tunnel coming back or by the Gateway starting.
        _gateway.HeldDeliveryTickInterval = TimeSpan.FromHours(1);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    private async Task ConnectDirectorAsync()
    {
        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, dispatch: Dispatch);
        await _director.PushSnapshotAsync(new SessionDto
        {
            SessionId = _sessionId,
            DirectorId = DirectorId,
            Status = "Running",
            ActivityState = "WaitingForInput",
            LastActivityAt = DateTime.UtcNow,
        });
    }

    // The Director: starved (nothing answers in time), or healthy with its own delivery record - it types a delivery id
    // once, refuses a copy as delivered, and answers the question from that record.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        if (cmd.Verb == "prompt")
        {
            var body = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson)!;
            lock (_gate)
            {
                _prompts++;
                if (_starved)
                    return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
                if (body.DeliveryId is { } id && !_delivered.Add(id))
                    return FakeTunnelDirector.Ok(new PromptResponse { Accepted = false, DeliveryState = DeliveryState.Delivered, Error = "already delivered" });
                _typed.Add(body.DeliveryId ?? "");
                return FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered });
            }
        }
        if (cmd.Verb == DeliveryStateRequest.Verb)
        {
            var asked = JsonSerializer.Deserialize<DeliveryStateRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson)!;
            lock (_gate)
            {
                _questions++;
                if (_starved)
                    return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer the question");
                return FakeTunnelDirector.Ok(new DeliveryStateResponse
                {
                    State = _delivered.Contains(asked.DeliveryId) ? DeliveryState.Delivered : DeliveryState.Unknown,
                });
            }
        }
        return FakeTunnelDirector.Ok(new PromptResponse());
    }

    private async Task<string> RegisterAndUploadAsync()
    {
        var uploadId = Guid.NewGuid().ToString();
        using (var req = new HttpRequestMessage(HttpMethod.Post, "dictation/upload") { Content = JsonContent.Create(new { sessionId = _sessionId }) })
        {
            req.Headers.Add("Idempotency-Key", uploadId);
            Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(req)).StatusCode);
        }
        var wav = PcmWav.Wrap(new byte[16_000 * 2], 16_000, 1, 16);
        using var content = new ByteArrayContent(wav);
        using var put = new HttpRequestMessage(HttpMethod.Put, $"dictation/{uploadId}/chunk/0") { Content = content };
        put.Headers.Add("X-Chunk-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)).ToLowerInvariant());
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(put)).StatusCode);
        return uploadId;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> CompleteAsync(string uploadId, bool resumed = false)
    {
        var resp = await _http.PostAsJsonAsync($"dictation/{uploadId}/complete", new
        {
            sessionId = _sessionId,
            totalChunks = 1,
            mime = "audio/wav",
            ext = "wav",
            sentAtUtc = DateTime.UtcNow,
            resumed,
        });
        return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<List<(string? Name, JsonElement Facts)>> DecisionsAsync(string uploadId)
    {
        var read = await _http.GetFromJsonAsync<JsonElement>($"dictation/{uploadId}/decisions");
        return read.GetProperty("decisions").EnumerateArray()
            .Select(l => (l.GetProperty("decision").GetString(),
                l.TryGetProperty("facts", out var f) ? f.Clone() : default))
            .ToList();
    }

    private GatewayTranscriptionService StubTranscription() => new(
        new KeyVault(_vaultPath),
        http: new HttpClient(new TranscriptStub()),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "transcription-history")),
        audioArchive: new TranscriptionAudioArchive(false, Path.Combine(_root, "audio")), aiGate: CcDirector.Gateway.Tenancy.TenantAiGate.SelfHost);

    private sealed class TranscriptStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"text\":\"{Transcript}\"}}", Encoding.UTF8, "application/json"),
            });
    }
}
