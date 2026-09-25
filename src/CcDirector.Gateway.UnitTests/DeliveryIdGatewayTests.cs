using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway half of the delivery id (Voice Delivery mission, phase 1): every dictation delivery carries the
/// recording's upload id, a Director answer that the recording was already delivered is treated as delivered, and
/// the Gateway can ask a Director what became of an id - keeping "never seen" apart from "cannot say".
///
/// The dictation cases drive the REAL <see cref="GatewayDictationEndpoint.RunCompleteCoreAsync"/> with a real upload
/// store and the real transcription service over a fake provider (the harness of <see cref="DictationExitedSessionTests"/>),
/// and read the prompt exactly as it left for the Director - never a hand-built request.
/// </summary>
public sealed class DeliveryIdGatewayTests : IDisposable
{
    private const string DirectorId = "director-1";
    private const string SpokenWords = "run the build and tell me what broke";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-delivery-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath;
    private readonly VoiceUploadStore _store;
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly TranscribingSessions _marks = new();
    private readonly FixedTranscriptHandler _handler = new(SpokenWords);

    public DeliveryIdGatewayTests()
    {
        _vaultPath = Path.Combine(_root, "keyvault.json");
        Directory.CreateDirectory(_root);
        _store = new VoiceUploadStore(Path.Combine(_root, "uploads"), TenantId.Local);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _registry.RegisterFromStream(DirectorId, "SOREN-NORTH", "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_test");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _handler.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ===== every dictation delivery carries the id ============================================================

    [Fact]
    public async Task Complete_ComposedWithTypedText_CarriesTheDeliveryId()
    {
        // Proves a recording composed with typed text - which is sent as a TYPED turn, without the voice marker - still
        // carries its upload id as the delivery id, so the Director can refuse a second copy of it.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        var sent = new List<PromptRequest>();

        var outcome = await RunAsync(uploadId, sid, sent, Accepted(), before: "typed before", after: "typed after");

        Assert.True(outcome.Terminal);
        var prompt = Assert.Single(sent);
        Assert.Equal($"typed before {SpokenWords} typed after", prompt.Text);
        Assert.Null(prompt.DeliveryUploadId);
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), prompt.DeliveryId);
    }

    [Fact]
    public async Task Complete_SpokenAlone_CarriesTheDeliveryIdAndTheVoiceMarker()
    {
        // Proves speech alone carries the same delivery id, and the voice-turn marker keeps its own meaning beside it.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        var sent = new List<PromptRequest>();

        await RunAsync(uploadId, sid, sent, Accepted());

        var prompt = Assert.Single(sent);
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), prompt.DeliveryId);
        Assert.Equal(uploadId, prompt.DeliveryUploadId);
    }

    [Fact]
    public async Task Complete_DirectorSaysAlreadyDelivered_IsResolvedAsDelivered()
    {
        // Proves a Director that refused this copy because the recording already reached the session is read as a
        // delivery: the DELIVERED tombstone is written as on the success path, and no 502 sends the client to retry.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);

        var outcome = await RunAsync(uploadId, sid, new List<PromptRequest>(), Refused(DeliveryState.Delivered));

        Assert.True(outcome.Terminal);
        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        Assert.True(record.Submitted);
        Assert.False(record.MovedOn);
        Assert.False(_store.IsPending(uploadId));
    }

    [Fact]
    public async Task Complete_DirectorSaysStillDelivering_IsNotResolvedAsDelivered()
    {
        // Proves any other refusal keeps the failure path: the words may still be going in, so nothing is written as
        // delivered and the record stays PENDING for the retry (asking the Director instead is phase 2).
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);

        var outcome = await RunAsync(uploadId, sid, new List<PromptRequest>(), Refused(DeliveryState.Delivering));

        Assert.False(outcome.Terminal);
        Assert.True(_store.IsPending(uploadId));
    }

    // ===== "what became of delivery id X?" ===================================================================

    [Fact]
    public async Task GetDeliveryStateAsync_DirectorAnswers_ReturnsItsRecord()
    {
        // Proves the Gateway reads the Director's own answer, run through the Director's real verb core.
        var directorRecord = new DeliveryRecord(Path.Combine(_root, "director-records"));
        var session = Guid.NewGuid();
        directorRecord.TryBeginDelivery(session, "upload-1");
        directorRecord.MarkDelivered(session, "upload-1");
        var client = SessionVerbClient.ForDirector(DirectorId,
            (_, cmd, _) => Task.FromResult<DirectorCommandResult?>(SessionReadExecutor.DeliveryStateOf(cmd, directorRecord)));

        var asked = await client.GetDeliveryStateAsync(session.ToString(), "upload-1");

        Assert.Equal(SessionVerbClient.DeliveryStateAskKind.Answered, asked.Kind);
        Assert.Equal(DeliveryState.Delivered, asked.Answer!.State);
    }

    [Fact]
    public async Task GetDeliveryStateAsync_DirectorOlderThanTheVerb_IsItsOwnAnswerNotUnknown()
    {
        // Proves "the Director cannot say" is not read as "the Director never saw it": an older Director's real
        // unknown-verb refusal (produced by the real dispatch) comes back as DirectorTooOld.
        var manager = new SessionManager(new AgentOptions());
        try
        {
            var client = SessionVerbClient.ForDirector(DirectorId, (dir, cmd, _) =>
                SessionCommandExecutor.DispatchAsync(manager, dir, new DirectorCommand
                {
                    CommandId = cmd.CommandId,
                    // The verb this Gateway asks for does not exist on an older Director - played by a name no Director has.
                    Verb = cmd.Verb + "-not-on-this-director",
                    SessionId = cmd.SessionId,
                    PayloadJson = cmd.PayloadJson,
                })!);

            var asked = await client.GetDeliveryStateAsync(Guid.NewGuid().ToString(), "upload-1");

            Assert.Equal(SessionVerbClient.DeliveryStateAskKind.DirectorTooOld, asked.Kind);
            Assert.Null(asked.Answer);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task GetDeliveryStateAsync_UnreadableRecordOrNoTunnel_AreNotUnknown()
    {
        // Proves a Director that cannot read its record, and a Director that is not connected, are two more answers of
        // their own - neither is "unknown".
        var session = Guid.NewGuid();
        var brokenRecord = new DeliveryRecord(Path.Combine(_root, "broken-records"));
        Directory.CreateDirectory(Path.Combine(_root, "broken-records"));
        File.WriteAllText(brokenRecord.FileFor(session), "garbage\n");
        var failing = SessionVerbClient.ForDirector(DirectorId,
            (_, cmd, _) => Task.FromResult<DirectorCommandResult?>(SessionReadExecutor.DeliveryStateOf(cmd, brokenRecord)));
        var disconnected = SessionVerbClient.ForDirector(DirectorId, sendCommand: null);

        var unreadable = await failing.GetDeliveryStateAsync(session.ToString(), "upload-1");
        var never = await disconnected.GetDeliveryStateAsync(session.ToString(), "upload-1");

        Assert.Equal(SessionVerbClient.DeliveryStateAskKind.NoAnswer, unreadable.Kind);
        Assert.Contains(brokenRecord.FileFor(session), unreadable.Detail);
        Assert.Equal(SessionVerbClient.DeliveryStateAskKind.NeverLeftTheGateway, never.Kind);
    }

    // ===== harness ===========================================================================================

    private static Func<PromptRequest, DirectorCommandResult> Accepted() => _ =>
        DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new PromptResponse
        {
            Accepted = true,
            DeliveryState = DeliveryState.Delivered,
        }));

    private static Func<PromptRequest, DirectorCommandResult> Refused(DeliveryState state) => _ =>
        DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new PromptResponse
        {
            Accepted = false,
            DeliveryState = state,
            DeliveryStateReason = $"already {state}; this copy was refused and nothing was typed",
            Error = $"already {state}; this copy was refused and nothing was typed",
        }));

    private string Seat()
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[]
        {
            new SessionDto
            {
                SessionId = sid,
                DirectorId = DirectorId,
                Agent = "ClaudeCode",
                RepoPath = @"D:\ReposFred\devthrottle",
                Status = "Running",
                ActivityState = "WaitingForInput",
                LastActivityAt = DateTime.UtcNow,
            },
        }));
        return sid;
    }

    private async Task<string> StagedClipAsync(string sid)
    {
        var uploadId = Guid.NewGuid().ToString();
        _store.Register(uploadId);
        await _store.StoreChunkAsync(uploadId, 0, Encoding.UTF8.GetBytes("fake-opus-bytes"), null);
        _store.MarkPending(uploadId, sid);
        return uploadId;
    }

    private Task<DictationOutcome> RunAsync(string uploadId, string sid, List<PromptRequest> sent,
        Func<PromptRequest, DirectorCommandResult> director, string? before = null, string? after = null)
        => GatewayDictationEndpoint.RunCompleteCoreAsync(
            uploadId, TenantId.Local,
            new DictationCompleteRequest
            {
                SessionId = sid, TotalChunks = 1, Mime = "audio/webm", Ext = "webm", Before = before, After = after,
            },
            _store, _registry, owners: null, Transcription(), _marks,
            deliverySurface: "cockpit", deliveryIdentityKind: "device-key",
            pushedSessions: _pushed,
            sendCommand: (directorId, command, ct) =>
            {
                if (command.Verb != "prompt")
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
                var prompt = JsonSerializer.Deserialize<PromptRequest>(command.PayloadJson, Json)!;
                sent.Add(prompt);
                return Task.FromResult<DirectorCommandResult?>(director(prompt));
            },
            streamStale: TimeSpan.FromSeconds(20));

    private GatewayTranscriptionService Transcription() => new(
        new KeyVault(_vaultPath),
        dictionaryProvider: _ => DictationDictionary.Empty,
        modeProvider: () => TranscriptionMode.DevThrottle,
        http: new HttpClient(_handler, disposeHandler: false),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive")));

    private sealed class FixedTranscriptHandler : HttpMessageHandler
    {
        private readonly string _text;
        public FixedTranscriptHandler(string text) => _text = text;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"" + _text + "\"}", Encoding.UTF8, "application/json"),
            });
    }
}
