using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 3: THE GATEWAY COUNTS (owner ruling 28 September 2026).
///
/// ONE count for the whole account. Each stop the owner answers without having played its narration adds one; any
/// narration played, on any session, sets it back to zero. It is persisted beside voice-sessions.json so a Gateway
/// restart does not reset it.
/// </summary>
public sealed class VoiceUnheardCountTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private readonly GatewayDbTestHarness _settingsData = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-voice-count-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _settingsData.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private TenantSettingsResolver? _settings;

    private WingmanVoiceService Voice()
    {
        Directory.CreateDirectory(_dir);
        Func<TenantId, Core.Configuration.WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => Task.FromResult<IAgentBrain>(null!);
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));
        return new WingmanVoiceService(brain, new KeyVault(Path.Combine(_dir, "vault.json")), _settings, Path.Combine(_dir, "voice-sessions.json"));
    }

    /// <summary>A voice session at a stop with a narration, answered by the owner without playing it.</summary>
    private static string AnswerUnheard(WingmanVoiceService voice)
    {
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3a"));
        Assert.Equal(VoiceListeningLedger.AnswerOutcome.Unheard, voice.Listening.NoteOwnerAnswered(Tenant, sid));
        return sid;
    }

    [Fact]
    public void NoteOwnerAnswered_EachUnheardStopAcrossSessions_AddsOneToOneAccountCount()
    {
        var voice = Voice();

        for (var i = 0; i < 4; i++) AnswerUnheard(voice);

        Assert.Equal(4, voice.Listening.UnheardInARow(Tenant));
    }

    [Fact]
    public void NoteOwnerAnswered_AHeardStopOrOneWithNoNarration_AddsNothing()
    {
        var voice = Voice();
        AnswerUnheard(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3a"));
        voice.Listening.NotePlayed(Tenant, sid, voice.Get(Tenant, sid)!.AtUtc);   // resets to zero
        AnswerUnheard(voice);                                                     // one

        Assert.Equal(VoiceListeningLedger.AnswerOutcome.Heard, voice.Listening.NoteOwnerAnswered(Tenant, sid));
        Assert.Equal(VoiceListeningLedger.AnswerOutcome.NoNarration, voice.Listening.NoteOwnerAnswered(Tenant, Guid.NewGuid().ToString()));
        Assert.Equal(1, voice.Listening.UnheardInARow(Tenant));
    }

    [Fact]
    public void NotePlayed_APlayOnAnotherSession_SetsTheCountBackToZero()
    {
        var voice = Voice();
        for (var i = 0; i < 3; i++) AnswerUnheard(voice);
        var other = Guid.NewGuid().ToString();
        voice.Mark(Tenant, other);
        voice.StoreReadyAudioForTest(Tenant, other, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3b"));

        voice.Listening.NotePlayed(Tenant, other, voice.Get(Tenant, other)!.AtUtc);

        Assert.Equal(0, voice.Listening.UnheardInARow(Tenant));
    }

    /// <summary>"Any narration played sets it back to zero": a play of an older clip is still the owner listening.</summary>
    [Fact]
    public void NotePlayed_APlayOfANarrationNoLongerCurrent_StillSetsTheCountBackToZero()
    {
        var voice = Voice();
        for (var i = 0; i < 3; i++) AnswerUnheard(voice);

        var matched = voice.Listening.NotePlayed(Tenant, Guid.NewGuid().ToString(), DateTime.UtcNow.AddMinutes(-10));

        Assert.False(matched);
        Assert.Equal(0, voice.Listening.UnheardInARow(Tenant));
    }

    [Fact]
    public void UnheardInARow_AfterAGatewayRestart_IsTheCountFromBefore()
    {
        var before = Voice();
        for (var i = 0; i < 3; i++) AnswerUnheard(before);

        var after = Voice();   // a new service over the same directory: a Gateway restart

        Assert.Equal(3, after.Listening.UnheardInARow(Tenant));
        Assert.True(File.Exists(Path.Combine(after.PartitionDirectoryFor(Tenant), "voice-listening.json")));
    }

    [Fact]
    public void UnheardInARow_ACountFileThatCannotBeRead_StartsTheAccountAtZero()
    {
        var voice = Voice();
        var partition = voice.PartitionDirectoryFor(Tenant);
        Directory.CreateDirectory(partition);
        File.WriteAllText(Path.Combine(partition, "voice-listening.json"), "{ not json");

        Assert.Equal(0, Voice().Listening.UnheardInARow(Tenant));
    }

    [Fact]
    public void UnheardInARow_OneAccountsCount_IsNotAnothers()
    {
        var ledger = new VoiceListeningLedger();
        var a = new TenantId("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");
        var b = new TenantId("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");
        var sid = Guid.NewGuid().ToString();
        ledger.NoteNarrationReady(a, sid, DateTime.UtcNow);

        ledger.NoteOwnerAnswered(a, sid);

        Assert.Equal(1, ledger.UnheardInARow(a));
        Assert.Equal(0, ledger.UnheardInARow(b));
    }
}
