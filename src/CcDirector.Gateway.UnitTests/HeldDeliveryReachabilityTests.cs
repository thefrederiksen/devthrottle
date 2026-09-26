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
/// THE REACHABILITY AND ENDING RULES OF AN OWNED DELIVERY (Voice Delivery mission, phase 5 round 2, contract
/// sections 8 and 9).
///
/// QA case 9 (upload 53baf90b): the Director froze right after the Gateway handed it the words, and every later
/// attempt answered 404 "session not found" more than 80 times - the Cockpit told the owner his session was gone,
/// untruthfully. The ruling: a Director that is stale or unreachable is a HELD delivery, never "session gone";
/// "session gone" only when the Gateway can PROVE the session ended. Case 8: a merely STALE Director (heartbeat 21
/// to 42 seconds) answered the same 404 and one paid transcript was thrown away - the ruling: a transcript is kept
/// on the record the moment it exists and never paid for twice.
///
/// These tests drive the REAL delivery core and driver over a real upload store on disk, with the Director played
/// by delegates and the pushed-session store on a controllable clock, so "stale", "frozen", "fresh" and "five
/// minutes and one second" are all exact.
/// </summary>
public sealed class HeldDeliveryReachabilityTests : IDisposable
{
    private const string DirectorId = "director-reach";
    private const string SpokenWords = "ship the hotfix and watch the queue";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-reach-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath;
    private readonly VoiceUploadStore _store;
    private readonly DirectorRegistry _registry;
    /// <summary>The pushed-session store's OWN clock, so "stale" and "fresh" are set by the test, not waited for.</summary>
    private DateTime _pushedNow = DateTime.UtcNow;
    /// <summary>The pushed store's sequence numbers, kept monotonic: two snapshots a moment apart must never race a
    /// clock that can tick backwards or stand still.</summary>
    private long _pushedSeq;
    private readonly Streaming.PushedSessionStore _pushed;
    private readonly TranscribingSessions _marks = new();
    private readonly CountingTranscriptHandler _transcriber = new(SpokenWords);
    private readonly AheadClock _clock = new();
    private readonly List<DirectorCommand> _commands = new();
    private readonly DateTime _sentAt = DateTime.UtcNow;
    private DictationDelivery? _delivery;

    /// <summary>How the Director answers the prompt verb. Null means the command never left this Gateway.</summary>
    private Func<DirectorCommand, Task<DirectorCommandResult?>> _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
    /// <summary>How the Director answers "what became of delivery id X?".</summary>
    private Func<DirectorCommand, DirectorCommandResult?> _deliveryState = _ => StateIs(DeliveryState.Unknown);

    public HeldDeliveryReachabilityTests()
    {
        _vaultPath = Path.Combine(_root, "keyvault.json");
        Directory.CreateDirectory(_root);
        _store = new VoiceUploadStore(Path.Combine(_root, "uploads"), TenantId.Local);
        _pushed = new Streaming.PushedSessionStore(() => _pushedNow);
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

    // ===== QA case 9: a frozen Director is a held delivery, and the time rules end it ======================

    [Fact]
    public async Task QACase9_AFrozenDirector_IsHeldWaitingForTheDirector_OnEveryWakeUp_AndEndsUnconfirmedWithTheWords()
    {
        // The prompt went unanswered and the Director froze: across MANY driver wake-ups every answer is 202
        // "waiting-for-director" - never a 404, never "session gone" - nothing is transcribed again, nothing is
        // typed again, and at 5:01 from Send the recording is ruled "could not confirm it arrived" WITH the words.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal("no-answer", first.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, _transcriber.Calls);

        // The Director freezes: its tunnel drops, so the session cannot be located by anything.
        _pushed.UnregisterConnection(TenantId.Local, DirectorId, "conn-1");

        foreach (var ahead in new[] { 60, 120, 180, 240, 299 })
        {
            _clock.Ahead = TimeSpan.FromSeconds(ahead);
            await driver.TickAsync();
            var held = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
            Assert.Equal(202, held.Status);
            Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, held.Body.GetProperty("directorState").GetString());
            Assert.Equal(1, _transcriber.Calls);
            Assert.Equal(1, Prompts());
            Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(uploadId)!.State);
        }

