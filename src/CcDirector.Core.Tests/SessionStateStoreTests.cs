using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

[Collection("ProcessWideState")]
public class SessionStateStoreTests
{
    [Fact]
    public void SaveAndLoad_SingleSession_RoundTrips()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-claude-session-123",
                CustomName = "Test Session",
                CustomColor = "#FF0000",
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Save
            store.Save(new[] { session });

            // Verify file exists and has content
            Assert.True(File.Exists(tempFile), "File should exist after save");
            var json = File.ReadAllText(tempFile);
            Assert.Contains("test-claude-session-123", json);

            // Load
            var result = store.Load();

            // Verify
            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Equal(session.Id, result.Sessions[0].Id);
            Assert.Equal("test-claude-session-123", result.Sessions[0].ClaudeSessionId);
            Assert.Equal("Test Session", result.Sessions[0].CustomName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyList()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, "[]");
            var store = new SessionStateStore(tempFile);

            var result = store.Load();

            Assert.True(result.Success);
            Assert.Empty(result.Sessions);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyList()
    {
        var store = new SessionStateStore(@"C:\nonexistent\path\sessions.json");

        var result = store.Load();

        Assert.True(result.Success);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void PersistedSession_PendingPromptText_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-abc",
                PendingPromptText = "fix the login bug in auth.cs",
                ActivityState = ActivityState.WaitingForInput,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Act
            store.Save(new[] { session });
            var result = store.Load();

            // Assert
            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Equal("fix the login bug in auth.cs", result.Sessions[0].PendingPromptText);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_NullPendingPromptText_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-xyz",
                PendingPromptText = null,
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Act
            store.Save(new[] { session });
            var result = store.Load();

            // Assert
            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Null(result.Sessions[0].PendingPromptText);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_QueuedPrompts_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);
            var queueItem1Id = Guid.NewGuid();
            var queueItem2Id = Guid.NewGuid();

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-queue",
                ActivityState = ActivityState.WaitingForInput,
                CreatedAt = DateTimeOffset.UtcNow,
                QueuedPrompts = new List<PersistedPromptQueueItem>
                {
                    new() { Id = queueItem1Id, Text = "fix the login bug", CreatedAt = DateTimeOffset.UtcNow },
                    new() { Id = queueItem2Id, Text = "add unit tests", CreatedAt = DateTimeOffset.UtcNow }
                }
            };

            // Act
            store.Save(new[] { session });
            var result = store.Load();

            // Assert
            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.NotNull(result.Sessions[0].QueuedPrompts);
            Assert.Equal(2, result.Sessions[0].QueuedPrompts!.Count);
            Assert.Equal("fix the login bug", result.Sessions[0].QueuedPrompts![0].Text);
            Assert.Equal(queueItem1Id, result.Sessions[0].QueuedPrompts![0].Id);
            Assert.Equal("add unit tests", result.Sessions[0].QueuedPrompts![1].Text);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PersistedSession_NullQueuedPrompts_BackwardCompatible()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            // Simulate old JSON without QueuedPrompts field
            File.WriteAllText(tempFile, """
            [{
                "Id": "11111111-1111-1111-1111-111111111111",
                "RepoPath": "C:\\test\\repo",
                "WorkingDirectory": "C:\\test\\repo",
                "ActivityState": "Idle",
                "CreatedAt": "2024-01-01T00:00:00+00:00"
            }]
            """);

            var store = new SessionStateStore(tempFile);
            var result = store.Load();

            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Null(result.Sessions[0].QueuedPrompts);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Session_RestoreConstructor_SetsPendingPromptText()
    {
        // Arrange
        var backend = new StubBackend();
        var id = Guid.NewGuid();

        // Act
        var session = new Session(
            id,
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-123",
            activityState: ActivityState.WaitingForInput,
            createdAt: DateTimeOffset.UtcNow,
            customName: "My Session",
            customColor: "#0000FF",
            pendingPromptText: "implement the feature");

        // Assert
        Assert.Equal("implement the feature", session.PendingPromptText);
        Assert.Equal("My Session", session.CustomName);
        Assert.Equal("#0000FF", session.CustomColor);
        Assert.Equal("claude-123", session.ClaudeSessionId);

        session.Dispose();
    }

    [Fact]
    public void Session_RestoreConstructor_NullPendingPromptText_DefaultsToNull()
    {
        // Arrange
        var backend = new StubBackend();

        // Act — omit pendingPromptText (defaults to null)
        var session = new Session(
            Guid.NewGuid(),
            repoPath: @"C:\test\repo",
            workingDirectory: @"C:\test\repo",
            claudeArgs: null,
            backend: backend,
            claudeSessionId: "claude-456",
            activityState: ActivityState.Idle,
            createdAt: DateTimeOffset.UtcNow,
            customName: null,
            customColor: null);

        // Assert
        Assert.Null(session.PendingPromptText);

        session.Dispose();
    }

    /// <summary>Minimal backend stub for Session constructor tests.</summary>
    private sealed class StubBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Stub";
        public bool IsRunning => false;
        public bool HasExited => true;
        public CircularTerminalBuffer? Buffer => null;

        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

        public void Dispose()
        {
            // Suppress unused warnings
            _ = StatusChanged;
            _ = ProcessExited;
        }
    }
}
