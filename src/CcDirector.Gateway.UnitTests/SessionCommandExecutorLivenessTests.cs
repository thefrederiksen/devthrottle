using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Git;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The stop verb's LIVENESS CHECK, driven against the production code path (mission "Stop a session",
/// inspection 1, findings I2 and I3).
///
/// The tests that started real child processes were removed with the rest of the tests that start a
/// process. What is left drives the stop verb with no process to ask about.
///
/// In the "DirectorRoot" collection for the same reason the sibling file is: <see cref="SessionManager"/>
/// resolves the Director root, which is process-global state.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionCommandExecutorLivenessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------------------ fixtures ----

    /// <summary>A probe that answers the worktree question without touching git. The worktree question is
    /// NOT what these tests are about, and a real git call would put a three-second timeout inside every
    /// one of them.</summary>
    private static Func<string, CancellationToken, Task<GitCountResult>> ProbeAnswering(bool success, int count) =>
        (_, _) => Task.FromResult(new GitCountResult(success, count));

    private static DirectorCommand KillCommand(Guid sessionId) =>
        new() { CommandId = "stop-liveness", Verb = "kill", SessionId = sessionId.ToString() };

    private static DirectorStopResult StopResultOf(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.NotNull(result.BodyJson);
        var dto = JsonSerializer.Deserialize<DirectorStopResult>(result.BodyJson!, Json);
        Assert.NotNull(dto);
        return dto!;
    }

    /// <summary>
    /// A backend carrying a REAL process identifier, which can optionally end that real process when it is
    /// asked to shut down - so a whole stop can run end to end against the operating system rather than
    /// against a stand-in for it.
    /// </summary>
    private sealed class RealProcessBackend : ISessionBackend
    {
        private readonly Action? _onShutdown;

        internal RealProcessBackend(int processId, Action? onShutdown = null)
        {
            ProcessId = processId;
            _onShutdown = onShutdown;
        }

        public int ProcessId { get; }
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(4096);
        public string? LastShutdownFailure { get; set; }

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) { _onShutdown?.Invoke(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private static (SessionManager sm, Session session, string worktree) NewSessionOn(ISessionBackend backend)
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var worktree = Directory.CreateTempSubdirectory("stop-a-session-liveness-").FullName;
        var session = sm.CreateEmbeddedSession(worktree, null, backend);
        return (sm, session, worktree);
    }

    // ---------------------------------------------------- a session with no process identifier ----

    /// <summary>
    /// I3's other half: where there is no identifier there is nothing to check, and nothing checked may be
    /// reported as an absence. The remote workflow backend reports zero while a remote run is actively
    /// going; the pipe and studio backends report zero always. All three used to land in "already stopped".
    /// </summary>
    [Fact]
    public async Task Kill_SessionCarryingNoProcessIdentifier_IsNotDescribedRatherThanAlreadyStopped()
    {
        var (sm, session, worktree) = NewSessionOn(new RealProcessBackend(processId: 0));
        var id = session.Id;
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), worktreeProbe: ProbeAnswering(true, 4));

            var stop = StopResultOf(result);
            Assert.NotEqual(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.Equal(SessionStopVerdict.StoppedNotDescribed, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.Null(stop.ProcessId);
            Assert.NotNull(stop.NotDescribedReason);
            Assert.Contains("no process identifier", stop.NotDescribedReason);

            // Everything this stop DID establish is still reported - the row and Ruling 2's tree.
            Assert.True(stop.RowRemoved);
            Assert.Equal(worktree, stop.WorktreePath);
            Assert.True(stop.WorktreeHadUncommittedChanges);
        }
        finally { sm.Dispose(); }
    }

    // ------------------------------------------------------ unreadable AFTER the stop, not before ----

    /// <summary>
    /// The other side of the same rule, and the one that needs the seam: the check was readable before the
    /// stop and is not readable after it. A live process was established, so this is not "already stopped" -
    /// but nothing established that it ENDED either, and it must not be reported as a process that would not
    /// die. The row still goes, because removing it is a fact this stop did establish.
    /// </summary>
    [Fact]
    public async Task Kill_WhenTheCheckStopsBeingReadableAfterTheStop_ReportsNeitherEndedNorStuck()
    {
        var (sm, session, _) = NewSessionOn(new RealProcessBackend(processId: 4242));
        var id = session.Id;
        try
        {
            var reads = 0;
            ProcessLivenessReading Liveness(int _) => ++reads == 1
                ? ProcessLivenessReading.IsAlive
                : ProcessLivenessReading.CouldNotRead("the handle was refused by the operating system");

            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), Liveness, ProbeAnswering(true, 0));

            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.StoppedNotDescribed, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.True(stop.RowRemoved);
            Assert.Equal(4242, stop.ProcessId);
            Assert.NotNull(stop.NotDescribedReason);
            Assert.Contains("could not be read afterwards", stop.NotDescribedReason);
            Assert.Contains("the handle was refused by the operating system", stop.NotDescribedReason);
        }
        finally { sm.Dispose(); }
    }

    // --------------------------------------------- a backend that says its own shutdown failed ----

    /// <summary>
    /// I3's first half at the seam the executor reads it through. A backend that reports a failed shutdown
    /// is Ruling 3's third failure - "the process would not die" - reached by a different road, and the row
    /// is deliberately LEFT so the operator can see the session and try again.
    /// </summary>
    [Fact]
    public async Task Kill_WhenTheBackendReportsItsShutdownFailed_IsAFailureAndTheRowStays()
    {
        var backend = new RealProcessBackend(processId: 0);
        var (sm, session, _) = NewSessionOn(backend);
        var id = session.Id;
        backend.LastShutdownFailure = "run 4711 on owner/repo would not cancel: 403 Forbidden.";
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), worktreeProbe: ProbeAnswering(true, 0));

            Assert.Equal(DirectorCommandStatus.Error, result.Status);
            Assert.NotNull(result.Error);
            Assert.Contains("would not cancel", result.Error);
            Assert.Contains("403 Forbidden", result.Error);
            Assert.NotNull(sm.GetSession(id));   // the row stays, so the operator can try again
        }
        finally { sm.Dispose(); }
    }
}
