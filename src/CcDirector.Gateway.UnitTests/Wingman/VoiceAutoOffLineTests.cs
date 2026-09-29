using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 5: THE QUIET LINE, and coming back (owner ruling 28 September 2026).
///
/// The Gateway writes the one line the owner finds out from, in his own time zone, and clients render it verbatim.
/// Switching voice back on clears the count and the line.
/// </summary>
public sealed class VoiceAutoOffLineTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
    private static readonly DateTime OffAtUtc = new(2026, 9, 28, 18, 32, 0, DateTimeKind.Utc);   // 14:32 in Toronto
    private static readonly VoiceListeningLedger.SwitchOff Off = new(OffAtUtc, VoiceListeningLedger.UnheardSwitchOffReason);

    [Fact]
    public void For_ASwitchOffToday_NamesTheClockTimeInTheAccountsZone()
    {
        var line = VoiceAutoOffLine.For(Off, voiceModeOn: false, Toronto, OffAtUtc.AddHours(1));

        Assert.Equal("Voice mode switched off at 14:32 - you answered five sessions without listening", line);
    }

    [Fact]
    public void For_ASwitchOffYesterday_SaysYesterday()
    {
        var line = VoiceAutoOffLine.For(Off, voiceModeOn: false, Toronto, OffAtUtc.AddDays(1));

        Assert.Equal("Voice mode switched off yesterday at 14:32 - you answered five sessions without listening", line);
    }

    [Fact]
    public void For_AnOlderSwitchOff_NamesTheDay()
    {
        var line = VoiceAutoOffLine.For(Off, voiceModeOn: false, Toronto, OffAtUtc.AddDays(3));

        Assert.Equal("Voice mode switched off on Monday 28 September at 14:32 - you answered five sessions without listening", line);
    }

    [Fact]
    public void For_NoSwitchOffOrVoiceModeOnAgain_IsNothing()
    {
        Assert.Null(VoiceAutoOffLine.For(null, voiceModeOn: false, Toronto, OffAtUtc));
        Assert.Null(VoiceAutoOffLine.For(Off, voiceModeOn: true, Toronto, OffAtUtc));
    }

    [Fact]
    public async Task ClearForVoiceOn_AfterAnAutoOff_StartsTheCountAgainAndRetiresTheLine()
    {
        var tenant = new TenantId("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
        var ledger = new VoiceListeningLedger(utcNow: () => OffAtUtc);
        ledger.UseSwitchOff(_ => Task.CompletedTask);
        for (var i = 0; i < VoiceListeningLedger.UnheardLimit; i++)
        {
            var sid = Guid.NewGuid().ToString();
            ledger.NoteNarrationReady(tenant, sid, OffAtUtc);
            ledger.NoteOwnerAnswered(tenant, sid);
        }
        await ledger.SwitchOffInFlight;
        Assert.NotNull(ledger.SwitchedOff(tenant));

        ledger.ClearForVoiceOn(tenant);

        Assert.Null(ledger.SwitchedOff(tenant));
        Assert.Equal(0, ledger.UnheardInARow(tenant));
    }

    /// <summary>
    /// Switching voice on must not fail because the cleared record could not be written, and the next voice-on writes
    /// it - otherwise a restart would bring back the old count and the old line.
    /// </summary>
    [Fact]
    public async Task ClearForVoiceOn_WhenTheSaveFails_DoesNotThrowAndTheNextVoiceOnWritesIt()
    {
        var tenant = new TenantId("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
        var dir = Path.Combine(Path.GetTempPath(), "cc-voice-line-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ledger = new VoiceListeningLedger(_ => dir, () => OffAtUtc);
            ledger.UseSwitchOff(_ => Task.CompletedTask);
            for (var i = 0; i < VoiceListeningLedger.UnheardLimit; i++)
            {
                var sid = Guid.NewGuid().ToString();
                ledger.NoteNarrationReady(tenant, sid, OffAtUtc);
                ledger.NoteOwnerAnswered(tenant, sid);
            }
            await ledger.SwitchOffInFlight;
            var path = Path.Combine(dir, "voice-listening.json");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                ledger.ClearForVoiceOn(tenant);
            Assert.NotNull(new VoiceListeningLedger(_ => dir, () => OffAtUtc).SwitchedOff(tenant));   // still the old record on disk

            ledger.ClearForVoiceOn(tenant);

            var restarted = new VoiceListeningLedger(_ => dir, () => OffAtUtc);
            Assert.Null(restarted.SwitchedOff(tenant));
            Assert.Equal(0, restarted.UnheardInARow(tenant));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
