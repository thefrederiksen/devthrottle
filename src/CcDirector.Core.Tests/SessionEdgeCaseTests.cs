using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Edge case and error handling tests for sessions: invalid operations,
/// concurrent access, disposed sessions, etc.
/// </summary>
[Collection("ProcessWideState")]
public class SessionEdgeCaseTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionEdgeCaseTests()
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
    public void CreateSession_InvalidPath_ThrowsDirectoryNotFoundException()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateSession(@"Z:\nonexistent\path\99999"));
    }

    [Fact]
    public void CreateSession_NullPath_ThrowsDirectoryNotFoundException()
    {
        // Path.GetTempPath() exists, but a made-up path shouldn't
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateSession(@"X:\this\does\not\exist"));
    }

    [WindowsOnlyFact("the embedded terminal type is Windows-only, so off Windows the product refuses with PlatformNotSupportedException before the guard under test is reached")]
    public void CreateSession_EmbeddedType_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(
            () => _manager.CreateSession(Path.GetTempPath(), null, SessionBackendType.Embedded));
    }

    [Fact]
    public void CreateSession_InvalidBackendType_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _manager.CreateSession(Path.GetTempPath(), null, (SessionBackendType)999));
    }

    [Fact]
    public void CreatePipeModeSession_InvalidPath_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreatePipeModeSession(@"Z:\nonexistent\path"));
    }

    [Fact]
    public void CreateEmbeddedSession_InvalidPath_Throws()
    {
        // Path validation happens before the backend is used, so a stub that throws on use is fine
        var stubBackend = new StubSessionBackend();
        Assert.Throws<DirectoryNotFoundException>(
            () => _manager.CreateEmbeddedSession(@"Z:\nonexistent\path", null, stubBackend));
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
