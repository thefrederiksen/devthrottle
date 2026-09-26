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
/// A HELD TYPED PROMPT IS RESOLVED BY THE GATEWAY, BY ASKING ONLY (Voice Delivery mission, phase 5, contract section 7,
/// T2 to T4). Phase 4 case 2f: a typed prompt was answered "delivering", the Director in the end refused it and typed
/// nothing, and with no delivery id nobody could ask. These drive the REAL <see cref="TypedPromptDelivery.DriveOnceAsync"/>
/// over a real <see cref="TypedPromptStore"/> on disk, a real registry and pushed-session store, and a fake Director on the
/// send-command hook that answers the <c>delivery-state</c> question as each case needs - and counts every prompt it is
/// sent, because the driver must never send typed text again.
/// </summary>
public sealed class TypedPromptDeliveryTests : IDisposable
{
    private const string DirectorId = "director-typed";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-typed-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly Streaming.PushedSessionStore _pushed = new();
    private readonly MovableClock _clock = new();
    private readonly Queue<Func<DirectorCommand, DirectorCommandResult?>> _answers = new();
    private int _prompts;
    private readonly List<PromptRequest> _sentPrompts = new();
    private Func<DirectorCommand, DirectorCommandResult>? _promptAnswer;
    private int _asks;
    private bool _directorConnected = true;

    public TypedPromptDeliveryTests()
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

    // ===== the route's reading of the prompt verb's answer (T2) ================================================

    [Fact]
    public void ReadSend_Unanswered_IsHeldWithNoAnswer_AndNeverAsksInline()
    {
        // Proves an unanswered typed prompt is held at once: the reading is Held "no-answer" - the route does not ask.
        var reading = TypedPromptDelivery.ReadSend(new SessionVerbClient.PromptSendOutcome(
            SessionVerbClient.PromptSendKind.Unanswered, null, "the Director did not answer within 30 seconds"));

        Assert.Equal(TypedSendKind.Held, reading.Kind);
        Assert.Equal("no-answer", reading.DirectorState);
        Assert.Equal("prompt-unanswered", reading.DirectorAnswer.Reason);
    }

    [Fact]
    public void ReadSend_AcceptedButDelivering_IsHeld()
    {
        // Proves the phase 4 case 2f answer - accepted, "delivering" - is no longer read as delivered: it is held.
        var reading = TypedPromptDelivery.ReadSend(Accepted(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivering }));

