using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression tests for the "dead row, no claude" symptom: a session whose agent
/// process exits must not linger in the roster as a zombie row. A clean exit of a
/// local interactive session is reaped (auto-removed); an abnormal exit, or a
/// remote/embedded backend, is kept so the user sees it and crash recovery (#212)
/// can act.
/// </summary>
[Collection("ProcessWideState")]
public sealed class SessionReapOnExitTests
{
    // ---- ShouldReapOnExit decision (pure) ----

    [Theory]
    [InlineData(SessionBackendType.ConPty, 0, true)]
    [InlineData(SessionBackendType.Pipe, 0, true)]
    [InlineData(SessionBackendType.ConPty, 1, false)]   // abnormal exit -> keep for recovery
    [InlineData(SessionBackendType.ConPty, -1, false)]
    [InlineData(SessionBackendType.GitHubActions, 0, false)] // remote run completion -> keep
    [InlineData(SessionBackendType.Studio, 0, false)]
    [InlineData(SessionBackendType.Embedded, 0, false)]
    public void ShouldReapOnExit_reaps_only_clean_local_process_exits(
        SessionBackendType backendType, int exitCode, bool expected)
    {
        Assert.Equal(expected, SessionManager.ShouldReapOnExit(backendType, exitCode));
    }

    // ---- IsUnexpectedExit (crash) decision (pure), issue #959 ----

    [Theory]
    [InlineData(0, false, false)]  // clean exit from idle -> intentional end, not a crash
    [InlineData(0, true, true)]    // exited WHILE WORKING with code 0 -> a crash that hides in a 0 code
    [InlineData(1, false, true)]   // non-zero exit -> crash
    [InlineData(-1, true, true)]   // non-zero exit while working -> crash
    public void IsUnexpectedExit_flags_nonzero_or_died_while_working(
        int exitCode, bool wasWorking, bool expected)
    {
        Assert.Equal(expected, Session.IsUnexpectedExit(exitCode, wasWorking));
    }

    [Fact]
    public async Task Crash_with_exit_zero_while_working_is_kept_in_error_state()
    {
        var manager = new SessionManager(new AgentOptions()) { CleanExitReapDelayMs = 0 };
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        session.ApplyTerminalActivityState(ActivityState.Working); // mid-task
        manager.AdoptSession(session);

        backend.RaiseExit(0); // exit 0 BUT it was working -> a crash, must be kept

        await Task.Delay(150); // give any (incorrect) reap a chance to run
        var kept = manager.GetSession(session.Id);
        Assert.NotNull(kept);
        Assert.True(kept!.Crashed);
        Assert.Equal(SessionStatus.Failed, kept.Status);
        Assert.Equal(StatusColor.Error, kept.StatusColor);
    }

    // ---- Session.OnExited one-shot semantics ----

    [Fact]
    public void Session_OnExited_fires_once_with_the_exit_code()
    {
        var backend = new ExitableBackend();
        using var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();

        var codes = new List<int>();
        session.OnExited += codes.Add;

        backend.RaiseExit(0);
        backend.RaiseExit(0); // a backend double-raise must not double-notify

        Assert.Single(codes);
        Assert.Equal(0, codes[0]);
    }

    // ---- End-to-end reaping through the manager ----

    [Fact]
    public async Task Clean_process_exit_reaps_the_session_and_fires_OnSessionRemoved()
    {
        var manager = new SessionManager(new AgentOptions()) { CleanExitReapDelayMs = 0 };
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();

        Guid? removed = null;
        manager.OnSessionRemoved += s => removed = s.Id;
        manager.AdoptSession(session);

        backend.RaiseExit(0); // clean exit -> should be reaped

        await WaitUntil(() => manager.GetSession(session.Id) is null);
        Assert.Equal(session.Id, removed);
        Assert.Null(manager.GetSession(session.Id));
    }

    [Fact]
    public async Task Abnormal_process_exit_keeps_the_session_for_recovery()
    {
        var manager = new SessionManager(new AgentOptions()) { CleanExitReapDelayMs = 0 };
        var backend = new ExitableBackend();
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            backend, SessionBackendType.ConPty);
        session.MarkRunning();
        manager.AdoptSession(session);

        backend.RaiseExit(1); // non-zero -> a crash, kept in the Error state (issue #959)

        await Task.Delay(150); // give any (incorrect) reap a chance to run
        var kept = manager.GetSession(session.Id);
        Assert.NotNull(kept);
        Assert.True(kept!.Crashed);
        Assert.Equal(SessionStatus.Failed, kept.Status);
        Assert.Equal(StatusColor.Error, kept.StatusColor);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>An ISessionBackend whose process exit can be raised on demand.</summary>
    private sealed class ExitableBackend : ISessionBackend
    {
        public int ProcessId => 4321;
        public string Status => "Exitable";
        public bool IsRunning => !_exited;
        public bool HasExited => _exited;
        public CircularTerminalBuffer? Buffer => null;
        private bool _exited;

#pragma warning disable CS0067 // StatusChanged is required by the interface but unused here
        public event Action<string>? StatusChanged;
#pragma warning restore CS0067
        public event Action<int>? ProcessExited;

        public void RaiseExit(int code)
        {
            _exited = true;
            ProcessExited?.Invoke(code);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
