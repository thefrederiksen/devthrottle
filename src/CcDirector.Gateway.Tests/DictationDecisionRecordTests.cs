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
/// The Gateway's decision record, driven through the REAL dictation endpoint (Voice Delivery mission, phase 1,
/// 25 September 2026).
///
/// That morning the owner's timeline had to be rebuilt from his Director's log and the agents' conversation
/// files, because the hosted Gateway's log is not kept and a finished upload's record was deleted on
/// acknowledgement. These tests prove every delivery decision now lands, in order, in the upload's own
/// <c>decisions.jsonl</c>, that acknowledging keeps that log while deleting the audio and the words, and that
/// the log never holds the words.
///
/// The rig: a real GatewayHost over loopback HTTP, the
/// real routes, the real store on disk, and a real tunnel-connected <see cref="FakeTunnelDirector"/> whose
/// prompt verb answers as each test tells it to. Only the speech-to-text provider is a stub.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DictationDecisionRecordTests : IAsyncLifetime
{
    private const string Token = "test-token-decision-record";
    private const string DirectorId = "dir-decision-record";
    // Distinctive so its absence from the log is a real check, not a coincidence of short words.
    private const string Transcript = "zebra quartz marmalade the owner said this";
    private const long RecordTimeBaseline = 1_000;

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-decrec-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath = Path.Combine(Path.GetTempPath(), "cc-decrec-vault-" + Guid.NewGuid().ToString("N") + ".json");

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _director = null!;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private long _pushSequence;
    private readonly AheadClock _clock = new();

    public DictationDecisionRecordTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-decrec-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        new KeyVault(_vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_test_key");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            dictationTranscription: StubTranscription());
        _gateway.DeliveryClock = _clock;
        // The Gateway's own driver of held deliveries (phase 5) ticks only when a test says so: an hour-long tick keeps
        // its attempts out of these sequences unless the test drives one on purpose (HeldDeliveries.TickAsync).
        _gateway.HeldDeliveryTickInterval = TimeSpan.FromHours(1);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId);
        await _director.PushSnapshotAsync(SessionAt(RecordTimeBaseline, "Running"));
        _pushSequence = 1;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* cleanup */ }
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task ADelivery_WritesEachDecisionInOrder_AndNeverTheWords()
    {
        // Proves one ordinary delivery through the real endpoint leaves received, transcribed,
        // sent-to-director, director-answer and delivered in that order - and not one of the owner's words.
        var uploadId = await RegisterAndUploadAsync();
        _director.OnCommand(_ => FakeTunnelDirector.Ok(new PromptResponse()));

        var result = await CompleteAsync(uploadId, resumed: false);
        Assert.Equal(HttpStatusCode.OK, result.status);
        Assert.True(result.body.GetProperty("submitted").GetBoolean());

        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.GatewayOwnsDelivery, DeliveryDecisions.Transcribed, DeliveryDecisions.SentToDirector,
            DeliveryDecisions.DirectorAnswer, DeliveryDecisions.Delivered,
        }, Decisions(uploadId));

        var lines = Store().ReadDecisions(uploadId).Lines;
        Assert.Equal(Transcript.Length, lines.Single(l => l.Decision == DeliveryDecisions.Transcribed).Facts!.Characters);
        Assert.True(lines.Single(l => l.Decision == DeliveryDecisions.DirectorAnswer).Facts!.Ok);
        Assert.DoesNotContain("zebra", File.ReadAllText(DecisionsFile(uploadId)));
        Assert.DoesNotContain("marmalade", File.ReadAllText(DecisionsFile(uploadId)));
    }

    [Fact]
    public async Task AnUnansweredPrompt_ThenTheGatewaysOwnAttempt_EveryDecisionIsReadableThroughTheRoute()
    {
        // Proves, through the real routes, the phase 2 sequence as phase 5 drives it: the prompt verb goes out and no
        // answer comes back, the Gateway asks the Director instead of calling it a failure, the Director says not
        // delivered - held as "retrying" (202), never a 502, because the Gateway owns it now. With NO further client
        // call, the Gateway's own attempt (its driver's tick) asks FIRST, hears not delivered again, transcribes, sends
        // and delivers. Every line - the ownership, the drive with what woke it and its attempt number, the question and
        // why - is read back through GET /dictation/{uploadId}/decisions, in order, and none holds the words.
        var uploadId = await RegisterAndUploadAsync();
        var prompts = 0;
        _director.OnCommand(cmd => cmd.Verb switch
        {
            "prompt" when Interlocked.Increment(ref prompts) == 1
                => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds"),
            "prompt" => FakeTunnelDirector.Ok(new PromptResponse { Accepted = true }),
            DeliveryStateRequest.Verb => FakeTunnelDirector.Ok(new DeliveryStateResponse
            {
                State = DeliveryState.NotDelivered,
                Reason = "the composer never echoed the text",
            }),
            _ => FakeTunnelDirector.Ok(new PromptResponse()),
        });

        var first = await CompleteAsync(uploadId, resumed: false);
        Assert.Equal(HttpStatusCode.Accepted, first.status);
        Assert.Equal("retrying", first.body.GetProperty("directorState").GetString());

        _clock.Ahead = TimeSpan.FromSeconds(5);   // the driver's first wait has passed
        await _gateway.HeldDeliveries!.TickAsync();

        var outcome = await _http.GetAsync($"/dictation/{uploadId}/outcome");
        Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
        Assert.True((await outcome.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("submitted").GetBoolean());
        var read = await _http.GetFromJsonAsync<JsonElement>($"/dictation/{uploadId}/decisions");
        var lines = read.GetProperty("decisions").EnumerateArray().ToList();
        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.GatewayOwnsDelivery, DeliveryDecisions.Transcribed,
            DeliveryDecisions.SentToDirector, DeliveryDecisions.DirectorAnswer, DeliveryDecisions.AskedDirector,
            DeliveryDecisions.DeliveryStateAnswer, DeliveryDecisions.StillDelivering,
            DeliveryDecisions.GatewayDrive, DeliveryDecisions.AskedDirector, DeliveryDecisions.DeliveryStateAnswer,
            DeliveryDecisions.Transcribed, DeliveryDecisions.SentToDirector, DeliveryDecisions.DirectorAnswer,
            DeliveryDecisions.Delivered,
        }, lines.Select(l => l.GetProperty("decision").GetString()));
        Assert.Equal(DeliveryDecisions.AskReasonPromptUnanswered, lines[5].GetProperty("facts").GetProperty("reason").GetString());
        Assert.Equal("not-delivered", lines[6].GetProperty("facts").GetProperty("state").GetString());
        Assert.Equal("retrying", lines[7].GetProperty("facts").GetProperty("state").GetString());
        Assert.Equal(DeliveryDecisions.DriveTick, lines[8].GetProperty("facts").GetProperty("trigger").GetString());
        Assert.Equal(1, lines[8].GetProperty("facts").GetProperty("attempt").GetInt32());
        Assert.Equal(DeliveryDecisions.AskReasonRetryAsksFirst, lines[9].GetProperty("facts").GetProperty("reason").GetString());
        Assert.True(lines[1].GetProperty("facts").TryGetProperty("sentAtUtc", out _));
        Assert.Equal(2, prompts);
        var raw = read.GetRawText();
        Assert.DoesNotContain("zebra", raw);
        Assert.DoesNotContain("marmalade", raw);
        Assert.DoesNotContain("zebra", File.ReadAllText(DecisionsFile(uploadId)));
        Assert.DoesNotContain("marmalade", File.ReadAllText(DecisionsFile(uploadId)));
    }

    [Fact]
    public async Task ACompleteWithoutSentAtUtc_IsRefusedWith400_AndNothingIsTyped()
    {
        // Proves the Send time is required on the wire: a complete without it is refused with the contract's words,
        // before anything is transcribed, decided or typed - the recording stays exactly as it was, pending.
        var uploadId = await RegisterAndUploadAsync();
        // Only prompts count: the Gateway sends a connected Director other commands of its own accord.
        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb == "prompt") Interlocked.Increment(ref prompts);
            return FakeTunnelDirector.Ok(new PromptResponse());
        });

        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete", new
        {
            sessionId = _sessionId,
            totalChunks = 1,
            mime = "audio/wav",
            ext = "wav",
            resumed = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("sentAtUtc (ISO 8601 UTC) is required",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(0, prompts);
        Assert.Equal(new[] { DeliveryDecisions.Received }, Decisions(uploadId));
        Assert.True(Store().IsPending(uploadId));
    }

    [Fact]
    public async Task AResolvedRecording_AnswersItsCachedOutcome_ToACompleteWithoutSentAtUtc()
    {
        // Proves review finding 3: a page left open across the deploy still runs the client from before the Send time
        // existed, and keeps completing without it. For a recording that is already resolved - delivered, or shown back
        // as too old - the Gateway answers the cached outcome from the durable record (200, the words, the reason and
        // the offer) instead of refusing it with the 400 forever. Nothing is typed again. A PENDING recording without a
        // Send time is still refused (ACompleteWithoutSentAtUtc_IsRefusedWith400_AndNothingIsTyped).
        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb == "prompt") Interlocked.Increment(ref prompts);
            return FakeTunnelDirector.Ok(new PromptResponse());
        });
        var delivered = await RegisterAndUploadAsync();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(delivered, resumed: false)).status);
        var shownBack = await RegisterAndUploadAsync();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(shownBack, resumed: false, sentAtUtc: DateTime.UtcNow - TimeSpan.FromMinutes(10))).status);
        Assert.Equal(1, prompts);

        var deliveredAgain = await CompleteWithoutSentAtAsync(delivered);
        var shownBackAgain = await CompleteWithoutSentAtAsync(shownBack);

        Assert.Equal(HttpStatusCode.OK, deliveredAgain.status);
        Assert.True(deliveredAgain.body.GetProperty("submitted").GetBoolean());
        Assert.False(deliveredAgain.body.GetProperty("movedOn").GetBoolean());
        Assert.Equal(Transcript, deliveredAgain.body.GetProperty("transcript").GetString());
        Assert.Equal(HttpStatusCode.OK, shownBackAgain.status);
        Assert.True(shownBackAgain.body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", shownBackAgain.body.GetProperty("reason").GetString());
        Assert.True(shownBackAgain.body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(Transcript, shownBackAgain.body.GetProperty("transcript").GetString());
        Assert.Equal(1, prompts);
    }

    private async Task<(HttpStatusCode status, JsonElement body)> CompleteWithoutSentAtAsync(string uploadId)
    {
        // The body the client sent before the Send time existed: everything else, and resumed.
        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete", new
        {
            sessionId = _sessionId,
            totalChunks = 1,
            mime = "audio/wav",
            ext = "wav",
            resumed = true,
        });
        return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task ATooOldRecording_IsShownBack_Kept_AndItsWordsComeBackOnEveryReComplete()
    {
        // Proves a recording sent more than five minutes ago is shown back through the real route - 200, movedOn, the
        // reason "too-old", the words - with nothing typed; that the tombstone keeps the words, so a re-complete and a
        // re-register both hand them back with the reason; and that the log says too-old, never the old moved-on.
        var uploadId = await RegisterAndUploadAsync();
        var prompts = 0;
        _director.OnCommand(cmd =>
        {
            if (cmd.Verb == "prompt") Interlocked.Increment(ref prompts);
            return FakeTunnelDirector.Ok(new PromptResponse());
        });
        var sentAt = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        var first = await CompleteAsync(uploadId, resumed: false, sentAtUtc: sentAt);
        var again = await CompleteAsync(uploadId, resumed: true, sentAtUtc: sentAt);
        var reRegister = await RegisterAsync(uploadId);

        foreach (var body in new[] { first.body, again.body, reRegister })
        {
            Assert.False(body.GetProperty("submitted").GetBoolean());
            Assert.True(body.GetProperty("movedOn").GetBoolean());
            Assert.Equal("too-old", body.GetProperty("reason").GetString());
            Assert.True(body.GetProperty("offerSendAnyway").GetBoolean());
            Assert.Equal(Transcript, body.GetProperty("transcript").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, first.status);
        Assert.Equal(0, prompts);
        var record = Store().ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        Assert.Equal(Transcript, record.Transcript);
        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.GatewayOwnsDelivery, DeliveryDecisions.Transcribed, DeliveryDecisions.TooOld, DeliveryDecisions.Retried,
        }, Decisions(uploadId));
        var tooOld = Store().ReadDecisions(uploadId).Lines.Single(l => l.Decision == DeliveryDecisions.TooOld).Facts!;
        Assert.InRange(tooOld.AgeSeconds!.Value, 600, 660);
    }

    [Fact]
    public async Task AnExitedSession_AnswersItsReason()
    {
        // Proves the session-exited answer carries its own reason on the wire too, so the client can name the exit.
        var uploadId = await RegisterAndUploadAsync();
        ReportSession(RecordTimeBaseline, "Exited");
        _director.OnCommand(_ => FakeTunnelDirector.Ok(new PromptResponse()));

        var result = await CompleteAsync(uploadId, resumed: false);
        var again = await CompleteAsync(uploadId, resumed: true);
        var reRegister = await RegisterAsync(uploadId);

        foreach (var body in new[] { result.body, again.body, reRegister })
        {
            Assert.True(body.GetProperty("movedOn").GetBoolean());
            Assert.Equal("session-exited", body.GetProperty("reason").GetString());
            Assert.True(body.GetProperty("offerSendAnyway").GetBoolean());
        }
    }

    [Fact]
    public async Task ARecordingNeverAnsweredFor_IsUnconfirmedPastFiveMinutes_OnEveryAnswer_WithItsWords_AndNoSendAnyway()
    {
        // Proves change 1 through the real routes: the prompt runs out of time and the Director never answers the
        // question, so the recording is held (202). Five minutes and one second after Send the Gateway's own attempt (phase
        // 5: no client call) rules it "could not confirm it arrived": shown back with the words the send kept and
        // offerSendAnyway false - and the outcome read, the re-complete and the re-register say exactly the same. Nothing
        // is typed again, and the decision record read through the route ends in the unconfirmed line, with none of the
        // words.
        var uploadId = await RegisterAndUploadAsync();
        var prompts = 0;
        _director.OnCommand(cmd => cmd.Verb switch
        {
            "prompt" => Counted(ref prompts, DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds")),
            DeliveryStateRequest.Verb => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer the question"),
            _ => FakeTunnelDirector.Ok(new PromptResponse()),
        });
        var sentAt = DateTime.UtcNow;

        var held = await CompleteAsync(uploadId, resumed: false, sentAtUtc: sentAt);
        Assert.Equal(HttpStatusCode.Accepted, held.status);
        Assert.Equal("no-answer", held.body.GetProperty("directorState").GetString());

        _clock.Ahead = TimeSpan.FromSeconds(301);
        await _gateway.HeldDeliveries!.TickAsync();
        var ruled = await _http.GetAsync($"/dictation/{uploadId}/outcome");
        var ruledBody = await ruled.Content.ReadFromJsonAsync<JsonElement>();
        var cached = await CompleteAsync(uploadId, resumed: true, sentAtUtc: sentAt);
        var reRegister = await RegisterAsync(uploadId);

        Assert.Equal(HttpStatusCode.OK, ruled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cached.status);
        foreach (var body in new[] { ruledBody, cached.body, reRegister })
        {
            Assert.False(body.GetProperty("submitted").GetBoolean());
            Assert.True(body.GetProperty("movedOn").GetBoolean());
            Assert.Equal("unconfirmed", body.GetProperty("reason").GetString());
            Assert.False(body.GetProperty("offerSendAnyway").GetBoolean());
            Assert.Equal(Transcript, body.GetProperty("transcript").GetString());
        }
        Assert.Equal(1, prompts);
        var read = await _http.GetFromJsonAsync<JsonElement>($"/dictation/{uploadId}/decisions");
        var lines = read.GetProperty("decisions").EnumerateArray().ToList();
        var unconfirmed = lines.Single(l => l.GetProperty("decision").GetString() == DeliveryDecisions.Unconfirmed);
        Assert.True(unconfirmed.GetProperty("facts").GetProperty("ageSeconds").GetInt64() >= 301);
        Assert.Equal("no-answer", unconfirmed.GetProperty("facts").GetProperty("directorNoAnswer").GetString());
        Assert.Equal("unconfirmed", read.GetProperty("reason").GetString());
        Assert.DoesNotContain("zebra", read.GetRawText());
        Assert.DoesNotContain("zebra", File.ReadAllText(DecisionsFile(uploadId)));
    }

    private static DirectorCommandResult Counted(ref int count, DirectorCommandResult answer)
    {
        Interlocked.Increment(ref count);
        return answer;
    }

    /// <summary>The real clock moved ahead by <see cref="Ahead"/>: the routes judge the five-minute limits by it.</summary>
    private sealed class AheadClock : TimeProvider
    {
        public TimeSpan Ahead;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Ahead;
    }

    [Fact]
    public async Task AnExitedSession_WritesSessionExited()
    {
        // Proves a recording for a session that has exited is resolved with its own decision, session-exited,
        // not folded into moved-on.
        var uploadId = await RegisterAndUploadAsync();
        ReportSession(RecordTimeBaseline, "Exited");
        _director.OnCommand(_ => FakeTunnelDirector.Ok(new PromptResponse()));

        var result = await CompleteAsync(uploadId, resumed: false);
        Assert.True(result.body.GetProperty("movedOn").GetBoolean());

        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.GatewayOwnsDelivery, DeliveryDecisions.SessionExited }, Decisions(uploadId));
    }

    [Fact]
    public async Task Acknowledge_KeepsTheDecisions_DeletesAudioAndWords_AndTheUploadIsGoneToEveryReader()
    {
        // Proves an acknowledgement through the real route keeps record.json and decisions.jsonl, deletes the
        // chunks and blanks the transcript, and that the upload then behaves exactly as a deleted one did:
        // no chunk accepted, no session lock, and a re-register opens it fresh.
        var uploadId = await RegisterAndUploadAsync();
        _director.OnCommand(_ => FakeTunnelDirector.Ok(new PromptResponse()));
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(uploadId, resumed: false)).status);
        // Precondition: the delivered tombstone holds the words until the ack.
        Assert.Contains("zebra", File.ReadAllText(RecordFile(uploadId)));

        var ack = await _http.PostAsync($"/dictation/{uploadId}/ack", content: null);
        Assert.True((await ack.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retired").GetBoolean());

        var dir = UploadDir(uploadId);
        Assert.Equal(new[] { "decisions.jsonl", "record.json" },
            Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("zebra", File.ReadAllText(RecordFile(uploadId)));
        Assert.Equal(DeliveryDecisions.Acknowledged, Decisions(uploadId).Last());

        // Every reader treats it as gone.
        Assert.Null(Store().ReadRecord(uploadId));
        Assert.False(Store().IsSessionLocked(_sessionId));
        Assert.Equal(HttpStatusCode.NotFound, (await PutChunkAsync(uploadId)).StatusCode);

        // The read route still answers, from the kept record.
        var read = await _http.GetFromJsonAsync<JsonElement>($"/dictation/{uploadId}/decisions");
        Assert.Equal("Acknowledged", read.GetProperty("state").GetString());
        Assert.Equal("Delivered", read.GetProperty("acknowledgedFrom").GetString());

        // A re-register opens the id afresh, exactly as it did after the directory was deleted.
        var again = await RegisterAsync(uploadId);
        Assert.False(again.TryGetProperty("terminal", out _));
        Assert.True(Store().IsSessionLocked(_sessionId));
    }

    [Fact]
    public async Task AcknowledgingAPendingUpload_ReleasesTheSessionLock()
    {
        // Proves acknowledging an upload that was still PENDING unlocks its session, as the delete did.
        var uploadId = await RegisterAndUploadAsync();
        Assert.True(Store().IsSessionLocked(_sessionId)); // precondition: the lock is really held

        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/dictation/{uploadId}/ack", content: null)).StatusCode);

        Assert.False(Store().IsSessionLocked(_sessionId));
        Assert.Empty(Store().LockedSessionIds());
        Assert.False(CcDirector.Core.Sessions.DictationLockReader.IsSessionLocked(CcStorage.DictationUploads(), _sessionId));
    }

    [Fact]
    public async Task AnEmptyRecording_KeepsItsRecordAndDecisions_AndTheClientSeesWhatADeletedUploadGave()
    {
        // Proves an empty recording through the real endpoint leaves record.json and decisions.jsonl with no
        // audio, writes empty-recording after received, and that the client's answers - the complete, a
        // re-complete, a chunk and a re-register - are the same as for an upload deleted the old way.
        var uploadId = await RegisterAndUploadAsync();
        var target = VoiceUploadStore.NormalizeUploadId(uploadId);
        GatewayDictationEndpoint.AssembledAudioForTests = (id, audio)
            => VoiceUploadStore.NormalizeUploadId(id) == target ? Array.Empty<byte>() : audio;
        (HttpStatusCode status, JsonElement body) empty;
        try { empty = await CompleteAsync(uploadId, resumed: false); }
        finally { GatewayDictationEndpoint.AssembledAudioForTests = null; }

        // Today's answer for an empty recording, unchanged.
        Assert.Equal(HttpStatusCode.BadGateway, empty.status);
        Assert.Equal("assembled recording was empty", empty.body.GetProperty("error").GetString());

        // The kept shape: the record (no words, retired from PENDING for this reason) and the decisions, no audio.
        Assert.Equal(new[] { "decisions.jsonl", "record.json" },
            Directory.EnumerateFileSystemEntries(UploadDir(uploadId)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.GatewayOwnsDelivery, DeliveryDecisions.EmptyRecording }, Decisions(uploadId));
        var log = Store().ReadDecisions(uploadId);
        Assert.Equal(DictationDeliveryState.Acknowledged, log.Record.Record!.State);
        Assert.Equal(DictationDeliveryState.Pending, log.Record.Record.AcknowledgedFrom);
        Assert.Equal(DeliveryDecisions.EmptyRecording, log.Record.Record.Reason);
        Assert.Equal("", log.Record.Record.Transcript);
        Assert.True(log.Lines.Last().Facts!.BytesDeleted > 0, "the chunk was really there and really deleted");
        Assert.False(Store().IsSessionLocked(_sessionId));

        // What the old code left: an upload whose directory was deleted outright.
        var deletedId = await RegisterAndUploadAsync();
        Store().Delete(deletedId);

        var reComplete = await CompleteAsync(uploadId, resumed: false);
        var reCompleteDeleted = await CompleteAsync(deletedId, resumed: false);
        Assert.Equal(HttpStatusCode.NotFound, reCompleteDeleted.status); // precondition: the old answer really is this
        Assert.Equal(reCompleteDeleted.status, reComplete.status);
        Assert.Equal(reCompleteDeleted.body.GetRawText(), reComplete.body.GetRawText());

        Assert.Equal((await PutChunkAsync(deletedId)).StatusCode, (await PutChunkAsync(uploadId)).StatusCode);

        var again = await RegisterAsync(uploadId);
        var againDeleted = await RegisterAsync(deletedId);
        Assert.False(againDeleted.TryGetProperty("terminal", out _)); // precondition: a deleted id re-opens fresh
        Assert.Equal(againDeleted.EnumerateObject().Select(p => p.Name), again.EnumerateObject().Select(p => p.Name));
        Assert.True(Store().IsPending(uploadId));
    }

    // ===== helpers =================================================================================

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

    private static string UploadDir(string uploadId)
        => Path.Combine(CcStorage.DictationUploads(), VoiceUploadStore.NormalizeUploadId(uploadId)!);

    private static string DecisionsFile(string uploadId) => Path.Combine(UploadDir(uploadId), "decisions.jsonl");
    private static string RecordFile(string uploadId) => Path.Combine(UploadDir(uploadId), "record.json");

    private static string[] Decisions(string uploadId)
        => Store().ReadDecisions(uploadId).Lines.Select(l => l.Decision).ToArray();

    private SessionDto SessionAt(long bufferBytes, string status) => new()
    {
        SessionId = _sessionId,
        Name = "dictation target",
        Status = status,
        ActivityState = status == "Exited" ? "Exited" : "Idle",
        TotalBufferBytes = bufferBytes,
    };

    // Writes the push store directly: the store is what the Gateway reads, and one sequence counter is shared with
    // the hub push so every write lands.
    private void ReportSession(long bufferBytes, string status)
    {
        var connectionId = _gateway.PushedSessions.GetActiveConnectionId(TenantId.Local, DirectorId)!;
        var applied = _gateway.PushedSessions.ApplyDelta(
            TenantId.Local, DirectorId, connectionId, Interlocked.Increment(ref _pushSequence), SessionAt(bufferBytes, status));
        Assert.True(applied, "the test's own session report must actually land, or it proves nothing");
    }

    private async Task<string> RegisterAndUploadAsync()
    {
        var uploadId = Guid.NewGuid().ToString();
        await RegisterAsync(uploadId);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(uploadId)).StatusCode);
        return uploadId;
    }

    private async Task<HttpResponseMessage> PutChunkAsync(string uploadId)
    {
        var wav = PcmWav.Wrap(new byte[16_000 * 2], 16_000, 1, 16);
        using var content = new ByteArrayContent(wav);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/dictation/{uploadId}/chunk/0") { Content = content };
        req.Headers.Add("X-Chunk-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)).ToLowerInvariant());
        return await _http.SendAsync(req);
    }

    private async Task<JsonElement> RegisterAsync(string uploadId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = _sessionId }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpStatusCode status, JsonElement body)> CompleteAsync(string uploadId, bool resumed, DateTime? sentAtUtc = null)
    {
        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete", new
        {
            sessionId = _sessionId,
            totalChunks = 1,
            mime = "audio/wav",
            ext = "wav",
            sentAtUtc = sentAtUtc ?? DateTime.UtcNow,
            resumed,
        });
        return (resp.StatusCode, await resp.Content.ReadFromJsonAsync<JsonElement>());
    }

    private GatewayTranscriptionService StubTranscription() => new(
        new KeyVault(_vaultPath),
        http: new HttpClient(new TranscriptStub()),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "transcription-history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "audio")));

    private sealed class TranscriptStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"text\":\"{Transcript}\"}}", Encoding.UTF8, "application/json"),
            });
    }
}
