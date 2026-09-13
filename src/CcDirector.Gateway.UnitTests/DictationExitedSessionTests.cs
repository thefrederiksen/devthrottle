using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A dictation aimed at a session whose agent has EXITED is RESOLVED, not merely refused - and it is
/// refused BEFORE anything is paid for (devthrottle_internal issue 1997).
///
/// THE DEFECT, as observed on 13 September 2026. The complete path marked the session actively
/// transcribing on its first line, reassembled and transcribed the clip, and only then asked whether the
/// session could receive it. For an exited session it answered 410 and wrote NOTHING durable: every other
/// terminal outcome in that method leaves a tombstone, so a re-complete is a no-op, but this one left the
/// record PENDING. The phone's background driver therefore re-completed forever (two-second exponential
/// backoff capped at fifteen for the first hour, then every five minutes, with no end), paying for a full
/// transcript each lap and repainting a dead session orange every few seconds. Nothing in the product could
/// stop it; the owner deleted the session.
///
/// These tests bind the REAL call site - <see cref="GatewayDictationEndpoint.RunCompleteCoreAsync"/> with
/// real collaborators: a real upload store over a temporary root, a real registry and pushed-session store
/// holding the session under test, and the real transcription service pointed at a COUNTING stub handler,
/// which is what makes "the transcription was not called" a measured fact rather than an inference. The
/// prompt route is a delegate this test owns, so a delivery that really happens is observable too.
///
/// The accepting control is deliberately alongside the refusal: a gate that refused everything would pass
/// the exited test and destroy the product, so the live session must still transcribe AND still deliver in
/// the same harness, with the same collaborators, in the same file.
/// </summary>
public sealed class DictationExitedSessionTests : IDisposable
{
    private const string DirectorId = "director-1";
    private const string Machine = "SOREN-NORTH";
    private const string SpokenWords = "run the build and tell me what broke";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-exited-" + Guid.NewGuid().ToString("N"));
    private readonly string _uploads;
    private readonly string _vaultPath;
    private readonly VoiceUploadStore _store;
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly TranscribingSessions _marks = new();
    private readonly CountingTranscriptionHandler _handler = new(SpokenWords);

