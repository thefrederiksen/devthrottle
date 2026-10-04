using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Money Saver, 4 October 2026: narration clips live on disk, not in memory, and the clips of sessions that are gone
/// are removed. Before this, the Gateway read every clip ever made into memory at start-up and never removed one
/// whose session simply ended - about 420 MB for one account, growing every day.
///
/// The on-disk tests prove the audio is NOT held in memory by changing the file underneath a service that has
/// already loaded it: a service serving from memory would keep answering with the old bytes.
/// </summary>
public sealed class NarrationClipsOnDiskTests : IDisposable
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly GatewayDbTestHarness _settingsData = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "narration-clips-" + Guid.NewGuid().ToString("N"));
    private TenantSettingsResolver? _settings;

    private TenantSettingsResolver Settings =>
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));

    public NarrationClipsOnDiskTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _settingsData.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string PersistPath => Path.Combine(_root, "voice-sessions.json");
    private string AudioDir => Path.Combine(_root, "tenants", "local", "voice-audio");

    private WingmanVoiceService NewService()
    {
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("brain must not be called");
        var svc = new WingmanVoiceService(brain, new KeyVault(Path.Combine(_root, "test.vault")), Settings, PersistPath);
        Assert.True(svc.ReadyAudioWarmup.Wait(TimeSpan.FromSeconds(30)), "the ready-audio warm load did not finish");
        return svc;
    }

    /// <summary>Back-date both files of a clip, as if it had been written <paramref name="age"/> before <see cref="Now"/>.</summary>
    private void AgeClip(string sid, TimeSpan age)
    {
        foreach (var ext in new[] { ".mp3", ".json" })
        {
            var path = Path.Combine(AudioDir, sid + ext);
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, Now - age);
        }
    }

    private static IReadOnlyCollection<string> Known(params string[] sids) => sids;

    [Fact]
    public void GetAudio_ServesTheFileOnDisk_NotACopyHeldInMemory()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 }, "audio/mpeg");

        File.WriteAllBytes(Path.Combine(AudioDir, "sid-1.mp3"), new byte[] { 9, 9, 9, 9 });

        Assert.Equal(new byte[] { 9, 9, 9, 9 }, svc.GetAudio(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void Reload_ReadsOnlyTheDescriptions_AndServesTheAudioFromDisk()
    {
        NewService().StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 }, "audio/mpeg");

        var reloaded = NewService();
        File.WriteAllBytes(Path.Combine(AudioDir, "sid-1.mp3"), new byte[] { 7, 7 });

        Assert.True(reloaded.HasVoice(TenantId.Local, "sid-1"));
        Assert.Equal(new byte[] { 7, 7 }, reloaded.GetAudio(TenantId.Local, "sid-1"));
        Assert.Equal("audio/mpeg", reloaded.GetAudioContentType(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void GetAudio_ClipFileGone_WithdrawsTheNarration()
    {
        // A play button over a clip that no longer exists can only fail, so the narration stops being ready.
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 }, "audio/mpeg");
        File.Delete(Path.Combine(AudioDir, "sid-1.mp3"));

        Assert.Null(svc.GetAudio(TenantId.Local, "sid-1"));
        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void Store_LeavesNoHalfWrittenFileBehind()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 }, "audio/mpeg");
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "newer", "reply", new byte[] { 4, 5 }, "audio/mpeg");

        Assert.Empty(Directory.GetFiles(AudioDir, "*.tmp"));
        Assert.Equal(new byte[] { 4, 5 }, svc.GetAudio(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void SweepClips_OldClipOfAGoneSession_IsRemoved()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        svc.StoreReadyAudioForTest(TenantId.Local, "alive", "spoken", "reply", new byte[] { 2 }, "audio/mpeg");
        AgeClip("gone", TimeSpan.FromDays(15));
        AgeClip("alive", TimeSpan.FromDays(15));

        // The clips were published at the real "now", so the sweep is run from a moment far enough ahead that the
        // published narrations are as old as their files.
        var removed = svc.SweepClips(_ => Known("alive"), Now + TimeSpan.FromDays(15));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(AudioDir, "gone.mp3")));
        Assert.False(File.Exists(Path.Combine(AudioDir, "gone.json")));
        Assert.False(svc.HasVoice(TenantId.Local, "gone"));
        Assert.True(File.Exists(Path.Combine(AudioDir, "alive.mp3")));
        Assert.True(svc.HasVoice(TenantId.Local, "alive"));
    }

    [Fact]
    public void SweepClips_RecentClipOfAGoneSession_IsKept()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        AgeClip("gone", TimeSpan.FromDays(13));

        var removed = svc.SweepClips(_ => Known("someone-else"), Now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(AudioDir, "gone.mp3")));
    }

    [Fact]
    public void SweepClips_NoSessionKnownForTheAccount_RemovesNothing()
    {
        // An empty list means nothing has reported yet - it says nothing about which sessions are gone.
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        AgeClip("gone", TimeSpan.FromDays(30));

        var removed = svc.SweepClips(_ => Known(), Now + TimeSpan.FromDays(30));

        Assert.Equal(0, removed);
        Assert.True(File.Exists(Path.Combine(AudioDir, "gone.mp3")));
        Assert.True(svc.HasVoice(TenantId.Local, "gone"));
    }

    [Fact]
    public void SweepClips_ClipsOnDiskThatWereNeverLoaded_AreRemovedToo()
    {
        // The 2,000 clips already on the share: written by earlier Gateways, never published in this one.
        Directory.CreateDirectory(AudioDir);
        File.WriteAllBytes(Path.Combine(AudioDir, "old.mp3"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(AudioDir, "old.json"), "{\"Spoken\":\"s\",\"Reply\":\"r\",\"AtUtc\":\"2026-07-22T00:00:00Z\"}");
        File.WriteAllBytes(Path.Combine(AudioDir, "orphan.mp3"), new byte[] { 1 });   // audio with no description
        AgeClip("old", TimeSpan.FromDays(70));
        AgeClip("orphan", TimeSpan.FromDays(70));
        var svc = NewService();

        var removed = svc.SweepClips(_ => Known("alive"), Now);

        Assert.Equal(2, removed);
        Assert.Empty(Directory.GetFiles(AudioDir));
    }

    [Fact]
    public void SweepClips_OldHalfWrittenFilesGo_AndAnythingNotAClipStays()
    {
        Directory.CreateDirectory(AudioDir);
        var partial = Path.Combine(AudioDir, "sid-1.mp3.tmp");
        var fresh = Path.Combine(AudioDir, "sid-2.mp3.tmp");
        var foreign = Path.Combine(AudioDir, "notes.txt");
        File.WriteAllBytes(partial, new byte[] { 1 });
        File.WriteAllBytes(fresh, new byte[] { 1 });
        File.WriteAllText(foreign, "not ours");
        File.SetLastWriteTimeUtc(partial, Now - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(fresh, Now - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(foreign, Now - TimeSpan.FromDays(100));
        var svc = NewService();

        svc.SweepClips(_ => Known("alive"), Now);

        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(foreign));
    }

    private static readonly TenantId AccountA = new("3f2c8a10-5b7e-4c21-9d4e-6a1b2c3d4e5f");
    private static readonly TenantId AccountB = new("9a8b7c6d-1e2f-4a3b-8c9d-0e1f2a3b4c5d");

    private string AudioDirOf(TenantId tenant) => Path.Combine(_root, "tenants", tenant.Value, "voice-audio");

    [Fact]
    public void Store_ClipCannotBeWritten_PublishesNothing()
    {
        // A FILE where the clip folder should be: the write fails, and a clip that is not on disk is not a clip.
        Directory.CreateDirectory(Path.GetDirectoryName(AudioDir)!);
        File.WriteAllText(AudioDir, "in the way");
        var svc = NewService();

        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 }, "audio/mpeg");

        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.Null(svc.GetAudio(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void Store_FailsBeforeReplacingAnything_KeepsTheOlderClipPlayable()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "older words", "reply", new byte[] { 1 }, "audio/mpeg");
        Directory.CreateDirectory(Path.Combine(AudioDir, "sid-1.mp3.tmp"));   // the audio's temporary file cannot be written

        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "newer words", "reply", new byte[] { 2 }, "audio/mpeg");

        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.Equal("older words", svc.Get(TenantId.Local, "sid-1")!.Spoken);
        Assert.Equal(new byte[] { 1 }, svc.GetAudio(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void Store_ReplaceFailsHalfWay_WithdrawsTheOlderNarrationToo()
    {
        // The new audio lands but its description cannot: the older words must not stay published over new audio.
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "older words", "reply", new byte[] { 1 }, "audio/mpeg");
        Directory.CreateDirectory(Path.Combine(AudioDir, "sid-1.json.tmp"));   // the description's temporary file cannot be written

        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "newer words", "reply", new byte[] { 2 }, "audio/mpeg");

        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.False(File.Exists(Path.Combine(AudioDir, "sid-1.mp3")));
    }

    [Fact]
    public void Store_NewerClipWhileAPhoneIsDownloadingTheOlder_Succeeds()
    {
        // The download is served from a copy, so the clip file is not held open for its length - on Windows an open
        // file cannot be replaced, and the newer clip's store would fail.
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 1, 1 }, "audio/mpeg");

        using (var downloading = svc.OpenAudio(TenantId.Local, "sid-1"))
        {
            Assert.NotNull(downloading);
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "newer", "reply", new byte[] { 2, 2 }, "audio/mpeg");
        }

        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.Equal(new byte[] { 2, 2 }, svc.GetAudio(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void GetAudio_ClipFileGone_TakesTheStopOffTheListeningLedger()
    {
        var svc = NewService();
        svc.Mark(TenantId.Local, "sid-1");
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        Assert.NotNull(svc.Listening.StopFor(TenantId.Local, "sid-1"));
        File.Delete(Path.Combine(AudioDir, "sid-1.mp3"));

        Assert.Null(svc.GetAudio(TenantId.Local, "sid-1"));

        Assert.Null(svc.Listening.StopFor(TenantId.Local, "sid-1"));
        Assert.False(File.Exists(Path.Combine(AudioDir, "sid-1.json")));
    }

    [Fact]
    public void SweepClips_OldFilesButAFreshlyPublishedNarration_IsKept()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        AgeClip("gone", TimeSpan.FromDays(20));

        var removed = svc.SweepClips(_ => Known("alive"), Now);

        Assert.Equal(0, removed);
        Assert.True(svc.HasVoice(TenantId.Local, "gone"));
    }

    [Fact]
    public void SweepClips_RemovedClip_IsTakenOffTheListeningLedger()
    {
        var svc = NewService();
        svc.Mark(TenantId.Local, "gone");
        svc.StoreReadyAudioForTest(TenantId.Local, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        AgeClip("gone", TimeSpan.FromDays(15));
        Assert.NotNull(svc.Listening.StopFor(TenantId.Local, "gone"));

        svc.SweepClips(_ => Known("alive"), Now + TimeSpan.FromDays(15));

        Assert.Null(svc.Listening.StopFor(TenantId.Local, "gone"));
    }

    [Fact]
    public void SweepClips_TwoAccounts_OnlyTheOneWithKnownSessionsIsSwept()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(AccountA, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        svc.StoreReadyAudioForTest(AccountB, "gone", "spoken", "reply", new byte[] { 1 }, "audio/mpeg");
        foreach (var dir in new[] { AudioDirOf(AccountA), AudioDirOf(AccountB) })
            foreach (var file in Directory.GetFiles(dir))
                File.SetLastWriteTimeUtc(file, Now - TimeSpan.FromDays(15));

        var removed = svc.SweepClips(t => t == AccountA ? Known("alive") : Known(), Now + TimeSpan.FromDays(15));

        Assert.Equal(1, removed);
        Assert.False(svc.HasVoice(AccountA, "gone"));
        Assert.True(svc.HasVoice(AccountB, "gone"));
        Assert.True(File.Exists(Path.Combine(AudioDirOf(AccountB), "gone.mp3")));
    }

    [Fact]
    public void SweepClips_AFileNameThisServiceCouldNotHaveWritten_IsLeftAndNotCounted()
    {
        Directory.CreateDirectory(AudioDir);
        var odd = Path.Combine(AudioDir, "abc (1).mp3");
        File.WriteAllBytes(odd, new byte[] { 1 });
        File.SetLastWriteTimeUtc(odd, Now - TimeSpan.FromDays(60));
        var svc = NewService();

        var removed = svc.SweepClips(_ => Known("alive"), Now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(odd));
    }
}
