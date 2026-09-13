using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LOOP, driven end to end over the real HTTP front door of a real <see cref="GatewayHost"/>
/// (devthrottle_internal issue 1997).
///
/// The unit-level proof of the gate lives in <c>DictationExitedSessionTests</c>; this file proves the thing
/// the owner actually watched happen, which only the whole route can show: complete a dictation for a
/// session whose agent has exited, then complete it AGAIN exactly as the phone's background driver does,
/// and the second call must be a cached no-op against a resolved record rather than a second transcription.
/// Before the fix the first complete transcribed the clip, answered 410, and left the record PENDING - so
/// the second complete transcribed it all over again, and so did the third, and there was no end to it.
///
/// The transcription service is INJECTED (the host's own seam) so the provider is a counting stub: zero
/// calls across both completes is the measured evidence that no transcript was paid for, and it is measured
/// at the transport rather than asserted about a mock.
/// </summary>
public sealed class DictationExitedSessionLoopTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";
    private const string DirectorId = "director-1";

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-exitloop-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-exitloop-instances-" + Guid.NewGuid().ToString("N"));

    private readonly CountingTranscriptionHandler _handler = new("the words nobody will ever read");
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    public async Task InitializeAsync()
    {
        // Isolate the storage root BEFORE the Gateway starts so its dictation staging, key vault and
        // transcription history bind the temp root, never the developer's real %LOCALAPPDATA%.
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);
        Directory.CreateDirectory(_storageRoot);

        var vaultPath = Path.Combine(_storageRoot, "keyvault.json");
        new KeyVault(vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test");

        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: false,
            instancesDirectory: _instancesDir,
            keyVaultPath: vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            dictationTranscription: new GatewayTranscriptionService(
                new KeyVault(vaultPath),
                dictionaryProvider: _ => DictationDictionary.Empty,
                modeProvider: () => TranscriptionMode.DevThrottle,
                http: new HttpClient(_handler, disposeHandler: false),
                history: new TranscriptionHistoryLog(Path.Combine(_storageRoot, "history")),
                audioArchive: new TranscriptionAudioArchive(Path.Combine(_storageRoot, "archive"))));
        await _gateway.StartAsync();
        _http = NewClient(_gateway.Port);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task ASecondCompleteForAnExitedSession_IsANoOp_AndNothingIsEverTranscribed()
    {
        var sid = SeatExitedSession();
        var uploadId = await StagedClipAsync(sid);

        // Attempt one: the phone finishes the upload and completes.
        var first = await CompleteAsync(uploadId, sid);
        Assert.Equal(HttpStatusCode.OK, first.status);
        Assert.False(first.body.GetProperty("submitted").GetBoolean());
        Assert.True(first.body.GetProperty("movedOn").GetBoolean(),
            "the words were not delivered, and the client's movedOn arm is the only terminal arm that says so and keeps the audio");

        // The record is resolved on disk, so the session it was locking is free. This is the durable half of
        // the defect: before the fix the record was still PENDING here, and a PENDING record locks its
        // session for as long as it exists - which, on this path, was forever.
        var record = Store().ReadRecord(uploadId);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.False(Store().IsSessionLocked(sid));

        // Attempt two - what the background driver does two seconds later, and every few seconds after that.
        // It returns the SAME cached terminal outcome, from the tombstone, without re-running the work.
        var second = await CompleteAsync(uploadId, sid);
        Assert.Equal(HttpStatusCode.OK, second.status);
        Assert.False(second.body.GetProperty("submitted").GetBoolean());
        Assert.True(second.body.GetProperty("movedOn").GetBoolean());

        // ZERO transcripts were paid for, across BOTH attempts. Each lap of the observed loop paid for one.
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task ALiveSessionStillReachesTheProvider_SoTheGateIsNotRefusingEverything()
    {
        // The accepting control at this level. The delivery itself needs a Director on the tunnel, which this
        // harness has none of, so the turn cannot land - but the clip IS transcribed, which is the half this
        // control exists to protect: a gate that refused every session would leave the provider at zero here
        // too, and this test would be the only thing to notice.
        var sid = SeatSession("Running", "WaitingForInput");
        var uploadId = await StagedClipAsync(sid);

        var resp = await CompleteAsync(uploadId, sid);

        Assert.Equal(1, _handler.Calls);
        Assert.NotEqual(HttpStatusCode.OK, resp.status); // no tunnel, so the submit could not land
        Assert.True(Store().IsPending(uploadId), "a failed delivery keeps the words for the retry");
    }

    // ===== helpers =================================================================================

    private string SeatExitedSession() => SeatSession("Failed", "WaitingForInput");

    /// <summary>Seat a session on the running Gateway's own pushed store and registry, which is where its
    /// dictation path locates sessions in production.</summary>
    private string SeatSession(string status, string activityState)
    {
        var sid = Guid.NewGuid().ToString();
        _gateway.Registry.RegisterFromStream(DirectorId, "SOREN-NORTH", "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        _gateway.PushedSessions.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_gateway.PushedSessions.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[]
        {
            new SessionDto
            {
                SessionId = sid,
                DirectorId = DirectorId,
                Agent = "Codex",
                RepoPath = @"D:\ReposFred\devthrottle",
                Status = status,
                ActivityState = activityState,
                LastActivityAt = DateTime.UtcNow,
            },
        }));
        return sid;
    }

    /// <summary>Register the upload and put its one chunk up through the real routes, so the state the
    /// completes run against is the state the phone leaves behind.</summary>
    private async Task<string> StagedClipAsync(string sid)
    {
        var uploadId = Guid.NewGuid().ToString();
        using var reg = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = sid, baselineBufferBytes = 0 }),
        };
        reg.Headers.Add("Idempotency-Key", uploadId);
        var regResp = await _http.SendAsync(reg);
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        var chunk = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-opus-bytes"));
        chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var put = await _http.PutAsync($"/dictation/{uploadId}/chunk/0", chunk);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(Store().IsSessionLocked(sid));
        return uploadId;
    }

    private async Task<(HttpStatusCode status, JsonElement body)> CompleteAsync(string uploadId, string sid)
    {
        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete",
            new { sessionId = sid, totalChunks = 1, mime = "audio/webm", ext = "webm" });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, body);
    }

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

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

    /// <summary>Answers the transcription POST with a fixed transcript and COUNTS THE CALLS - the measured
    /// evidence that an exited session costs no transcript at all.</summary>
    private sealed class CountingTranscriptionHandler : HttpMessageHandler
    {
        private readonly string _text;
        private int _calls;

        public CountingTranscriptionHandler(string text) => _text = text;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"" + _text + "\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
