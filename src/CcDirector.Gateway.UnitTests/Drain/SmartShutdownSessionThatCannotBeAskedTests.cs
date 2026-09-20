using CcDirector.ControlApi.Drain;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// ONE SESSION THAT CANNOT BE ASKED MUST NOT END THE WHOLE SMART SHUTDOWN (issue #3235).
///
/// These run the REAL drain over the REAL seam (<see cref="SessionManagerDrainControl"/>) over a REAL
/// <see cref="SessionManager"/> holding real sessions. Only the agent processes are stand-ins, and only
/// one of them behaves badly: its terminal takes the text and never starts a turn, which is what the
/// submit path reports as <see cref="PromptNotSubmittedException"/>. That is the exact shape measured on
/// the quality assurance rig, where a raw command line session took the words, produced too little output
/// for the submit verifier, and the exception came out of the whole run: it stopped at the sixth of seven
/// sessions, the seventh was never asked, every other session was left running and nothing was handed
/// over.
///
/// The exception travels the product's own path here - session, submit, seam, drain - so removing the
/// catch that holds it leaves these red.
/// </summary>
[Collection(CcDirector.Gateway.UnitTests.Restart.DirectorGatesCollection.Name)]
public sealed class SmartShutdownSessionThatCannotBeAskedTests
{
    private DateTime _now = new(2026, 9, 20, 12, 25, 0, DateTimeKind.Utc);

    /// <summary>A stand-in for an agent process. It records every prompt typed into it, and - when it is
    /// the wedged one - answers the way a terminal that takes the words and never runs them does.</summary>
    private sealed class PromptRecordingBackend : ISessionBackend
    {
        /// <summary>When set, this terminal takes the text and never starts a turn, exactly as the submit
        /// verifier reports it.</summary>
        public string? NeverStartsATurn { get; set; }

        public List<string> Prompts { get; } = new();
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
        public void Write(byte[] data) { }

        public Task SendTextAsync(string text)
        {
            if (NeverStartsATurn is not null) throw new PromptNotSubmittedException(NeverStartsATurn);
            lock (Prompts) Prompts.Add(text);
            return Task.CompletedTask;
        }

        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public void Dispose() { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) { ShutdownsAskedFor++; return Task.CompletedTask; }
    }

    /// <summary>A Director with real sessions, one seat per session, in the order they were made.</summary>
    private sealed class Rig : IDisposable
    {
        public SessionManager Sessions { get; } = new(new Core.Configuration.AgentOptions());
        public SessionManagerDrainControl Control { get; }
        public List<(Session Session, PromptRecordingBackend Backend)> Seats { get; } = new();
        private readonly string _directory;

        public Rig(int seats)
        {
            _directory = Directory.CreateTempSubdirectory("cannot-be-asked-").FullName;
            for (var i = 0; i < seats; i++)
            {
                var backend = new PromptRecordingBackend();
                Seats.Add((Sessions.CreateEmbeddedSession(_directory, null, backend), backend));
            }
            Control = new SessionManagerDrainControl(Sessions);
        }

        public string Id(int seat) => Seats[seat].Session.Id.ToString();

        public void Dispose()
        {
            Sessions.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* a test machine holding the folder open must not fail the test */ }
        }
    }

    private DirectorDrain NewDrain(SessionManagerDrainControl control, FakeWorkspaceSink sink)
        => new(control, sink, "director-under-test", null,
            utcNow: () => _now,
            delay: (d, _) => { _now = _now.Add(d); return Task.CompletedTask; });

    private static DrainOptions SmartOptions(WorkspaceDocument captured) => new()
    {
        WorkspaceId = "test-smart-shutdown",
        WorkspaceName = "Test smart shutdown",
        Reason = "update to 2.9.0",
        PollInterval = TimeSpan.FromSeconds(10),
        ReapTimeout = TimeSpan.FromMinutes(6),
        SmartShutdown = new SmartShutdownDrainOptions { TimeAllowed = TimeSpan.FromMinutes(10) },
    };

    /// <summary>
    /// Shows: a session whose terminal takes the words and never starts a turn is recorded as one that
    /// could not be asked, in the submit path's own words; EVERY OTHER SESSION IS STILL ASKED, before it
    /// and after it; and the run finishes and empties the Director instead of ending on an exception.
    ///
    /// The wedged seat is the middle one deliberately: the measured failure stopped at the sixth of seven
    /// sessions, so a seat AFTER it is what proves the run carried on rather than finishing early.
    /// </summary>
    [Fact]
    public async Task ASessionThatNeverStartsATurn_IsRecordedAsSuchAndEveryOtherSessionIsStillAsked()
    {
        using var dir = new TempDir();
        using var rig = new Rig(seats: 3);
        rig.Seats[1].Backend.NeverStartsATurn =
            "[SubmitVerifier] 'RawCli: powershell' never started a turn within 8 beats (4 nudge(s) sent): " +
            "the agent produced under 2048 bytes, so the prompt is parked in the composer unsubmitted.";

        var seats = new[]
        {
            DrainTestRig.Seat(rig.Id(0), "first"),
            DrainTestRig.Seat(rig.Id(1), "the one that will not take it"),
            DrainTestRig.Seat(rig.Id(2), "last"),
        };
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var result = await NewDrain(rig.Control, sink).RunAsync(SmartOptions(sink.Captured), dir.Path);

        // The run reached its own end and emptied the Director. Before the fix it never got here: the
        // exception came out of RunAsync itself.
        Assert.True(result.Emptied, result.NotEmptiedReason);

        // Both healthy sessions were asked - the one before the wedged seat and the one after it.
        Assert.NotEmpty(rig.Seats[0].Backend.Prompts);
        Assert.NotEmpty(rig.Seats[2].Backend.Prompts);

        // The wedged one is named, with the words the submit path used, and it is ended at the limit like
        // any other session that never handed over.
        var wedged = sink.Last.Seats.Single(s => s.SessionId == rig.Id(1));
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, wedged.DrainState);
        Assert.Contains(
            sink.Last.Integrity!.Problems,
            p => p.Contains("never started a turn") && p.Contains("the one that will not take it"));

        // And nothing is left running: all three sessions are off the Director.
        Assert.Empty(rig.Control.LiveSessionIds());
    }

    /// <summary>
    /// Shows: the seam itself answers "it could not be asked, and here is why", rather than throwing.
    /// This is the one line the whole defect turned on - its sibling exception was caught and this one was
    /// not - so it is asserted directly on the seam as well as through the run above.
    /// </summary>
    [Fact]
    public async Task TheSeam_WhenASessionNeverStartsATurn_AnswersCouldNotWithTheSubmitPathsOwnWords()
    {
        using var rig = new Rig(seats: 1);
        rig.Seats[0].Backend.NeverStartsATurn = "never started a turn within 8 beats (4 nudge(s) sent)";

        var answer = await rig.Control.SendAsync(rig.Id(0), "please hand over");

        // "Could not" and "gone" are two facts, and this session is neither gone nor asked: it is alive,
        // it took the words, and nothing ran.
        Assert.False(answer.Delivered);
        Assert.False(answer.SessionGone);
        Assert.Contains("never started a turn", answer.Reason);
        Assert.True(rig.Control.IsPresent(rig.Id(0)));
    }
}
