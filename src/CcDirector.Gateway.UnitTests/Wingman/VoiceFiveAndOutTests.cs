using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 4: FIVE AND OUT (owner ruling 28 September 2026).
///
/// The stop that brings the account's count to five presses the account-wide voice switch off - the switch itself,
/// handed to the ledger, not a second mechanism - and the time and reason are recorded. These pin that it presses once,
/// at five and not before, that the record survives a restart, and that a switch that cannot be pressed leaves no
/// claim that voice went off.
/// </summary>
public sealed class VoiceFiveAndOutTests : IDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
    private static readonly DateTime Now = new(2026, 9, 28, 14, 32, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-voice-five-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private VoiceListeningLedger Ledger() => new(_ => _dir, () => Now);

    private static void AnswerUnheard(VoiceListeningLedger ledger)
    {
        var sid = Guid.NewGuid().ToString();
        ledger.NoteNarrationReady(Tenant, sid, Now.AddMinutes(-1));
        Assert.Equal(VoiceListeningLedger.AnswerOutcome.Unheard, ledger.NoteOwnerAnswered(Tenant, sid));
    }

    [Fact]
    public async Task NoteOwnerAnswered_TheFifthUnheardStop_PressesTheSwitchOffOnceAndRecordsWhenAndWhy()
    {
        var ledger = Ledger();
        var presses = new List<TenantId>();
        ledger.UseSwitchOff(t => { presses.Add(t); return Task.CompletedTask; });

        for (var i = 0; i < 4; i++) AnswerUnheard(ledger);
        Assert.Empty(presses);
        Assert.Null(ledger.SwitchedOff(Tenant));

        AnswerUnheard(ledger);
        await ledger.SwitchOffInFlight;

        Assert.Equal(new[] { Tenant }, presses);
        Assert.Equal(new VoiceListeningLedger.SwitchOff(Now, "you answered five sessions without listening"), ledger.SwitchedOff(Tenant));
    }

    [Fact]
    public async Task NoteOwnerAnswered_AStopAfterTheSwitchOff_DoesNotPressItAgain()
    {
        var ledger = Ledger();
        var presses = 0;
        ledger.UseSwitchOff(_ => { presses++; return Task.CompletedTask; });

        for (var i = 0; i < 5; i++) AnswerUnheard(ledger);
        await ledger.SwitchOffInFlight;
        AnswerUnheard(ledger);   // a stop that was already in flight when voice went off
        await ledger.SwitchOffInFlight;

        Assert.Equal(1, presses);
    }

    [Fact]
    public async Task NoteOwnerAnswered_APlayBeforeTheFifth_StartsTheCountAgain()
    {
        var ledger = Ledger();
        var presses = 0;
        ledger.UseSwitchOff(_ => { presses++; return Task.CompletedTask; });

        for (var i = 0; i < 4; i++) AnswerUnheard(ledger);
        ledger.NotePlayed(Tenant, Guid.NewGuid().ToString(), Now);
        for (var i = 0; i < 4; i++) AnswerUnheard(ledger);
        await ledger.SwitchOffInFlight;

        Assert.Equal(0, presses);
        Assert.Equal(4, ledger.UnheardInARow(Tenant));
    }

    [Fact]
    public async Task SwitchedOff_AfterAGatewayRestart_IsStillRecorded()
    {
        var before = Ledger();
        before.UseSwitchOff(_ => Task.CompletedTask);
        for (var i = 0; i < 5; i++) AnswerUnheard(before);
        await before.SwitchOffInFlight;

        var after = Ledger();

        Assert.Equal(new VoiceListeningLedger.SwitchOff(Now, VoiceListeningLedger.UnheardSwitchOffReason), after.SwitchedOff(Tenant));
    }

    /// <summary>
    /// Review of step 4: the switch-off is saved before the press runs. A Gateway that stops in between must not come
    /// back believing voice was switched off - that would stop every later press. An unconfirmed switch-off is withdrawn
    /// at start-up, and the next unheard stop presses the switch.
    /// </summary>
    [Fact]
    public async Task SwitchedOff_AGatewayThatStoppedBeforeThePressFinished_WithdrawsItAndPressesAgain()
    {
        var before = Ledger();
        var neverFinishes = new TaskCompletionSource();
        before.UseSwitchOff(_ => neverFinishes.Task);   // the Gateway stops while the switch is still being pressed
        for (var i = 0; i < VoiceListeningLedger.UnheardLimit; i++) AnswerUnheard(before);
        Assert.NotNull(before.SwitchedOff(Tenant));

        var after = Ledger();   // a restart over the same directory
        var presses = 0;
        after.UseSwitchOff(_ => { presses++; return Task.CompletedTask; });

        Assert.Null(after.SwitchedOff(Tenant));
        AnswerUnheard(after);
        await after.SwitchOffInFlight;
        Assert.Equal(1, presses);
        Assert.NotNull(after.SwitchedOff(Tenant));
        Assert.NotNull(Ledger().SwitchedOff(Tenant));   // and this one, confirmed, survives the next restart
    }

    [Fact]
    public async Task NoteOwnerAnswered_ASwitchThatCannotBePressed_WithdrawsTheSwitchOffAndTriesAgainNextStop()
    {
        var ledger = Ledger();
        var attempts = 0;
        ledger.UseSwitchOff(_ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException(new InvalidOperationException("the settings store is down")) : Task.CompletedTask;
        });

        for (var i = 0; i < 5; i++) AnswerUnheard(ledger);
        await ledger.SwitchOffInFlight;
        Assert.Null(ledger.SwitchedOff(Tenant));

        AnswerUnheard(ledger);
        await ledger.SwitchOffInFlight;

        Assert.Equal(2, attempts);
        Assert.NotNull(ledger.SwitchedOff(Tenant));
    }
}
