using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for Claude session ID ↔ Director session mapping:
/// RegisterClaudeSession, RelinkClaudeSession, GetSessionByClaudeId.
/// </summary>
public class ClaudeSessionMappingTests : IDisposable
{
    private readonly SessionManager _manager;

    public ClaudeSessionMappingTests()
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
    public void RegisterClaudeSession_MapsCorrectly()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("claude-id-1", session.Id);

        Assert.Equal("claude-id-1", session.ClaudeSessionId);
        Assert.Equal(session, _manager.GetSessionByClaudeId("claude-id-1"));
    }

    [Fact]
    public void RegisterClaudeSession_FiresEvent()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Session? eventSession = null;
        string? eventClaudeId = null;

        _manager.OnClaudeSessionRegistered += (s, id) =>
        {
            eventSession = s;
            eventClaudeId = id;
        };

        _manager.RegisterClaudeSession("claude-id-1", session.Id);

        Assert.Equal(session, eventSession);
        Assert.Equal("claude-id-1", eventClaudeId);
    }

    [Fact]
    public void RegisterClaudeSession_DuplicateId_IgnoresSecondRegistration()
    {
        var s1 = _manager.CreateSession(Path.GetTempPath());
        var s2 = _manager.CreateSession(Path.GetTempPath());

        // Both sessions get preassigned ClaudeSessionIds; remember s2's original
        var s2OriginalId = s2.ClaudeSessionId;

        _manager.RegisterClaudeSession("same-claude-id", s1.Id);
        _manager.RegisterClaudeSession("same-claude-id", s2.Id); // Should be ignored

        // s1 should still own the mapping
        Assert.Equal(s1, _manager.GetSessionByClaudeId("same-claude-id"));
        // s2 should keep its preassigned ID, not adopt the duplicate
        Assert.Equal(s2OriginalId, s2.ClaudeSessionId);
        Assert.NotEqual("same-claude-id", s2.ClaudeSessionId);
    }

    [Fact]
    public void RegisterClaudeSession_SameSessionSameId_Succeeds()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("claude-id-1", session.Id);
        _manager.RegisterClaudeSession("claude-id-1", session.Id); // Re-register same

        Assert.Equal(session, _manager.GetSessionByClaudeId("claude-id-1"));
    }

    [Fact]
    public void GetSessionByClaudeId_Unknown_ReturnsNull()
    {
        Assert.Null(_manager.GetSessionByClaudeId("nonexistent-id"));
    }

    [Fact]
    public void RelinkClaudeSession_UpdatesMapping()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("old-claude-id", session.Id);

        _manager.RelinkClaudeSession(session.Id, "new-claude-id");

        // Old mapping should be gone
        Assert.Null(_manager.GetSessionByClaudeId("old-claude-id"));
        // New mapping should work
        Assert.Equal(session, _manager.GetSessionByClaudeId("new-claude-id"));
        Assert.Equal("new-claude-id", session.ClaudeSessionId);
    }

    [Fact]
    public void RelinkClaudeSession_NoOldMapping_JustSetsNew()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        // New sessions get a preassigned ClaudeSessionId; clear it to test relink from scratch
        session.ClaudeSessionId = null;

        _manager.RelinkClaudeSession(session.Id, "new-claude-id");

        Assert.Equal(session, _manager.GetSessionByClaudeId("new-claude-id"));
        Assert.Equal("new-claude-id", session.ClaudeSessionId);
    }

    [Fact]
    public void RelinkClaudeSession_FiresEvent()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        Session? eventSession = null;

        _manager.OnClaudeSessionRegistered += (s, _) => eventSession = s;

        _manager.RelinkClaudeSession(session.Id, "new-id");

        Assert.Equal(session, eventSession);
    }

    [Fact]
    public void RelinkClaudeSession_NonExistentSession_DoesNotThrow()
    {
        _manager.RelinkClaudeSession(Guid.NewGuid(), "some-id");
    }

    [Fact]
    public void CreateSession_WithResumeSessionId_PrePopulatesMapping()
    {
        var session = _manager.CreateSession(
            Path.GetTempPath(), null, SessionBackendType.ConPty, "resume-id-123");

        Assert.Equal("resume-id-123", session.ClaudeSessionId);
        Assert.Equal(session, _manager.GetSessionByClaudeId("resume-id-123"));
    }

    [Fact]
    public void CreateSession_WithResumeSessionId_AppendsResumeArg()
    {
        // The resume ID should be added as --resume arg to the process.
        // We can verify indirectly that the session was created successfully.
        var session = _manager.CreateSession(
            Path.GetTempPath(), "--custom-arg", SessionBackendType.ConPty, "resume-id-123");

        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.Equal("resume-id-123", session.ClaudeSessionId);
    }

    [Fact]
    public void GetTrackedProcessIds_ReturnsEmbeddedOnly()
    {
        // ConPty sessions should NOT be in tracked process IDs (that's for embedded mode)
        var session = _manager.CreateSession(Path.GetTempPath());
        Assert.True(session.ProcessId > 0);

        var pids = _manager.GetTrackedProcessIds();
        Assert.Empty(pids); // ConPty, not Embedded
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