        // Past the limit: the next wake-up that is due (the wait between attempts has risen to 15 seconds by
        // now, so 5:01 itself may not be due) rules it "could not confirm it arrived" with the words.
        foreach (var at in new[] { 301, 316 })
        {
            _clock.Fixed = _sentAt.AddSeconds(at);
            await driver.TickAsync();
        }
        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.False(read.Body.GetProperty("submitted").GetBoolean());
        Assert.True(read.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("unconfirmed", read.Body.GetProperty("reason").GetString());
        Assert.False(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(1, Prompts());
        Assert.Equal(0, driver.HeldCount);
    }

    // ===== QA case 8: a merely stale Director is held, and the transcript is paid for once ===================

    [Fact]
    public async Task QACase8_AStaleDirectorOnTheFirstComplete_IsHeld_ThenTheDirectorIsBack_SentOnceDelivered_OneTranscription()
    {
        // The Director's pushes have gone stale (the registry entry and the pushed entry both survive): the first
        // complete is HELD, not 404, and nothing is transcribed. The tunnel comes back and the driver - woken by the
        // Director's sessions arriving - sends the words exactly once, with exactly one transcription in total.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _pushedNow = DateTime.UtcNow.AddSeconds(45);   // past stale (20s) plus the locate grace (10s)

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(0, _transcriber.Calls);
        Assert.Equal(0, Prompts());

        // The tunnel comes back: a NEW connection of the SAME Director (the registry entry never left) and its
        // first full snapshot - the driver's director-connected wake.
        _pushedNow = DateTime.UtcNow;
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", ++_pushedSeq, new[] { Session(sid) }));
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
        driver.OnSessionsArrived(TenantId.Local, DirectorId);
        // The director-connected wake runs off the caller's thread, exactly as it does in the host: wait for the
        // record to reach its end, bounded, rather than asserting a race.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.ReadRecord(uploadId) is not { State: DictationDeliveryState.Delivered }
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.True(read.Body.GetProperty("submitted").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(DeliveryDecisions.DriveDirectorConnected,
            Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayDrive).Facts!.Trigger);
    }

    [Fact]
    public async Task NotDeliveredThenAResend_TranscribesOnceAcrossBothAttempts()
    {
        // A transcript is kept on the record the moment it exists and REUSED: the first attempt transcribes and the
        // Director says not delivered; the driver's attempt asks first, hears not delivered again, and sends the KEPT
        // words without ever paying for them twice.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Refused(DeliveryState.NotDelivered, "the session refused it"));
        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.RetryingState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(SpokenWords, _store.SentWords(uploadId));

        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
        _deliveryState = _ => StateIs(DeliveryState.NotDelivered);
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.True(read.Body.GetProperty("submitted").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(2, Prompts());
        Assert.Equal(1, _transcriber.Calls);
    }

    // ===== a recording that could never be transcribed is not held for 24 hours =============================

    [Theory]
    [InlineData(299, 202)]
    [InlineData(301, 200)]
    public async Task ARecordingWhoseTranscriptionKeepsFailing_IsHeldWithinTheLimit_AndShownBackTooOldPastItWithNoWords(
        int secondsSinceSend, int status)
    {
        // The transcription fails every time, so there are no words to keep: within the limit the delivery is held
        // (202 "retrying", the Gateway tries again itself); past the limit it is shown back too old with NO words
        // and "Send anyway" - the client has the no-words wording, and the recording is still on the device.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _transcriber.Fail = true;

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.RetryingState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, _transcriber.Calls);

        _clock.Fixed = _sentAt.AddSeconds(secondsSinceSend);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(status, read.Status);
        if (status == 202)
        {
            Assert.Equal(DeliverySendAndAsk.RetryingState, read.Body.GetProperty("directorState").GetString());
            Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(uploadId)!.State);
            return;
        }
        Assert.False(read.Body.GetProperty("submitted").GetBoolean());
        Assert.True(read.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", read.Body.GetProperty("reason").GetString());
        Assert.True(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal("", read.Body.GetProperty("transcript").GetString());
        Assert.Equal(0, Prompts());
    }

    // ===== a session that has ENDED resolves the delivery at once (F4) =====================================

    [Fact]
    public async Task AnEndedSession_LocatedAndExited_ResolvesAtOnce_SessionExitedWithTheWords_AndNoSendAnyway()
    {
        // The first attempt is held with the words kept; the session then exits and its Director still lists it
        // (Exited): the driver's attempt resolves AT ONCE - session-exited, the words handed back, NO "Send anyway"
        // (there is no session left to send to) - and the driver stops driving it.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq,
            new[] { Session(sid, status: "Exited") }));
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.False(read.Body.GetProperty("submitted").GetBoolean());
        Assert.True(read.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("session-exited", read.Body.GetProperty("reason").GetString());
        Assert.False(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());   // the first attempt's; the ended session was sent nothing
        Assert.Equal(0, driver.HeldCount);
    }

    [Fact]
    public async Task AnEndedSession_AFreshDirectorThatNoLongerListsIt_ResolvesTheSameWay()
    {
        // The OTHER proof of an ended session: its Director is connected and FRESH and no longer lists it (it pushed
        // a remove). The locate finds nothing - and instead of holding or answering 404, the driver proves the end
        // from the Director its own record remembered and resolves session-exited with the words.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);
        Assert.Equal(DirectorId, _store.ReadRecord(uploadId)!.Owned!.DirectorId);

        Assert.True(_pushed.ApplyRemove(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq, sid));
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.Equal("session-exited", read.Body.GetProperty("reason").GetString());
        Assert.False(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(0, driver.HeldCount);
    }

    [Fact]
    public async Task AStaleDirectorWhoseSessionCannotBeLocated_IsHeld_NotEnded()
    {
        // The proof needs a CONNECTED AND FRESH Director: a merely stale one proves nothing, so the same snapshot
        // that ended the session above ends nothing here - the delivery is held "waiting-for-director", the words
        // are kept, and the driver keeps driving it.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        Assert.True(_pushed.ApplyRemove(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq, sid));
        _pushedNow = DateTime.UtcNow.AddSeconds(45);   // the Director went stale after the remove
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(202, read.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, read.Body.GetProperty("directorState").GetString());
        Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(uploadId)!.State);
        Assert.Equal(SpokenWords, _store.SentWords(uploadId));
        Assert.Equal(1, driver.HeldCount);
    }

    [Fact]
    public async Task AHeldSendAnyway_WhoseSessionEnded_IsResolvedSessionExited_NotRetriedToTheLimit()
    {
        // A's open point, ruled by F4: a held "Send anyway" whose session has ended is resolved session-exited at
        // once - the words kept on its delivery and handed back with Dismiss and NO "Send anyway" - not retried to
        // the limit. The Director named on the held delivery (the one the owner's own press located) is the proof.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _store.MarkDelivered(uploadId, submitted: false, movedOn: true, SpokenWords, reason: DeliveryDecisions.TooOld);
        Assert.True(_store.ResolveDeliveryClaim(uploadId, sid, SpokenWords.Length, DateTime.UtcNow).Verified);
        Assert.True(_store.TakeSendAnywayOwnership(uploadId, new SendAnywayDelivery(
            DateTime.UtcNow, sid, SpokenWords, "cockpit", null, DirectorId: DirectorId)));
        driver.Track(TenantId.Local, uploadId, HeldDeliveryKind.SendAnyway);

        Assert.True(_pushed.ApplyRemove(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq, sid));
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.False(read.Body.GetProperty("submitted").GetBoolean());
        Assert.True(read.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("session-exited", read.Body.GetProperty("reason").GetString());
        Assert.False(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(0, Prompts());
        Assert.Equal(0, driver.HeldCount);
        Assert.Equal(DeliveryDecisions.SessionExited, Names(uploadId).Last());
    }

    // ===== the Director itself refused it for age (contract section 9, F6) ================================

    [Fact]
    public async Task ATooOldRefusalFromTheDirector_HeardByAnAskingRetry_PastTheLimit_IsShownBackTooOldWithTheWords()
    {
        // The Director's own age check (Developer D's) answers not-delivered with reason too-old. Known not to be
        // in, and past the limit from Send: shown back too old AT ONCE with the kept words - not sent again - and
        // the decision record says the DIRECTOR refused it for age.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        _clock.Fixed = _sentAt.AddSeconds(301);
        _deliveryState = _ => StateIs(DeliveryState.NotDelivered, reason: DeliveryDecisions.TooOld);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, read.Status);
        Assert.False(read.Body.GetProperty("submitted").GetBoolean());
        Assert.True(read.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", read.Body.GetProperty("reason").GetString());
        Assert.True(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, read.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(DeliveryDecisions.TooOld,
            Lines(uploadId).Last(l => l.Decision == DeliveryDecisions.DeliveryStateAnswer).Facts!.Reason);
    }

    [Fact]
    public async Task ATooOldRefusalOnTheSendItself_PastTheLimit_IsShownBackTooOld_WithTheWords()
    {
        // The same ruling on the send's own answer: the words are sent within the limit by the Gateway's clock, the
        // prompt verb takes its time answering, and the Director - whose clock reads past the limit - refuses it as
        // too old. Nothing is re-sent and nothing transcribed again: shown back too old with the words.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _clock.Fixed = _sentAt.AddSeconds(299);
        _prompt = _ =>
        {
            _clock.Fixed = _sentAt.AddSeconds(310);   // the verb's wait moved the Gateway's clock past the limit
            return Task.FromResult<DirectorCommandResult?>(Refused(DeliveryState.NotDelivered, DeliveryDecisions.TooOld));
        };

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);

        Assert.Equal(200, first.Status);
        Assert.False(first.Body.GetProperty("submitted").GetBoolean());
        Assert.True(first.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", first.Body.GetProperty("reason").GetString());
        Assert.True(first.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, first.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(DeliveryDecisions.TooOld,
            Lines(uploadId).Last(l => l.Decision == DeliveryDecisions.DirectorAnswer).Facts!.Reason);
    }

    // ===== harness ==========================================================================================

    private (HeldDeliveryDriver Driver, DictationDelivery Delivery) NewDriver()
    {
        var delivery = new DictationDelivery(_registry, owners: null, Transcription(), _marks, _pushed, SendAsync,
            TimeSpan.FromSeconds(20), _clock);
        var driver = new HeldDeliveryDriver(_store, _ => new NoScope(),
            (tenant, sid) => _pushed.TryLocateIgnoringFreshness(tenant, sid)?.DirectorId, hosted: false)
        {
            // Only a test's own TickAsync ticks it.
            TickInterval = TimeSpan.FromHours(1),
        };
        driver.Attach(delivery);
        _delivery = delivery;
        return (driver, delivery);
    }

    private sealed class NoScope : IDisposable { public void Dispose() { } }

    private DictationOwnedDelivery Owned(string sid)
        => new(DateTime.UtcNow, sid, 1, "audio/webm", "webm", null, null, null, _sentAt, "cockpit", "device-key");

    /// <summary>The first attempt, made exactly as the complete route makes it: take the delivery over, hand it to the
    /// driver, attempt it through the shared single-flight - and render the answer the client would receive.</summary>
    private async Task<(int Status, JsonElement Body)> OwnAndAttemptAsync(HeldDeliveryDriver driver, string uploadId, string sid)
    {
        var owned = Owned(sid);
        Assert.Equal(DictationOwnership.Taken, _store.TakeOwnership(uploadId, owned).Result);
        driver.Track(TenantId.Local, uploadId, HeldDeliveryKind.Dictation);
        var outcome = await _delivery!.AttemptAsync(TenantId.Local, _store, uploadId, owned, driveTrigger: null);
        return await RenderAsync(outcome.ToResult());
    }

    private Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
    {
        lock (_commands) _commands.Add(command);
        return command.Verb switch
        {
            "prompt" => _prompt(command),
            DeliveryStateRequest.Verb => Task.FromResult(_deliveryState(command)),
            _ => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}")),
        };
    }

    private int Prompts()
    {
        lock (_commands) return _commands.Count(c => c.Verb == "prompt");
    }

    private static async Task<(int Status, JsonElement Body)> RenderAsync(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    private SessionDto Session(string sid, string status = "Running") => new()
    {
        SessionId = sid,
        DirectorId = DirectorId,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle",
        Status = status,
        ActivityState = status == "Exited" ? "Exited" : "Working",
        LastActivityAt = DateTime.UtcNow,
    };

    private string Seat()
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq, new[] { Session(sid) }));
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

    private IReadOnlyList<DeliveryDecisionLine> Lines(string uploadId) => _store.ReadDecisions(uploadId).Lines;
    private string[] Names(string uploadId) => Lines(uploadId).Select(l => l.Decision).ToArray();

    private static DirectorCommandResult Answer(PromptResponse response)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(response));

    private static DirectorCommandResult Accepted()
        => Answer(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered });

    private static DirectorCommandResult Refused(DeliveryState state, string reason) => Answer(new PromptResponse
    {
        Accepted = false,
        DeliveryState = state,
        DeliveryStateReason = reason,
        Error = reason,
    });

    private static DirectorCommandResult Timeout()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");

    private static DirectorCommandResult NoAnswer()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer the question within 30 seconds");

    private static DirectorCommandResult StateIs(DeliveryState state, string? reason = null)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(
            new DeliveryStateResponse { State = state, Reason = reason }));

