using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE RAIL DOES ITS PER-CHANGE WORK ONCE (terminal slowdown plan, step 4, second pull request).
///
/// The "N need you" recount used to be posted once per session whose verdict moved, so a Gateway fold
/// sweep across N sessions queued N recounts of N sessions. And RebuildRail re-stamped every row every
/// fifteen seconds with twelve raises each, although almost every stamp equals the last. These pin the cure:
/// a burst of changes costs one recount, and a row stamped with what it already holds raises nothing.
/// </summary>
public sealed class RailRaiseCoalescingTests
{
    [AvaloniaFact]
    public void TenSessionsChangingInOneBurst_RunTheRecountOnce()
    {
        var recounts = 0;
        var recount = new CoalescedUiAction(() => recounts++);
        var rows = Enumerable.Range(0, 10).Select(_ => NewRow()).ToList();
        // Subscribed the way MainWindow.SubscribeNeedsYou subscribes the header.
        foreach (var (_, vm) in rows)
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is null or nameof(SessionViewModel.NeedsYou)) recount.Request();
            };

        foreach (var (session, _) in rows)
            session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, recounts);
        // CONTROL: all ten really moved, so one recount is a coalesced ten and not one change.
        Assert.All(rows, r => Assert.True(r.Vm.NeedsYou));
    }

    [AvaloniaFact]
    public void ARequestAfterTheRecountRan_RunsItAgain()
    {
        var runs = 0;
        var action = new CoalescedUiAction(() => runs++);

        action.Request();
        Dispatcher.UIThread.RunJobs();
        action.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, runs);
    }

    [AvaloniaFact]
    public void ARequestMadeByTheActionItself_IsNotSwallowed()
    {
        var runs = 0;
        CoalescedUiAction? action = null;
        action = new CoalescedUiAction(() =>
        {
            runs++;
            if (runs == 1) action!.Request();
        });

        action.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, runs);
    }

    [AvaloniaFact]
    public void ARowStampedWithWhatItAlreadyHolds_RaisesNothing()
    {
        var (_, vm) = NewRow();
        var squares = new List<ISolidColorBrush> { StatusPalette.BrushFor("blue"), StatusPalette.BrushFor("red") };
        vm.ApplyRailRow(1, true, false, "2 under it: 1 working, 1 stopped, 1 need you", "5h 29m", squares, "NEEDS YOU 1", true);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        // The same stamp again, as RebuildRail sends it on its fifteen second tick: new list, same squares.
        vm.ApplyRailRow(1, true, false, "2 under it: 1 working, 1 stopped, 1 need you", "5h 29m",
            new List<ISolidColorBrush>(squares), "NEEDS YOU 1", true);

        Assert.Empty(raised);
    }

    [AvaloniaFact]
    public void AStampMovingOnlyTheCrewAge_RaisesOnlyTheCrewAge()
    {
        var (_, vm) = NewRow();
        var squares = new List<ISolidColorBrush> { StatusPalette.BrushFor("blue") };
        vm.ApplyRailRow(0, true, false, "1 under it: 1 working, 0 stopped, 0 need you", "5h 29m", squares, "", false);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.ApplyRailRow(0, true, false, "1 under it: 1 working, 0 stopped, 0 need you", "5h 44m", squares, "", false);

        Assert.Equal(new[] { nameof(SessionViewModel.CrewAgeText) }, raised);
        Assert.Equal("5h 44m", vm.CrewAgeText);
    }

    [AvaloniaFact]
    public void OpeningTheCrew_RaisesTheChevronAndTheCrewLineTogether()
    {
        var (_, vm) = NewRow();
        vm.ApplyRailRow(0, true, false, "1 under it", "1m", Array.Empty<ISolidColorBrush>(), "", false);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.ApplyRailRow(0, true, true, "1 under it", "1m", Array.Empty<ISolidColorBrush>(), "", false);

        Assert.Contains(nameof(SessionViewModel.IsCrewExpanded), raised);
        Assert.Contains(nameof(SessionViewModel.ShowCrewLine), raised);
        Assert.False(vm.ShowCrewLine);
    }

    private static (Session Session, SessionViewModel Vm) NewRow()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        return (session, new SessionViewModel(session));
    }

    /// <summary>An inert backend: the Session needs one, and these tests never run a process.</summary>
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