    public DictationExitedSessionTests()
    {
        _uploads = Path.Combine(_root, "uploads");
        _vaultPath = Path.Combine(_root, "keyvault.json");
        Directory.CreateDirectory(_root);
        _store = new VoiceUploadStore(_uploads, TenantId.Local);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _handler.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    [Fact]
    public async Task ExitedSession_ResolvesTheRecord_WithoutTranscribingAndWithoutDelivering()
    {
        // The session the owner was looking at: the agent process is gone and the Director marked it Failed.
        var sid = Seat("Failed");
        var uploadId = await StagedClipAsync(sid);
        var prompts = new List<string>();

        var outcome = await RunAsync(uploadId, sid, prompts);

        // NOTHING WAS PAID FOR. The gate needs the session and nothing else, so it is asked first: the
        // provider was never called, and no turn was pushed at the Director.
        Assert.Equal(0, _handler.Calls);
        Assert.Empty(prompts);

        // THE LOOP ENDS. The record is a durable terminal tombstone, so it is no longer PENDING, the session
        // is no longer locked by it (this is the flag that painted the dead seat orange forever), and a
        // re-complete short-circuits on it instead of re-running. Before this fix the record stayed PENDING
        // and every one of these three assertions failed.
        var record = _store.ReadRecord(uploadId);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.False(_store.IsPending(uploadId));
        Assert.False(_store.IsSessionLocked(sid));

        // THE CAUSE IS WRITTEN DOWN. On the wire the outcome rides the client's existing moved-on arm (the
        // only terminal arm that keeps the audio and tells the user), but "the session moved on" and "the
        // session exited" are different facts and the record is the only place either survives, so the
        // record carries the exited reason of its own.
        Assert.False(record.Submitted);
        Assert.True(record.MovedOn);
        Assert.Equal(GatewayDictationEndpoint.ExitedSessionReason, record.Reason);
        Assert.Equal("", record.Transcript);
        Assert.True(outcome.Terminal, "an undeliverable dictation must be terminal, or the client retries it");
    }

    [Fact]
    public async Task ExitedSession_LeavesNothingForTheDriverToReDrive()
    {
        // THE LOOP, on its own, with no other assertion in front of it. The test above fails on today's code
        // at its FIRST assertion (the transcription count), which would hide whether the durable half was
        // ever fixed - so the durable half is asserted here by itself: after one complete for an exited
        // session there is no PENDING record left, so there is nothing the phone's background driver can
        // re-drive and nothing left to lock the session. On today's code every line below fails.
        var sid = Seat("Failed");
        var uploadId = await StagedClipAsync(sid);

        await RunAsync(uploadId, sid, new List<string>());

        Assert.False(_store.IsPending(uploadId), "a PENDING record is exactly what the phone re-completes forever");
        Assert.False(_store.IsSessionLocked(sid), "a PENDING record locks its session, which is the orange that would not stop");
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
    }

    [Theory]
    [InlineData("Exited", "Idle")]
    [InlineData("Failed", "WaitingForInput")]
    [InlineData("Running", "Exited")]
    public async Task EveryExitedShape_IsResolved_NotLeftPending(string status, string activityState)
    {
        // IsExited answers on three different readings of a dead seat, and the roster showed the second of
        // them (Failed + WaitingForInput - the same words a healthy session waiting on the owner uses).
        // Each must resolve the record; none may leave it pending for the driver to re-drive.
        var sid = Seat(status, activityState);
        var uploadId = await StagedClipAsync(sid);

        await RunAsync(uploadId, sid, new List<string>());

        Assert.Equal(0, _handler.Calls);
        Assert.False(_store.IsPending(uploadId));
        Assert.Equal(GatewayDictationEndpoint.ExitedSessionReason, _store.ReadRecord(uploadId)!.Reason);
    }

    [Fact]
    public async Task LiveSession_StillTranscribes_AndStillDelivers()
    {
        // THE ACCEPTING CONTROL. A gate that refused every session would pass the tests above and break
        // dictation outright, so the ordinary case is proven in the same harness: the clip is transcribed
        // once, the words reach the session, and the record is a DELIVERED tombstone that says so.
        var sid = Seat("Running");
        var uploadId = await StagedClipAsync(sid);
        var prompts = new List<string>();

        var outcome = await RunAsync(uploadId, sid, prompts);

        Assert.Equal(1, _handler.Calls);
        Assert.Equal(SpokenWords, Assert.Single(prompts));
        Assert.True(outcome.Terminal);

        var record = _store.ReadRecord(uploadId);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.True(record.Submitted);
        Assert.False(record.MovedOn);
        Assert.Null(record.Reason);
        Assert.Equal(SpokenWords, record.Transcript);
    }

    [Fact]
    public async Task AnUnreachableSession_IsStillHeld_NotResolved()
    {
        // The OTHER control, and the line this fix must not cross. A session that cannot be LOCATED is not a
        // session that has EXITED: the owning Director may simply be between pushes or briefly offline, and
        // the words must wait for it. So that arm stays a retryable error with the record left PENDING -
        // resolving it would throw away a recording the next attempt would have delivered.
        var sid = Guid.NewGuid().ToString(); // never seated in the pushed store
        var uploadId = await StagedClipAsync(sid);

        var outcome = await RunAsync(uploadId, sid, new List<string>());

        Assert.False(outcome.Terminal);
        Assert.Equal(0, _handler.Calls);
        Assert.True(_store.IsPending(uploadId), "an unlocatable session must keep the words, not tombstone them");
    }

    // ===== harness =================================================================================

    /// <summary>Seat a session on the pushed store (and its owning Director on the registry) so the real
    /// locate finds it exactly as it does in production.</summary>
    private string Seat(string status, string activityState = "WaitingForInput")
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[]
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

    /// <summary>A registered upload id with one real chunk on disk and a PENDING marker bound to the
    /// session - the state the phone leaves behind when it has finished uploading and is completing.</summary>
    private async Task<string> StagedClipAsync(string sid)
    {
        var uploadId = _store.Register(null);
        await _store.StoreChunkAsync(uploadId, 0, Encoding.UTF8.GetBytes("fake-opus-bytes"), null);
        _store.MarkPending(uploadId, sid);
        Assert.True(_store.IsSessionLocked(sid));
        return uploadId;
    }

    private Task<DictationOutcome> RunAsync(string uploadId, string sid, List<string> prompts)
        => GatewayDictationEndpoint.RunCompleteCoreAsync(
            uploadId, TenantId.Local,
            new DictationCompleteRequest { SessionId = sid, TotalChunks = 1, Mime = "audio/webm", Ext = "webm" },
            _store, _registry, owners: null, Transcription(), _marks,
            deliverySurface: "mobile", deliveryIdentityKind: "device-key",
            pushedSessions: _pushed,
            sendCommand: (directorId, command, ct) =>
            {
                if (command.Verb == "prompt")
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
                    prompts.Add(doc.RootElement.GetProperty("text").GetString() ?? "");
                }
                return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
            },
            streamStale: TimeSpan.FromSeconds(20));

    /// <summary>The real transcription service, pointed at this test's counting handler and given a
    /// scratch archive, history and glossary so it touches nothing outside this test's own root. Every
    /// dependency that would otherwise resolve a shared location is supplied explicitly.</summary>
    private GatewayTranscriptionService Transcription() => new(
        new KeyVault(_vaultPath),
        dictionaryProvider: _ => DictationDictionary.Empty,
        modeProvider: () => TranscriptionMode.DevThrottle,
        http: new HttpClient(_handler, disposeHandler: false),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive")));

    /// <summary>
    /// Answers the transcription POST with a fixed transcript AND COUNTS THE CALLS. The count is the whole
    /// point: "the exited gate no longer pays for a transcript" is a claim about whether the provider was
    /// reached, and only the transport can answer it. A mock of the service would answer a question about
    /// the mock.
    /// </summary>
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
