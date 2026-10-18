using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for Claude session ID ↔ Director session mapping:
/// RegisterClaudeSession, RelinkClaudeSession, GetSessionByClaudeId.
/// </summary>
[Collection("ProcessWideState")]
public class ClaudeSessionMappingTests : IDisposable
{
    private readonly SessionManager _manager;

    public ClaudeSessionMappingTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void GetSessionByClaudeId_Unknown_ReturnsNull()
    {
        Assert.Null(_manager.GetSessionByClaudeId("nonexistent-id"));
    }

    [Fact]
    public void RelinkClaudeSession_NonExistentSession_DoesNotThrow()
    {
        _manager.RelinkClaudeSession(Guid.NewGuid(), "some-id");
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
