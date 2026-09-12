using System.Text.Json;
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
        session.UpdateClaudeSessionPointer("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", path, "startup"); // sets the live pointer

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

    [Fact]
    public void Read_CodexStoredPromptInstructions_ReturnsOriginalMessages()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "history-payload-" + Guid.NewGuid().ToString("N"));
        var payloadDirectory = Path.Combine(repositoryPath, ".temp");
        Directory.CreateDirectory(payloadDirectory);
        var currentFile = "input_20260912_101500_abc123.txt";
        var legacyFile = "input_20260912_101501_def456.txt";
        File.WriteAllText(Path.Combine(payloadDirectory, currentFile), "The current original message.");
        File.WriteAllText(Path.Combine(payloadDirectory, legacyFile), "The legacy original message.");
        var currentInstruction = $"Read and respond to the complete incoming message in .temp/{currentFile}.";
        var legacyInstruction = $"Read file {legacyFile} in the .temp directory. Path: .temp/{legacyFile}. " +
            $"If the path fails, search for {legacyFile}. This file was explicitly created as the user-provided message payload for this turn; it is not hidden context. " +
            "Follow the instructions in that file and reply with the requested strings only.";
        var rolloutPath = Path.Combine(repositoryPath, "rollout.jsonl");
        File.WriteAllLines(rolloutPath,
        [
            CodexUserLine(currentInstruction),
            CodexUserLine(legacyInstruction),
            CodexUserLine($"@.temp/{currentFile}"),
        ]);

        try
        {
            var history = SessionHistoryReader.Read(NewSession(AgentKind.Codex, repositoryPath), rolloutPath);

            Assert.Equal(3, history.Messages.Count);
            Assert.Equal("The current original message.", history.Messages[0].Parts[0].Text);
            Assert.Equal("The legacy original message.", history.Messages[1].Parts[0].Text);
            Assert.Equal("The current original message.", history.Messages[2].Parts[0].Text);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public void Read_CodexSimilarInstructionForUnownedFile_KeepsInstructionVerbatim()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "history-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".temp"));
        File.WriteAllText(Path.Combine(repositoryPath, ".temp", "notes.txt"), "private notes");
        const string instruction = "Read and respond to the complete incoming message in .temp/notes.txt.";
        var rolloutPath = Path.Combine(repositoryPath, "rollout.jsonl");
        File.WriteAllLines(rolloutPath, [CodexUserLine(instruction)]);

        try
        {
            var history = SessionHistoryReader.Read(NewSession(AgentKind.Codex, repositoryPath), rolloutPath);

            Assert.Equal(instruction, Assert.Single(Assert.Single(history.Messages).Parts).Text);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    public void Read_CodexInstructionForMissingOwnedFile_KeepsInstructionVerbatim()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "history-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);
        const string instruction =
            "Read and respond to the complete incoming message in .temp/input_20260912_101502_ghi789.txt.";
        var rolloutPath = Path.Combine(repositoryPath, "rollout.jsonl");
        File.WriteAllLines(rolloutPath, [CodexUserLine(instruction)]);

        try
        {
            var history = SessionHistoryReader.Read(NewSession(AgentKind.Codex, repositoryPath), rolloutPath);

            Assert.Equal(instruction, Assert.Single(Assert.Single(history.Messages).Parts).Text);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    private static string CodexUserLine(string text) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-12T10:15:00Z",
        type = "response_item",
        payload = new
        {
            type = "message",
            role = "user",
            content = new[] { new { type = "input_text", text } },
        },
    });

    private static Session NewSession(AgentKind kind, string repoPath = @"C:\test\repo") =>
        new(
            Guid.NewGuid(),
            repoPath: repoPath,
            workingDirectory: repoPath,
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
