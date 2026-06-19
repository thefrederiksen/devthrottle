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
}
