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
using CcDirector.Gateway.Prompts;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ONCE THE GATEWAY HOLDS THE WORDS, THE GATEWAY DRIVES THE DELIVERY (Voice Delivery mission, phase 5).
///
/// Phase 4 found a recording held as "still delivering" that nothing retried for 7 minutes 44 seconds, because the
/// only retry lived in a frozen background browser tab; it was then shown back as too old. These tests drive the REAL
/// <see cref="HeldDeliveryDriver"/> over the REAL <see cref="DictationDelivery"/> - the same core and single-flight the
/// complete route uses - with a real upload store on disk, the real transcription service over a counting stub, and a
/// Director played by a delegate. The first attempt is made exactly as the route makes it (take the delivery over,
/// hand it to the driver, attempt it); every later attempt is the driver's own, with no client call. The clock is the
/// real one moved ahead by the test, so the five-minute boundaries need no five-minute wait.
/// </summary>
public sealed class HeldDeliveryDriverTests : IDisposable
{
    private const string DirectorId = "director-held";
    private const string SpokenWords = "deploy the preview and tell me the link";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-held-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath;
    private readonly VoiceUploadStore _store;
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly TranscribingSessions _marks = new();
    private readonly CountingTranscriptHandler _transcriber = new(SpokenWords);
    private readonly AheadClock _clock = new();
    private readonly List<DirectorCommand> _commands = new();
    private readonly DateTime _sentAt = DateTime.UtcNow;
    /// <summary>The delivery core of the newest driver: the one the route-shaped first attempt goes through.</summary>
    private DictationDelivery? _delivery;

    /// <summary>How the Director answers the prompt verb. Null means the command never left this Gateway.</summary>
    private Func<DirectorCommand, Task<DirectorCommandResult?>> _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
    /// <summary>How the Director answers "what became of delivery id X?".</summary>
    private Func<DirectorCommand, DirectorCommandResult?> _deliveryState = _ => StateIs(DeliveryState.Unknown);

    public HeldDeliveryDriverTests()
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

    // ===== the five-minute rule, applied by the Gateway's own re-send ======================================

