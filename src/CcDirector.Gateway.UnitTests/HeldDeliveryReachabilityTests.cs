using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Transcription;
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

    [Fact]
    public async Task ARecordThatNamesNoDirector_CannotProveAnEnding_IsHeld_EvenWhenTheOwnerCacheKnowsItsDirector()
    {
        // NO FALLBACK (the repository's first law). The ended proof reads ONE source: the Director the delivery's
        // own durable record names. A record that names none - its session was never located by any attempt -
        // cannot prove an ending, and the in-memory owner cache is NOT consulted for it: that was a second way
        // to answer the same question, and no production record can predate the field anyway (the record and
        // its DirectorId ship in the same release). So here the cache DOES know the session's Director, and the
        // Director is connected and FRESH and does not list the session - everything the old fallback needed
        // to end it - and the delivery is HELD all the same: 202 waiting-for-director, never session-exited.
        var owners = new SessionOwnerCache();
        var (driver, _) = NewDriver(owners);
        var sid = Guid.NewGuid().ToString();
        // The Director is connected and fresh, and has never listed the session (no attempt ever located it,
        // so the record never remembered a Director):
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq,
            Array.Empty<SessionDto>()));
        owners.Remember(TenantId.Local, sid, DirectorId);   // what the removed fallback would have read
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Null(_store.ReadRecord(uploadId)!.Owned!.DirectorId);

        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(202, read.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, read.Body.GetProperty("directorState").GetString());
        Assert.Equal(DictationDeliveryState.Pending, _store.ReadRecord(uploadId)!.State);
        Assert.Equal(0, Prompts());
        Assert.Equal(0, _transcriber.Calls);
        Assert.Equal(1, driver.HeldCount);
    }

    [Fact]
    public async Task AHeldSendAnyway_WhoseRecordNamesNoDirector_IsHeld_EvenWhenTheOwnerCacheKnowsItsDirector()
    {
        // The same rule on the "Send anyway" half: the Director named on the held delivery (the one the owner's
        // own press located) is the ONE proof. A held record that names no Director - the press never located the
        // session, so nothing was remembered - cannot prove an ending, the owner cache is not consulted, and
        // the claim stays held for the driver to keep driving: never session-exited.
        var owners = new SessionOwnerCache();
        var (driver, _) = NewDriver(owners);
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq,
            Array.Empty<SessionDto>()));
        owners.Remember(TenantId.Local, sid, DirectorId);   // what the removed fallback would have read
        var uploadId = await StagedClipAsync(sid);
        _store.MarkDelivered(uploadId, submitted: false, movedOn: true, SpokenWords, reason: DeliveryDecisions.TooOld);
        Assert.True(_store.ResolveDeliveryClaim(uploadId, sid, SpokenWords.Length, DateTime.UtcNow).Verified);
        Assert.True(_store.TakeSendAnywayOwnership(uploadId, new SendAnywayDelivery(
            DateTime.UtcNow, sid, SpokenWords, "cockpit", null, DirectorId: null)));
        driver.Track(TenantId.Local, uploadId, HeldDeliveryKind.SendAnyway);

        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(202, read.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, read.Body.GetProperty("directorState").GetString());
        Assert.Equal(0, Prompts());
        Assert.Equal(1, driver.HeldCount);
    }

    // ===== a delivery the Gateway HANDED BACK to the client (review round, the ruling on finding 1) =========

    [Fact]
    public async Task AnOutOfCreditsDriverAttempt_IsHandedBack_TheOutcomeReadAnswers402_AndARetryDeliversOnce()
    {
        // The reviewer's scenario: the first complete is held waiting-for-director (nothing transcribed), the
        // Director comes back, and the DRIVER's attempt is the one that discovers the account is out of
        // transcription credits - there is no client listening to that 402. The ruling: the Gateway hands the
        // recording back - the outcome read answers the SAME 402 body the complete path gives (never 404 "the
        // server has lost track"), the record is kept, the owner sees the out-of-credits state with Retry, and
        // his Retry complete re-enters through the FAILED re-entry and delivers once.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _pushed.UnregisterConnection(TenantId.Local, DirectorId, "conn-1");   // the Director's tunnel is down

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(0, _transcriber.Calls);
        Assert.Equal(0, Prompts());

        // The tunnel comes back - and the transcription provider answers out of credits.
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", ++_pushedSeq, new[] { Session(sid) }));
        _transcriber.OutOfCredits = true;
        driver.OnSessionsArrived(TenantId.Local, DirectorId);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.ReadRecord(uploadId) is not { State: DictationDeliveryState.Failed } && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // The driver stopped driving it and said why.
        Assert.Equal(0, driver.HeldCount);
        var handback = Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayHandedBack);
        Assert.Equal(DeliveryDecisions.HandbackOutOfCredits, handback.Facts!.Reason);
        // The record is KEPT: parked FAILED with the provider's code, its chunks retained for the retry.
        var parked = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Failed, parked.State);
        Assert.Equal("insufficient_credits", parked.Reason);
        Assert.True(File.Exists(ChunkPath(uploadId)));

        // The outcome read answers the same 402 the complete path gives - never 404.
        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(402, read.Status);
        Assert.Equal("NeedsCredits", read.Body.GetProperty("state").GetString());

        // The owner's Retry: a new complete, which re-enters through the FAILED re-entry, is owned again,
        // and delivers ONCE.
        _transcriber.OutOfCredits = false;
        var owned = Owned(sid);
        Assert.True(_store.OpenPending(uploadId, sid).Opened);   // the register half of the retry
        Assert.Equal(DictationOwnership.Taken, _store.TakeOwnership(uploadId, owned).Result);
        var outcome = await _delivery!.AttemptAsync(TenantId.Local, _store, uploadId, owned, driveTrigger: null);
        Assert.True((await RenderAsync(outcome.ToResult())).Body.GetProperty("submitted").GetBoolean());
        Assert.Equal(1, Prompts());
        Assert.Equal(2, _transcriber.Calls);   // the one that ran out of credits, and the retry's
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
    }

    [Fact]
    public async Task APermanentFailureOnADriverAttempt_IsHandedBack_TheOutcomeReadAnswers422_AndARetryReEnters()
    {
        // The same ruling for a clip that can never be transcribed: nothing before ownership proved it
        // (the first attempt held at the reachability gate), so the DRIVER's attempt is the first to hear
        // the permanent failure. The outcome read answers the same 422 the complete path gives, and the
        // owner's Retry re-enters PENDING and is owned again - no dead end.
        if (!OperatingSystem.IsWindows()) return;   // the failing-ffmpeg transcode path below is Windows-shaped
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedBigNonWavClipAsync(sid);
        _pushed.UnregisterConnection(TenantId.Local, DirectorId, "conn-1");

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(0, _transcriber.Calls);

        // The tunnel comes back; the clip is over-budget non-WAV, so the pipeline transcodes it - and the
        // ffmpeg the test points at cannot decode anything, which is the REAL permanent path: a clip
        // ffmpeg cannot decode is a permanent failure, never retried forever.
        var failingFfmpeg = Path.Combine(_root, "failing-ffmpeg.bat");
        File.WriteAllText(failingFfmpeg, "@exit /b 1\r\n");
        var prevFfmpeg = Environment.GetEnvironmentVariable("CCDIRECTOR_FFMPEG");
        Environment.SetEnvironmentVariable("CCDIRECTOR_FFMPEG", failingFfmpeg);
        try
        {
            _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
            Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", ++_pushedSeq, new[] { Session(sid) }));
            driver.OnSessionsArrived(TenantId.Local, DirectorId);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (_store.ReadRecord(uploadId) is not { State: DictationDeliveryState.Failed } && DateTime.UtcNow < deadline)
                await Task.Delay(50);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CCDIRECTOR_FFMPEG", prevFfmpeg);
        }

        Assert.Equal(0, driver.HeldCount);
        var handback = Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayHandedBack);
        Assert.Equal(DeliveryDecisions.HandbackPermanent, handback.Facts!.Reason);
        Assert.Equal(TranscriptionPermanentException.UnsupportedFormat, _store.ReadRecord(uploadId)!.Reason);
        Assert.True(File.Exists(ChunkPath(uploadId)));

        // The outcome read answers the same 422 the complete path gives - never 404.
        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(422, read.Status);
        Assert.True(read.Body.GetProperty("permanent").GetBoolean());
        Assert.Equal("unsupported-format", read.Body.GetProperty("reason").GetString());

        // The owner's Retry re-enters through the FAILED re-entry and is owned again (the same clip then
        // parks again, honestly - it can never be transcribed): no dead end either way.
        Assert.True(_store.OpenPending(uploadId, sid).Opened);
        var owned = Owned(sid);
        Assert.Equal(DictationOwnership.Taken, _store.TakeOwnership(uploadId, owned).Result);
        Assert.Equal(DictationOwnership.AlreadyOwned, _store.TakeOwnership(uploadId, owned).Result);
    }

    [Fact]
    public async Task AnOwnedRecordWhoseChunkVanished_IsHandedBack_TheOutcomeReadAnswers409_AndTheClientCanCompleteAgain()
    {
        // The third handback: a staged chunk is gone, so only the client - which holds the audio - can
        // finish the upload. The driver hands it back (ownership leaves with it, so a re-uploading
        // client's complete takes the delivery over again instead of being answered "still delivering"
        // by a record the Gateway can no longer finish), the outcome read answers the same 409 with the
        // missing chunks, and once the client has sent them the delivery is taken over and delivered.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _pushed.UnregisterConnection(TenantId.Local, DirectorId, "conn-1");
        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());

        // The staged chunk vanishes after ownership (a sweep, a disk fault). The Director's tunnel comes
        // BACK first - the handback is discovered by an attempt that gets PAST the reachability gate to the
        // assemble, where the missing chunk is found before any transcription.
        File.Delete(ChunkPath(uploadId));
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", ++_pushedSeq, new[] { Session(sid) }));
        driver.OnSessionsArrived(TenantId.Local, DirectorId);

        // The director-connected wake runs off the caller's thread, exactly as it does in the host: wait for
        // the handback to land, bounded.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_store.ReadRecord(uploadId) is not { State: DictationDeliveryState.Pending, Owned: null }
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // The driver handed it back and stopped driving it.
        Assert.Equal(0, driver.HeldCount);
        var handback = Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayHandedBack);
        Assert.Equal(DeliveryDecisions.HandbackIncomplete, handback.Facts!.Reason);
        Assert.Equal(1, handback.Facts!.TotalChunks);
        // Ownership left with the handback; the words, the session and the register-time Director stay.
        var record = _store.ReadRecord(uploadId)!;
        Assert.Equal(DictationDeliveryState.Pending, record.State);
        Assert.Null(record.Owned);
        Assert.Equal(sid, record.SessionId);

        // The outcome read answers the same 409 the complete path gives, naming the missing chunk.
        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(409, read.Status);
        Assert.Equal("incomplete", read.Body.GetProperty("status").GetString());
        Assert.Equal(new[] { 0 }, read.Body.GetProperty("missing").EnumerateArray().Select(m => m.GetInt32()).ToArray());

        // The client re-uploads the chunk and completes again: the delivery is taken over and delivered once.
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", ++_pushedSeq, new[] { Session(sid) }));
        await _store.StoreChunkAsync(uploadId, 0, Encoding.UTF8.GetBytes("fake-opus-bytes"), null);
        var owned = Owned(sid);
        Assert.Equal(DictationOwnership.Taken, _store.TakeOwnership(uploadId, owned).Result);
        var outcome = await _delivery!.AttemptAsync(TenantId.Local, _store, uploadId, owned, driveTrigger: null);
        Assert.True((await RenderAsync(outcome.ToResult())).Body.GetProperty("submitted").GetBoolean());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
    }

    // ===== the Director is remembered at REGISTER, before any locate (review round, finding 2) ===========

    [Fact]
    public async Task ASessionDeletedBeforeTheFirstComplete_IsProvedEndedAtOnce_ByTheDirectorTheRegisterWrote()
    {
        // The reviewer's probe. The owner starts a recording - the session is live enough for that - and
        // deletes it while the clip is uploading, so NO attempt ever locates the session and the
        // locate-time remember never runs. Until this fix the record named no Director, the end could not
        // be proved, and the owner was held for five minutes and then offered "Send anyway" to a session
        // that does not exist. The register now writes the session's Director on the record the moment the
        // Gateway first knows the upload, so the FIRST complete proves the end at once: session-exited, no
        // "Send anyway", nothing sent, and the words the record kept are what the answer carries.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        // The register, exactly as the route performs it: the session is live, so its Director is located
        // and written on the record before anything else happens.
        var (directorAtRegister, _) = await GatewayEndpoints.LocateSessionAsync(
            _registry, sid, _pushed, TimeSpan.FromSeconds(20), TenantId.Local, owners: null);
        Assert.NotNull(directorAtRegister);
        Assert.True(_store.RememberSessionDirector(uploadId, directorAtRegister!.DirectorId));
        Assert.Equal(DirectorId, _store.ReadRecord(uploadId)!.DirectorId);

        // The owner deletes the session: its Director is connected and fresh and no longer lists it.
        Assert.True(_pushed.ApplyRemove(TenantId.Local, DirectorId, "conn-1", ++_pushedSeq, sid));

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(200, first.Status);
        Assert.False(first.Body.GetProperty("submitted").GetBoolean());
        Assert.True(first.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("session-exited", first.Body.GetProperty("reason").GetString());
        Assert.False(first.Body.GetProperty("offerSendAnyway").GetBoolean());
        // The words the record kept are what travels - here none were kept (resolving at once precedes any
        // transcription), and the recording is still on the device.
        Assert.Equal(_store.SentWords(uploadId), first.Body.GetProperty("transcript").GetString());
        Assert.Equal(0, Prompts());
        Assert.Equal(0, _transcriber.Calls);
        // The driver notices on its next pass that the record is no longer an owned one, and drops it.
        _clock.Ahead = TimeSpan.FromSeconds(3);
        await driver.TickAsync();
        Assert.Equal(0, driver.HeldCount);
        Assert.Equal(DeliveryDecisions.SessionExited, Names(uploadId).Last());
    }

    [Fact]
    public async Task ARegisterWhoseSessionCannotBeLocated_WritesNoDirector_AndTheDeliveryHolds()
    {
        // The other half of the ruling: a register whose session cannot be located at that moment writes
        // nothing, and the record then holds exactly as before - a record that names no Director proves
        // nothing and must never guess an ending.
        var (driver, _) = NewDriver();
        var sid = Guid.NewGuid().ToString();   // no Director holds this session
        var uploadId = await StagedClipAsync(sid);
        var (directorAtRegister, _) = await GatewayEndpoints.LocateSessionAsync(
            _registry, sid, _pushed, TimeSpan.FromSeconds(20), TenantId.Local, owners: null);
        Assert.Null(directorAtRegister);   // the register writes nothing
        Assert.Null(_store.ReadRecord(uploadId)!.DirectorId);

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(1, driver.HeldCount);
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

    [Fact]
    public async Task TheDirectorsOwnAgeRefusalSentence_IsReadAsTooOld_WithTheWordsShownBack()
    {
        // The one-constant proof, end to end (contract section 9, F6): the real Director does not answer with the
        // bare word - its refusal is the word followed by the measured age and where it was measured, built by the
        // SAME PromptAgeLimit.TooOldReason the Director builds it with. The Gateway must read that sentence as an
        // age refusal, or a real Director refusing for age would be held as "retrying" while its words are known
        // not to be in the session.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _clock.Fixed = _sentAt.AddSeconds(299);
        _prompt = _ =>
        {
            _clock.Fixed = _sentAt.AddSeconds(310);   // the verb's wait moved the Gateway's clock past the limit
            return Task.FromResult<DirectorCommandResult?>(Refused(DeliveryState.NotDelivered,
                CcDirector.Core.Sessions.PromptAgeLimit.TooOldReason(TimeSpan.FromSeconds(310), "when the Director received it")));
        };

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);

        Assert.Equal(200, first.Status);
        Assert.False(first.Body.GetProperty("submitted").GetBoolean());
        Assert.True(first.Body.GetProperty("movedOn").GetBoolean());
        Assert.Equal(CcDirector.Gateway.Contracts.MaxDeliveryAge.TooOldReason, first.Body.GetProperty("reason").GetString());
        Assert.True(first.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, first.Body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
    }

    // ===== harness ==========================================================================================

    private (HeldDeliveryDriver Driver, DictationDelivery Delivery) NewDriver(SessionOwnerCache? owners = null)
    {
        var delivery = new DictationDelivery(_registry, owners, Transcription(), _marks, _pushed, SendAsync,
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

    // A staged clip over the pipeline's per-request byte budget and not a WAV - the one shape whose
    // transcription goes through the transcode, where a clip nothing can decode is a PERMANENT failure.
    private async Task<string> StagedBigNonWavClipAsync(string sid)
    {
        var uploadId = Guid.NewGuid().ToString();
        _store.Register(uploadId);
        await _store.StoreChunkAsync(uploadId, 0, new byte[5_000_000], null);
        _store.MarkPending(uploadId, sid);
        return uploadId;
    }

    private string ChunkPath(string uploadId)
        => Path.Combine(_root, "uploads", VoiceUploadStore.NormalizeUploadId(uploadId)!, "00000.part");

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
        /// <summary>Set by a test to make the provider answer out of credits (the hosted proxy's 402 shape).</summary>
        public bool OutOfCredits;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (OutOfCredits)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"insufficient_credits\"}}", Encoding.UTF8, "application/json"),
                });
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
