using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A "SEND ANYWAY" ON A SHOWN-BACK TYPED PROMPT CLAIMS ITS ORIGINAL DELIVERY ID, ATOMICALLY, AT THE GATEWAY (Voice
/// Delivery phase 5, the Delivery Lead's ruling on the review's finding 2). A typed "Send anyway" used to be a fresh
/// typed send with a fresh id, guarded only per browser tab - so two tabs sent the words twice, the exact doubling the
/// recording "Send anyway" is protected against. Now the press claims the ORIGINAL id through the same request field a
/// recording's does, ONE resolver finds the id in the typed prompt store as well as the upload store, the first verified
/// claim marks the record durably under its own lock BEFORE anything is sent, and a second claim - another tab, a double
/// press, a retry - is refused without sending and answered with the record's current state.
///
/// These drive the REAL claim pieces (<see cref="TypedPromptStore.ClaimSendAnyway"/>, <see cref="ClaimedSendCore"/>,
/// <see cref="TypedPromptDelivery.DriveOnceAsync"/>) over a real store on disk and a fake Director on the send-command
/// hook that counts every prompt it is sent - because exactly one press of these words may ever reach the Director.
/// </summary>
public sealed class TypedPromptClaimTests : IDisposable
{
    private const string DirectorId = "director-typed-claim";
    private const string Words = "run the release gate";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-typed-claims-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly MovableClock _clock = new();
    private readonly Queue<Func<DirectorCommand, DirectorCommandResult?>> _answers = new();
    private int _prompts;
    private readonly List<PromptRequest> _sentPrompts = new();
    private Func<DirectorCommand, DirectorCommandResult>? _promptAnswer;
    private int _asks;

    public TypedPromptClaimTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _registry.RegisterFromStream(DirectorId, "SOREN-NORTH", "soren", "1.0", pid: 1234,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);
    }

    public void Dispose()
    {
        _registry.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ===== the atomic claim: one press wins, the second is refused with the state ==============================

    [Fact]
    public async Task TwoConcurrentClaimsOfTheSameTypedId_ExactlyOneIsVerified_TheOtherIsRefused()
    {
        // Proves the atomicity the ruling demands: two claims of the same typed delivery id, as two tabs would send
        // them, at the same moment - exactly one is verified and marked, the other is refused without a mark, because
        // the mark happens under the record's own lock before anything is sent.
        var (store, id, sid) = ShownBack();

        var claims = await Task.WhenAll(
            Task.Run(() => store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime)),
            Task.Run(() => store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime)));

        var verified = claims.Count(c => c.Kind == TypedPromptClaimKind.Verified);
        var refused = claims.Count(c => c.Kind == TypedPromptClaimKind.Refused);
        Assert.Equal(1, verified);
        Assert.Equal(1, refused);
        var refusedClaim = claims.Single(c => c.Kind == TypedPromptClaimKind.Refused);
        Assert.Equal("already-delivering", refusedClaim.Reason);
        Assert.NotNull(refusedClaim.Record);
        Assert.Equal(TypedPromptState.Held, refusedClaim.Record!.State); // the state to answer with
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);
        var lines = store.ReadDecisions(id).Select(l => l.Decision).ToArray();
        Assert.Contains("send-anyway-claim-verified", lines);
        Assert.Contains("send-anyway-claim-dropped", lines);
    }

    [Fact]
    public async Task TwoPressesAsTheRouteSendsThem_ExactlyOnePromptReachesTheDirector()
    {
        // Proves what the atomic mark buys at the send: the first claim's press sends once under the ORIGINAL id; the
        // second claim is refused and answers the record's current state, so no second copy of the words exists.
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var route = Route();
        var req = PressRequest(id);

        var first = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);
        var attemptA = await ClaimedSendCore.AttemptAsync(route, sid, req, Log(store), id, "Working", _clock, gatewayDriven: false);
        var second = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);

        Assert.Equal(TypedPromptClaimKind.Verified, first.Kind);
        Assert.Equal(ClaimAttemptKind.Held, attemptA.Kind); // unanswered: the Gateway owns the claim from here
        Assert.Equal(TypedPromptClaimKind.Refused, second.Kind);
        Assert.Equal(1, _prompts);
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);
    }

    [Fact]
    public async Task ARefusedClaimIsAnsweredWithTheRecordsCurrentState()
    {
        // Proves the answer a refused claim gets is the record's own outcome body - the same one the outcome route
        // gives - for every state the record can be in when a second press lands.
        var (store, id, sid) = ShownBack();

        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var held = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(TypedPromptClaimKind.Refused, held.Kind);
        Assert.Equal(202, (await Execute(TypedPromptDelivery.OutcomeResult(held.Record!))).Status);

        store.ResolveDelivered(id);
        var delivered = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(TypedPromptClaimKind.Refused, delivered.Kind);
        var body = await Execute(TypedPromptDelivery.OutcomeResult(delivered.Record!));
        Assert.Equal(200, body.Status);
        Assert.True(body.Json.GetProperty("submitted").GetBoolean());

        // A claim of a record the Gateway ruled could-not-confirm: refused, answered with that outcome.
        var (held2, id2, sid2) = ShownBack();
        Assert.Equal(TypedPromptClaimKind.Verified,
            held2.ClaimSendAnyway(id2, sid2, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        held2.SettleClaimResolved(id2, TypedPromptState.Unconfirmed, "unconfirmed");
        var unconfirmed = held2.ClaimSendAnyway(id2, sid2, Words, Press(), _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(TypedPromptClaimKind.Refused, unconfirmed.Kind);
        Assert.Equal("unconfirmed", (await Execute(TypedPromptDelivery.OutcomeResult(unconfirmed.Record!))).Json.GetProperty("reason").GetString());
    }

    [Fact]
    public void AClaimOfAnAcknowledgedShownBackRecord_IsVerifiedAndRestoresThePressedWords()
    {
        // The owner may have dismissed the strip in one tab while another still shows it: the claim is still in window
        // and known not in, so it is verified - and the record's deleted text is restored to exactly what was pressed.
        var (store, id, sid) = ShownBack();
        Assert.Equal(TypedPromptStore.AcknowledgeResult.Acknowledged, store.Acknowledge(id));
        Assert.Null(store.Read(id).Record!.Text);

        var claim = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);

        Assert.Equal(TypedPromptClaimKind.Verified, claim.Kind);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Held, record.State);
        Assert.Equal(Words, record.Text);
    }

    // ===== the claim is the caller's own account, this session, known not in, in the window =====================

    [Fact]
    public void AClaimOfAnotherAccountsTypedId_IsDroppedAndWritesNothing()
    {
        var (store, id, sid) = ShownBack();
        var other = store.ForTenant(new TenantId("77777777-7777-7777-7777-777777777777"));

        var claim = other.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);

        Assert.Equal(TypedPromptClaimKind.Dropped, claim.Kind);
        Assert.Equal("no-typed-prompt-here", claim.Reason);
        Assert.Empty(other.ReadDecisions(id));
        Assert.Equal(TypedPromptState.NotDelivered, store.Read(id).Record!.State); // untouched
    }

    [Fact]
    public void AClaimOfAnotherSessionsTypedId_IsDroppedWithALine()
    {
        var (store, id, _) = ShownBack();

        var claim = store.ClaimSendAnyway(id, Guid.NewGuid().ToString(), Words, Press(), _clock.GetUtcNow().UtcDateTime);

        Assert.Equal(TypedPromptClaimKind.Dropped, claim.Kind);
        Assert.Equal("another-session", claim.Reason);
        var line = store.ReadDecisions(id).Single(l => l.Decision == "send-anyway-claim-dropped");
        Assert.Equal("another-session", line.Facts!.Reason);
        Assert.Equal(TypedPromptState.NotDelivered, store.Read(id).Record!.State);
    }

    [Fact]
    public void AClaimPastTheClaimWindow_IsDropped()
    {
        var (store, id, sid) = ShownBack();
        _clock.Ahead = TimeSpan.FromDays(31);

        var claim = store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime);

        Assert.Equal(TypedPromptClaimKind.Dropped, claim.Kind);
        Assert.Equal("outside-claim-window", claim.Reason);
    }

    // ===== the press carries the original id and the press time, and the driver presses the claim ================

    [Fact]
    public async Task TheClaimedTypedPromptCarriesTheOriginalIdAndThePressTime()
    {
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(
            new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered, ActivityState = "Working" }, Json));
        var before = _clock.GetUtcNow().UtcDateTime;

        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var attempt = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);

        Assert.Equal(ClaimAttemptKind.Delivered, attempt.Kind);
        var sent = Assert.Single(_sentPrompts);
        Assert.Equal(id, sent.DeliveryId); // the ORIGINAL id - the Director refuses a duplicate of it
        Assert.InRange(sent.SentAtUtc!.Value, before.AddSeconds(-1), _clock.GetUtcNow().UtcDateTime.AddSeconds(1)); // the press time
        // What the route does with a delivered claim (DeliverClaimedTypedPromptAsync): the record resolves and the
        // text is deleted.
        store.ResolveDelivered(id);
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
        Assert.Null(store.Read(id).Record!.Text);
    }

    [Fact]
    public async Task TheDriverAsksAHeldClaim_First_ThenPressesItAgainUnderTheOriginalIdWhenItIsKnownNotIn()
    {
        // Proves the held claim rides the SAME driver a recording's does (contract section 4): an earlier claimed send
        // may have reached the Director, so the driver asks first; the Director saying delivering holds; saying
        // not-delivered within the limit from the FIRST CLAIM makes the driver press again - under the original id,
        // with the FIRST CLAIM's time as its send time, never this attempt's clock.
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var firstClaim = store.ReadClaimSends(id).FirstClaimVerifiedAtUtc!.Value;
        var press = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);
        Assert.Equal(ClaimAttemptKind.Held, press.Kind);
        store.StayHeld(id, press.DirectorState!, countUnknown: false);
        Assert.Equal(1, _prompts); // the owner's press
        Assert.Equal(1, _asks);    // its own question, asked by the claim mechanism

        // The driver's first wake-up: ask (the press may have landed), hear delivering, hold.
        Answer(DeliveryState.Delivering);
        Assert.Equal(TypedDriveResult.Held, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.Equal(1, _prompts);
        Assert.Equal(2, _asks);

        // Known not in, within the limit: the driver PRESSES the claim again, under the original id and the first
        // claim's time - not a fresh id, not a fresh clock.
        _clock.Ahead = TimeSpan.FromSeconds(60);
        Answer(DeliveryState.NotDelivered);
        _promptAnswer = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(
            new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered, ActivityState = "Working" }, Json));
        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        Assert.Equal(2, _prompts); // the owner's press, then the Gateway's re-press
        Assert.Equal(3, _asks);
        var repress = _sentPrompts[1];
        Assert.Equal(id, repress.DeliveryId);
        Assert.Equal(firstClaim, repress.SentAtUtc!.Value, TimeSpan.FromMilliseconds(50));
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
    }

    [Fact]
    public async Task TheDriversPressPastTheLimitFromTheFirstClaim_IsShownBackNotDelivered_WithNoSecondSend()
    {
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var press = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);
        store.StayHeld(id, press.DirectorState!, countUnknown: false);

        // More than five minutes from the FIRST CLAIM, and the words are known not in: shown back with "Send anyway",
        // nothing sent - the same ruling a recording's claim gets.
        _clock.Ahead = TimeSpan.FromSeconds(301);
        Answer(DeliveryState.NotDelivered);
        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        Assert.Equal(1, _prompts); // the owner's press only; the Gateway's press never went out
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Equal(Words, record.Text);
        Assert.Contains("too-old", store.ReadDecisions(id).Select(l => l.Decision));
    }

    [Fact]
    public async Task AClaimWhoseDirectorIsNotConnected_IsHeldWithinTheLimit_AndUnconfirmedPastIt()
    {
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var press = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);
        store.StayHeld(id, press.DirectorState!, countUnknown: false);

        _clock.Ahead = TimeSpan.FromSeconds(240);
        Assert.Equal(TypedDriveResult.Held, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);

        _clock.Ahead = TimeSpan.FromSeconds(301);
        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Unconfirmed, record.State);
        Assert.Equal(Words, record.Text);
    }

    [Fact]
    public async Task ThePressesOwnSendKnownNotIn_SettlesTheRecordBackToShownBack()
    {
        // A claim settled on the press (its send was known not in) goes back to shown-back, so a later press starts
        // afresh - and the re-press ASKS FIRST, because its own earlier claimed send may have reached the Director.
        var (store, id, sid) = ShownBack();
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");
        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var press = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);

        Assert.Equal(ClaimAttemptKind.NotIn, press.Kind);
        // What the route does with a claim whose own send was known not in: the record goes back to shown-back.
        store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonSendAnywayNotIn, null);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Equal(Words, record.Text);
        Assert.True(store.ReadClaimSends(id).MayHaveReachedDirector);

        // The fresh claim is verified again, and its press asks the Director FIRST (the earlier send may have landed).
        _answers.Enqueue(cmd => StateAnswer(cmd, DeliveryState.NotDelivered));
        _promptAnswer = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(
            new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered, ActivityState = "Working" }, Json));
        Assert.Equal(TypedPromptClaimKind.Verified,
            store.ClaimSendAnyway(id, sid, Words, Press(), _clock.GetUtcNow().UtcDateTime).Kind);
        var again = await ClaimedSendCore.AttemptAsync(Route(), sid, PressRequest(id), Log(store), id, "Working", _clock, gatewayDriven: false);

        Assert.Equal(ClaimAttemptKind.Delivered, again.Kind);
        Assert.Equal(1, _asks); // the ask-first, before the second send
        Assert.Equal(2, _prompts);
        store.ResolveDelivered(id); // the route's settle of a delivered claim
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
    }

    // ===== helpers ============================================================================================

    /// <summary>A typed prompt held, then shown back not-delivered - the record a "Send anyway" claims.</summary>
    private (TypedPromptStore Store, string Id, string Sid) ShownBack()
    {
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var sid = Seat();
        var id = TypedPromptDelivery.MintDeliveryId();
        store.Hold(id, sid, Words, _clock.GetUtcNow().UtcDateTime, "delivering",
            new TypedPromptDecisionFacts { Ok = false, Reason = "prompt-unanswered" });
        store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonDirectorSaidNotDelivered, "not-delivered");
        return (store, id, sid);
    }

    private static TypedPromptUnsentRequest Press() => new()
    {
        AppendEnter = true,
        AgentDriven = false,
        Surface = "cockpit",
    };

    private static PromptRequest PressRequest(string id) => new()
    {
        Text = Words,
        AppendEnter = true,
        AgentDriven = false,
        Surface = "cockpit",
        DeliveryId = id,
        SentAtUtc = DateTime.UtcNow,
    };

    private static TypedPromptClaimLog Log(TypedPromptStore store) => new(store);

    private TypedPromptDelivery Driver() => new(_registry, owners: null, _pushed, SendAsync, TimeSpan.FromMinutes(10), _clock);

    private SessionVerbClient Route() => new(new DirectorDto { DirectorId = DirectorId, MachineName = "typed-claim-machine" }, SendAsync);

    private string Seat(string activityState = "Working", string status = "Running")
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        var known = _pushed.GetLastKnown(TenantId.Local, DirectorId).Sessions.ToList();
        known.Add(new SessionDto
        {
            SessionId = sid,
            ActivityState = activityState,
            Status = status,
        });
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_snapshotSeq, known.ToArray()));
        return sid;
    }

    private static string Sid() => Guid.NewGuid().ToString();

    private long _snapshotSeq;

    private Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand cmd, CancellationToken ct)
    {
        if (cmd.Verb == "prompt")
        {
            Interlocked.Increment(ref _prompts);
            lock (_sentPrompts) _sentPrompts.Add(JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, Json)!);
            var played = _promptAnswer?.Invoke(cmd) ?? DirectorCommandResult.Fail(DirectorCommandStatus.Error, "no prompt expected");
            played.CommandId = cmd.CommandId;
            return Task.FromResult<DirectorCommandResult?>(played);
        }
        Assert.Equal(DeliveryStateRequest.Verb, cmd.Verb);
        Interlocked.Increment(ref _asks);
        Func<DirectorCommand, DirectorCommandResult?> answer;
        lock (_answers)
            answer = _answers.Count > 0 ? _answers.Dequeue() : throw new InvalidOperationException("the test gave the Director no answer");
        var result = answer(cmd);
        if (result is not null) result.CommandId = cmd.CommandId;
        return Task.FromResult(result);
    }

    private void Answer(DeliveryState state)
    {
        lock (_answers) _answers.Enqueue(cmd => StateAnswer(cmd, state));
    }

    private static DirectorCommandResult StateAnswer(DirectorCommand cmd, DeliveryState state)
    {
        var request = string.IsNullOrEmpty(cmd.PayloadJson) ? null : JsonSerializer.Deserialize<DeliveryStateRequest>(cmd.PayloadJson, Json);
        var result = DirectorCommandResult.Success(JsonSerializer.Serialize(
            new DeliveryStateResponse { DeliveryId = request?.DeliveryId ?? "", State = state }, Json));
        result.CommandId = cmd.CommandId;
        return result;
    }

    private static async Task<(int Status, JsonElement Json)> Execute(Microsoft.AspNetCore.Http.IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, JsonDocument.Parse(stream.ToArray()).RootElement.Clone());
    }

    private sealed class MovableClock : TimeProvider
    {
        public TimeSpan Ahead;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Ahead;
    }
}
