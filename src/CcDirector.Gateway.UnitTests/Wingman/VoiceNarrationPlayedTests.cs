using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 1: THE PLAYER SAYS "PLAYED" (owner ruling 28 September 2026).
///
/// The phone pre-downloads every clip, so the audio route being read is not a play. The player reports a play by
/// naming the clip it started - by the <c>generatedAt</c> stamp the Gateway served it under - and only a report naming
/// the session's CURRENT narration counts. These pin that the Gateway enters every narration it makes ready, that a
/// play report round-trips through the exact stamp the phone was handed, and that a report about a replaced clip, a
/// session no longer on voice, or a malformed stamp records nothing.
/// </summary>
public sealed class VoiceNarrationPlayedTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private readonly GatewayDbTestHarness _settingsData = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-voice-played-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _settingsData.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private WingmanVoiceService Service()
    {
        Directory.CreateDirectory(_dir);
        Func<TenantId, Core.Configuration.WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => Task.FromResult<IAgentBrain>(null!);
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));
        return new WingmanVoiceService(brain, new KeyVault(Path.Combine(_dir, "vault.json")), settings, Path.Combine(_dir, "voice-sessions.json"));
    }

    private static byte[] Mp3(string marker) => Encoding.ASCII.GetBytes("ID3" + marker);

    private static int StatusOf(IResult result)
        => result.GetType().GetProperty("StatusCode")?.GetValue(result) as int? ?? StatusCodes.Status200OK;

    /// <summary>The generatedAt stamp exactly as <c>GET /sessions/{sid}/wingman/voice</c> serializes it.</summary>
    private static string ServedStamp(DateTime atUtc) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { generatedAt = atUtc }))
            .GetProperty("generatedAt").GetString()!;

    [Fact]
    public void StoreReady_ANarrationBecomesReady_IsTheSessionsStopAndNotYetPlayed()
    {
        var voice = Service();
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);

        voice.StoreReadyAudioForTest(Tenant, sid, "The branch is pushed.", "I pushed the branch.", Mp3("a"));

        var stop = voice.Listening.StopFor(Tenant, sid);
        Assert.NotNull(stop);
        Assert.Equal(voice.Get(Tenant, sid)!.AtUtc, stop.Value.NarrationAtUtc);
        Assert.False(stop.Value.Played);
    }

    [Fact]
    public void NotePlayReport_TheStampThePhoneWasServed_RecordsTheCurrentNarrationPlayed()
    {
        var voice = Service();
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "The branch is pushed.", "I pushed the branch.", Mp3("a"));
        var served = ServedStamp(voice.Get(Tenant, sid)!.AtUtc);

        var result = GatewayWingmanVoiceEndpoint.NotePlayReport(voice.Listening, Tenant, sid, served);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.True(voice.Listening.StopFor(Tenant, sid)!.Value.Played);
    }

    [Fact]
    public void NotePlayReport_AClipANewerStopReplaced_RecordsNothing()
    {
        var voice = Service();
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "First.", "First reply.", Mp3("a"));
        var oldStamp = ServedStamp(voice.Get(Tenant, sid)!.AtUtc);
        Thread.Sleep(5); // the next narration is a later moment on the Gateway's clock
        voice.StoreReadyAudioForTest(Tenant, sid, "Second.", "Second reply.", Mp3("b"));

        var played = voice.Listening.NotePlayed(Tenant, sid, DateTime.Parse(oldStamp, null, System.Globalization.DateTimeStyles.RoundtripKind));

        Assert.False(played);
        Assert.False(voice.Listening.StopFor(Tenant, sid)!.Value.Played);
    }

    [Fact]
    public void Unmark_ASessionSwitchedOffVoice_ForgetsItsNarrationSoALatePlayRecordsNothing()
    {
        var voice = Service();
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "The branch is pushed.", "I pushed the branch.", Mp3("a"));
        var atUtc = voice.Get(Tenant, sid)!.AtUtc;

        voice.Unmark(Tenant, sid);

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
        Assert.False(voice.Listening.NotePlayed(Tenant, sid, atUtc));
    }

    [Theory]
    [InlineData("not-a-session", "2026-09-28T10:00:00Z")]
    [InlineData("11111111-1111-4111-8111-111111111111", null)]
    [InlineData("11111111-1111-4111-8111-111111111111", "")]
    [InlineData("11111111-1111-4111-8111-111111111111", "yesterday-ish")]
    public void NotePlayReport_AMalformedReport_Is400AndRecordsNothing(string sid, string? stamp)
    {
        var voice = Service();

        var result = GatewayWingmanVoiceEndpoint.NotePlayReport(voice.Listening, Tenant, sid, stamp);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public void NotePlayed_ASessionWithNoNarrationOnRecord_ReturnsFalse()
    {
        var ledger = new VoiceListeningLedger();

        Assert.False(ledger.NotePlayed(Tenant, Guid.NewGuid().ToString(), DateTime.UtcNow));
    }

    [Fact]
    public void NotePlayed_OneAccountsPlay_DoesNotTouchAnotherAccountsNarration()
    {
        var ledger = new VoiceListeningLedger();
        var a = new TenantId("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
        var b = new TenantId("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");
        var sid = Guid.NewGuid().ToString();
        var at = new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
        ledger.NoteNarrationReady(a, sid, at);
        ledger.NoteNarrationReady(b, sid, at);

        Assert.True(ledger.NotePlayed(a, sid, at));

        Assert.True(ledger.StopFor(a, sid)!.Value.Played);
        Assert.False(ledger.StopFor(b, sid)!.Value.Played);
    }
}
