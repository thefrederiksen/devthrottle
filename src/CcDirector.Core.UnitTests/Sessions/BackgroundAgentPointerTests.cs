using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// A BACKGROUND AGENT CANNOT TAKE OVER ITS SESSION'S POINTER (issue 3480). On 29 September 2026 Claude Code in session 121
/// launched a background agent - a second claude.exe (--bg-pty-host ... --session-id 8a7bf36e-...) that inherited the
/// session's environment and the Director's hook settings. Its SessionStart hook wrote into the session's own drop box
/// with the session's own token, source=startup, 4.4 seconds after the real startup, and the pointer moved to a
/// conversation file that never existed. These replay those two drops, with the ids from the Director log, through the
/// real <see cref="SessionPointerWatcher.Apply"/>. The 4.4 seconds between them is not replayed as a wait: no rule reads
/// the gap, so waiting would only slow the test.
/// </summary>
public sealed class BackgroundAgentPointerTests : IDisposable
{
    private const string RealId = "e1533e66-2813-46bc-9ade-d63cdd21e1ce";
    private const string AgentId = "8a7bf36e-f6fb-4866-bcbe-f92c9ab1808b";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccd-bg-agent-pointer-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _sessions = new(new AgentOptions());
    private readonly LiveBackend _backend = new();

    public BackgroundAgentPointerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _sessions.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A Claude Code process that is running: the one whose startup the pointer took.</summary>
    private sealed class LiveBackend : ISessionBackend
    {
        public int ProcessId => 21944;
        public string Status => "Running";
        public bool IsRunning => !HasExited;
        public bool HasExited { get; set; }
        public CircularTerminalBuffer? Buffer => null;
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

    private Session Adopt()
    {
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, _backend, RealId, ActivityState.Idle,
            DateTimeOffset.UtcNow, "BPM 2026 Talks - Speaker Prep", null);
        _sessions.AdoptSession(session);
        return session;
    }

    private string Transcript(string id) => Path.Combine(_dir, id + ".jsonl");

    /// <summary>Write and apply one drop exactly as the hook does: the raw SessionStart event at the session's own path.</summary>
    private bool Drop(Session session, string claudeId, string source = "startup")
    {
        var path = SessionHookFiles.PointerPathFor(session.Id, session.PointerDropToken, _dir);
        var transcript = Transcript(claudeId).Replace("\\", "\\\\");
        File.WriteAllText(path,
            "{\"session_id\":\"" + claudeId + "\",\"transcript_path\":\"" + transcript +
            "\",\"hook_event_name\":\"SessionStart\",\"source\":\"" + source + "\",\"cwd\":\"D:\\\\ReposMindzie\\\\mindzieWeb\"}");
        return new SessionPointerWatcher(_sessions, _dir).Apply(path);
    }

    [Fact]
    public void Apply_SecondStartupFromABackgroundAgent_PointerStaysOnTheRealConversation()
    {
        // Arrange: the real startup at 06:21:22.757; its conversation is on disk.
        var session = Adopt();
        Assert.True(Drop(session, RealId));
        File.WriteAllText(Transcript(RealId), "{\"type\":\"user\"}\n");

        // Act: the background agent's startup at 06:21:27.126, through the same session's drop box and token. Its
        // conversation file is never written.
        var applied = Drop(session, AgentId);

        // Assert
        Assert.False(applied);
        Assert.Equal(RealId, session.ClaudeSessionId);
        Assert.Equal(Transcript(RealId), session.ClaudeTranscriptPath);
        Assert.Equal(session.Id, _sessions.GetSessionByClaudeId(RealId)?.Id);
        Assert.Null(_sessions.GetSessionByClaudeId(AgentId));
        Assert.False(File.Exists(Transcript(AgentId)));
    }

    [Fact]
    public void Apply_ClearAfterStartup_StillMovesThePointer()
    {
        // A /clear is not a startup: the new conversation it reports is followed, as it always was.
        var session = Adopt();
        Drop(session, RealId);
        File.WriteAllText(Transcript(RealId), "{\"type\":\"user\"}\n");
        const string clearedId = "cccccccc-7777-4777-8777-cccccccccccc";

        Assert.True(Drop(session, clearedId, source: "clear"));

        Assert.Equal(clearedId, session.ClaudeSessionId);
    }

    [Fact]
    public void Apply_ChildWonTheRace_TheRealStartupStillReplacesIt()
    {
        // If the child's startup is applied first, its conversation does not exist, so the real startup that follows
        // is not refused: the guard holds only a pointer whose conversation is on disk.
        var session = Adopt();
        Drop(session, AgentId);
        File.WriteAllText(Transcript(RealId), "{\"type\":\"user\"}\n");

        Assert.True(Drop(session, RealId));

        Assert.Equal(RealId, session.ClaudeSessionId);
    }

    [Fact]
    public void Apply_StartupAfterTheProcessExited_IsAccepted()
    {
        // A new process starts once too: after the first one exited, its startup is the session starting again.
        var session = Adopt();
        Drop(session, RealId);
        File.WriteAllText(Transcript(RealId), "{\"type\":\"user\"}\n");
        _backend.HasExited = true;
        const string restartedId = "dddddddd-8888-4888-8888-dddddddddddd";

        Assert.True(Drop(session, restartedId));

        Assert.Equal(restartedId, session.ClaudeSessionId);
    }
}
