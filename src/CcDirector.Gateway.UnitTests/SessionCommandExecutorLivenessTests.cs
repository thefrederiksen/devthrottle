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
/// WHY THIS FILE EXISTS AND WHY IT LOOKS LIKE THIS. Every liveness test next door in
/// <c>SessionCommandExecutorTests</c> injects its own substitute for the check, and the inspection proved
/// what that costs: it replaced the production method's body with the constant <c>false</c> and ALL 68 OF
/// THOSE TESTS STILL PASSED. A test that cannot fail is not coverage. So the tests here deliberately pass
/// NO liveness seam at all - <c>SessionCommandExecutor.DefaultProcessLiveness</c> is what runs - and they
/// use REAL child processes this file starts and ends itself, one per test.
///
/// The three answers each get one, because the whole finding is that they had been collapsed into two:
/// a live process is <c>Alive</c>, an ended one is <c>Gone</c>, and a process this machine may not open is
/// <c>Unreadable</c> - which is neither, and must never be reported as "already stopped" or certify that a
/// process ended.
///
/// A HOST THAT CANNOT PRODUCE THE UNREADABLE STATE FAILS LOUDLY, in the manner of
/// <c>PathContainmentLinkEscapeTests</c>. It does not skip, and it does not fall back to an injected
/// substitute: a silently skipped test for the exact defect under repair reads as coverage it does not
/// provide.
///
/// Nothing here touches a process it did not start. Each child is a <c>ping</c> that ends on its own
/// within ten minutes even if a test were killed mid-run, so nothing can be orphaned indefinitely.
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

    /// <summary>
    /// A real child process that outlives the whole test and still ends on its own if this test never gets
    /// to end it. Nothing here is ever asked about a process it did not start.
    ///
    /// TEN MINUTES, NOT THIRTY SECONDS, AND THE REASON IS A REAL FAILURE. The first version of this waited
    /// out about twenty-nine seconds. Run on its own that was ample; run inside the full local gate, with
    /// eleven test projects competing for the machine, two of these tests reached their assertion AFTER the
    /// child had already exited on its own - and then read a genuinely dead process and reported
    /// "already stopped", which is a true answer about the wrong thing. A fixture must not be able to
    /// expire underneath the test it is holding up. It is still bounded rather than endless, so a test host
    /// killed mid-run cannot leave a child sitting on this machine indefinitely.
    /// </summary>
    private static Process StartAChildProcess()
    {
        // The redirect is inside the command, so no pipe is created that nobody reads.
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 600 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var child = Process.Start(psi);
        Assert.NotNull(child);
        RequireStillRunning(child!, "it had only just been started");
        return child!;
    }

    /// <summary>
    /// This test's own child must be RUNNING, or everything after it is measuring the wrong thing.
    ///
    /// THIS IS THE CHECK WHOSE ABSENCE COST THE FIRST RUN. Without it a child that had quietly exited was
    /// handed to the executor, the production check read it as gone - correctly - and the test failed on an
    /// assertion about a state it had never actually set up. A fixture that cannot prove it built what it
    /// claims to have built certifies nothing.
    /// </summary>
    private static void RequireStillRunning(Process child, string when)
    {
        if (!child.HasExited) return;
        Assert.Fail($"The child process this test started ({child.Id}) had already exited when {when}, so "
            + "the state this test is about was never set up. This is a fault in the test fixture, not a "
            + "verdict on the production liveness check.");
    }

    private static void EndTheChild(Process child)
    {
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(10_000);
        }
        catch (Exception ex)
        {
            // The handle this test opened at creation carries full access, so a failure here means the
            // machine itself refused - say so rather than leaving a silent orphan.
            Assert.Fail($"This test could not end the child process it started ({child.Id}): {ex.Message}");
        }
        finally
        {
            child.Dispose();
        }
    }

    // ------------------------------------------------- a process this machine may not read ----

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint securityInformation, byte[] securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DaclSecurityInformation = 4;
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Start a child process the operating system will still LIST but will not let this machine OPEN, so
    /// that reading <c>HasExited</c> on it throws a <c>Win32Exception</c> while the process is demonstrably
    /// alive. That is the exact state inspection 1 reproduced, and the state the old <c>bool</c> check
    /// reported as "gone".
    ///
    /// It is produced by replacing the child's own discretionary access control list with one that denies
    /// everyone, using the full-access handle this process received when it created the child. The child is
    /// still endable afterwards through that same handle, because access is checked when a handle is opened
    /// and not when it is used.
    ///
    /// If the state cannot be produced this FAILS the test with the reason. It never skips.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Process StartAChildThisMachineCannotRead()
    {
        var child = StartAChildProcess();

        var securityDescriptor = new RawSecurityDescriptor("D:P(D;;GA;;;WD)");
        var bytes = new byte[securityDescriptor.BinaryLength];
        securityDescriptor.GetBinaryForm(bytes, 0);

        if (!SetKernelObjectSecurity(child.Handle, DaclSecurityInformation, bytes))
        {
            var error = Marshal.GetLastWin32Error();
            EndTheChild(child);
            Assert.Fail($"This host would not let the test close a process off from itself "
                + $"(SetKernelObjectSecurity failed with Windows error {error}), so the state where a LIVE "
                + "process cannot be read could not be produced. This test protects the production liveness "
                + "check against exactly that state and must not be skipped.");
        }

        // PROVE THE STATE IS PRESENT, DO NOT INFER IT FROM AN ABSENCE. The first version of this asked
        // only whether OpenProcess had failed - and OpenProcess fails on a process that has EXITED too, so
        // a child that had quietly died read as "successfully closed off" and the test carried on against a
        // corpse. So this now demands the exact thing the production check will hit: the process is still
        // listed, and reading HasExited on it throws Win32Exception. Anything else fails, with which.
        var reopened = OpenProcess(ProcessQueryLimitedInformation, false, child.Id);
        var openError = Marshal.GetLastWin32Error();
        if (reopened != IntPtr.Zero)
        {
            CloseHandle(reopened);
            EndTheChild(child);
            Assert.Fail("This host still granted the test PROCESS_QUERY_LIMITED_INFORMATION on a process "
                + "whose access control list denies everyone, so a live-but-unreadable process could not be "
                + "produced here. This test protects the production liveness check against exactly that "
                + "state and must not be skipped.");
        }
        if (openError != ErrorAccessDenied)
        {
            EndTheChild(child);
            Assert.Fail($"Opening the child process failed with Windows error {openError} rather than "
                + $"{ErrorAccessDenied} (access denied), which means it was not closed off - most likely it "
                + "had already exited. The live-but-unreadable state was not produced, and this test must "
                + "not pass without it.");
        }

        try
        {
            using var reader = Process.GetProcessById(child.Id);
            var exited = reader.HasExited;
            EndTheChild(child);
            Assert.Fail($"Reading HasExited on the closed-off child answered {exited} instead of throwing, "
                + "so this host does not produce the live-but-unreadable state at all. The production "
                + "liveness check's Unreadable answer is UNPROVEN here and this test must not pass.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exactly the state this fixture exists to build: listed by the operating system, unreadable.
        }
        catch (ArgumentException)
        {
            child.Dispose();
            Assert.Fail("The child process had exited before the unreadable state could be built, so this "
                + "test would have been asserting against a process that really was gone. This is a fault "
                + "in the test fixture, not a verdict on the production liveness check.");
        }

        return child;
    }

    // ------------------------------------------------------------------------ the three answers ----

    /// <summary>
    /// ALIVE, THROUGH THE PRODUCTION CHECK. No liveness seam is passed, so the real
    /// <c>DefaultProcessLiveness</c> reads a real running process before the stop and reads it gone after -
    /// which is the only combination that may be called "stopped".
    /// </summary>
    [Fact]
    public async Task Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped()
    {
        var child = StartAChildProcess();
        var ended = false;
        var (sm, session, _) = NewSessionOn(new RealProcessBackend(child.Id, onShutdown: () =>
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(10_000);
            ended = true;
        }));
        var id = session.Id;
        try
        {
            RequireStillRunning(child, "the stop was about to be asked for");

            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), worktreeProbe: ProbeAnswering(true, 0));

            Assert.True(ended, "the test backend was never asked to shut the child down");
            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.Stopped, stop.Verdict);
            Assert.True(stop.ProcessEnded);
            Assert.Equal(child.Id, stop.ProcessId);
            Assert.Null(stop.NotDescribedReason);
            Assert.Null(sm.GetSession(id));
        }
        finally
        {
            EndTheChild(child);
            sm.Dispose();
        }
    }

    /// <summary>
    /// GONE, THROUGH THE PRODUCTION CHECK. A process identifier that belonged to a real process which has
    /// really exited. The operating system LOOKED and said there is no such process, which is the one
    /// established absence, and the only thing that may be called "already stopped".
    /// </summary>
    [Fact]
    public async Task Kill_WithNoInjectedCheck_ReadsAnEndedProcessAsGoneAndReportsAlreadyStopped()
    {
        var child = StartAChildProcess();
        var deadProcessId = child.Id;
        EndTheChild(child);

        var (sm, session, worktree) = NewSessionOn(new RealProcessBackend(deadProcessId));
        var id = session.Id;
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), worktreeProbe: ProbeAnswering(true, 2));

            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.True(stop.RowRemoved);
            Assert.Equal(deadProcessId, stop.ProcessId);
            Assert.Null(stop.NotDescribedReason);
            // Ruling 2's sentence is still owed on this verdict, and it names THIS session's tree.
            Assert.Equal(worktree, stop.WorktreePath);
            Assert.True(stop.WorktreeHadUncommittedChanges);
            Assert.Null(sm.GetSession(id));
        }
        finally { sm.Dispose(); }
    }

    /// <summary>
    /// UNREADABLE, THROUGH THE PRODUCTION CHECK, AND IT IS THE WHOLE FINDING. A process that is
    /// demonstrably alive and that this machine may not open. Before the fix this answered "already
    /// stopped" with the row cleared, while the process carried on running.
    ///
    /// The stop still HAPPENS - it was always best-effort - and what may not happen is the claim.
    /// </summary>
    [Fact]
    public async Task Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Fail("This test produces a live-but-unreadable process using Windows process security, "
                + "and this host is not Windows. The production liveness check's Unreadable answer is "
                + "therefore UNPROVEN here. It must not be skipped into a green run.");
            return;
        }

        var child = StartAChildThisMachineCannotRead();
        var askedToShutDown = false;
        var (sm, session, worktree) = NewSessionOn(
            new RealProcessBackend(child.Id, onShutdown: () => askedToShutDown = true));
        var id = session.Id;
        try
        {
            RequireStillRunning(child, "the stop was about to be asked for");

            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(id), worktreeProbe: ProbeAnswering(true, 1));

            var stop = StopResultOf(result);

            // The forbidden inferences, named one at a time so a failure says which one came back.
            Assert.NotEqual(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.Equal(SessionStopVerdict.StoppedNotDescribed, stop.Verdict);

            // It names what specifically could not be read, in the machine's own words.
            Assert.NotNull(stop.NotDescribedReason);
            Assert.Contains("could not be read before the stop", stop.NotDescribedReason);
            Assert.Contains(child.Id.ToString(), stop.NotDescribedReason);

            // The stop was still attempted, and what WAS established is still reported.
            Assert.True(askedToShutDown, "an unreadable liveness check must not suppress the shutdown");
            Assert.True(stop.RowRemoved);
            Assert.Equal(child.Id, stop.ProcessId);
            Assert.Equal(worktree, stop.WorktreePath);
            Assert.True(stop.WorktreeHadUncommittedChanges);
            Assert.Null(sm.GetSession(id));

            // And the process really is still there, which is what made the old answer a false report.
            Assert.False(child.HasExited,
                "the child exited during the stop, so this run did not actually exercise a LIVE unreadable "
                + "process. This is a fault in the test fixture, not a verdict on the production check.");
        }
        finally
        {
            EndTheChild(child);
            sm.Dispose();
        }
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
