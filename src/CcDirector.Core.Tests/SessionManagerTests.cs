using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

[Collection("ProcessWideState")]
public class SessionManagerTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path, // cmd.exe on Windows, /bin/sh elsewhere
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void CreateSession_InvalidPath_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateSession(@"C:\nonexistent\path\that\does\not\exist"));
    }

    [Fact]
    public void GetSession_UnknownId_ReturnsNull()
    {
        var result = _manager.GetSession(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void ListSessions_Empty_ReturnsEmpty()
    {
        var sessions = _manager.ListSessions();
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task KillSession_UnknownId_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _manager.KillSessionAsync(Guid.NewGuid()));
    }

    [Fact]
    public void ScanForOrphans_DoesNotThrow()
    {
        // Just verify it doesn't crash
        _manager.ScanForOrphans();
    }

    [Fact]
    public void RemoveSession_UnknownId_DoesNotFireOnSessionRemoved()
    {
        var fired = false;
        _manager.OnSessionRemoved += _ => fired = true;

        _manager.RemoveSession(Guid.NewGuid());

        Assert.False(fired);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
