using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// End-to-end launch test for the OpenCode agent. Proves the real spawn path
/// (ExecutableResolver -> ConPty -> CreateProcess) can start the opencode CLI even
/// though it is installed as a ".cmd" shim that CreateProcess cannot find from the
/// bare command name. Skipped when opencode is not installed so it stays green on
/// machines/CI without it.
/// </summary>
public class OpenCodeLaunchTests
{
    [Fact]
    public async Task CreateSession_OpenCode_SpawnsProcess()
    {
        // Skip cleanly when opencode is not on this machine.
        if (ExecutableResolver.Resolve("opencode") is null)
            return;

        var options = new AgentOptions
        {
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        var manager = new SessionManager(options);
        var agent = new OpenCodeAgent(options);

        Session? session = null;
        try
        {
            session = manager.CreateSession(
                Path.GetTempPath(),
                agent,
                userArgs: null,
                SessionBackendType.ConPty,
                resumeSessionId: null);

            Assert.Equal(AgentKind.OpenCode, session.AgentKind);
            Assert.Equal(SessionStatus.Running, session.Status);
            Assert.True(session.ProcessId > 0, "OpenCode session should have a live process id");
        }
        finally
        {
            if (session != null)
                await manager.KillSessionAsync(session.Id);
        }
    }
}