    [Theory]
    [InlineData(299, true)]
    [InlineData(301, false)]
    public async Task AGatewayReSend_AfterUnknown_IsSentUnderFiveMinutes_AndNeverPastThem(int secondsSinceSend, bool sent)
    {
        // Proves the Gateway's own re-send keeps the owner's limit. The first attempt's prompt runs out of time and the
        // Director says it never saw the id - held (202 "unknown"). The driver wakes 4:59 after Send: the words are known
        // not to be in, so they are typed, once, and the recording is delivered. It wakes 5:01 after Send: NOTHING is
        // typed, the recording is shown back as too old with the words and "Send anyway", and the outcome read says so.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal("unknown", first.Body.GetProperty("directorState").GetString());

        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
        _clock.Fixed = _sentAt.AddSeconds(secondsSinceSend);
        await driver.TickAsync();

        var (status, body) = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, status);
        Assert.Equal(sent ? 2 : 1, Prompts());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        if (sent)
        {
            Assert.True(body.GetProperty("submitted").GetBoolean());
            Assert.False(body.GetProperty("movedOn").GetBoolean());
            return;
        }
        Assert.False(body.GetProperty("submitted").GetBoolean());
        Assert.True(body.GetProperty("movedOn").GetBoolean());
        Assert.Equal("too-old", body.GetProperty("reason").GetString());
        Assert.True(body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(DeliveryDecisions.TooOld, Names(uploadId).Last());
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(301, true)]
    public async Task AGatewayAttempt_WithNoAnswer_IsUnconfirmedOnlyPastFiveMinutes_AndNothingIsTyped(int secondsSinceSend, bool unconfirmed)
    {
        // Proves the no-answer case under the driver: the prompt ran out of time and the question got no answer, so the
        // words may be in. The driver's attempt at 5:00 still holds it (202 "no-answer"); at 5:01 it rules "could not
        // confirm it arrived" - the words handed back, no "Send anyway" - and in neither case is anything typed again.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        _clock.Fixed = _sentAt.AddSeconds(secondsSinceSend);
        await driver.TickAsync();

        var (status, body) = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(1, Prompts());
        if (!unconfirmed)
        {
            Assert.Equal(202, status);
            Assert.Equal("no-answer", body.GetProperty("directorState").GetString());
            Assert.Equal(DeliveryDecisions.StillDelivering, Names(uploadId).Last());
            return;
        }
        Assert.Equal(200, status);
        Assert.Equal("unconfirmed", body.GetProperty("reason").GetString());
        Assert.False(body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
    }

    [Fact]
    public async Task ARecordingWhoseDirectorNeverReturns_IsHeldWaiting_ThenShownBackPastFiveMinutes_WithItsWords()
    {
        // Proves the time rules apply when no Director ever comes back. The complete arrives while the session's Director
        // is not connected: it is still TAKEN OVER (every chunk is staged) and held "waiting-for-director", with nothing
        // transcribed. Past five minutes the driver transcribes it once, so the words can be shown back, and resolves it
        // too old - never typed anywhere.
        var (driver, _) = NewDriver();
        var sid = Guid.NewGuid().ToString();   // no Director holds this session
        var uploadId = await StagedClipAsync(sid);

        var first = await OwnAndAttemptAsync(driver, uploadId, sid);
        Assert.Equal(202, first.Status);
        Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, first.Body.GetProperty("directorState").GetString());
        Assert.Equal(0, _transcriber.Calls);

        _clock.Fixed = _sentAt.AddSeconds(301);
        await driver.TickAsync();

        var (status, body) = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, status);
        Assert.Equal("too-old", body.GetProperty("reason").GetString());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Equal(1, _transcriber.Calls);
        Assert.Equal(0, Prompts());
    }

    // ===== a Gateway restart resumes a held delivery ======================================================

    [Fact]
    public async Task AFreshDriverOverTheSameFolder_ResumesAHeldDelivery_AndSendsItExactlyOnce()
    {
        // Proves a restart forgets nothing: a delivery owned and held on disk (the first attempt ran out of time and the
        // question got no answer) is picked up by a FRESH delivery core and driver over the same folder - nothing carried
        // in memory - and, the Director now saying it never saw the id, sent exactly once and resolved delivered. The
        // decision record names what woke it: gateway-started.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);
        driver.Dispose();

        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
        _deliveryState = _ => StateIs(DeliveryState.Unknown);
        var (restarted, _) = NewDriver();
        await restarted.StartAsync();

        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
        Assert.True(_store.ReadRecord(uploadId)!.Submitted);
        Assert.Equal(2, Prompts());   // the first that ran out of time, and exactly one re-send
        var drive = Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayDrive).Facts!;
        Assert.Equal(DeliveryDecisions.DriveGatewayStarted, drive.Trigger);
        Assert.Equal(1, drive.Attempt);
        Assert.Equal(0, restarted.HeldCount);
    }

    // ===== a Director that already delivered it refuses the re-send ========================================

    [Fact]
    public async Task AReSend_ToADirectorThatAlreadyDeliveredIt_AsksFirst_AndTypesNothing()
    {
        // Proves the first send landed late: the Gateway had stopped waiting and the question got no answer, so it held.
        // The driver's attempt asks first, hears delivered, and resolves delivered with the kept words - ONE prompt ever
        // reached the Director, and nothing was transcribed again.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        _deliveryState = _ => StateIs(DeliveryState.Delivered);
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        var (status, body) = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(200, status);
        Assert.True(body.GetProperty("submitted").GetBoolean());
        Assert.Equal(SpokenWords, body.GetProperty("transcript").GetString());
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
    }

    [Fact]
    public async Task AReSend_TheDirectorRefusesAsADuplicate_IsResolvedDelivered()
    {
        // Proves the other way the Director stops a second copy: the question raced the late first send and said
        // "unknown", so the driver sent again - and the Director refused that copy as already delivered, typing nothing.
        // The recording is resolved delivered, and the Director's answer line says it was a refused duplicate.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        Assert.Equal(202, (await OwnAndAttemptAsync(driver, uploadId, sid)).Status);

        _deliveryState = _ => StateIs(DeliveryState.Unknown);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Refused(DeliveryState.Delivered));
        _clock.Ahead = TimeSpan.FromSeconds(5);
        await driver.TickAsync();

        Assert.Equal(DictationDeliveryState.Delivered, _store.ReadRecord(uploadId)!.State);
        Assert.True(_store.ReadRecord(uploadId)!.Submitted);
        Assert.True(Lines(uploadId).Last(l => l.Decision == DeliveryDecisions.DirectorAnswer).Facts!.RefusedDuplicate);
    }

    // ===== one attempt at a time =============================================================================

    [Fact]
    public async Task TheDriverAndAClientAttempt_NeverRunTwoAttemptsOfOneUploadAtOnce()
    {
        // Proves the single-flight is shared: while the client's attempt is inside the prompt verb, the driver wakes - the
        // tunnel came back, and the tick is due - and starts NOTHING: no second transcription, no second prompt, no drive
        // line. When the attempt finishes, exactly one prompt was sent.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        var release = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompt = _ =>
        {
            inPrompt.TrySetResult();
            return release.Task;
        };

        var client = OwnAndAttemptAsync(driver, uploadId, sid);
        await inPrompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Ahead = TimeSpan.FromSeconds(5);
        // Not awaited before the release: a tick that wrongly started its own attempt would sit in the held prompt, and
        // the test must fail on the counts below rather than hang.
        var tick = driver.TickAsync();
        driver.OnSessionsArrived(TenantId.Local, DirectorId);
        await Task.Delay(300);
        release.SetResult(Accepted());
        var answer = await client.WaitAsync(TimeSpan.FromSeconds(10));
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(200, answer.Status);
        Assert.Equal(1, Prompts());
        Assert.Equal(1, _transcriber.Calls);
        Assert.DoesNotContain(DeliveryDecisions.GatewayDrive, Names(uploadId));
    }

    // ===== the outcome read ==================================================================================

    [Fact]
    public async Task TheOutcomeRead_AnswersEachRecord_AndNeverWritesSendsOrAsks()
    {
        // Proves GET /dictation/{id}/outcome's table on its real body builder: a held delivery is 202 with its last
        // directorState; a delivered one is 200 with the complete's own body; an upload the Gateway does not own yet, an
        // unknown id, and another account's id are 404 - and reading changes nothing: no command, no decision line.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var held = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => StateIs(DeliveryState.Delivering);
        await OwnAndAttemptAsync(driver, held, sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Accepted());
        var delivered = await StagedClipAsync(sid);
        await OwnAndAttemptAsync(driver, delivered, sid);
        var notOwned = await StagedClipAsync(sid);
        var commandsBefore = _commands.Count;
        var linesBefore = Names(held).Length + Names(delivered).Length + Names(notOwned).Length;

        var heldRead = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, held));
        var deliveredRead = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, delivered));
        var notOwnedRead = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, notOwned));
        var unknownRead = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, Guid.NewGuid().ToString()));
        var otherAccount = _store.ForTenant(new TenantId("33333333-3333-3333-3333-333333333333"));
        var foreignRead = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(otherAccount, held));

        Assert.Equal(202, heldRead.Status);
        Assert.True(heldRead.Body.GetProperty("delivering").GetBoolean());
        Assert.Equal("delivering", heldRead.Body.GetProperty("directorState").GetString());
        Assert.Equal(200, deliveredRead.Status);
        Assert.True(deliveredRead.Body.GetProperty("submitted").GetBoolean());
        Assert.Equal(SpokenWords, deliveredRead.Body.GetProperty("transcript").GetString());
        Assert.Equal(404, notOwnedRead.Status);
        Assert.Equal(404, unknownRead.Status);
        Assert.Equal(404, foreignRead.Status);
        Assert.Equal(commandsBefore, _commands.Count);
        Assert.Equal(linesBefore, Names(held).Length + Names(delivered).Length + Names(notOwned).Length);
    }

    [Theory]
    [InlineData(null, 202)]
    [InlineData(SendAnywayOutcomes.Delivered, 200)]
    [InlineData(SendAnywayOutcomes.Unconfirmed, 200)]
    [InlineData(SendAnywayOutcomes.TooOld, 200)]
    [InlineData(SendAnywayOutcomes.SessionExited, 200)]
    public async Task TheOutcomeRead_OfASendAnyway_AnswersInTheDictationsOwnShapes(string? outcome, int status)
    {
        // Proves the "Send anyway" rows of the outcome table, on a recording already acknowledged (as every real "Send
        // anyway" names one): held is 202; delivered is 200 submitted with the words; unconfirmed is shown back with no
        // "Send anyway"; too old is shown back with it; session-exited (contract section 9, F4) is shown back with the
        // words and NO "Send anyway" - there is no session left to send to.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _store.MarkDelivered(uploadId, submitted: false, movedOn: true, SpokenWords, reason: DeliveryDecisions.TooOld);
        _store.Acknowledge(uploadId);
        Assert.True(_store.TakeSendAnywayOwnership(uploadId, new SendAnywayDelivery(DateTime.UtcNow, sid, "typed words", "cockpit", null)));
        DeliverySendAndAsk.RecordHeld(_store, uploadId, sid, "no-answer");
        if (outcome is not null) Assert.True(_store.ResolveSendAnyway(uploadId, outcome, DateTime.UtcNow));

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));

        Assert.Equal(status, read.Status);
        if (outcome is null)
        {
            Assert.Equal("no-answer", read.Body.GetProperty("directorState").GetString());
            return;
        }
        Assert.Equal("typed words", read.Body.GetProperty("transcript").GetString());
        Assert.Equal(outcome == SendAnywayOutcomes.Delivered, read.Body.GetProperty("submitted").GetBoolean());
        if (outcome == SendAnywayOutcomes.Delivered) return;
        Assert.Equal(outcome, read.Body.GetProperty("reason").GetString());
        Assert.Equal(outcome == SendAnywayOutcomes.TooOld, read.Body.GetProperty("offerSendAnyway").GetBoolean());

        // The words leave with the acknowledgement, as every kept transcript does.
        _store.Acknowledge(uploadId);
        Assert.Null(_store.ReadSendAnyway(uploadId));
    }

    // ===== a "Send anyway" whose Director never returns ======================================================

    [Theory]
    [InlineData(299, false)]
    [InlineData(301, true)]
    public async Task AHeldSendAnyway_WhoseDirectorNeverReturns_IsUnconfirmedOnlyPastFiveMinutesFromTheFirstClaim(int seconds, bool unconfirmed)
    {
        // Proves the Gateway's own press of a held "Send anyway" keeps phase 2's limit when its Director is not connected:
        // held "waiting-for-director" 4:59 after the first verified claim, "could not confirm it arrived" at 5:01 - an
        // earlier press may be in, so no "Send anyway" - and nothing is ever sent.
        var (driver, _) = NewDriver();
        var sid = Guid.NewGuid().ToString();
        var uploadId = await StagedClipAsync(sid);
        _store.MarkDelivered(uploadId, submitted: false, movedOn: true, SpokenWords, reason: DeliveryDecisions.TooOld);
        Assert.True(_store.ResolveDeliveryClaim(uploadId, sid, SpokenWords.Length, DateTime.UtcNow).Verified);
        Assert.True(_store.TakeSendAnywayOwnership(uploadId, new SendAnywayDelivery(DateTime.UtcNow, sid, SpokenWords, "cockpit", null)));
        driver.Track(TenantId.Local, uploadId, HeldDeliveryKind.SendAnyway);
        var firstClaim = Lines(uploadId).First(l => l.Decision == DeliveryDecisions.ClaimVerified).AtUtc;

        _clock.Fixed = firstClaim.AddSeconds(seconds);
        await driver.TickAsync();

        var read = await RenderAsync(GatewayDictationEndpoint.OutcomeOf(_store, uploadId));
        Assert.Equal(0, Prompts());
        Assert.Equal(DeliveryDecisions.DriveTick, Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayDrive).Facts!.Trigger);
        if (!unconfirmed)
        {
            Assert.Equal(202, read.Status);
            Assert.Equal(DeliverySendAndAsk.WaitingForDirectorState, read.Body.GetProperty("directorState").GetString());
            return;
        }
        Assert.Equal(200, read.Status);
        Assert.Equal("unconfirmed", read.Body.GetProperty("reason").GetString());
        Assert.False(read.Body.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal(DeliverySendAndAsk.DirectorNotConnected,
            Lines(uploadId).Last(l => l.Decision == DeliveryDecisions.Unconfirmed).Facts!.DirectorNoAnswer);
    }

    // ===== what the driver cannot read, it does not guess at ==================================================

    [Fact]
    public async Task ADeliveryTheDriverCannotRead_IsWrittenUp_AndNotDriven()
    {
        // Proves "no fallback": an owned delivery whose record is corrupted on disk is not guessed at. At start the driver
        // writes gateway-drive-refused, naming what it could not read, and sends nothing.
        var (driver, _) = NewDriver();
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        _prompt = _ => Task.FromResult<DirectorCommandResult?>(Timeout());
        _deliveryState = _ => NoAnswer();
        await OwnAndAttemptAsync(driver, uploadId, sid);
        driver.Dispose();
        File.WriteAllText(Path.Combine(UploadDir(uploadId), "record.json"), "{ this is not a record");
        var promptsBefore = Prompts();

        var (restarted, _) = NewDriver();
        await restarted.StartAsync();

        Assert.Equal(promptsBefore, Prompts());
        var refused = Lines(uploadId).Single(l => l.Decision == DeliveryDecisions.GatewayDriveRefused).Facts!;
        Assert.Contains("Malformed", refused.Error);
        Assert.Equal(0, restarted.HeldCount);
    }

    // ===== the store keeps what the Gateway owns =============================================================

    [Fact]
    public async Task OwnershipIsWrittenOnce_SurvivesAReRegister_AndLeavesWithTheTombstone()
    {
        // Proves the durable record: taking over writes the request fields and ONE gateway-owns-delivery line; a second
        // take-over is AlreadyOwned and writes nothing; an old client's re-register keeps the ownership; and the delivered
        // tombstone drops it, so the typed words it held leave with the delivery.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);
        var owned = Owned(sid, before: "typed before");

        Assert.Equal(DictationOwnership.Taken, _store.TakeOwnership(uploadId, owned).Result);
        Assert.Equal(DictationOwnership.AlreadyOwned, _store.TakeOwnership(uploadId, owned).Result);
        Assert.True(_store.OpenPending(uploadId, sid).Opened);

        Assert.Equal("typed before", _store.ReadRecord(uploadId)!.Owned!.Before);
        Assert.Single(Names(uploadId), n => n == DeliveryDecisions.GatewayOwnsDelivery);
        Assert.Single(_store.HeldDeliveries(), h => h.UploadId == VoiceUploadStore.NormalizeUploadId(uploadId));
        _store.MarkDelivered(uploadId, submitted: true, movedOn: false, SpokenWords);
        Assert.Null(_store.ReadRecord(uploadId)!.Owned);
        Assert.Empty(_store.HeldDeliveries());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingChunks_AreCountedWithoutReadingAudio_AndAnUnknownUploadIsNull()
    {
        // Proves the check the route runs before the session lookup: a staged chunk is present, a chunk never sent is
        // missing, and an upload that is not staged at all answers null.
        var sid = Seat();
        var uploadId = await StagedClipAsync(sid);

        Assert.Empty(_store.MissingChunks(uploadId, 1)!);
        Assert.Equal(new[] { 1, 2 }, _store.MissingChunks(uploadId, 3));
        Assert.Null(_store.MissingChunks(Guid.NewGuid().ToString(), 1));
    }

    // ===== the tunnel coming back ============================================================================

    [Fact]
    public void TheReconnectSignal_IsTheFirstSnapshotOfANewConnection_AndOnlyThat()
    {
        // Proves the wake the driver listens for: the first full snapshot a NEW connection of a Director delivers raises
        // it - including a reconnect of a Director the store already held, which the registry's "Director added" never
        // reports. A repeated snapshot on the same connection (the ten-second reseed) and the Hello alone do not.
        var raised = new List<(TenantId, string)>();
        var store = new Streaming.PushedSessionStore();
        store.SessionsArrivedOnNewConnection += (t, d) => raised.Add((t, d));
        var session = new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = DirectorId };

        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.Empty(raised);
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 1, new[] { session }));
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 2, new[] { session }));
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-1");   // a repeated Hello on the same connection
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", 3, new[] { session }));
        Assert.Single(raised);

        store.UnregisterConnection(TenantId.Local, DirectorId, "conn-1");
        store.RegisterConnection(TenantId.Local, DirectorId, "conn-2");
        Assert.True(store.ApplyDelta(TenantId.Local, DirectorId, "conn-2", 1, session));   // a delta first: not the roster
        Assert.Single(raised);
        Assert.True(store.ApplySnapshot(TenantId.Local, DirectorId, "conn-2", 2, new[] { session }));
        Assert.Equal(2, raised.Count);
        Assert.Equal((TenantId.Local, DirectorId), raised[1]);
    }

    [Fact]
    public void TheWaitBetweenAttempts_RisesFromTwoSecondsToFifteen()
    {
        Assert.Equal(new[] { 2.0, 4.0, 8.0, 15.0, 15.0 },
            Enumerable.Range(1, 5).Select(n => HeldDeliveryDriver.WaitAfter(n).TotalSeconds));
    }

    // ===== typed prompts, driven by the same driver (contract section 7, T4) =================================

    [Fact]
    public async Task AHeldTypedPrompt_OnDisk_IsPickedUpWhenTheGatewayStarts_AndAskedAbout_NeverResent()
    {
        // Proves the typed half rides the same driver: a typed prompt held by an earlier Gateway process is found on disk
        // with nothing in memory when the driver starts, asked about once under the gateway-started trigger, and resolved
        // delivered with its text deleted - and no prompt is sent again.
        var sid = Seat();
        var typed = TypedStore();
        var id = TypedPromptDelivery.MintDeliveryId();
        typed.Hold(id, sid, "typed words", DateTime.UtcNow, DeliverySendAndAsk.NoAnswerState, new TypedPromptDecisionFacts());
        _deliveryState = _ => StateIs(DeliveryState.Delivered);
        var (driver, _) = NewDriver();
        driver.AttachTypedPrompts(typed, () => TypedDriver());

        using (driver) await driver.StartAsync();

        var record = typed.Read(id).Record!;
        Assert.Equal(TypedPromptState.Delivered, record.State);
        Assert.Null(record.Text);
        Assert.Equal(0, Prompts());
        var drive = Assert.Single(typed.ReadDecisions(id), l => l.Decision == DeliveryDecisions.GatewayDrive);
        Assert.Equal(DeliveryDecisions.DriveGatewayStarted, drive.Facts!.Trigger);
        Assert.Equal(0, driver.HeldCount);
    }

    [Fact]
    public async Task ATrackedTypedPrompt_IsAskedOnTheTick_ThenFinishedWhenItsDirectorComesBack()
    {
        // Proves the other two wake-ups reach a typed prompt: the tick, once its wait is due, asks (still delivering, so it
        // stays driven), and the Director's tunnel coming back asks again and finishes it - with no client call at all.
        var sid = Seat();
        var typed = TypedStore();
        var id = TypedPromptDelivery.MintDeliveryId();
        typed.Hold(id, sid, "typed words", DateTime.UtcNow, DeliverySendAndAsk.NoAnswerState, new TypedPromptDecisionFacts());
        var (driver, _) = NewDriver();
        driver.AttachTypedPrompts(typed, () => TypedDriver());
        driver.Track(TenantId.Local, id, HeldDeliveryKind.TypedPrompt);

        _deliveryState = _ => StateIs(DeliveryState.Delivering);
        await driver.TickAsync();
        Assert.DoesNotContain(typed.ReadDecisions(id), l => l.Decision == DeliveryDecisions.GatewayDrive);
        _clock.Ahead = HeldDeliveryDriver.ShortestWait + TimeSpan.FromSeconds(1);
        await driver.TickAsync();
        Assert.Equal(TypedPromptState.Held, typed.Read(id).Record!.State);
        Assert.Equal(1, driver.HeldCount);

        _deliveryState = _ => StateIs(DeliveryState.Delivered);
        driver.OnSessionsArrived(TenantId.Local, DirectorId);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (typed.Read(id).Record!.State == TypedPromptState.Held && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(TypedPromptState.Delivered, typed.Read(id).Record!.State);
        Assert.Equal(new[] { DeliveryDecisions.DriveTick, DeliveryDecisions.DriveDirectorConnected },
            typed.ReadDecisions(id).Where(l => l.Decision == DeliveryDecisions.GatewayDrive).Select(l => l.Facts!.Trigger));
        Assert.Equal(0, Prompts());
        Assert.Equal(0, driver.HeldCount);
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

    private TypedPromptStore TypedStore() => new(Path.Combine(_root, "typed"), TenantId.Local, _clock);

    private TypedPromptDelivery TypedDriver() => new(_registry, owners: null, _pushed, SendAsync, TimeSpan.FromSeconds(20), _clock);

    private DictationOwnedDelivery Owned(string sid, string? before = null)
        => new(DateTime.UtcNow, sid, 1, "audio/webm", "webm", before, null, null, _sentAt, "cockpit", "device-key");

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

    private string Seat()
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", DateTime.UtcNow.Ticks, new[]
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

    private IReadOnlyList<DeliveryDecisionLine> Lines(string uploadId) => _store.ReadDecisions(uploadId).Lines;
    private string[] Names(string uploadId) => Lines(uploadId).Select(l => l.Decision).ToArray();
    private string UploadDir(string uploadId) => Path.Combine(_root, "uploads", VoiceUploadStore.NormalizeUploadId(uploadId)!);

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

    private static DirectorCommandResult Timeout()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");

    private static DirectorCommandResult NoAnswer()
        => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer the question within 30 seconds");

    private static DirectorCommandResult StateIs(DeliveryState state)
        => DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new DeliveryStateResponse { State = state }));

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
