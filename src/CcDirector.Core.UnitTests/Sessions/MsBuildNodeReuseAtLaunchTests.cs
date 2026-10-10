using System.Runtime.InteropServices;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// EVERY SESSION THE DIRECTOR STARTS HAS MSBUILD NODE REUSE TURNED OFF.
///
/// These go through <see cref="SessionManager.CreateSession(string, IAgent, string?, Backends.SessionBackendType, string?, Guid?, string?, string?, Func{Guid, string}?, Guid?, Action{Session}?, PooledWorktree?)"/>
/// and read the variable back out of the process the Director actually started: the agent is a real shell that
/// writes its own environment variable to a file and exits. A dictionary handed to a test would prove nothing
/// about the launch.
///
/// The test process deliberately carries the OPPOSITE value while the session starts, so a pass cannot come from
/// the variable merely being inherited from whoever ran the tests.
/// </summary>
[Collection(MsBuildNodeReuseEnvironmentCollection.Name)]
public sealed class MsBuildNodeReuseAtLaunchTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly List<SessionManager> _managers = new();

    public MsBuildNodeReuseAtLaunchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-nodereuse-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            try { manager.KillAllSessionsAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
            try { manager.Dispose(); } catch { /* best effort */ }
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; } catch { Thread.Sleep(200); }
        }
    }

    /// <summary>An agent that is a shell: it writes the node-reuse variable to <paramref name="outFile"/> and exits.</summary>
    private sealed class EnvEchoAgent : IAgent
    {
        private readonly string _outFile;
        private readonly AgentKind _kind;
        public EnvEchoAgent(string outFile, AgentKind kind) { _outFile = outFile; _kind = kind; }
        public AgentKind Kind => _kind;
        public string ExecutablePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";
        public bool SupportsPreassignedSessionId => false;
        public bool SupportsStudioMode => false;
        public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode) =>
            new(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"/c \"echo [%{MsBuildNodeReuse.DisableEnvVar}%]> \"{_outFile}\"\""
                : $"-c 'printf \"[%s]\" \"${MsBuildNodeReuse.DisableEnvVar}\" > \"{_outFile}\"'", null);
    }

    private SessionManager Manager()
    {
        var manager = new SessionManager(
            new AgentOptions { DefaultBufferSizeBytes = 65536, GracefulShutdownTimeoutSeconds = 2 },
            log: null,
            reservations: new WorktreeReservationStore(Path.Combine(_root, "reservations")));
        _managers.Add(manager);
        return manager;
    }

    private static string ReadWhatTheAgentSaw(string outFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(outFile))
            {
                try
                {
                    var text = File.ReadAllText(outFile).Trim();
                    if (text.EndsWith(']')) return text;
                }
                catch (IOException) { /* the shell is still writing it */ }
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"the agent never wrote {outFile}");
    }

    // The variable is stamped before any per-agent branch, so one agent kind proves the launch for all of them.
    // RawCli is used because the Claude Code and Codex launches install hooks into the user's own home folder.
    [Fact]
    public void CreateSession_StartedProcessHasNodeReuseDisabled_EvenWhenTheDirectorInheritedItOn()
    {
        const AgentKind kind = AgentKind.RawCli;
        var previous = Environment.GetEnvironmentVariable(MsBuildNodeReuse.DisableEnvVar);
        try
        {
            // The Director's own environment says the opposite; the session must still get "1".
            Environment.SetEnvironmentVariable(MsBuildNodeReuse.DisableEnvVar, "0");
            var outFile = Path.Combine(_root, $"env-{kind}.txt");

            Manager().CreateSession(_repo, new EnvEchoAgent(outFile, kind), userArgs: null,
                Backends.SessionBackendType.ConPty, resumeSessionId: null);

            Assert.Equal("[1]", ReadWhatTheAgentSaw(outFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MsBuildNodeReuse.DisableEnvVar, previous);
        }
    }
}

/// <summary>The tests above change a process-wide environment variable, so they never run beside each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MsBuildNodeReuseEnvironmentCollection
{
    public const string Name = "MSBuild node reuse environment";
}
