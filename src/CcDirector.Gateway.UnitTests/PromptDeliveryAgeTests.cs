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
        // The 5-minute limit moved into the contracts (phase 5) so the Gateway and the Director cannot drift: one
        // name, one number - the dictation endpoint's old aliases are gone, and both halves read this class. The
        // strict-boundary test above proves the Director enforces exactly it.
        Assert.Equal(MaxDeliveryAge.Minutes, 5);
        // The reason WORD is one constant too (Voice Delivery phase 5, F6): the Director writes its age refusal
        // with MaxDeliveryAge.TooOldReason, and the Gateway's decision name, its shown-back reason and its reading
        // of the Director's answer are that same spelling - the aliases below are that constant, not a second
        // string that could drift. And the Director's refusal is the word followed by the measured age, built by
        // PromptAgeLimit.TooOldReason, so the Gateway must read THAT sentence - a bare-word match would miss every
        // refusal the real Director ever sends.
        Assert.Equal(MaxDeliveryAge.TooOldReason, CcDirector.Gateway.Voice.DeliveryDecisions.TooOld);
        Assert.Equal(MaxDeliveryAge.TooOldReason, GatewayDictationEndpoint.TooOldReason);
        var refused = PromptAgeLimit.TooOldReason(TimeSpan.FromSeconds(301), "when the Director received it");
        Assert.StartsWith(MaxDeliveryAge.TooOldReason + ": ", refused);
        Assert.True(GatewayDictationEndpoint.IsTheDirectorsAgeRefusal(refused));
        Assert.True(GatewayDictationEndpoint.IsTheDirectorsAgeRefusal(MaxDeliveryAge.TooOldReason));
        Assert.False(GatewayDictationEndpoint.IsTheDirectorsAgeRefusal("not too old at all"));
        Assert.False(GatewayDictationEndpoint.IsTheDirectorsAgeRefusal("too-oldish"));
        Assert.False(GatewayDictationEndpoint.IsTheDirectorsAgeRefusal(null));
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
