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
