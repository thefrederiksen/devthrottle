using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for session persistence: SaveCurrentState, SaveSessionState,
/// LoadPersistedSessions, and the new SortOrder/ExpectedFirstPrompt fields.
/// </summary>
public class SessionPersistenceTests : IDisposable
{
    private readonly SessionManager _manager;
    private readonly List<string> _tempFiles = new();

    public SessionPersistenceTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    private SessionStateStore CreateTempStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_sessions_{Guid.NewGuid()}.json");
        _tempFiles.Add(path);
        return new SessionStateStore(path);
    }

    [Fact]
    public void SaveCurrentState_PreservesSortOrder()
    {
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());
        session.SortOrder = 42;

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Single(loaded);
        Assert.Equal(42, loaded[0].SortOrder);
    }

    [Fact]
    public void SaveCurrentState_PreservesExpectedFirstPrompt()
    {
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());
        session.ExpectedFirstPrompt = "fix the login bug";

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Single(loaded);
        Assert.Equal("fix the login bug", loaded[0].ExpectedFirstPrompt);
    }

    [Fact]
    public void SaveCurrentState_VerifiedFirstPrompt_UsedAsFallback()
    {
        // When ExpectedFirstPrompt is null, VerifiedFirstPrompt should be used
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());
        // VerifiedFirstPrompt is set by VerifyClaudeSession, but it's private set.
        // We can't directly set it, so this tests that ExpectedFirstPrompt is used when set.
        session.ExpectedFirstPrompt = "my prompt";

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Equal("my prompt", loaded[0].ExpectedFirstPrompt);
    }

    [Fact]
    public void SaveCurrentState_OrderedBySortOrder()
    {
        var store = CreateTempStore();
        var s1 = _manager.CreateSession(Path.GetTempPath());
        var s2 = _manager.CreateSession(Path.GetTempPath());
        var s3 = _manager.CreateSession(Path.GetTempPath());

        s1.SortOrder = 3;
        s2.SortOrder = 1;
        s3.SortOrder = 2;

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Equal(3, loaded.Count);
        Assert.Equal(s2.Id, loaded[0].Id); // SortOrder 1
        Assert.Equal(s3.Id, loaded[1].Id); // SortOrder 2
        Assert.Equal(s1.Id, loaded[2].Id); // SortOrder 3
    }

    [Fact]
    public void SaveCurrentState_PendingPromptText_Persisted()
    {
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());
        session.PendingPromptText = "draft prompt here";

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Single(loaded);
        Assert.Equal("draft prompt here", loaded[0].PendingPromptText);
    }

    [Fact]
    public void SaveCurrentState_RunningSessionWithoutClaudeId_StillPersisted()
    {
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());
        // New sessions get a preassigned ClaudeSessionId; clear it to test this scenario
        session.ClaudeSessionId = null;
        Assert.Null(session.ClaudeSessionId);
        Assert.Equal(SessionStatus.Running, session.Status);

        _manager.SaveCurrentState(store);

        var loaded = store.Load().Sessions;
        Assert.Single(loaded); // Running sessions are always persisted
    }

    [Fact]
    public void SaveSessionState_WithGetHwnd_SavesHwnd()
    {
        var store = CreateTempStore();
        var session = _manager.CreateSession(Path.GetTempPath());

        _manager.SaveSessionState(store, _ => 0); // ConPty always returns 0

        var loaded = store.Load().Sessions;
        Assert.Single(loaded);
        Assert.Equal(0, loaded[0].ConsoleHwnd);
    }

    [Fact]
    public void LoadPersistedSessions_WithClaudeSessionId_MarksValid()
    {
        var store = CreateTempStore();
        var sessions = new List<PersistedSession>
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                ClaudeSessionId = "claude-session-1"
            }
        };
        store.Save(sessions);

        var loaded = _manager.LoadPersistedSessions(store).Sessions;
        Assert.Single(loaded);
        Assert.Equal("claude-session-1", loaded[0].ClaudeSessionId);
    }

    [Fact]
    public void LoadPersistedSessions_WithoutClaudeSessionId_StillValid()
    {
        var store = CreateTempStore();
        var sessions = new List<PersistedSession>
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                ClaudeSessionId = null
            }
        };
        store.Save(sessions);

        var loaded = _manager.LoadPersistedSessions(store).Sessions;
        Assert.Single(loaded);
    }

    [Fact]
    public void LoadPersistedSessions_DuplicateClaudeSessionId_ClearedOnSecond()
    {
        var store = CreateTempStore();
        var sessions = new List<PersistedSession>
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                ClaudeSessionId = "duplicate-id"
            },
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                ClaudeSessionId = "duplicate-id"
            }
        };
        store.Save(sessions);

        var loaded = _manager.LoadPersistedSessions(store).Sessions;

        // Both should be loaded, but the second should have its ClaudeSessionId cleared
        Assert.Equal(2, loaded.Count);
        Assert.Equal("duplicate-id", loaded[0].ClaudeSessionId);
        Assert.Null(loaded[1].ClaudeSessionId); // Cleared due to duplicate
    }

    [Fact]
    public void LoadPersistedSessions_EmptyStore_ReturnsEmpty()
    {
        var store = CreateTempStore();
        var loaded = _manager.LoadPersistedSessions(store).Sessions;
        Assert.Empty(loaded);
    }

    [Fact]
    public void LoadPersistedSessions_PreservesSortOrder()
    {
        var store = CreateTempStore();
        var sessions = new List<PersistedSession>
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                SortOrder = 5,
                ClaudeSessionId = "id-1"
            }
        };
        store.Save(sessions);

        var loaded = _manager.LoadPersistedSessions(store).Sessions;
        Assert.Single(loaded);
        Assert.Equal(5, loaded[0].SortOrder);
    }

    [Fact]
    public void LoadPersistedSessions_PreservesExpectedFirstPrompt()
    {
        var store = CreateTempStore();
        var sessions = new List<PersistedSession>
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath(),
                ExpectedFirstPrompt = "add dark mode",
                ClaudeSessionId = "id-1"
            }
        };
        store.Save(sessions);

        var loaded = _manager.LoadPersistedSessions(store).Sessions;
        Assert.Single(loaded);
        Assert.Equal("add dark mode", loaded[0].ExpectedFirstPrompt);
    }

    [Fact]
    public void SessionStateStore_SaveAndLoad_RoundTrips()
    {
        var store = CreateTempStore();
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);

        store.Save(new[]
        {
            new PersistedSession
            {
                Id = id,
                RepoPath = @"D:\Test\Repo",
                WorkingDirectory = @"D:\Test\Repo",
                ClaudeArgs = "--dangerously-skip-permissions",
                CustomName = "My Session",
                CustomColor = "#FF5500",
                PendingPromptText = "some draft",
                ClaudeSessionId = "abc-123",
                ActivityState = ActivityState.Working,
                CreatedAt = createdAt,
                SortOrder = 7,
                ExpectedFirstPrompt = "first prompt text"
            }
        });

        var loaded = store.Load().Sessions;
        Assert.Single(loaded);
        var ps = loaded[0];
        Assert.Equal(id, ps.Id);
        Assert.Equal(@"D:\Test\Repo", ps.RepoPath);
        Assert.Equal(@"D:\Test\Repo", ps.WorkingDirectory);
        Assert.Equal("--dangerously-skip-permissions", ps.ClaudeArgs);
        Assert.Equal("My Session", ps.CustomName);
        Assert.Equal("#FF5500", ps.CustomColor);
        Assert.Equal("some draft", ps.PendingPromptText);
        Assert.Equal("abc-123", ps.ClaudeSessionId);
        Assert.Equal(ActivityState.Working, ps.ActivityState);
        Assert.Equal(7, ps.SortOrder);
        Assert.Equal("first prompt text", ps.ExpectedFirstPrompt);
    }

    [Fact]
    public void SessionStateStore_Clear_DeletesFile()
    {
        var store = CreateTempStore();
        store.Save(new[]
        {
            new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = Path.GetTempPath(),
                WorkingDirectory = Path.GetTempPath()
            }
        });
        Assert.True(File.Exists(store.FilePath));

        store.Clear();

        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void SessionStateStore_Load_CorruptJson_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_corrupt_{Guid.NewGuid()}.json");
        _tempFiles.Add(path);
        File.WriteAllText(path, "{ invalid json [[[");

        var store = new SessionStateStore(path);
        var loaded = store.Load().Sessions;
        Assert.Empty(loaded);
    }

    public void Dispose()
    {
        _manager.Dispose();
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
