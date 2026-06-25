using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Proves Session.IsAlternateScreen reflects the live terminal mode, which the Control API
/// exposes on SessionDto so a caller can classify a session as full screen (alternate screen)
/// versus normal terminal buffer. Feeds the actual enter/leave sequences through the same path
/// the real backend uses (the terminal buffer's OnBytesWritten event -> the server-side parser).
/// </summary>
public sealed class SessionAlternateScreenTests
{
    [Fact]
    public void IsAlternateScreen_TracksEnterAndLeaveSequences()
    {
        var backend = new BufferBackend();
        using var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-test",
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);
        s.MarkRunning();

        // A normal-buffer agent: no alternate screen.
        Assert.False(s.IsAlternateScreen);

        // Agent enters the alternate screen (full screen mode): ESC [ ? 1049 h.
        backend.Buffer!.Write(Encoding.ASCII.GetBytes("\x1b[?1049h"));
        Assert.True(s.IsAlternateScreen);

        // Agent leaves the alternate screen: ESC [ ? 1049 l.
        backend.Buffer!.Write(Encoding.ASCII.GetBytes("\x1b[?1049l"));
        Assert.False(s.IsAlternateScreen);
    }

    /// <summary>Minimal backend with a real terminal buffer so the session's server-side parser
    /// is initialized and fed; only the buffer is exercised here.</summary>
    private sealed class BufferBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(64 * 1024);

        public int ProcessId => 1234;
        public string Status => "Buffered";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() => Buffer?.Dispose();
    }
}
