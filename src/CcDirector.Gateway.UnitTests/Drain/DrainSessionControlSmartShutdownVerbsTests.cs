using CcDirector.ControlApi.Drain;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The two verbs the smart shutdown added to the drain's seam, on the REAL seam
/// (<see cref="SessionManagerDrainControl"/>) over a REAL <see cref="SessionManager"/>.
///
/// Only the agent process is a stand-in: a backend that records what was written to it and whether it was
/// asked to shut down. Everything between the verb and that backend - the interrupt verb, the session, the
/// agent driver, the stop verb, the session manager's removal - is the product's own code, so reverting
/// either verb's body leaves these red.
/// </summary>
public class DrainSessionControlSmartShutdownVerbsTests
{
    private const byte CtrlC = 0x03;

    /// <summary>A stand-in for the agent process: it remembers what was typed into it and whether it was
    /// asked to stop.</summary>
    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte> Written { get; } = new();
        public int ShutdownsAskedFor { get; private set; }

        public int ProcessId => 0;
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
        public void Write(byte[] data) { lock (Written) Written.AddRange(data); }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) { ShutdownsAskedFor++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public SessionManager Sessions { get; } = new(new Core.Configuration.AgentOptions());
        public RecordingBackend Backend { get; } = new();
        public Session Session { get; }
        public SessionManagerDrainControl Control { get; }
        private readonly string _directory;

        public Rig()
        {
            _directory = Directory.CreateTempSubdirectory("smart-shutdown-verbs-").FullName;
            Session = Sessions.CreateEmbeddedSession(_directory, null, Backend);
            Control = new SessionManagerDrainControl(Sessions);
        }

        public string Id => Session.Id.ToString();

        public void Dispose()
        {
            Sessions.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* a test machine holding the folder open must not fail the test */ }
        }
    }

    // ================= interrupt =================

    [Fact]
    public async Task InterruptAsync_ALiveSession_SendsTheAgentsInterruptKeyAndAnswersDelivered()
    {
        using var rig = new Rig();

        var answer = await rig.Control.InterruptAsync(rig.Id);

        Assert.Equal(DrainDelivery.Ok, answer);
        Assert.Contains(CtrlC, rig.Backend.Written);

        // Interrupting is not ending: the session is still on the Director, and nobody asked it to stop.
        Assert.True(rig.Control.IsPresent(rig.Id));
        Assert.Equal(0, rig.Backend.ShutdownsAskedFor);
    }

    [Fact]
    public async Task InterruptAsync_AnAgentWithNoSafeInterrupt_AnswersCouldNotWithTheDriversOwnReason()
    {
        using var rig = new Rig();
        rig.Session.DriverOverride = AgentDrivers.For(AgentKind.Pi);

        var answer = await rig.Control.InterruptAsync(rig.Id);

        // "Could not" and "gone" are two facts. This session is alive; its agent simply has no key that
        // interrupts without quitting, and the driver's words come back so the caller can show them.
        Assert.False(answer.Delivered);
        Assert.False(answer.SessionGone);
        Assert.Contains("no safe hard interrupt", answer.Reason);
        Assert.DoesNotContain(CtrlC, rig.Backend.Written);
        Assert.True(rig.Control.IsPresent(rig.Id));
    }

    [Theory]
    [InlineData("4b0d3c5e-0000-4000-8000-000000000001")]
    [InlineData("not-a-session-id")]
    public async Task InterruptAsync_ASessionThatIsNotHere_AnswersGone(string sessionId)
    {
        using var rig = new Rig();

        var answer = await rig.Control.InterruptAsync(sessionId);

        Assert.Equal(DrainDelivery.Gone, answer);
        Assert.Empty(rig.Backend.Written);
    }

    // ================= end =================

    [Fact]
    public async Task EndAsync_ALiveSession_StopsItsProcessAndTakesItOffTheDirectorAtOnce()
    {
        using var rig = new Rig();

        var answer = await rig.Control.EndAsync(rig.Id, "the smart shutdown reached its limit");

        Assert.Equal(DrainEnd.Ok, answer);
        Assert.Equal(1, rig.Backend.ShutdownsAskedFor);

        // No flag, no reaper, no grace window: absent on the very next question. That is the difference
        // between this verb and MarkForDeletion, and it is why the older drain must never call it.
        Assert.False(rig.Control.IsPresent(rig.Id));
        Assert.Null(rig.Sessions.GetSession(rig.Session.Id));
        Assert.DoesNotContain(rig.Id, rig.Control.LiveSessionIds());
    }

    [Fact]
    public async Task EndAsync_ASessionMidTurn_IsEndedAnyway()
    {
        using var rig = new Rig();
        rig.Session.MarkForDeletion("flagged first, as the older drain would");
        rig.Session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Equal(ActivityState.Working, rig.Session.ActivityState);

        // The reaper leaves a working session alone for ever. This verb does not.
        var answer = await rig.Control.EndAsync(rig.Id, "the smart shutdown reached its limit");

        Assert.Equal(DrainEnd.Ok, answer);
        Assert.False(rig.Control.IsPresent(rig.Id));
    }

    [Theory]
    [InlineData("4b0d3c5e-0000-4000-8000-000000000002")]
    [InlineData("not-a-session-id")]
    public async Task EndAsync_ASessionThatIsNotHere_AnswersGoneAndTouchesNothing(string sessionId)
    {
        using var rig = new Rig();

        var answer = await rig.Control.EndAsync(sessionId, "the smart shutdown reached its limit");

        // The stop verb underneath calls a missing session a SUCCESS ("already stopped"). The seam keeps
        // "I ended it" and "it was not here" apart, because they are different rows in the record.
        Assert.Equal(DrainEnd.Gone, answer);
        Assert.False(answer.Ended);
        Assert.Equal(0, rig.Backend.ShutdownsAskedFor);
        Assert.True(rig.Control.IsPresent(rig.Id));
    }

    [Fact]
    public async Task EndAsync_AStopTheBackendRefused_AnswersCouldNotAndLeavesTheSessionOnTheDirector()
    {
        using var rig = new Rig();
        rig.Backend.LastShutdownFailure = "the remote run would not cancel";

        var answer = await rig.Control.EndAsync(rig.Id, "the smart shutdown reached its limit");

        Assert.False(answer.Ended);
        Assert.False(answer.SessionGone);
        Assert.Contains("the remote run would not cancel", answer.Reason);

        // A session that would not stop is still running, so it is still listed: a record that called it
        // closed would hide a live agent from everything that could stop it again.
        Assert.True(rig.Control.IsPresent(rig.Id));
    }
}
