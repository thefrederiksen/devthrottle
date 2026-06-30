using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The "voice mode" yellow window (issue #531): while the wingman is actively producing a
/// session's spoken summary, <see cref="WingmanVoiceService.IsGenerating"/> is true, which the
/// gateway folds into the existing "Briefing" yellow so the session goes red -> yellow -> red.
/// </summary>
public sealed class WingmanVoiceServiceTests
{
    private static WingmanVoiceService NewService()
    {
        // The flag methods never touch the brain; a provider that throws proves that.
        Func<CancellationToken, Task<IAgentBrain>> brain =
            _ => throw new InvalidOperationException("brain must not be called for flag state");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), new DirectorEndpointClient(), persistPath);
    }

    [Fact]
    public void IsGenerating_DefaultsFalse()
    {
        var svc = NewService();
        Assert.False(svc.IsGenerating("sid-1"));
    }

    [Fact]
    public void BeginGenerating_ThenIsGenerating_IsTrue()
    {
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        Assert.True(svc.IsGenerating("sid-1"));
        // Independent per session: a second session is unaffected.
        Assert.False(svc.IsGenerating("sid-2"));
    }

    [Fact]
    public void EndGenerating_ClearsTheFlag()
    {
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        svc.EndGenerating("sid-1");
        Assert.False(svc.IsGenerating("sid-1"));
    }

    [Fact]
    public void OnSessionWorking_ClearsGenerating()
    {
        // A new turn (blue) supersedes any in-flight wingman run for the previous turn, so the
        // yellow marker must drop - raw activity wins while the agent works.
        var svc = NewService();
        svc.BeginGenerating("sid-1");
        svc.OnSessionWorking("sid-1");
        Assert.False(svc.IsGenerating("sid-1"));
    }

    // ---------- Durable audio cache (issue #553) ----------

    /// <summary>Build a service over a SPECIFIC persist path so a second instance can reload from
    /// the same on-disk cache (the gateway-restart case). The empty vault means TtsAsync returns null.</summary>
    private static WingmanVoiceService ServiceAt(string persistPath)
    {
        Func<CancellationToken, Task<IAgentBrain>> brain =
            _ => throw new InvalidOperationException("brain must not be called");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), new DirectorEndpointClient(), persistPath);
    }

    private static void Cleanup(string persistPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(persistPath);
            if (dir is not null && Directory.Exists(Path.Combine(dir, "voice-audio")))
                Directory.Delete(Path.Combine(dir, "voice-audio"), recursive: true);
            if (File.Exists(persistPath)) File.Delete(persistPath);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task StoreSpokenAsync_WithFailingTts_DoesNotMarkReady()
    {
        // No OpenAI key in the vault -> TtsAsync returns null -> the "if anything fails, remove the
        // triangle" rule: the session is a voice session but has NO playable audio, so no triangle.
        var svc = NewService();
        await svc.StoreSpokenAsync("sid-1", "a spoken summary", "the reply");
        Assert.True(svc.IsVoiceSession("sid-1"));
        Assert.False(svc.HasVoice("sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds());
    }

    [Fact]
    public async Task StoreSpokenAsync_WithEmptySpoken_DoesNotMarkReady()
    {
        var svc = NewService();
        await svc.StoreSpokenAsync("sid-1", "   ", "the reply");
        Assert.False(svc.HasVoice("sid-1"));
    }

    [Fact]
    public void ReadyAudio_PersistsAndReloadsAcrossRestart()
    {
        // A successful synthesis is durable: a fresh service over the same persist path reloads the
        // ready audio, so the triangle/playability survives a gateway restart and a tap still plays.
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            var audio = new byte[] { 1, 2, 3, 4, 5 };
            svc.StoreReadyAudioForTest("sid-1", "spoken text", "reply text", audio);
            Assert.True(svc.HasVoice("sid-1"));

            // Simulate a gateway restart: a brand-new service over the same path.
            var reloaded = ServiceAt(persistPath);
            Assert.True(reloaded.HasVoice("sid-1"));
            Assert.Contains("sid-1", reloaded.ReadySessionIds());
            var got = reloaded.GetAudio("sid-1");
            Assert.NotNull(got);
            Assert.Equal(audio, got);
            var ready = reloaded.Get("sid-1");
            Assert.NotNull(ready);
            Assert.Equal("spoken text", ready.Spoken);
            Assert.Equal("reply text", ready.Reply);
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void OnSessionWorking_DeletesDurableAudio()
    {
        // A new turn drops the stale audio from disk too, so a 5s-stale list row cannot point at
        // audio that no longer exists (which would 404 on /audio).
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 9, 9, 9 });
            Assert.True(svc.HasVoice("sid-1"));

            svc.OnSessionWorking("sid-1");
            Assert.False(svc.HasVoice("sid-1"));

            // A restart must NOT resurrect the dropped audio.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.HasVoice("sid-1"));
        }
        finally { Cleanup(persistPath); }
    }

    // ---------- Turn voice off / Unmark (issue #859) ----------

    [Fact]
    public void Unmark_AfterMark_RemovesFromVoiceSessionSet()
    {
        // Turning voice off stops the session being a voice session, so the turn-end watcher and the
        // background sweep (both gate on IsVoiceSession / VoiceSessionIds) skip it - no more per-turn
        // Opus + text-to-speech spend.
        var svc = NewService();
        svc.Mark("sid-1");
        svc.Mark("sid-2");
        Assert.True(svc.IsVoiceSession("sid-1"));

        svc.Unmark("sid-1");

        Assert.False(svc.IsVoiceSession("sid-1"));
        Assert.DoesNotContain("sid-1", svc.VoiceSessionIds());
        // Independent per session: a second voice session is unaffected.
        Assert.True(svc.IsVoiceSession("sid-2"));
        Assert.Contains("sid-2", svc.VoiceSessionIds());
    }

    [Fact]
    public void Unmark_DropsTheReadyClip()
    {
        // After unmark, GET /wingman/voice/ready (ReadySessionIds) must no longer list the session,
        // so the roster/phone stop offering a stale clip.
        var svc = NewService();
        svc.Mark("sid-1");
        svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 1, 2, 3 });
        Assert.True(svc.HasVoice("sid-1"));

        svc.Unmark("sid-1");

        Assert.False(svc.HasVoice("sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds());
    }

    [Fact]
    public void Unmark_PersistsAcrossRestart()
    {
        // The removal is durable: a gateway restart must NOT bring the session back as a voice
        // session (otherwise turn-end re-narration would resume on its own after a restart).
        var persistPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = ServiceAt(persistPath);
            svc.Mark("sid-1");
            svc.StoreReadyAudioForTest("sid-1", "spoken", "reply", new byte[] { 7, 7, 7 });
            Assert.True(svc.IsVoiceSession("sid-1"));

            svc.Unmark("sid-1");

            // Simulate a gateway restart over the same persist path.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.IsVoiceSession("sid-1"));
            Assert.DoesNotContain("sid-1", reloaded.VoiceSessionIds());
            Assert.False(reloaded.HasVoice("sid-1")); // and the durable clip is gone too
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void Unmark_UnknownSession_IsNoOp()
    {
        // Idempotent: unmarking a session that was never a voice session does nothing and does not throw.
        var svc = NewService();
        svc.Unmark("never-marked");
        Assert.False(svc.IsVoiceSession("never-marked"));
    }
}
