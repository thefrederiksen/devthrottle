using CcDirector.Core.Configuration;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class EventRouterTests : IDisposable
{
    private readonly SessionManager _manager;
    private readonly EventRouter _router;
    private readonly List<string> _logs = new();

    public EventRouterTests()
    {
        var options = new AgentOptions
        {
            ClaudePath = TestShell.Path,
            DefaultBufferSizeBytes = 65536,
            GracefulShutdownTimeoutSeconds = 2
        };
        _manager = new SessionManager(options, msg => _logs.Add(msg));
        _router = new EventRouter(_manager, msg => _logs.Add(msg));
    }

    [Fact]
    public void Route_UnknownClaudeSession_DoesNotAutoRegister()
    {
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);

        // New sessions get a preassigned ClaudeSessionId via --session-id
        var preassignedId = session.ClaudeSessionId;
        Assert.NotNull(preassignedId);

        // Unknown Claude session IDs should NOT be auto-registered;
        // routing an event with a different session ID should not change the mapping.
        var msg = new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "claude-abc-123",
            Cwd = tempPath
        };

        _router.Route(msg);

        // Session should keep its preassigned ID, not adopt the unknown one
        Assert.Equal(preassignedId, session.ClaudeSessionId);
        Assert.Contains(_logs, l => l.Contains("No linked session"));
    }

    [Fact]
    public void Route_RoutesToCorrectSession_AfterRegistration()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("known-session", session.Id);

        var msg = new PipeMessage
        {
            HookEventName = "UserPromptSubmit",
            SessionId = "known-session"
        };

        _router.Route(msg);

        Assert.Equal(ActivityState.Working, session.ActivityState);
    }

    [Fact]
    public void Route_UnknownSessionId_NoUnmatchedSessions_Skips()
    {
        // No sessions exist
        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "unknown-session",
            Cwd = @"C:\does\not\match"
        };

        _router.Route(msg);

        // Should log and skip without throwing
        Assert.Contains(_logs, l => l.Contains("No linked session"));
    }

    [Fact]
    public void Route_RaisesOnRawMessage()
    {
        PipeMessage? received = null;
        _router.OnRawMessage += m => received = m;

        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = "any-session-id"
        };

        _router.Route(msg);

        Assert.NotNull(received);
        Assert.Equal("Stop", received.HookEventName);
    }

    [Fact]
    public void Route_NoSessionId_Skips()
    {
        var msg = new PipeMessage
        {
            HookEventName = "Stop",
            SessionId = null
        };

        _router.Route(msg);

        Assert.Contains(_logs, l => l.Contains("no session_id"));
    }

    [Fact]
    public void Route_SessionStart_SourceClear_RelinksOrphanByCwd()
    {
        // Simulate the /clear flow:
        //   1. Director session exists, linked to OLD Claude id, and just received SessionEnd
        //   2. SessionStart arrives with NEW id, source="clear", same cwd
        //   3. Router should relink the orphan and route the event
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);
        var oldClaudeId = "old-claude-id-12345678";
        _manager.RegisterClaudeSession(oldClaudeId, session.Id);

        // Old session has been "ended" by /clear (per Phase 1A this would actually
        // hold state, but for test we just simulate the post-rotation condition).
        session.HandlePipeEvent(new PipeMessage { HookEventName = "SessionStart" }); // → Idle

        var newClaudeId = "new-claude-id-abcdef01";
        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = newClaudeId,
            Source = "clear",
            Cwd = tempPath
        });

        Assert.Equal(newClaudeId, session.ClaudeSessionId);
        Assert.NotNull(_manager.GetSessionByClaudeId(newClaudeId));
        Assert.Equal(session.Id, _manager.GetSessionByClaudeId(newClaudeId)!.Id);
        Assert.Contains(_logs, l => l.Contains("Relinked") && l.Contains("/clear"));
    }

    [Fact]
    public void Route_SessionStart_SourceCompact_RelinksOrphanByCwd()
    {
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);
        _manager.RegisterClaudeSession("old-id", session.Id);

        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "new-id-after-compact",
            Source = "compact",
            Cwd = tempPath
        });

        Assert.Equal("new-id-after-compact", session.ClaudeSessionId);
    }

    [Fact]
    public void Route_SessionStart_SourceClear_ResetsWingmanContext()
    {
        // /clear wipes the conversation, so the pre-clear Wingman context must be
        // dropped: the OnSessionContextReset event fires and the session's status-event
        // log is emptied. Without this the Wingman keeps narrating the old conversation.
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);
        _manager.RegisterClaudeSession("old-id", session.Id);

        session.SetStatusColor("red", "waiting on user before /clear");
        Assert.NotEmpty(session.RecentWingmanEvents);

        var resetForThisSession = false;
        _manager.OnSessionContextReset += s => { if (s.Id == session.Id) resetForThisSession = true; };

        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "new-id-after-clear",
            Source = "clear",
            Cwd = tempPath
        });

        Assert.True(resetForThisSession);
        Assert.Empty(session.RecentWingmanEvents);
    }

    [Fact]
    public void Route_SessionStart_SourceCompact_KeepsWingmanContext()
    {
        // /compact keeps the conversation going, so its Wingman context must survive:
        // no reset event, status-event log untouched.
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);
        _manager.RegisterClaudeSession("old-id", session.Id);

        session.SetStatusColor("green", "all good before /compact");
        Assert.NotEmpty(session.RecentWingmanEvents);

        var resetFired = false;
        _manager.OnSessionContextReset += _ => resetFired = true;

        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "new-id-after-compact",
            Source = "compact",
            Cwd = tempPath
        });

        Assert.False(resetFired);
        Assert.NotEmpty(session.RecentWingmanEvents);
    }

    [Fact]
    public void Route_SessionStart_SourceStartup_DoesNotRelink()
    {
        // A genuinely new session must not adopt an existing Director session's slot.
        var tempPath = Path.GetTempPath();
        var session = _manager.CreateSession(tempPath);
        _manager.RegisterClaudeSession("existing-id", session.Id);

        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "brand-new-startup-id",
            Source = "startup",
            Cwd = tempPath
        });

        // Director session still points at its original id, not the new one.
        Assert.Equal("existing-id", session.ClaudeSessionId);
        Assert.Null(_manager.GetSessionByClaudeId("brand-new-startup-id"));
    }

    [Fact]
    public void Route_SessionStart_SourceClear_NoMatchingCwd_DoesNotRelink()
    {
        var session = _manager.CreateSession(Path.GetTempPath());
        _manager.RegisterClaudeSession("old-id", session.Id);

        _router.Route(new PipeMessage
        {
            HookEventName = "SessionStart",
            SessionId = "new-id",
            Source = "clear",
            Cwd = @"C:\unrelated\path"
        });

        Assert.Equal("old-id", session.ClaudeSessionId);
        Assert.Contains(_logs, l => l.Contains("No linked session"));
    }

    public void Dispose()
    {
        _manager.Dispose();
    }
}
