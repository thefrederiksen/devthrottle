using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Verifies Session.UpdateClaudeSessionPointer (the target of the POST /claude-hook endpoint):
/// it updates the Claude session id and transcript path on a non-blank report, and ignores
/// blank values so a partial hook payload never clears a good pointer.
/// </summary>
public sealed class SessionClaudePointerTests
{
    [Fact]
    public void UpdateClaudeSessionPointer_UpdatesOnNonBlank_IgnoresBlank()
    {
        using var s = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new NullBackend(),
            claudeSessionId: "claude-orig",
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);

        Assert.Equal("claude-orig", s.ClaudeSessionId);
        Assert.Null(s.ClaudeTranscriptPath);

        // A /clear hook reports the new id + transcript file.
        s.UpdateClaudeSessionPointer("claude-new", @"C:\proj\new.jsonl", "clear");
        Assert.Equal("claude-new", s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);

        // A payload missing fields must not wipe the good pointer.
        s.UpdateClaudeSessionPointer(null, null, "startup");
        Assert.Equal("claude-new", s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);

        s.UpdateClaudeSessionPointer("   ", "   ", "x");
        Assert.Equal("claude-new", s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);
    }

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
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
        public void Dispose() { }
    }
}
