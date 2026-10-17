using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for session lifecycle: create, run, kill, dispose, and state transitions.
/// Uses cmd.exe (Windows) or /bin/sh (Unix) as a lightweight stand-in for claude.exe.
/// </summary>
[Collection("ProcessWideState")]
public class SessionLifecycleTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionLifecycleTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            // The cmd.exe / sh stand-in must stay alive for the lifecycle assertions
            // (status Running after create, then killed). Production's DefaultClaudeArgs is
            // now empty (issue #391), under which the bare stand-in shell exits immediately,
            // so pin an arg here that keeps it running - this test exercises kill/lifecycle,
            // not the production default.
            DefaultClaudeArgs = "--dangerously-skip-permissions",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void RemoveSession_NonExistentId_DoesNotThrow()
    {
        // Should silently do nothing
        _manager.RemoveSession(Guid.NewGuid());
    }

    [Fact]
    public async Task KillAllSessions_NoSessions_DoesNotThrow()
    {
        await _manager.KillAllSessionsAsync();
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
