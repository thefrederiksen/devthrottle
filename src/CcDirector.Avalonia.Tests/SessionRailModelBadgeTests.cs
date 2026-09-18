using Avalonia.Headless.XUnit;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The rail's agent badge now carries the MODEL as well (issue devthrottle_internal#1340) - the owner's
/// chosen shape: one pill reading "Claude Code | fable-5", because the tool and the model it is running are
/// one identity and the rail has no room for two badges.
///
/// What these pin is that the rail RENDERS and does not rule. Every word comes from the shared
/// <c>ModelDisplayFold</c>, the same function the Gateway stamps for the browser clients, so the desktop
/// and the Cockpit cannot word one session two ways - and the two absences stay apart on this surface too.
/// </summary>
public sealed class SessionRailModelBadgeTests
{
    // WHY [AvaloniaFact] AND NOT [Fact]: SessionViewModel builds
    // its brushes in a static initialiser, and building a brush is an Avalonia property write that verifies
    // it is on the dispatcher's thread. A plain [Fact] gets whatever thread xUnit hands it, so whether that
    // succeeds depends on whether another class in this assembly has already started a headless session - and
    // a static initialiser that throws once stays thrown for the rest of the process. These run ON the
    // dispatcher thread instead. Same reason as SessionRailStateTests, where the accident actually fired.
    private static Session Bare()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        return session;
    }

    [AvaloniaFact]
    public void BeforeTheFirstTurn_TheBadgeSaysTheModelHasNotArrived_NotThatThereIsNone()
    {
        // A Claude session can report its model, so a blank badge here would be a fact the rail failed to
        // fetch. The words say it is coming.
        var vm = new SessionViewModel(Bare());

        Assert.Equal("no model yet", vm.ModelLabel);
        Assert.True(vm.IsModelAbsent);
        Assert.Contains("No model recorded yet", vm.AgentModelTooltip);
    }

    [AvaloniaFact]
    public void ARecordedModel_ShowsShortenedOnTheBadgeAndInFullInTheTooltip()
    {
        var session = Bare();
        var vm = new SessionViewModel(session);

        session.SetCurrentModel("claude-fable-5");

        // The badge already says "Claude Code", so the model half does not repeat the vendor.
        Assert.Equal("fable-5", vm.ModelLabel);
        Assert.False(vm.IsModelAbsent);
        // The tooltip carries the agent AND the id exactly as the records spell it - the shortening on the
        // badge must never be the only spelling a reader can get to.
        Assert.Contains("Claude Code", vm.AgentModelTooltip);
        Assert.Contains("claude-fable-5", vm.AgentModelTooltip);
    }

    [AvaloniaFact]
    public void AMidSessionModelSwitch_ReachesTheRail()
    {
        // The failure this guards is not a wrong string, it is a stale one: the model is re-read at
        // turn-end, and a /model switch inside a working session raises none of the events the rail already
        // listens to. Without its own signal the badge keeps its old value until something unrelated
        // repaints the row - which reads as deliberate, and so is believed.
        var session = Bare();
        var vm = new SessionViewModel(session);
        session.SetCurrentModel("claude-opus-5");
        Assert.Equal("opus-5", vm.ModelLabel);

        var raised = 0;
        session.OnCurrentModelChanged += () => raised++;

        session.SetCurrentModel("claude-fable-5");

        Assert.Equal(1, raised);
        Assert.Equal("fable-5", vm.ModelLabel);
    }

    [AvaloniaFact]
    public void AFailedRead_LeavesTheLastKnownModelStanding_AndSaysNothingNew()
    {
        // A read that could not be taken (torn records, agent restarting) is a missed read, not evidence
        // the session lost its model. Session.SetCurrentModel already ignores a null; this pins that the
        // rail therefore keeps the last known answer rather than falling back to "no model yet".
        var session = Bare();
        var vm = new SessionViewModel(session);
        session.SetCurrentModel("claude-opus-5");

        var raised = 0;
        session.OnCurrentModelChanged += () => raised++;
        session.SetCurrentModel(null);
        session.SetCurrentModel("   ");

        Assert.Equal(0, raised);
        Assert.Equal("opus-5", vm.ModelLabel);
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process. A local copy
    /// because the neighbouring rail tests keep theirs private to that class.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
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
}
