using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for session lifecycle: create, run, kill, dispose, and state transitions.
/// Uses cmd.exe (Windows) or /bin/sh (Unix) as a lightweight stand-in for claude.exe.
/// </summary>
public class SessionLifecycleTests : IDisposable
{
    private readonly SessionManager _manager;

    public SessionLifecycleTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            // The cmd.exe / sh stand-in must stay alive for the lifecycle assertions
            // (status Running after create, then killed). Production's DefaultClaudeArgs is
            // now empty (issue #391), under which the bare stand-in shell exits immediately,
            // so pin an arg here that keeps it running - this test exercises kill/lifecycle,
            // not the production default.
            DefaultClaudeArgs = "--dangerously-skip-permissions",
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options);
    }

    [Fact]
    public void CreateSession_NewSession_StatusIsRunning()
    {
        var session = _manager.CreateSession(Path.GetTempPath());

        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.True(session.ProcessId > 0);
        Assert.Equal(SessionBackendType.ConPty, session.BackendType);
    }

    [Fact]
    public void CreateSession_WithCustomArgs_StoresArgs()
    {
        var session = _manager.CreateSession(Path.GetTempPath(), "--test-arg");

        Assert.Equal("--test-arg", session.ClaudeArgs);
    }

    [Fact]
    public void CreateSession_DefaultProperties_AreCorrect()
    {
        var session = _manager.CreateSession(Path.GetTempPath());

        // New sessions get a preassigned ClaudeSessionId via --session-id flag
        Assert.NotNull(session.ClaudeSessionId);
        Assert.True(Guid.TryParse(session.ClaudeSessionId, out _), "ClaudeSessionId should be a valid GUID");
        Assert.Null(session.CustomName);
        Assert.Null(session.CustomColor);
        Assert.Null(session.PendingPromptText);
        Assert.Null(session.ClaudeMetadata);
        Assert.Equal(0, session.SortOrder);
        Assert.Null(session.ExpectedFirstPrompt);
        Assert.Null(session.VerifiedFirstPrompt);
    }

    [Fact]
    public async Task KillSession_RunningSession_TransitionsToExited()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Equal(SessionStatus.Running, session.Status);

        await _manager.KillSessionAsync(session.Id);

        // Allow time for exit
        for (int i = 0; i < 20 && session.Status != SessionStatus.Exited; i++)
            await Task.Delay(100);

        Assert.True(session.Status is SessionStatus.Exiting or SessionStatus.Exited);
    }

    [Fact]
    public async Task KillSession_SetsActivityStateToExited()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Equal(ActivityState.Working, session.ActivityState);

        await _manager.KillSessionAsync(session.Id);

        for (int i = 0; i < 20 && session.ActivityState != ActivityState.Exited; i++)
            await Task.Delay(100);

        Assert.Equal(ActivityState.Exited, session.ActivityState);
    }

    [Fact]
    public async Task KillSession_AlreadyExited_DoesNotThrow()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        await _manager.KillSessionAsync(session.Id);

        for (int i = 0; i < 20 && session.Status != SessionStatus.Exited; i++)
            await Task.Delay(100);

        // Killing again should not throw
        await _manager.KillSessionAsync(session.Id);
    }

    [Fact]
    public void CreateMultipleSessions_AllTracked()
    {
        var s1 = _manager.CreateSession(Path.GetTempPath());
        var s2 = _manager.CreateSession(Path.GetTempPath());
        var s3 = _manager.CreateSession(Path.GetTempPath());

        var sessions = _manager.ListSessions();
        Assert.Equal(3, sessions.Count);
        Assert.Contains(sessions, s => s.Id == s1.Id);
        Assert.Contains(sessions, s => s.Id == s2.Id);
        Assert.Contains(sessions, s => s.Id == s3.Id);
    }

    [Fact]
    public void RemoveSession_RemovesFromTracking()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.Single(_manager.ListSessions());

        _manager.RemoveSession(session.Id);

        Assert.Empty(_manager.ListSessions());
        Assert.Null(_manager.GetSession(session.Id));
    }

    [Fact]
    public void RemoveSession_WithClaudeSessionId_ClearsMapping()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        session.ClaudeSessionId = "test-claude-id";
        _manager.RegisterClaudeSession("test-claude-id", session.Id);

        Assert.NotNull(_manager.GetSessionByClaudeId("test-claude-id"));

        _manager.RemoveSession(session.Id);

        Assert.Null(_manager.GetSessionByClaudeId("test-claude-id"));
    }

    [Fact]
    public void RemoveSession_NonExistentId_DoesNotThrow()
    {
        // Should silently do nothing
        _manager.RemoveSession(Guid.NewGuid());
    }

    [Fact]
    public async Task KillAllSessions_KillsAllRunning()
    {
        var s1 = _manager.CreateSession(Path.GetTempPath());
        var s2 = _manager.CreateSession(Path.GetTempPath());

        Assert.Equal(SessionStatus.Running, s1.Status);
        Assert.Equal(SessionStatus.Running, s2.Status);

        await _manager.KillAllSessionsAsync();

        // Wait for exits
        for (int i = 0; i < 20; i++)
        {
            if (s1.Status == SessionStatus.Exited && s2.Status == SessionStatus.Exited)
                break;
            await Task.Delay(100);
        }

        Assert.True(s1.Status is SessionStatus.Exiting or SessionStatus.Exited);
        Assert.True(s2.Status is SessionStatus.Exiting or SessionStatus.Exited);
    }

    [Fact]
    public async Task KillAllSessions_NoSessions_DoesNotThrow()
    {
        await _manager.KillAllSessionsAsync();
    }

    [Fact]
    public void Session_CustomProperties_Settable()
    {
        var session = _manager.CreateSession(Path.GetTempPath());

        session.CustomName = "My Test";
        session.CustomColor = "#FF0000";
        session.PendingPromptText = "some prompt";
        session.SortOrder = 5;
        session.ExpectedFirstPrompt = "hello world";

        Assert.Equal("My Test", session.CustomName);
        Assert.Equal("#FF0000", session.CustomColor);
        Assert.Equal("some prompt", session.PendingPromptText);
        Assert.Equal(5, session.SortOrder);
        Assert.Equal("hello world", session.ExpectedFirstPrompt);
    }

    [Fact]
    public void Dispose_DisposesAllSessions()
    {
        var s1 = _manager.CreateSession(Path.GetTempPath());
        var s2 = _manager.CreateSession(Path.GetTempPath());

        _manager.Dispose();

        Assert.Empty(_manager.ListSessions());
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
