using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway asks instead of guessing, and a recording is judged by its age, not by bytes (Voice Delivery mission,
/// phase 2).
///
/// On 25 September 2026 a slow success was read as a failure: the Gateway stopped waiting for the prompt verb at 30
/// seconds, told the client to retry, and the retry was dropped as "moved on" by a byte rule - while the first copy
/// landed two minutes later. These tests drive the REAL <see cref="GatewayDictationEndpoint.RunCompleteCoreAsync"/>
/// with a real upload store, the real transcription service over a COUNTING stub provider, and a Director played by
/// a delegate that answers each verb as the test says - the harness of <see cref="DeliveryIdGatewayTests"/>. The
/// clock is injected, so the five-minute boundary is proved without a five-minute wait. Every answer is rendered
/// through the outcome's real HTTP result, so the status and body asserted are the ones a client receives.
/// </summary>
public sealed class DictationAskInsteadOfGuessingTests : IDisposable
{
    private const string DirectorId = "director-1";
    private const string SpokenWords = "run the build and tell me what broke";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 9, 5, 12, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-ask-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath;
    private readonly VoiceUploadStore _store;
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly TranscribingSessions _marks = new();
    private readonly CountingTranscriptHandler _transcriber = new(SpokenWords);
    private readonly FixedClock _clock = new(T0);
    private readonly List<DirectorCommand> _commands = new();

    /// <summary>How the Director answers the prompt verb. Null means the command never left this Gateway.</summary>
    private Func<DirectorCommand, DirectorCommandResult?> _prompt = _ => Accepted();

    /// <summary>How the Director answers "what became of delivery id X?". Null means never left this Gateway.</summary>
    private Func<DirectorCommand, DirectorCommandResult?> _deliveryState = _ => StateIs(DeliveryState.Unknown);

    public DictationAskInsteadOfGuessingTests()
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
        _transcriber.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ===== 1. when the prompt verb does not answer, the Gateway asks ========================================

    [Fact]
    public async Task APromptThatTimesOut_IsFollowedByAQuestion_NotAFailure_AndNothingIsReBaselined()
    {
        // Proves the 09:05 case: the prompt verb went out and the Gateway stopped waiting. The Gateway asks the Director
        // what became of the delivery id - carrying the id it sent - and holds the recording on the answer instead of
        // telling the client it failed. The record stays PENDING and carries nothing but its own fields: there is no
        // baseline left anywhere to move.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => StateIs(DeliveryState.Delivering);

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.True(body.GetProperty("delivering").GetBoolean());
        Assert.Equal("delivering", body.GetProperty("directorState").GetString());
        Assert.Equal(new[] { "prompt", DeliveryStateRequest.Verb }, _commands.Select(c => c.Verb));
        var asked = JsonSerializer.Deserialize<DeliveryStateRequest>(_commands[1].PayloadJson, Json)!;
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), asked.DeliveryId);
        Assert.True(_store.IsPending(uploadId));
        Assert.DoesNotContain("Rebaseline", File.ReadAllText(RecordFile(uploadId)));
        Assert.Equal(new[]
        {
            DeliveryDecisions.SentToDirector, DeliveryDecisions.DirectorAnswer, DeliveryDecisions.AskedDirector,
            DeliveryDecisions.DeliveryStateAnswer, DeliveryDecisions.StillDelivering,
        }, Names(uploadId).SkipWhile(n => n != DeliveryDecisions.SentToDirector));
        Assert.Equal(DeliveryDecisions.AskReasonPromptUnanswered, Line(uploadId, DeliveryDecisions.AskedDirector).Reason);
        Assert.Equal(DeliveryDecisions.AskReasonPromptUnanswered, Line(uploadId, DeliveryDecisions.DirectorAnswer).Reason);
    }

    [Theory]
    [InlineData("delivered", 200, null, DictationDeliveryState.Delivered)]
    [InlineData("delivering", 202, "delivering", DictationDeliveryState.Pending)]
    [InlineData("unknown", 202, "unknown", DictationDeliveryState.Pending)]
    [InlineData("not-delivered", 502, null, DictationDeliveryState.Pending)]
    [InlineData("no-answer", 202, "no-answer", DictationDeliveryState.Pending)]
    [InlineData("director-too-old", 202, "no-answer", DictationDeliveryState.Pending)]
    [InlineData("never-left-the-gateway", 202, "no-answer", DictationDeliveryState.Pending)]
    public async Task AnUnansweredPrompt_IsAnsweredByWhatTheDirectorSays(
        string answer, int expectedStatus, string? expectedDirectorState, DictationDeliveryState expectedRecord)
    {
        // Proves each answer to the question maps to its HTTP answer and leaves the record where the contract says:
        // delivered resolves (200); delivering, unknown and every kind of no-answer hold (202, the record PENDING so the
        // client's retry re-runs); not-delivered is the retryable 502. The answer line names the state, or which kind
        // of no-answer it was - never folded into "unknown".
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = DirectorAnswers(answer);

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(expectedStatus, status);
        if (expectedStatus == 200)
        {
            Assert.True(body.GetProperty("submitted").GetBoolean());
            Assert.False(body.GetProperty("movedOn").GetBoolean());
            Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        }
        else if (expectedStatus == 202)
        {
            Assert.True(body.GetProperty("delivering").GetBoolean());
            Assert.Equal(expectedDirectorState, body.GetProperty("directorState").GetString());
        }
        else
        {
            Assert.Contains("did not deliver", body.GetProperty("error").GetString());
        }
        var read = _store.Read(uploadId);
        Assert.Equal(expectedRecord, read.Record!.State);

        var stateAnswer = Line(uploadId, DeliveryDecisions.DeliveryStateAnswer);
        if (answer is "delivered" or "delivering" or "unknown" or "not-delivered")
            Assert.Equal(answer, stateAnswer.State);
        else
        {
            Assert.Null(stateAnswer.State);
            Assert.Equal(answer, stateAnswer.Reason);
        }
    }

    [Fact]
    public async Task ARefusalAsStillDelivering_IsHeld_NotFailed()
    {
        // Proves a Director that refused this copy because the same delivery id is still being typed is the held 202,
        // not the failure path phase 1 left it on: the words may be in, so nothing is retyped and nothing is shown back.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Refused(DeliveryState.Delivering);

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Equal("delivering", body.GetProperty("directorState").GetString());
        Assert.True(_store.IsPending(uploadId));
        Assert.Equal(new[] { "prompt" }, _commands.Select(c => c.Verb)); // the answer said it all; nothing to ask
        Assert.Equal(DeliveryDecisions.StillDelivering, Names(uploadId).Last());
    }

    [Fact]
    public async Task AnAcceptedAnswerThatIsStillDelivering_IsHeld()
    {
        // Proves the answer phase 3's Director gives at its own budget - accepted, the send still going - is held like
        // any other "still delivering", so a send that later fails is not reported as delivered.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Answer(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivering });

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Equal("delivering", body.GetProperty("directorState").GetString());
        Assert.True(_store.IsPending(uploadId));
    }

    [Fact]
    public async Task APromptThatNeverLeftTheGateway_IsStillA502_AndNothingIsAsked()
    {
        // Proves the one case that is definitely not in stays the retryable 502: nothing was sent, so there is nothing
        // to ask about.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => null;

        var (status, _) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal(new[] { "prompt" }, _commands.Select(c => c.Verb));
        Assert.True(_store.IsPending(uploadId));
    }

    // ===== 2. a retry asks FIRST, before paying for a transcript ============================================

    [Fact]
    public async Task ARetryAfterStillDelivering_AsksFirst_AndDoesNotTranscribe()
    {
        // Proves the retry of a recording that was already handed to the Director asks before it transcribes: while the
        // Director says delivering the retry is held with no transcription and no second prompt, and when it then says
        // delivered the recording resolves as delivered - still with one transcription and one prompt in total.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Refused(DeliveryState.Delivering);
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);
        Assert.Equal(1, _transcriber.Calls);

        _deliveryState = _ => StateIs(DeliveryState.Delivering);
        var held = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);
        Assert.Equal(202, held.Status);
        Assert.Equal("delivering", held.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, _transcriber.Calls);

        _deliveryState = _ => StateIs(DeliveryState.Delivered);
        var delivered = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);
        Assert.Equal(200, delivered.Status);
        Assert.True(delivered.Body.GetProperty("submitted").GetBoolean());
        // Change 1, G1: the words come back although nothing was transcribed again - the send kept them on the record.
        Assert.Equal(SpokenWords, delivered.Body.GetProperty("transcript").GetString());
        Assert.Equal(SpokenWords, _store.ReadRecord(uploadId)!.Transcript);
        Assert.Equal(1, _transcriber.Calls);
        Assert.Single(_commands, c => c.Verb == "prompt");
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
        Assert.Equal(DeliveryDecisions.AskReasonRetryAsksFirst, Lines(uploadId)
            .Last(l => l.Decision == DeliveryDecisions.AskedDirector).Facts!.Reason);
    }

    [Fact]
    public async Task ARetryWithNoAnswerToTheQuestion_IsHeld_AndDoesNotTranscribe()
    {
        // Proves a retry whose question gets no answer is held too - the words may be in - and pays for nothing.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        var held = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(202, held.Status);
        Assert.Equal("no-answer", held.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Single(_commands, c => c.Verb == "prompt");
    }

    [Theory]
    [InlineData(DeliveryState.NotDelivered)]
    [InlineData(DeliveryState.Unknown)]
    public async Task ARetryTheDirectorSaysIsNotIn_TranscribesAndSendsAgain(DeliveryState answer)
    {
        // Proves a retry the Director says is not in (a failed send, or an id it never saw) carries on as a first
        // attempt: it transcribes and sends, and delivers.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => StateIs(DeliveryState.NotDelivered);
        Assert.Equal(502, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        _prompt = _ => Accepted();
        _deliveryState = _ => StateIs(answer);
        var retry = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(200, retry.Status);
        Assert.Equal(2, _transcriber.Calls);
        Assert.Equal(2, _commands.Count(c => c.Verb == "prompt"));
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
    }

    // ===== 4. the five-minute rule ==========================================================================

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public async Task TheAgeLimit_IsStrictlyMoreThanFiveMinutesFromSend(int secondsSinceSend, bool shownBack)
    {
        // Proves the boundary: 4:59 and 5:00 after Send are typed; 5:01 is shown back - 200 with movedOn, the reason
        // "too-old" and the words - and NOTHING is typed. The words are kept on the record, never deleted.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _clock.Now = T0 + TimeSpan.FromSeconds(secondsSinceSend);

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(200, status);
        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        if (!shownBack)
        {
            Assert.True(body.GetProperty("submitted").GetBoolean());
            Assert.False(body.GetProperty("movedOn").GetBoolean());
            Assert.Single(_commands, c => c.Verb == "prompt");
            return;
        }
        Assert.False(body.GetProperty("submitted").GetBoolean());
        Assert.True(body.GetProperty("movedOn").GetBoolean());
        Assert.Equal(GatewayDictationEndpoint.TooOldReason, body.GetProperty("reason").GetString());
        Assert.Equal("too-old", body.GetProperty("reason").GetString());
        Assert.True(body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Empty(_commands);
        Assert.True(record.MovedOn);
        Assert.Equal("too-old", record.Reason);
        Assert.Equal(SpokenWords, record.Transcript);
        var tooOld = Line(uploadId, DeliveryDecisions.TooOld);
        Assert.Equal(301, tooOld.AgeSeconds);
        Assert.DoesNotContain(DeliveryDecisions.MovedOn, Names(uploadId));
    }

    [Fact]
    public void TheLimitIsFiveMinutes_StatedInMinutes()
    {
        // Pins the owner's number where it is stated, so a change to it is a change to this test too.
        Assert.Equal(5, GatewayDictationEndpoint.MaxDeliveryAgeMinutes);
        Assert.Equal(TimeSpan.FromMinutes(5), GatewayDictationEndpoint.MaxDeliveryAge);
    }

    [Fact]
    public async Task ARecordingHeldAsDelivering_IsNeverShownBack_HoweverLongItHasBeen()
    {
        // Proves the age rule applies only to words known not to be in, and that "could not confirm it arrived" is for a
        // Director that gives NO answer: a recording the Director keeps saying it is delivering is held at ten minutes
        // and at an hour - never shown back as too old, never ruled unconfirmed. The Director's own watch ends it.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Refused(DeliveryState.Delivering);
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        _deliveryState = _ => StateIs(DeliveryState.Delivering);
        _clock.Now = T0 + TimeSpan.FromMinutes(10);
        var tenMinutes = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);
        _clock.Now = T0 + TimeSpan.FromHours(1);
        var anHour = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        foreach (var held in new[] { tenMinutes, anHour })
        {
            Assert.Equal(202, held.Status);
            Assert.Equal("delivering", held.Body.GetProperty("directorState").GetString());
        }
        Assert.True(_store.IsPending(uploadId));
        Assert.DoesNotContain(DeliveryDecisions.TooOld, Names(uploadId));
        Assert.DoesNotContain(DeliveryDecisions.Unconfirmed, Names(uploadId));
        Assert.Single(_commands, c => c.Verb == "prompt");
    }

    // ===== change 1: nothing is held forever - "could not confirm it arrived" =============================

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public async Task AHeldRecordingWithNoAnswer_IsUnconfirmed_OnlyPastFiveMinutesFromSend(int secondsSinceSend, bool unconfirmed)
    {
        // Proves the boundary of the Delivery Lead's ruling on the dictation path: a recording sent and never answered
        // for is held while it is within five minutes of Send (4:59 and 5:00 are still the 202), and at 5:01 its retry,
        // which asks first and again gets no answer, is resolved as "could not confirm it arrived" - 200, not sent, shown
        // back with the words the send kept, offerSendAnyway false - with nothing transcribed again, nothing typed again,
        // and a decision line carrying the age and the kind of no answer.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        _clock.Now = T0 + TimeSpan.FromSeconds(secondsSinceSend);
        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(1, _transcriber.Calls);
        Assert.Single(_commands, c => c.Verb == "prompt");
        if (!unconfirmed)
        {
            Assert.Equal(202, status);
            Assert.Equal("no-answer", body.GetProperty("directorState").GetString());
            Assert.True(_store.IsPending(uploadId));
            Assert.DoesNotContain(DeliveryDecisions.Unconfirmed, Names(uploadId));
            return;
        }
        Assert.Equal(200, status);
        Assert.False(body.GetProperty("submitted").GetBoolean());
        Assert.True(body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("unconfirmed", body.GetProperty("reason").GetString());
        Assert.Equal(GatewayDictationEndpoint.UnconfirmedReason, body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Delivered, record.State);
        Assert.True(record.MovedOn);
        Assert.False(record.Submitted);
        Assert.Equal("unconfirmed", record.Reason);
        Assert.Equal(SpokenWords, record.Transcript);
        var line = Line(uploadId, DeliveryDecisions.Unconfirmed);
        Assert.Equal(301, line.AgeSeconds);
        Assert.Equal("no-answer", line.DirectorNoAnswer);
        Assert.Equal(DeliveryDecisions.Unconfirmed, Names(uploadId).Last());
    }

    [Theory]
    [InlineData("no-answer")]
    [InlineData("director-too-old")]
    [InlineData("never-left-the-gateway")]
    public async Task EveryKindOfNoAnswer_PastFiveMinutes_IsUnconfirmed_AndTheLineSaysWhichKind(string kind)
    {
        // Proves "any kind of no answer" means all three: a Director too old for the question, a silent one, and one the
        // question never reached are each ruled unconfirmed past five minutes, and the line names which it was.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = DirectorAnswers(kind);
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        _clock.Now = T0 + TimeSpan.FromMinutes(6);
        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(200, status);
        Assert.Equal("unconfirmed", body.GetProperty("reason").GetString());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Equal(kind, Line(uploadId, DeliveryDecisions.Unconfirmed).DirectorNoAnswer);
        Assert.Single(_commands, c => c.Verb == "prompt");
    }

    [Fact]
    public async Task AFirstAttemptWhoseQuestionGetsNoAnswerPastFiveMinutes_IsUnconfirmed()
    {
        // Proves the verdict applies on ANY attempt: a first attempt judged at 4:50 is sent (not too old), the prompt
        // runs out of time 30 seconds later, the question gets no answer, and by then it is past five minutes from Send -
        // so it is ruled unconfirmed at once, with this attempt's words, and one prompt only.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _clock.Now = T0 + TimeSpan.FromSeconds(290);
        _prompt = _ =>
        {
            _clock.Now += TimeSpan.FromSeconds(30);
            return Timeout();
        };
        _deliveryState = _ => NoAnswer();

        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0);

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("unconfirmed", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Single(_commands, c => c.Verb == "prompt");
        Assert.Equal(320, Line(uploadId, DeliveryDecisions.Unconfirmed).AgeSeconds);
    }

    [Fact]
    public async Task TheSendKeepsTheWordsOnThePendingRecord()
    {
        // Proves G1 at its source: the moment the words are sent they are on the PENDING record, so a held recording
        // carries them for any later answer that does not transcribe again.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => NoAnswer();

        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Pending, record.State);
        Assert.Equal(SpokenWords, record.Transcript);
        Assert.Equal(SpokenWords, _store.SentWords(uploadId));
    }

    [Theory]
    [InlineData("too-old", true)]
    [InlineData("session-exited", true)]
    [InlineData(null, true)]
    [InlineData("unconfirmed", false)]
    public void OfferSendAnyway_IsRuledFromTheReasonAlone(string? reason, bool offered)
    {
        // Pins the Gateway's ruling every shown-back answer carries: "Send anyway" for words known not to be in (too
        // old, session exited) and for an old tombstone with no reason; never for unconfirmed, where it could double.
        Assert.Equal(offered, GatewayDictationEndpoint.OffersSendAnyway(reason));
    }

    [Theory]
    [InlineData(DeliveryState.Unknown)]
    [InlineData(DeliveryState.NotDelivered)]
    public async Task AHeldRecording_WhoseDirectorLaterSaysNotIn_IsJudgedByItsAge(DeliveryState laterAnswer)
    {
        // Proves the other side (contract sections 4 and 5): a recording held with no answer, whose next attempt asks
        // first and hears the words are not in - never seen, or not delivered when the Director's fifteen-minute watch
        // ends - is judged by the age rule like a first attempt, so six minutes after Send it is shown back with its
        // words (200, movedOn, "too-old") and nothing is typed.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await CompleteAsync(uploadId, sid, sentAt: T0)).Status);

        _clock.Now = T0 + TimeSpan.FromMinutes(6);
        _deliveryState = _ => StateIs(laterAnswer);
        var (status, body) = await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", body.GetProperty("reason").GetString());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Single(_commands, c => c.Verb == "prompt");
        Assert.Equal(360, Line(uploadId, DeliveryDecisions.TooOld).AgeSeconds);
    }

    // ===== 5. every decision is written, never the words ====================================================

    [Fact]
    public async Task EveryNewDecision_IsWrittenInOrder_AndNoneHoldsTheWords()
    {
        // Proves the whole new sequence lands in the upload's decision log, in order: the unanswered send, the question
        // and its answer, the hold; then the retry's question first, its answer, and the delivery - and the file never
        // holds a word the owner said.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Timeout();
        _deliveryState = _ => StateIs(DeliveryState.Delivering);
        await CompleteAsync(uploadId, sid, sentAt: T0);
        _deliveryState = _ => StateIs(DeliveryState.Delivered);
        await CompleteAsync(uploadId, sid, sentAt: T0, resumed: true);

        Assert.Equal(new[]
        {
            DeliveryDecisions.Received, DeliveryDecisions.Transcribed, DeliveryDecisions.SentToDirector,
            DeliveryDecisions.DirectorAnswer, DeliveryDecisions.AskedDirector, DeliveryDecisions.DeliveryStateAnswer,
            DeliveryDecisions.StillDelivering,
            DeliveryDecisions.Retried, DeliveryDecisions.AskedDirector, DeliveryDecisions.DeliveryStateAnswer,
            DeliveryDecisions.Delivered,
        }, Names(uploadId));
        var file = File.ReadAllText(Path.Combine(UploadDir(uploadId), "decisions.jsonl"));
        foreach (var word in new[] { "build", "broke" })
            Assert.DoesNotContain(word, file);
    }

    // ===== harness ==========================================================================================

    private static DirectorCommandResult Answer(PromptResponse response)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(response));

    private static DirectorCommandResult Accepted()
        => Answer(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered });

    private static DirectorCommandResult Refused(DeliveryState state) => Answer(new PromptResponse
    {
        Accepted = false,
        DeliveryState = state,
        DeliveryStateReason = $"already {state}; this copy was refused and nothing was typed",
        Error = $"already {state}; this copy was refused and nothing was typed",
    });

    // What the Gateway's own command router returns when it stops waiting for the Director.
    private static DirectorCommandResult Timeout()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");

    private static DirectorCommandResult NoAnswer()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer the question within 30 seconds");

    private static DirectorCommandResult StateIs(DeliveryState state, string? reason = null)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new DeliveryStateResponse
        {
            State = state,
            Reason = reason ?? (state == DeliveryState.NotDelivered ? "the composer never echoed the text" : null),
        }));

    private static Func<DirectorCommand, DirectorCommandResult?> DirectorAnswers(string answer) => answer switch
    {
        "delivered" => _ => StateIs(DeliveryState.Delivered),
        "delivering" => _ => StateIs(DeliveryState.Delivering),
        "unknown" => _ => StateIs(DeliveryState.Unknown),
        "not-delivered" => _ => StateIs(DeliveryState.NotDelivered),
        "no-answer" => _ => NoAnswer(),
        // The real refusal an older Director's dispatch gives a verb it does not know.
        "director-too-old" => _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown verb '{DeliveryStateRequest.Verb}'"),
        "never-left-the-gateway" => _ => null,
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, null),
    };

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
                ActivityState = "Working",
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

    private async Task<(int Status, JsonElement Body)> CompleteAsync(string uploadId, string sid, DateTimeOffset sentAt, bool resumed = false)
    {
        // The route writes "retried" for a resumed attempt before the core runs; this harness drives the core, so it
        // writes the same line to keep the log the shape a client produces.
        if (resumed)
            _store.RecordDecision(uploadId, DeliveryDecisions.Retried, new DeliveryDecisionFacts { SessionId = sid, Resumed = true });
        var outcome = await GatewayDictationEndpoint.RunCompleteCoreAsync(
            uploadId, TenantId.Local,
            new DictationCompleteRequest
            {
                SessionId = sid, TotalChunks = 1, Mime = "audio/webm", Ext = "webm",
                SentAtUtc = sentAt.UtcDateTime, Resumed = resumed,
            },
            _store, _registry, owners: null, Transcription(), _marks,
            deliverySurface: "cockpit", deliveryIdentityKind: "device-key",
            pushedSessions: _pushed,
            sendCommand: (directorId, command, ct) =>
            {
                lock (_commands) _commands.Add(command);
                var answer = command.Verb switch
                {
                    "prompt" => _prompt(command),
                    DeliveryStateRequest.Verb => _deliveryState(command),
                    _ => DirectorCommandResult.Success("{}"),
                };
                return Task.FromResult(answer);
            },
            streamStale: TimeSpan.FromSeconds(20), clock: _clock);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await outcome.ToResult().ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    private IReadOnlyList<DeliveryDecisionLine> Lines(string uploadId) => _store.ReadDecisions(uploadId).Lines;
    private string[] Names(string uploadId) => Lines(uploadId).Select(l => l.Decision).ToArray();
    private DeliveryDecisionFacts Line(string uploadId, string decision) => Lines(uploadId).Single(l => l.Decision == decision).Facts!;
    private string UploadDir(string uploadId) => Path.Combine(_root, "uploads", VoiceUploadStore.NormalizeUploadId(uploadId)!);
    private string RecordFile(string uploadId) => Path.Combine(UploadDir(uploadId), "record.json");

    private GatewayTranscriptionService Transcription() => new(
        new KeyVault(_vaultPath),
        dictionaryProvider: _ => DictationDictionary.Empty,
        modeProvider: () => TranscriptionMode.DevThrottle,
        http: new HttpClient(_transcriber, disposeHandler: false),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive")));

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now;
        public FixedClock(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingTranscriptHandler : HttpMessageHandler
    {
        private readonly string _text;
        private int _calls;
        public CountingTranscriptHandler(string text) => _text = text;
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
