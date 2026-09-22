using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// TerminalVerificationExhausted lets the screen thread skip building the terminal text for a
/// verification that would return at once.
/// </summary>
public sealed class SessionTerminalVerificationExhaustedTests
{
    [Fact]
    public void TerminalVerificationExhausted_NewSession_IsFalse()
    {
        using var s = NewSession();
        Assert.False(s.TerminalVerificationExhausted);
    }

    [Fact]
    public void TerminalVerificationExhausted_AfterMarkAsPreVerified_IsTrue()
    {
        using var s = NewSession();
        s.MarkAsPreVerified();
        Assert.True(s.TerminalVerificationExhausted);
    }

    private static Session NewSession() => new Session(
        Guid.NewGuid(),
        repoPath: @"C:	estepo",
        workingDirectory: @"C:	estepo",
        claudeArgs: null,
        backend: new BufferBackend(),
        claudeSessionId: "claude-test",
        activityState: ActivityState.Working,
        createdAt: DateTimeOffset.UtcNow,
        customName: null,
        customColor: null);

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
