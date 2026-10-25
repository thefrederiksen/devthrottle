using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE OWNER'S WORD IS FINAL, THROUGH THE REAL CREATE (the owner, 25 October 2026: "Yes, I want to be God everywhere.").
/// A session starts on the Studio backend, whose start runs no process, with the machine's settings pointed at a scratch
/// home: the refusals in the files its agent reads are ask rules before the agent starts, and the session carries what
/// was changed.
/// </summary>
public sealed class DenyRulesBecomeAskAtLaunchTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-deny-launch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    /// <summary>OpenCode, because its create adds only environment variables: nothing else on the machine is touched.</summary>
    private sealed class OpenCodeAgent : IAgent
    {
        private readonly RawCliAgent _inner;
        public OpenCodeAgent(string executable) => _inner = new RawCliAgent(executable);
        public AgentKind Kind => AgentKind.OpenCode;
        public string ExecutablePath => _inner.ExecutablePath;
        public bool SupportsPreassignedSessionId => _inner.SupportsPreassignedSessionId;
        public bool SupportsStudioMode => _inner.SupportsStudioMode;
        public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode)
            => _inner.BuildLaunchSpec(userArgs, resumeSessionId, studioMode);
    }

    private string Agent()
    {
        var exe = Path.Combine(_scratch, "cc-test-agent.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "");
        return exe;
    }

    [Fact]
    public void CreateSession_TheFoldersDenyRules_AreAskRulesBeforeTheAgentStarts_AndTheSessionSaysSo()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_scratch, "repo")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_scratch, "home")).FullName;
        var file = Path.Combine(repo, "opencode.json");
        File.WriteAllText(file, "{\"permission\": {\"bash\": {\"rm *\": \"deny\"}}}");
        using var manager = new SessionManager(new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 })
        {
            MachineSettingsHome = home,
            MachineSettingsEnvironment = _ => null,
        };
        string? atLaunch = null;

        var session = manager.CreateSession(repo, new OpenCodeAgent(Agent()), userArgs: null, SessionBackendType.Studio,
            resumeSessionId: null, beforeLaunch: _ => atLaunch = File.ReadAllText(file));

        Assert.NotNull(atLaunch);
        Assert.Contains("\"ask\"", atLaunch);
        Assert.DoesNotContain("\"deny\"", atLaunch);
        var entry = Assert.Single(session.FolderDenyRules);
        Assert.Equal(file, entry.File);
        Assert.True(entry.Rewritten);
        Assert.Equal(new[] { "permission.bash.rm *" }, entry.Rules);
    }
}
