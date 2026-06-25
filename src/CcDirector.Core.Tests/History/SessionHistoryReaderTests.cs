using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.History;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.History;

public sealed class SessionHistoryReaderTests
{
    [Fact]
    public void Read_ClaudeSession_UsesPointer_ReturnsParsedHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hist-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, new[]
        {
            """{"type":"user","message":{"role":"user","content":"hello"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi there"}]}}""",
        });

        var session = NewSession(AgentKind.ClaudeCode);
        session.UpdateClaudeSessionPointer("claude-id", path, "startup"); // sets the live pointer

        try
        {
            var history = SessionHistoryReader.Read(session);

            Assert.Equal(2, history.Messages.Count);
            Assert.Equal(ConversationRole.User, history.Messages[0].Role);
            Assert.Equal("hello", history.Messages[0].Parts[0].Text);
            Assert.Equal(ConversationRole.Assistant, history.Messages[1].Role);
            Assert.Equal("hi there", history.Messages[1].Parts[0].Text);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Read_NonClaudeAgent_ReturnsEmpty()
    {
        var session = NewSession(AgentKind.Gemini);
        var history = SessionHistoryReader.Read(session);
        Assert.Empty(history.Messages);
    }

    private static Session NewSession(AgentKind kind) =>
        new(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: new NullBackend(),
            claudeSessionId: null,
            activityState: ActivityState.Working,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null)
        {
            AgentKind = kind,
        };

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