        Assert.Equal(TypedSendKind.Held, reading.Kind);
        Assert.Equal("delivering", reading.DirectorState);
    }

    [Fact]
    public void ReadSend_AcceptedAndDelivered_IsDelivered()
    {
        var reading = TypedPromptDelivery.ReadSend(Accepted(new PromptResponse { Accepted = true, DeliveryState = DeliveryState.Delivered }));

        Assert.Equal(TypedSendKind.Delivered, reading.Kind);
    }

    [Fact]
    public void ReadSend_AnsweredButNotTyped_KeepsTodaysAnswer()
    {
        // Proves a Director that answered and typed nothing for a reason of its own (busy) keeps today's 200 answer.
        var reading = TypedPromptDelivery.ReadSend(Accepted(new PromptResponse
        {
            Accepted = false, RefusedBusy = true, DeliveryState = DeliveryState.NotDelivered, Error = "busy",
        }));

        Assert.Equal(TypedSendKind.AnsweredNotTyped, reading.Kind);
        Assert.NotNull(reading.Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadSend_KnownNotIn_IsNotIn(bool refusedByTheDirector)
    {
        var kind = refusedByTheDirector ? SessionVerbClient.PromptSendKind.DirectorRefused : SessionVerbClient.PromptSendKind.NeverLeftTheGateway;
        var reading = TypedPromptDelivery.ReadSend(new SessionVerbClient.PromptSendOutcome(kind, null, "no"));

        Assert.Equal(TypedSendKind.NotIn, reading.Kind);
    }

    // ===== the driver asks, and only asks (T4) ================================================================

    [Fact]
    public async Task Drive_DirectorSaysDelivered_ResolvesDelivered_DeletesTheText_SendsNothing()
    {
        var (store, id, sid) = HeldPrompt("no-answer");
        Answer(DeliveryState.Delivered);

        var result = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.Finished, result);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Delivered, record.State);
        Assert.Null(record.Text);
        Assert.Equal(0, _prompts);
        Assert.Equal(1, _asks);
        Assert.Equal(new[] { "gateway-drive", "asked-director", "delivery-state-answer", "delivered" }, DecisionsAfterHold(store, id));
        var drive = store.ReadDecisions(id).Single(l => l.Decision == "gateway-drive").Facts!;
        Assert.Equal("tick", drive.Trigger);
        Assert.Equal(1, drive.Attempt);
        Assert.Equal(sid, drive.SessionId);
    }

    [Fact]
    public async Task Drive_DirectorSaysDelivering_StaysHeld()
    {
        var (store, id, _) = HeldPrompt("no-answer");
        Answer(DeliveryState.Delivering);

        var result = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected);

        Assert.Equal(TypedDriveResult.Held, result);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Held, record.State);
        Assert.Equal("delivering", record.DirectorState);
        Assert.Equal("hello agent", record.Text);
    }

    [Fact]
    public async Task Drive_DirectorSaysNotDelivered_ShowsBackWithTheText_AndNeverResends()
    {
        // Proves the case 2f ending at the driver: the Director refused in the end, so the words are shown back with
        // "Send anyway" - and the driver sent NOTHING; it only asked.
        var (store, id, _) = HeldPrompt("delivering");
        Answer(DeliveryState.NotDelivered);

        var result = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.Finished, result);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Equal("hello agent", record.Text);
        Assert.Equal(0, _prompts);
        Assert.Equal("not-delivered", DecisionsAfterHold(store, id).Last());
    }

    [Fact]
    public async Task Drive_UnknownOnce_IsHeld_UnknownAgainOnALaterWakeUp_IsShownBack()
    {
        // Proves "unknown" from a starved Director is not taken as "not in" the first time - the command may still be
        // queued - and is shown back when a later wake-up still hears it.
        var (store, id, _) = HeldPrompt("no-answer");
        var driver = Driver();

        Answer(DeliveryState.Unknown);
        Assert.Equal(TypedDriveResult.Held, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);
        Assert.Equal("unknown", store.Read(id).Record!.DirectorState);

        Answer(DeliveryState.Unknown);
        Assert.Equal(TypedDriveResult.Finished, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Equal("hello agent", record.Text);
        Assert.Equal("unknown-twice", store.ReadDecisions(id).Last().Facts!.Reason);
        Assert.Equal(0, _prompts);
    }

    [Theory]
    [InlineData(299, TypedPromptState.Held)]
    [InlineData(300, TypedPromptState.Held)]
    [InlineData(301, TypedPromptState.Unconfirmed)]
    public async Task Drive_NoAnswer_IsHeldUntilPastFiveMinutesFromTheSentTime_ThenUnconfirmed(int secondsAfterSend, TypedPromptState expected)
    {
        // Proves the one five-minute boundary (GatewayDictationEndpoint.MaxDeliveryAge, the same strict test): no answer
        // of any kind at 4:59 and at 5:00 is still held; at 5:01 it is ruled could-not-confirm, with the text kept.
        var (store, id, _) = HeldPrompt("no-answer");
        _clock.Ahead = TimeSpan.FromSeconds(secondsAfterSend);
        _answers.Enqueue(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer to the question"));

        await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        var record = store.Read(id).Record!;
        Assert.Equal(expected, record.State);
        Assert.Equal("hello agent", record.Text);
        if (expected == TypedPromptState.Unconfirmed)
        {
            var line = store.ReadDecisions(id).Last();
            Assert.Equal("unconfirmed", line.Decision);
            Assert.Equal("no-answer", line.Facts!.DirectorNoAnswer);
            Assert.True(line.Facts.AgeSeconds >= 301);
        }
    }

    [Fact]
    public async Task Drive_DirectorNotConnected_IsNoAnswer_NeverNotIn()
    {
        // Proves a Director that is not connected is a no-answer (held, then unconfirmed past the limit) - never "not in".
        var (store, id, _) = HeldPrompt("no-answer");
        _directorConnected = false;

        Assert.Equal(TypedDriveResult.Held, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);
        Assert.Equal("waiting-for-director", store.Read(id).Record!.DirectorState);

        _clock.Ahead = TimeSpan.FromMinutes(6);
        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.Equal(TypedPromptState.Unconfirmed, store.Read(id).Record!.State);
        Assert.Equal("never-left-the-gateway", store.ReadDecisions(id).Last().Facts!.DirectorNoAnswer);
    }

    [Fact]
    public async Task Drive_ANoAnswerBetweenTwoUnknowns_IsNeverCountedAsTheSecondUnknown()
    {
        // Proves a no-answer is never read as "not in": unknown, then a timed-out question, then unknown - the timed-out
        // question does not count, so the record is shown back only on the second REAL unknown.
        var (store, id, _) = HeldPrompt("no-answer");
        var driver = Driver();

        Answer(DeliveryState.Unknown);
        await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);
        _answers.Enqueue(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds"));
        await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);
        Assert.Equal(TypedPromptState.Held, store.Read(id).Record!.State);
        Assert.Equal(1, store.Read(id).Record!.UnknownAnswers);

        Answer(DeliveryState.Unknown);
        await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);
        Assert.Equal(TypedPromptState.NotDelivered, store.Read(id).Record!.State);
    }

    [Fact]
    public async Task Drive_ALateAnswerToAnEarlierQuestion_IsNeverTakenForTheLaterQuestion()
    {
        // Proves the late, slow answer case. The first wake-up's question runs out of time; the Director's answer to it -
        // "not-delivered", already stale because the record then moved on - arrives only after the second question went
        // out. Each question is its own tunnel command with its own id, so the second wake-up reads ITS answer
        // ("delivered") and the stale one is never read: the record resolves delivered, and no not-delivered line exists.
        var (store, id, _) = HeldPrompt("no-answer");
        var driver = Driver();
        string? firstQuestion = null;
        _answers.Enqueue(cmd =>
        {
            firstQuestion = cmd.CommandId;
            return DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        });
        await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        string? secondQuestion = null;
        _answers.Enqueue(cmd =>
        {
            secondQuestion = cmd.CommandId;
            return StateAnswer(cmd, DeliveryState.Delivered);
        });
        // The stale answer, addressed to the FIRST question's command id, has nowhere to land: nothing is waiting on it.
        var lateAnswerForFirst = StateAnswer(new DirectorCommand { CommandId = firstQuestion! }, DeliveryState.NotDelivered);
        await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.NotEqual(firstQuestion, secondQuestion);
        Assert.Equal(firstQuestion, lateAnswerForFirst.CommandId);
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
        Assert.DoesNotContain("not-delivered", store.ReadDecisions(id).Select(l => l.Decision));
    }

    [Fact]
    public async Task Router_ALateAnswerArrivingAfterItsTimeout_IsNotReturnedToTheNextQuestion()
    {
        // Proves the property the driver relies on, at the ONE place a command is sent: a question whose answer comes after
        // its wait ends is reported as a timeout, and when that late answer does arrive it completes only its own command -
        // the next question, sent meanwhile, returns its OWN answer.
        var pending = new Dictionary<string, TaskCompletionSource<DirectorCommandResult?>>();
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, cmd, ct) =>
        {
            var tcs = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending[cmd.CommandId] = tcs;
            return tcs.Task.WaitAsync(ct);
        };

        var first = await DirectorCommandRouter.TrySendAsync(send, DirectorId, DeliveryStateRequest.Verb, "s", null,
            CancellationToken.None, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(DirectorCommandStatus.Timeout, first!.Status);

        var secondTask = DirectorCommandRouter.TrySendAsync(send, DirectorId, DeliveryStateRequest.Verb, "s", null,
            CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        KeyValuePair<string, TaskCompletionSource<DirectorCommandResult?>>[] both;
        lock (pending) both = pending.ToArray();
        Assert.Equal(2, both.Length);
        var (firstId, firstTcs) = (both[0].Key, both[0].Value);
        var secondTcs = both.Single(p => p.Key != firstId).Value;
        firstTcs.SetResult(StateAnswer(new DirectorCommand { CommandId = firstId }, DeliveryState.NotDelivered));
        secondTcs.SetResult(StateAnswer(new DirectorCommand { CommandId = both.Single(p => p.Key != firstId).Key }, DeliveryState.Delivered));

        var second = await secondTask;
        var answer = DirectorCommandRouter.ReadBody<DeliveryStateResponse>(second!)!;
        Assert.Equal(DeliveryState.Delivered, answer.State);
    }

    [Fact]
    public async Task Drive_AfterAGatewayRestart_AFreshStoreAndDriverOverTheSameFolderResumeTheHeldPrompt()
    {
        // Proves a Gateway restart forgets nothing: the held record is found by a NEW store over the same folder (as the
        // driver finds it when the Gateway starts), and a NEW driver resolves it.
        var (store, id, sid) = HeldPrompt("no-answer");

        var afterRestart = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var held = Assert.Single(afterRestart.HeldDeliveries());
        Assert.Equal((id, (string?)sid), held);
        Answer(DeliveryState.Delivered);

        var result = await Driver().DriveOnceAsync(TenantId.Local, afterRestart, id, TypedPromptDecisions.DriveGatewayStarted);

        Assert.Equal(TypedDriveResult.Finished, result);
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
        Assert.Equal("gateway-started", afterRestart.ReadDecisions(id).Single(l => l.Decision == "gateway-drive").Facts!.Trigger);
        Assert.Empty(afterRestart.HeldDeliveries());
    }

    [Fact]
    public async Task Drive_TwoWakeUpsAtOnce_RunOneAttempt()
    {
        // Proves the single-flight: a tunnel-back wake-up and a tick at the same moment ask ONCE.
        var (store, id, _) = HeldPrompt("no-answer");
        var gate = new TaskCompletionSource();
        _answers.Enqueue(cmd =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(10));
            return StateAnswer(cmd, DeliveryState.Delivering);
        });
        var driver = Driver();

        var first = Task.Run(() => driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected));
        await WaitUntil(() => _asks == 1);
        var second = await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);
        gate.SetResult();
        await first;

        Assert.Equal(TypedDriveResult.Held, second);
        Assert.Equal(1, _asks);
        Assert.Single(store.ReadDecisions(id), l => l.Decision == "gateway-drive");
    }

    [Fact]
    public async Task Drive_ARecordNoLongerHeld_IsNotDriven()
    {
        var (store, id, _) = HeldPrompt("no-answer");
        Answer(DeliveryState.Delivered);
        await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        var again = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.NotHeld, again);
        Assert.Equal(1, _asks);
    }

    // ===== section 8 and 9: not located is held, only a provably ended session is shown back as ended ============

    [Fact]
    public async Task Drive_SessionNotLocatedNow_IsHeldWaitingForDirector_ThenUnconfirmedPastFiveMinutes()
    {
        // Proves QA case 9 at the driver: a prompt that went out unanswered, then a Director frozen so its session is not
        // located, is held "waiting-for-director" on every wake-up - never "gone" - and at 5:01 from the sent time is
        // could-not-confirm with the words.
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.Hold(id, Guid.NewGuid().ToString(), "hello agent", _clock.GetUtcNow().UtcDateTime, "no-answer", new TypedPromptDecisionFacts());
        var driver = Driver();

        for (var wake = 0; wake < 20; wake++)
        {
            _clock.Ahead = TimeSpan.FromSeconds(15 * wake);
            Assert.Equal(TypedDriveResult.Held, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
            Assert.Equal("waiting-for-director", store.Read(id).Record!.DirectorState);
        }
        _clock.Ahead = TimeSpan.FromSeconds(301);
        Assert.Equal(TypedDriveResult.Finished, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Unconfirmed, record.State);
        Assert.Equal("hello agent", record.Text);
        Assert.Equal(0, _asks);
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Drive_APromptThatNeverLeft_PastFiveMinutesUnlocated_IsShownBackWithSendAnyway_NotUnconfirmed()
    {
        // Proves a typed prompt that never left the Gateway is provably not in: past the limit it is shown back
        // not-delivered (with "Send anyway"), never "could not confirm".
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Guid.NewGuid().ToString(), "hello agent", _clock.GetUtcNow().UtcDateTime, new TypedPromptUnsentRequest());

        _clock.Ahead = TimeSpan.FromSeconds(299);
        Assert.Equal(TypedDriveResult.Held, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        _clock.Ahead = TimeSpan.FromSeconds(301);
        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Equal("hello agent", record.Text);
        Assert.Equal("never-sent", store.ReadDecisions(id).Last().Facts!.Reason);
    }

    [Fact]
    public async Task Drive_APromptThatNeverLeft_WhenItsSessionIsBack_IsSentOnce_UnderItsMintedId()
    {
        // Proves contract section 10: a typed prompt that never left the Gateway is provably not in, so when its session is
        // reachable again within the limit the driver SENDS it - once, with its minted delivery id - and a later wake-up
        // sends nothing more.
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Seat(), "hello agent", _clock.GetUtcNow().UtcDateTime,
            new TypedPromptUnsentRequest { AppendEnter = true, Surface = "cockpit" });
        _promptAnswer = _ => Accepted(DeliveryState.Delivered);
        _clock.Ahead = TimeSpan.FromSeconds(300);

        var result = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected);
        var again = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.Finished, result);
        Assert.Equal(TypedDriveResult.NotHeld, again);
        Assert.Equal(1, _prompts);
        Assert.Equal(0, _asks);
        var sent = Assert.Single(_sentPrompts);
        Assert.Equal(id, sent.DeliveryId);
        Assert.Equal("hello agent", sent.Text);
        Assert.Equal("cockpit", sent.Surface);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.Delivered, record.State);
        Assert.Null(record.Text);
        Assert.Equal(new[] { "session-not-found", "still-delivering", "gateway-drive", "sent-to-director", "director-answer", "delivered" },
            store.ReadDecisions(id).Select(l => l.Decision));
    }

    [Fact]
    public async Task Drive_APromptSentOnceAndUnanswered_IsAskOnlyFromThenOn()
    {
        // Proves the send is once: the Gateway's send goes unanswered, so it is held, and the next wake-up ASKS - it never
        // sends a second copy.
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Seat(), "hello agent", _clock.GetUtcNow().UtcDateTime, new TypedPromptUnsentRequest());
        _promptAnswer = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer within 30 seconds");
        var driver = Driver();

        Assert.Equal(TypedDriveResult.Held, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected));
        Answer(DeliveryState.Delivered);
        Assert.Equal(TypedDriveResult.Finished, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        Assert.Equal(1, _prompts);
        Assert.Equal(1, _asks);
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
    }

    [Fact]
    public async Task Drive_APromptThatNeverLeft_BackOnlyAt5_01_IsShownBack_NotSent()
    {
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Seat(), "hello agent", _clock.GetUtcNow().UtcDateTime, new TypedPromptUnsentRequest());
        _clock.Ahead = TimeSpan.FromSeconds(301);

        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected));

        Assert.Equal(0, _prompts);
        Assert.Equal(TypedPromptState.NotDelivered, store.Read(id).Record!.State);
        Assert.Equal("hello agent", store.Read(id).Record!.Text);
    }

    [Fact]
    public async Task Drive_APromptThatNeverLeft_AndAskedForTheMenuGuard_IsShownBack_NeverTypedBlind()
    {
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Seat(), "hello agent", _clock.GetUtcNow().UtcDateTime, new TypedPromptUnsentRequest { MenuGuard = true });

        Assert.Equal(TypedDriveResult.Finished, await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));

        Assert.Equal(0, _prompts);
        Assert.Equal(TypedPromptState.NotDelivered, store.Read(id).Record!.State);
        Assert.Equal("menu-guard-needs-the-prompt-route", store.ReadDecisions(id).Last().Facts!.Reason);
    }

    [Fact]
    public async Task Drive_AGatewaySendThatNeverLeft_StaysNeverSent_AndIsSentOnTheNextWakeUp()
    {
        // Proves a send the Director's tunnel was gone for left nothing behind: it is written as never-left, the record is
        // never-sent again, and the next wake-up sends it - still exactly one prompt reaches a Director.
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.HoldNeverSent(id, Seat(), "hello agent", _clock.GetUtcNow().UtcDateTime, new TypedPromptUnsentRequest());
        var driver = Driver();

        _directorConnected = false;
        Assert.Equal(TypedDriveResult.Held, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick));
        Assert.True(store.Read(id).Record!.NeverSent);
        Assert.Equal("waiting-for-director", store.Read(id).Record!.DirectorState);

        _directorConnected = true;
        _promptAnswer = _ => Accepted(DeliveryState.Delivered);
        Assert.Equal(TypedDriveResult.Finished, await driver.DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveDirectorConnected));
        Assert.Equal(1, _prompts);
        Assert.Equal(TypedPromptState.Delivered, store.Read(id).Record!.State);
    }

    [Fact]
    public async Task Drive_ASessionLocatedAndExited_IsShownBackAsEnded_WithTheTextAndNoSendAnyway()
    {
        // Proves F4 at the driver: only a session the Gateway can prove ended - located, and exited - is resolved as
        // ended; the words are kept and nothing is asked or sent.
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var id = TypedPromptDelivery.MintDeliveryId();
        store.Hold(id, Seat(activityState: "Exited", status: "Exited"), "hello agent", _clock.GetUtcNow().UtcDateTime, "no-answer",
            new TypedPromptDecisionFacts());

        var result = await Driver().DriveOnceAsync(TenantId.Local, store, id, TypedPromptDecisions.DriveTick);

        Assert.Equal(TypedDriveResult.Finished, result);
        var record = store.Read(id).Record!;
        Assert.Equal(TypedPromptState.SessionEnded, record.State);
        Assert.Equal("hello agent", record.Text);
        Assert.Equal(0, _asks);
        var body = await ExecuteAsync(TypedPromptDelivery.OutcomeResult(record));
        Assert.Equal(200, body.Status);
        Assert.Equal("session-exited", body.Json.GetProperty("reason").GetString());
        Assert.False(body.Json.GetProperty("offerSendAnyway").GetBoolean());
        Assert.Equal("hello agent", body.Json.GetProperty("transcript").GetString());
    }

    private static async Task<(int Status, JsonElement Json)> ExecuteAsync(Microsoft.AspNetCore.Http.IResult result)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
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

    // ===== the store (T3) =====================================================================================

    [Fact]
    public void Store_AnotherAccountsDeliveryId_IsNotFound()
    {
        var (store, id, _) = HeldPrompt("no-answer");
        var other = store.ForTenant(new TenantId("33333333-3333-3333-3333-333333333333"));

        Assert.Equal(TypedPromptReadKind.Absent, other.Read(id).Kind);
        Assert.Empty(other.HeldDeliveries());
        Assert.Equal(TypedPromptStore.AcknowledgeResult.NotFound, other.Acknowledge(id));
    }

    [Fact]
    public void Store_Acknowledge_DeletesTheTextOfAResolvedRecord_OnceAndIdempotently()
    {
        var (store, id, _) = HeldPrompt("no-answer");
        Assert.Equal(TypedPromptStore.AcknowledgeResult.StillHeld, store.Acknowledge(id));
        store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonDirectorSaidNotDelivered, "not-delivered");

        Assert.Equal(TypedPromptStore.AcknowledgeResult.Acknowledged, store.Acknowledge(id));
        Assert.Equal(TypedPromptStore.AcknowledgeResult.AlreadyAcknowledged, store.Acknowledge(id));

        var record = store.Read(id).Record!;
        Assert.Null(record.Text);
        Assert.Equal(TypedPromptState.NotDelivered, record.State);
        Assert.Single(store.ReadDecisions(id), l => l.Decision == "acknowledged");
    }

    [Fact]
    public void Store_TheDecisionLogNeverHoldsTheWords()
    {
        var (store, id, _) = HeldPrompt("no-answer");

        var log = File.ReadAllText(Path.Combine(store.Root, id, "decisions.jsonl"));

        Assert.DoesNotContain("hello agent", log);
        Assert.Equal(new[] { "sent-to-director", "director-answer", "still-delivering" },
            store.ReadDecisions(id).Select(l => l.Decision));
    }

    [Fact]
    public void Store_Sweep_RetiresRecordsOlderThanThirtyDays_AndKeepsYoungerOnes()
    {
        var (store, oldId, _) = HeldPrompt("no-answer");
        store.ResolveNotDelivered(oldId, TypedPromptDecisions.ReasonDirectorSaidNotDelivered, "not-delivered");
        _clock.Ahead = TimeSpan.FromDays(20);
        var (_, youngId, _) = HeldPrompt("no-answer");

        _clock.Ahead = TimeSpan.FromDays(31);
        var removed = store.SweepOlderThan(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Equal(TypedPromptReadKind.Absent, store.Read(oldId).Kind);
        Assert.Equal(TypedPromptReadKind.Present, store.Read(youngId).Kind);
    }

    [Fact]
    public void Store_TenantsWithPartitions_FindsEveryAccountOnDisk()
    {
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var account = new TenantId("44444444-4444-4444-4444-444444444444");
        store.ForTenant(account).Hold(TypedPromptDelivery.MintDeliveryId(), Guid.NewGuid().ToString(), "x", _clock.GetUtcNow().UtcDateTime,
            "no-answer", new TypedPromptDecisionFacts());

        var tenants = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock).TenantsWithPartitions();

        Assert.Contains(TenantId.Local, tenants);
        Assert.Contains(account, tenants);
    }

    // ===== helpers ============================================================================================

    private (TypedPromptStore Store, string Id, string Sid) HeldPrompt(string directorState)
    {
        var store = new TypedPromptStore(Path.Combine(_root, "typed"), TenantId.Local, _clock);
        var sid = Seat();
        var id = TypedPromptDelivery.MintDeliveryId();
        store.Hold(id, sid, "hello agent", _clock.GetUtcNow().UtcDateTime, directorState,
            new TypedPromptDecisionFacts { Ok = false, Reason = "prompt-unanswered" });
        return (store, id, sid);
    }

    private static List<string> DecisionsAfterHold(TypedPromptStore store, string id)
        => store.ReadDecisions(id).Select(l => l.Decision).Skip(3).ToList();

    private TypedPromptDelivery Driver() => new(_registry, owners: null, _pushed, SendAsync, TimeSpan.FromMinutes(10), _clock);

    private Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand cmd, CancellationToken ct)
    {
        if (!_directorConnected) return Task.FromResult<DirectorCommandResult?>(null);
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

    private static DirectorCommandResult Accepted(DeliveryState state)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(
            new PromptResponse { Accepted = true, DeliveryState = state, ActivityState = "Working" }, Json));

    private static SessionVerbClient.PromptSendOutcome Accepted(PromptResponse body)
        => new(SessionVerbClient.PromptSendKind.Accepted, body, "");

    private string Seat(string activityState = "Working", string status = "Running")
    {
        var sid = Guid.NewGuid().ToString();
        _pushed.RegisterConnection(TenantId.Local, DirectorId, "conn-1");
        var known = _pushed.GetLastKnown(TenantId.Local, DirectorId).Sessions.ToList();
        known.Add(new SessionDto
        {
            SessionId = sid,
            DirectorId = DirectorId,
            Agent = "ClaudeCode",
            RepoPath = @"D:\ReposFred\devthrottle",
            Status = status,
            ActivityState = activityState,
            LastActivityAt = DateTime.UtcNow,
        });
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, DirectorId, "conn-1", ++_snapshotSeq, known.ToArray()));
        return sid;
    }

    private long _snapshotSeq;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never held");
            await Task.Delay(10);
        }
    }

    /// <summary>A clock standing still at the moment the test began, moved only by <see cref="Ahead"/> - so 5:00 is exactly 5:00.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        public TimeSpan Ahead;
        public override DateTimeOffset GetUtcNow() => _start + Ahead;
    }
}
