using CcDirector.AgentBrain;
using CcDirector.Core.Agents;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.HostedAgent.Tests;

public class HostedAgentTests : IDisposable
{
    private readonly string _workDir;

    public HostedAgentTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "hosted-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private HostedAgentOptions FastOptions() => new()
    {
        WorkingDirectory = _workDir,
        QuietSeconds = 0.0,
        QuietTimeoutSeconds = 2.0,
        StartTimeoutSeconds = 2.0,
        AskTimeoutSeconds = 2.0,
        ClearTimeoutSeconds = 1.0,
        PollIntervalSeconds = 0.01,
        ReplyStableSeconds = 0.05,
        Log = _ => { },
    };

    private static SessionUsageDto UsageOf(long context) => new()
    {
        ContextTokens = context,
        Turns = new List<TurnUsageDto> { new() { Index = 1 } },
    };

    // -------------------------------------------------------------- Start

    [Fact]
    public async Task StartAsync_SpawnsViaDriverLaunchSpec()
    {
        var backend = new FakeBackend();
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => backend);

        await agent.StartAsync();

        Assert.NotNull(agent.SessionId);
        Assert.Single(driver.IssuedSessionIds);
        Assert.Equal(driver.IssuedSessionIds[0], agent.SessionId);
        Assert.NotNull(backend.StartedArgs);
        Assert.Contains($"--session-id {agent.SessionId}", backend.StartedArgs);
        Assert.Equal(_workDir, backend.StartedWorkingDir);
        Assert.Equal(4242, agent.ProcessId);
    }

    [Fact]
    public async Task StartAsync_MissingWorkingDirectory_Throws()
    {
        var options = FastOptions();
        options.WorkingDirectory = Path.Combine(_workDir, "does-not-exist");
        var agent = new HostedAgent(options, new FakeDriver(), () => new FakeBackend());

        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => agent.StartAsync());
        Assert.Contains("WorkingDirectory does not exist", ex.Message);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_Throws()
    {
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () => new FakeBackend());
        await agent.StartAsync();
        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => agent.StartAsync());
        Assert.Contains("already started", ex.Message);
    }

    [Fact]
    public void For_UnsupportedAgentKinds_FailLoud()
    {
        foreach (var kind in new[] { AgentKind.Codex, AgentKind.Gemini, AgentKind.Pi, AgentKind.OpenCode })
        {
            var ex = Assert.Throws<NotSupportedException>(() => HostedAgent.For(kind, FastOptions()));
            Assert.Contains("No agent driver exists", ex.Message);
        }
    }

    [Fact]
    public void For_ClaudeCode_UsesClaudeDriver()
    {
        using var agent = HostedAgent.For(AgentKind.ClaudeCode, FastOptions());
        Assert.Equal(AgentKind.ClaudeCode, agent.Driver.Kind);
        Assert.True(agent.Driver.Capabilities.HasFlag(Core.Drivers.DriverCapabilities.Cancel));
    }

    // ---------------------------------------------------------------- Ask

    [Fact]
    public async Task AskAsync_ReturnsReplyFromDriverTranscript()
    {
        var backend = new FakeBackend();
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => backend);
        await agent.StartAsync();
        var sid = agent.SessionId;
        Assert.NotNull(sid);

        driver.OnSubmit = prompt =>
        {
            driver.AddTextReply(sid, prompt, "the full answer");
            driver.Usage[sid] = UsageOf(62000);
        };

        var result = await agent.AskAsync("what is up?");

        Assert.Equal("the full answer", result.Text);
        Assert.Equal(62000, result.ContextTokens);
        Assert.Contains("what is up?", driver.Submits);
    }

    [Fact]
    public async Task AskAsync_BackendDiesMidTurn_Throws()
    {
        var backend = new FakeBackend();
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => backend);
        await agent.StartAsync();

        driver.OnSubmit = _ => backend.SimulateExit();

        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => agent.AskAsync("q"));
        Assert.Contains("exited", ex.Message);
    }

    [Fact]
    public async Task AskAsync_NoReplyWithinTimeout_Throws()
    {
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () => new FakeBackend());
        await agent.StartAsync();

        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => agent.AskAsync("q"));
        Assert.Contains("no reply", ex.Message);
    }

    [Fact]
    public async Task AskAsync_BeforeStart_Throws()
    {
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () => new FakeBackend());
        await Assert.ThrowsAsync<AgentBrainException>(() => agent.AskAsync("q"));
    }

    // -------------------------------------------------------------- Cancel

    [Fact]
    public async Task CancelAsync_RoutesToTheDriver()
    {
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => new FakeBackend());
        await agent.StartAsync();

        await agent.CancelAsync();

        Assert.Equal(1, driver.CancelCount);
    }

    [Fact]
    public async Task CancelAsync_BeforeStart_Throws()
    {
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () => new FakeBackend());
        await Assert.ThrowsAsync<AgentBrainException>(() => agent.CancelAsync());
    }

    // -------------------------------------------------------------- Clear

    [Fact]
    public async Task ClearAsync_TracksTheNewAgentSessionId()
    {
        var backend = new FakeBackend();
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => backend);
        await agent.StartAsync();
        var oldId = agent.SessionId;
        Assert.NotNull(oldId);

        // A week-old transcript sits in the same working dir - must not be picked.
        driver.Transcripts.Add(("ancient", DateTime.UtcNow.AddDays(-7)));
        driver.OnClear = () => driver.Transcripts.Insert(0, ("fresh-id", DateTime.UtcNow));

        var result = await agent.ClearAsync();

        Assert.Equal(oldId, result.OldClaudeSessionId);
        Assert.Equal("fresh-id", result.NewClaudeSessionId);
        Assert.Equal("fresh-id", agent.SessionId);
        Assert.Equal(1, driver.ClearCount);
    }

    [Fact]
    public async Task ClearAsync_OnlyStaleTranscripts_TimesOutAndThrows()
    {
        var driver = new FakeDriver();
        driver.Transcripts.Add(("ancient", DateTime.UtcNow.AddDays(-7)));
        var agent = new HostedAgent(FastOptions(), driver, () => new FakeBackend());
        await agent.StartAsync();

        var ex = await Assert.ThrowsAsync<AgentBrainException>(() => agent.ClearAsync());
        Assert.Contains("no new transcript", ex.Message);
    }

    // ------------------------------------------------------ Restart / Kill

    [Fact]
    public async Task RestartAsync_SpawnsAFreshBackendAndSessionId()
    {
        var spawned = new List<FakeBackend>();
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () =>
        {
            var b = new FakeBackend();
            spawned.Add(b);
            return b;
        });

        await agent.StartAsync();
        var firstId = agent.SessionId;

        await agent.RestartAsync();

        Assert.Equal(2, spawned.Count);
        Assert.True(spawned[0].HasExited);
        Assert.False(spawned[1].HasExited);
        Assert.NotNull(agent.SessionId);
        Assert.NotEqual(firstId, agent.SessionId);
    }

    [Fact]
    public async Task RestartAsync_AfterCrash_Recovers()
    {
        var spawned = new List<FakeBackend>();
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () =>
        {
            var b = new FakeBackend();
            spawned.Add(b);
            return b;
        });

        await agent.StartAsync();
        spawned[0].SimulateExit();

        var health = await agent.GetHealthAsync();
        Assert.False(health.IsAlive);

        await agent.RestartAsync();
        var sid = agent.SessionId;
        Assert.NotNull(sid);
        driver.OnSubmit = p => driver.AddTextReply(sid, p, "RECOVERED");

        var result = await agent.AskAsync("alive?");
        Assert.Equal("RECOVERED", result.Text);
    }

    [Fact]
    public async Task KillAsync_ShutsDownAndForgets()
    {
        var backend = new FakeBackend();
        var agent = new HostedAgent(FastOptions(), new FakeDriver(), () => backend);
        await agent.StartAsync();

        await agent.KillAsync();

        Assert.True(backend.HasExited);
        Assert.Null(agent.SessionId);
        var health = await agent.GetHealthAsync();
        Assert.False(health.IsAlive);
        Assert.Equal("NotStarted", health.Status);
    }

    // -------------------------------------------------------------- Health

    [Fact]
    public async Task GetHealthAsync_AliveSession_MapsUsage()
    {
        var driver = new FakeDriver();
        var agent = new HostedAgent(FastOptions(), driver, () => new FakeBackend());
        await agent.StartAsync();
        var sid = agent.SessionId;
        Assert.NotNull(sid);
        driver.Usage[sid] = UsageOf(61973);

        var health = await agent.GetHealthAsync();

        Assert.True(health.IsAlive);
        Assert.Equal(61973, health.ContextTokens);
        Assert.Equal(1, health.TurnCount);
    }

    // -------------------------------------------- Multiple brains, one process

    [Fact]
    public async Task TwoHostedAgents_InOneProcess_AreIndependent()
    {
        var backendA = new FakeBackend();
        var backendB = new FakeBackend();
        var driverA = new FakeDriver();
        var driverB = new FakeDriver();
        var agentA = new HostedAgent(FastOptions(), driverA, () => backendA);
        var agentB = new HostedAgent(FastOptions(), driverB, () => backendB);

        await agentA.StartAsync();
        await agentB.StartAsync();
        var sidA = agentA.SessionId;
        var sidB = agentB.SessionId;
        Assert.NotNull(sidA);
        Assert.NotNull(sidB);
        Assert.NotEqual(sidA, sidB);

        driverA.OnSubmit = p => driverA.AddTextReply(sidA, p, "answer A");
        driverB.OnSubmit = p => driverB.AddTextReply(sidB, p, "answer B");

        var a = await agentA.AskAsync("qa");
        var b = await agentB.AskAsync("qb");

        Assert.Equal("answer A", a.Text);
        Assert.Equal("answer B", b.Text);
        // Killing one must not touch the other.
        await agentA.KillAsync();
        Assert.True(backendA.HasExited);
        Assert.False(backendB.HasExited);
    }
}
