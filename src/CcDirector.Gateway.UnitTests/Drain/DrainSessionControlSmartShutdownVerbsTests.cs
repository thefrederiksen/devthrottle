using CcDirector.ControlApi.Drain;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The two verbs the smart shutdown added to the drain's seam - stopping a turn, and ending a session - on
/// the REAL seam (<see cref="SessionManagerDrainControl"/>) over a REAL <see cref="SessionManager"/>.
///
/// Only the agent process is a stand-in: a backend that records what was written to it and whether it was
/// asked to shut down. Everything between the verb and that backend - the interrupt verb, the escape verb,
/// the session, the agent driver, the stop verb, the session manager's removal - is the product's own code,
/// so reverting either verb's body leaves these red.
///
/// WHICH VERB STOPS A TURN IS READ FROM THE SESSION'S OWN DRIVER, and the drivers disagree, so the choice
/// is proved here against the real driver registry rather than against one agent (issue #3207).
/// </summary>
public class DrainSessionControlSmartShutdownVerbsTests
{
    private const byte CtrlC = 0x03;
    private const byte Escape = 0x1B;

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

    // ================= stopping a turn, with the verb the driver declares =================

    [Fact]
    public async Task StopTurnAsync_AnAgentThatDeclaresTheHardInterrupt_SendsCtrlCAndSaysSo()
    {
        using var rig = new Rig();
        rig.Session.DriverOverride = AgentDrivers.For(AgentKind.ClaudeCode);

        var answer = await rig.Control.StopTurnAsync(rig.Id);

        Assert.Equal(DrainStopVerb.Interrupt, answer.Verb);
        Assert.Equal(DrainDelivery.Ok, answer.Delivery);
        Assert.Contains(CtrlC, rig.Backend.Written);
        Assert.DoesNotContain(Escape, rig.Backend.Written);

        // Stopping a turn is not ending: the session is still on the Director, and nobody asked it to stop.
        Assert.True(rig.Control.IsPresent(rig.Id));
        Assert.Equal(0, rig.Backend.ShutdownsAskedFor);
    }

    [Fact]
    public async Task StopTurnAsync_AnAgentThatDeclaresOnlyTheSoftCancel_SendsEscapeAndNeverCtrlC()
    {
        using var rig = new Rig();
        rig.Session.DriverOverride = AgentDrivers.For(AgentKind.Pi);

        var answer = await rig.Control.StopTurnAsync(rig.Id);

        // THE WHOLE OF ISSUE #3207. pi declares no safe hard interrupt - its Ctrl+C clears the editor and
        // twice QUITS it - so the seam chooses Escape from what the driver declares and the session is
        // genuinely stopped. Before this, the interrupt was sent anyway, refused, and the session was
        // never told to hand over at all.
        Assert.Equal(DrainStopVerb.Escape, answer.Verb);
        Assert.Equal(DrainDelivery.Ok, answer.Delivery);
        Assert.Contains(Escape, rig.Backend.Written);
        Assert.DoesNotContain(CtrlC, rig.Backend.Written);
        Assert.True(rig.Control.IsPresent(rig.Id));
        Assert.Equal(0, rig.Backend.ShutdownsAskedFor);
    }

    [Fact]
    public async Task StopTurnAsync_AnAgentThatDeclaresNeither_SendsNothingAndNamesTheAgent()
    {
        using var rig = new Rig();
        rig.Session.DriverOverride = new DeclaresNeitherDriver();

        var answer = await rig.Control.StopTurnAsync(rig.Id);

        // Nothing is sent, and the answer is a sentence a row and a record can show. "Could not" and
        // "gone" stay two facts: this session is alive and working, its agent simply has no verb that
        // stops a turn, and the limit will end it like any other session still present.
        Assert.Equal(DrainStopVerb.None, answer.Verb);
        Assert.False(answer.Delivery.Delivered);
        Assert.False(answer.Delivery.SessionGone);
        Assert.Contains("declares neither", answer.Delivery.Reason);
        Assert.Contains(AgentKind.RawCli.ToString(), answer.Delivery.Reason);
        Assert.Empty(rig.Backend.Written);
        Assert.True(rig.Control.IsPresent(rig.Id));
    }

    [Theory]
    [MemberData(nameof(EveryAgentKind))]
    public async Task StopTurnAsync_EveryShippedDriver_GetsTheVerbItDeclaresAndNoOther(AgentKind kind)
    {
        // CHECK EVERY DRIVER, NOT ONLY PI (issue #3207). The drivers disagree, and the disagreement is not
        // a corner: pi declares only the soft cancel, Cursor and Copilot declare only the hard interrupt,
        // Claude Code and Codex declare both, and the generic driver declares both for every remaining
        // kind. This walks the real registry, so a driver added later is covered the day it is added.
        using var rig = new Rig();
        var driver = AgentDrivers.For(kind);
        rig.Session.DriverOverride = driver;

        var answer = await rig.Control.StopTurnAsync(rig.Id);

        if (driver.Capabilities.HasFlag(DriverCapabilities.Interrupt))
        {
            Assert.Equal(DrainStopVerb.Interrupt, answer.Verb);
            Assert.Contains(CtrlC, rig.Backend.Written);
        }
        else if (driver.Capabilities.HasFlag(DriverCapabilities.Cancel))
        {
            Assert.Equal(DrainStopVerb.Escape, answer.Verb);
            Assert.Contains(Escape, rig.Backend.Written);
            Assert.DoesNotContain(CtrlC, rig.Backend.Written);
        }
        else
        {
            Assert.Equal(DrainStopVerb.None, answer.Verb);
            Assert.Empty(rig.Backend.Written);
            Assert.Contains(kind.ToString(), answer.Delivery.Reason);
        }
    }