    private GatewayTranscriptionService Transcription() => new(
        new KeyVault(_vaultPath),
        dictionaryProvider: _ => DictationDictionary.Empty,
        modeProvider: () => TranscriptionMode.DevThrottle,
        http: new HttpClient(_transcriber, disposeHandler: false),
        history: new TranscriptionHistoryLog(Path.Combine(_root, "history")),
        audioArchive: new TranscriptionAudioArchive(Path.Combine(_root, "archive")));

    /// <summary>The real clock moved ahead by <see cref="Ahead"/>, or pinned to <see cref="Fixed"/> when a boundary must be
    /// exact: every limit is judged by it.</summary>
    private sealed class AheadClock : TimeProvider
    {
        public TimeSpan Ahead;
        public DateTime? Fixed;
        public override DateTimeOffset GetUtcNow()
            => Fixed is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : DateTimeOffset.UtcNow + Ahead;
    }

    private sealed class CountingTranscriptHandler : HttpMessageHandler
    {
        private readonly string _text;
        private int _calls;
        public CountingTranscriptHandler(string text) => _text = text;
        public int Calls => Volatile.Read(ref _calls);
        /// <summary>Set by a test to make every transcription fail - a recording that can never be transcribed.</summary>
        public bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Fail)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"transcriber unavailable\"}", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"" + _text + "\"}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
