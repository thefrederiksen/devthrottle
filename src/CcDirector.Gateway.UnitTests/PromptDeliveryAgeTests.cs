using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director never types a prompt older than the delivery age limit (Voice Delivery mission, phase 5, QA
/// finding F6). In phase 4's case 9 a frozen Director held a spoken command for seven minutes and typed it the
/// moment it woke: the Gateway's 5-minute rule guarded only what the Gateway still holds, so a command already
/// handed to the Director had no age limit at all. Now the Gateway puts the Send time on the request
/// (<see cref="PromptRequest.SentAtUtc"/>) and the Director refuses to type one strictly past
/// <see cref="MaxDeliveryAge"/> - on receipt, and again at the first keystroke, after every gate and wait.
/// Driven through the real prompt core against a real session, asserted on what the terminal RECEIVED, and on
/// the delivery record the Gateway's questions read. Every age is made with an injected clock; no test waits.
/// </summary>
public sealed class PromptDeliveryAgeTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-prompt-age-" + Guid.NewGuid().ToString("N"));
    private readonly DeliveryRecord _record;

    public PromptDeliveryAgeTests() => _record = new DeliveryRecord(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A recording's delivery: it carries its delivery id and the spoken-turn marker, both the upload id.</summary>
    private static PromptRequest Delivery(string deliveryId, string text) => new()
    {
        Text = text,
        AppendEnter = true,
        Surface = "cockpit",
        DeliveryId = deliveryId,
        DeliveryUploadId = deliveryId,
    };

    /// <summary>A terminal session over a scripted terminal, for an agent the Director keeps no conversation
    /// records for: the send completes on the terminal alone.</summary>
    private static (Session Session, ScriptedTerminal Terminal) NewTerminalSession()
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = Core.Agents.AgentKind.Gemini;
        return (session, terminal);
    }

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, Json)!;
    }

    /// <summary>A fixed clock, so a test can give a prompt any age without a real wait: the verb reads it at
    /// receipt, the session reads the same moment at the first keystroke.</summary>
    private static readonly DateTime ClockNow = new(2026, 9, 26, 4, 30, 0, DateTimeKind.Utc);

    // ===== the check, on receipt =====================================================================

    [Fact]
    public async Task SendPromptAsync_FiveMinutesAndOneSecondOldOnReceipt_TypesNothingAnswersTooOldAndTheRecordAnswersToo()
    {
        // A command that arrives already past the limit is refused without waiting: nothing is typed, the verb
        // answers not-delivered with the too-old reason, the delivery id is recorded not-delivered with that
        // reason (so the Gateway's question gets that answer, never "unknown"), and the delivery-state verb
        // reads the same refusal back.
        var (session, terminal) = NewTerminalSession();
        var request = Delivery("age-receipt-1", "token AGE1 run the build");
        request.SentAtUtc = ClockNow - MaxDeliveryAge.Span - TimeSpan.FromSeconds(1);

        var response = Body(await SessionCommandExecutor.SendPromptAsync(
            session, request, SendSource.Delivery, _record, utcNow: () => ClockNow));

        Assert.False(response.Accepted);
        Assert.Equal(DeliveryState.NotDelivered, response.DeliveryState);
        Assert.StartsWith(MaxDeliveryAge.TooOldReason, response.DeliveryStateReason);
        Assert.Contains("nothing was typed", response.DeliveryStateReason);
        Assert.Empty(terminal.Writes);
        Assert.Empty(terminal.Submitted);

        var entry = _record.Read(session.Id, "age-receipt-1");
        Assert.Equal(DeliveryState.NotDelivered, entry.State);
        Assert.StartsWith(MaxDeliveryAge.TooOldReason, entry.Reason);
        Assert.Equal(DeliveryState.NotDelivered, AnswerDeliveryState(session.Id, "age-receipt-1").State);
    }

    // ===== the check, at the first keystroke: the frozen Director (phase 4, case 9) ==================

    /// <summary>Hold the session's send gate with a first send that blocks inside its first write, exactly the
    /// way a frozen Director held the command in phase 4's case 9: the prompt behind it waits, ages, and is
    /// checked again at the first keystroke when the gate comes back.</summary>
    private static (Task Holder, TaskCompletionSource Release) HoldTheSendGate(Session session, ScriptedTerminal terminal, string holderText)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        terminal.OnWrite = _ =>
        {
            if (held) return;
            held = true;
            release.Task.Wait();
        };
        // Task.Run: the send's synchronous prefix must NOT run on the test's thread, or the block below would
        // deadlock the one thread that releases it.
        var holder = Task.Run(() => session.SendTextAsync(holderText, SendSource.UserInput));
        return (holder, release);
    }

    /// <summary>Waits until the holder send has typed its first character - the moment the send gate is known held.</summary>
    private static async Task UntilTheGateIsHeld(ScriptedTerminal terminal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (terminal.Writes.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(terminal.Writes);
    }

    [Fact]
    public async Task SendPromptAsync_FourMinutesOldThatAgesPastTheLimitBehindTheSendGate_TypesNothing()
    {
        // THE FROZEN-DIRECTOR SHAPE (QA finding F6): a prompt received four minutes old - inside the limit - that
        // waits behind another send until it is five minutes and one second old types NOTHING. The check is made
        // at the first keystroke, after the gate, not only on receipt.
        var (session, terminal) = NewTerminalSession();
        var sessionNow = ClockNow;
        session.UtcNowForTests = () => sessionNow;

        var (holder, release) = HoldTheSendGate(session, terminal, "the send in front, holding the gate");
        try
        {
            await UntilTheGateIsHeld(terminal);
            var request = Delivery("age-gate-1", "token AGE2 a spoken command that waits");
            request.SentAtUtc = ClockNow - TimeSpan.FromMinutes(4); // four minutes old at receipt: accepted

            var aged = SessionCommandExecutor.SendPromptAsync(session, request, SendSource.Delivery, _record, utcNow: () => ClockNow);

            // The prompt ages behind the gate, from four minutes to five minutes and one second.
            sessionNow = ClockNow + TimeSpan.FromSeconds(61);
            release.SetResult();

            var response = Body(await aged.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.False(response.Accepted);
            Assert.Equal(DeliveryState.NotDelivered, response.DeliveryState);
            Assert.StartsWith(MaxDeliveryAge.TooOldReason, response.DeliveryStateReason);
            Assert.Contains("at the first keystroke", response.DeliveryStateReason);
            // The terminal received the holder's bytes and NONE of the aged prompt's: no text, no Enter.
            Assert.DoesNotContain("AGE2", string.Concat(terminal.Writes));
            Assert.Single(terminal.Submitted, w => w.Contains("holding the gate"));

            var entry = _record.Read(session.Id, "age-gate-1");
            Assert.Equal(DeliveryState.NotDelivered, entry.State);
            Assert.StartsWith(MaxDeliveryAge.TooOldReason, entry.Reason);

            await holder.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task SendPromptAsync_AgedOutBehindTheSendGateAfterTheAnswerBudget_TheLateOutcomeSettlesNotDelivered()
    {
    // The same frozen shape, one gate further on: the verb has already answered "delivering" at its budget - the
    // Gateway holds the delivery - and the send then refuses to type past the limit. The late outcome settles the
    // record not-delivered with the too-old reason, never leaving it delivering forever.
        var (session, terminal) = NewTerminalSession();
        var sessionNow = ClockNow;
        session.UtcNowForTests = () => sessionNow;

        var (holder, release) = HoldTheSendGate(session, terminal, "the send in front, holding the gate");
        try
        {
            await UntilTheGateIsHeld(terminal);
            var request = Delivery("age-late-1", "token AGE3 a spoken command that waits");
            request.SentAtUtc = ClockNow - TimeSpan.FromMinutes(4);

            var response = Body(await SessionCommandExecutor.SendPromptAsync(
                session, request, SendSource.Delivery, _record,
                answerBudget: TimeSpan.FromMilliseconds(200), utcNow: () => ClockNow));

            // At its answer budget the verb knows only that the send is still going: "delivering", as phase 3 ruled.
            Assert.True(response.Accepted);
            Assert.Equal(DeliveryState.Delivering, response.DeliveryState);
            Assert.Equal(DeliveryState.Delivering, _record.Read(session.Id, "age-late-1").State);

            // The send refuses at the first keystroke once the prompt is past the limit; the late outcome settles it.
            // Observe first: the late outcome is written the moment the gate comes back, and no test may miss it.
            var settled = await LateOutcomeFor("age-late-1", () =>
            {
                sessionNow = ClockNow + TimeSpan.FromSeconds(61);
                release.SetResult();
                return Task.CompletedTask;
            });
            Assert.Equal(DeliveryStates.NotDelivered, settled);
            var entry = _record.Read(session.Id, "age-late-1");
            Assert.Equal(DeliveryState.NotDelivered, entry.State);
            Assert.StartsWith(MaxDeliveryAge.TooOldReason, entry.Reason);
            Assert.DoesNotContain("AGE3", string.Concat(terminal.Writes));

            await holder.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>Waits for the verb's late outcome for <paramref name="deliveryId"/>, as written by the one place it is written.</summary>
    private static async Task<string> LateOutcomeFor(string deliveryId, Func<Task> afterStarted)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string id, string state) { if (id == deliveryId) seen.TrySetResult(state); }
        SessionCommandExecutor.LateOutcomeObserver += Observe;
        try
        {
            await afterStarted();
            var done = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(done == seen.Task, "no late outcome was written within 30 seconds");
            return await seen.Task;
        }
        finally
        {
            SessionCommandExecutor.LateOutcomeObserver -= Observe;
        }
    }

    // ===== the boundary is strict ====================================================================

    [Fact]
    public async Task SendPromptAsync_FourFiftyNineAndFiveMinutesExactly_AreTyped()
    {
        // The limit is STRICTLY more than five minutes: four minutes fifty-nine seconds and five minutes
        // exactly are both typed. Only five minutes and one second is refused.
        var (session, terminal) = NewTerminalSession();
        session.UtcNowForTests = () => ClockNow;

        var fourFiftyNine = Delivery("age-459", "token AGE4 four fifty-nine");
        fourFiftyNine.SentAtUtc = ClockNow - TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(59);
        var first = Body(await SessionCommandExecutor.SendPromptAsync(session, fourFiftyNine, SendSource.Delivery, _record, utcNow: () => ClockNow));

        Assert.True(first.Accepted);
        Assert.Equal(DeliveryState.Delivered, first.DeliveryState);
        Assert.Single(terminal.Submitted, w => w.Contains("AGE4"));

        var fiveMinutesExactly = Delivery("age-500", "token AGE5 five minutes exactly");
        fiveMinutesExactly.SentAtUtc = ClockNow - MaxDeliveryAge.Span;
        var second = Body(await SessionCommandExecutor.SendPromptAsync(session, fiveMinutesExactly, SendSource.Delivery, _record, utcNow: () => ClockNow));

        Assert.True(second.Accepted);
        Assert.Equal(DeliveryState.Delivered, second.DeliveryState);
        Assert.Single(terminal.Submitted, w => w.Contains("AGE5"));
        Assert.Equal(DeliveryState.Delivered, _record.Read(session.Id, "age-500").State);
    }

    // ===== version skew: a Gateway older than the field ==============================================

    [Fact]
    public async Task SendPromptAsync_NoSendTime_IsTypedAsToday()
    {
        // A request with no SentAtUtc comes from a Gateway older than the field: version skew between two
        // separately shipped parts, not a second path - the prompt is typed as today and the Director logs that
        // it had no Send time.
        var (session, terminal) = NewTerminalSession();
        var request = Delivery("age-none-1", "token AGE6 from an older gateway");
        Assert.Null(request.SentAtUtc);

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session, request, SendSource.Delivery, _record));

        Assert.True(response.Accepted);
        Assert.Equal(DeliveryState.Delivered, response.DeliveryState);
        Assert.Single(terminal.Submitted, w => w.Contains("AGE6"));
        Assert.Equal(DeliveryState.Delivered, _record.Read(session.Id, "age-none-1").State);
    }

    // ===== one number, one place =====================================================================

    [Fact]
    public void TheGatewaysLimitAndTheDirectors_AreTheOneConstant()
    {
        // The 5-minute limit moved into the contracts (phase 5) so the Gateway and the Director cannot drift:
        // the dictation endpoint's two fields are that number, read from the same class the Director's check
        // reads - and the strict-boundary test above proves the Director enforces exactly it.
        Assert.Equal(CcDirector.Gateway.Contracts.MaxDeliveryAge.Minutes, GatewayDictationEndpoint.MaxDeliveryAgeMinutes);
        Assert.Equal(CcDirector.Gateway.Contracts.MaxDeliveryAge.Span, GatewayDictationEndpoint.MaxDeliveryAge);
        Assert.Equal(MaxDeliveryAge.Minutes, 5);
        Assert.Equal(MaxDeliveryAge.TooOldReason, "too-old");
    }

    // ===== the delivery-state verb ===================================================================

    private static DirectorCommand Ask(Guid session, string deliveryId) => new()
    {
        CommandId = "ask-1",
        Verb = DeliveryStateRequest.Verb,
        SessionId = session.ToString(),
        PayloadJson = SessionCommandExecutor.Serialize(new DeliveryStateRequest { DeliveryId = deliveryId }),
    };

    private DeliveryStateResponse AnswerDeliveryState(Guid session, string deliveryId)
    {
        var result = SessionReadExecutor.DeliveryStateOf(Ask(session, deliveryId), _record);
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<DeliveryStateResponse>(result.BodyJson!, Json)!;
    }
}