    public static TheoryData<AgentKind> EveryAgentKind()
    {
        var data = new TheoryData<AgentKind>();
        foreach (var kind in Enum.GetValues<AgentKind>()) data.Add(kind);
        return data;
    }

    [Fact]
    public async Task StopTurnAsync_AVerbTheDirectorRefuses_AnswersCouldNotWithTheHandlersOwnReason()
    {
        using var rig = new Rig();
        // A driver that declares the hard interrupt and then throws from it: the verb handler turns that
        // into a refusal, and the seam carries the words back rather than letting them escape the run.
        rig.Session.DriverOverride = new DeclaresInterruptButRefusesDriver();

        var answer = await rig.Control.StopTurnAsync(rig.Id);

        Assert.Equal(DrainStopVerb.Interrupt, answer.Verb);
        Assert.False(answer.Delivery.Delivered);
        Assert.False(answer.Delivery.SessionGone);
        Assert.Contains("this stand-in agent refuses the interrupt", answer.Delivery.Reason);
        Assert.True(rig.Control.IsPresent(rig.Id));
    }

    [Theory]
    [InlineData("4b0d3c5e-0000-4000-8000-000000000001")]
    [InlineData("not-a-session-id")]
    public async Task StopTurnAsync_ASessionThatIsNotHere_AnswersGone(string sessionId)
    {
        using var rig = new Rig();

        var answer = await rig.Control.StopTurnAsync(sessionId);

        Assert.Equal(DrainTurnStop.Gone, answer);
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

    /// <summary>
    /// An agent that declares NEITHER verb. No shipped driver is this shape today - every one of them
    /// declares the hard interrupt, the soft cancel, or both - so the shape has to be built here to be
    /// tested at all. Its kind is only what the seam's sentence names; it says nothing about how the real
    /// raw command line driver is declared (that one declares both).
    /// </summary>
    private sealed class DeclaresNeitherDriver : IAgentDriver
    {
        public AgentKind Kind => AgentKind.RawCli;
        public DriverCapabilities Capabilities => DriverCapabilities.None;
        public IReadOnlyList<AgentSlashCommand> SlashCommands => [];
        public string ModelFlag => "";
        public IReadOnlyList<AgentModelOption> KnownModels => [];
        public string? ReadConfiguredDefaultModel() => null;

        public string ResolveExecutable(string? configuredPath) => throw new NotSupportedException();
        public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) => throw new NotSupportedException();
        public Task SubmitAsync(ISessionBackend backend, string text) => throw new NotSupportedException();
        public Task CancelAsync(ISessionBackend backend) => throw new NotSupportedException(
            "[DeclaresNeitherDriver] this stand-in agent has no soft cancel.");
        public Task InterruptAsync(ISessionBackend backend) => throw new NotSupportedException(
            "[DeclaresNeitherDriver] this stand-in agent has no hard interrupt.");
        public Task ShowHistoryAsync(ISessionBackend backend) => throw new NotSupportedException();
        public Task ClearContextAsync(ISessionBackend backend) => throw new NotSupportedException();
        public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) => throw new NotSupportedException();
        public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) => throw new NotSupportedException();
        public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) => throw new NotSupportedException();
    }

    /// <summary>An agent that DECLARES the hard interrupt and then refuses it - the one shape where the
    /// verb was chosen correctly and still did not land.</summary>
    private sealed class DeclaresInterruptButRefusesDriver : IAgentDriver
    {
        public AgentKind Kind => AgentKind.RawCli;
        public DriverCapabilities Capabilities => DriverCapabilities.Interrupt;
        public IReadOnlyList<AgentSlashCommand> SlashCommands => [];
        public string ModelFlag => "";
        public IReadOnlyList<AgentModelOption> KnownModels => [];
        public string? ReadConfiguredDefaultModel() => null;

        public string ResolveExecutable(string? configuredPath) => throw new NotSupportedException();
        public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) => throw new NotSupportedException();
        public Task SubmitAsync(ISessionBackend backend, string text) => throw new NotSupportedException();
        public Task CancelAsync(ISessionBackend backend) => throw new NotSupportedException();
        public Task InterruptAsync(ISessionBackend backend) => throw new NotSupportedException(
            "this stand-in agent refuses the interrupt");
        public Task ShowHistoryAsync(ISessionBackend backend) => throw new NotSupportedException();
        public Task ClearContextAsync(ISessionBackend backend) => throw new NotSupportedException();
        public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) => throw new NotSupportedException();
        public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) => throw new NotSupportedException();
        public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) => throw new NotSupportedException();
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
