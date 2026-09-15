using System.Collections;
using Avalonia.Headless.XUnit;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE WIRING, in the real window. The row projection being right and the template drawing it right are
/// two separate proofs, and neither of them says the window hands the list box the projection.
///
/// So these drive the REAL <see cref="MainWindow"/>: real sessions in its real rail, its own
/// <c>BindSessionRail</c>, its own <c>RebuildRail</c>. What is read back is what the two list controls
/// are actually pointed at.
///
/// TWO LISTS, DELIBERATELY DIFFERENT. The session list draws the tree, so a closed crew's sessions have
/// no row in it. The collapsed sidebar's slim strip of dots draws EVERY session, unchanged - collapsing
/// the sidebar is how the user asks to see everything at a glance, so hiding sessions inside it would
/// answer a question nobody asked. That claim is asserted here rather than asserted in prose.
///
/// WHAT THESE DO NOT COVER, and it is deliberate: the REMEMBERING. Persisting the open crews and the
/// order writes the running user's own config.json, so the tests drive the redraw half
/// (<c>ToggleCrew</c>, <c>ApplyRailOrder</c>) and the persisting half is proved separately, on its own
/// pure reader and writer, in SessionRailConfigTests.
/// </summary>
public sealed class SessionRailWindowTests
{
    private static Session Make(string name, Session? supervisor = null, int sortOrder = 0)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.CustomName = name;
        session.SortOrder = sortOrder;
        if (supervisor is not null)
        {
            session.ControllerSessionId = supervisor.Id;
            session.SetGatewayResolvedRole(SessionRoles.Worker, hasLiveSupervisor: true);
        }
        return session;
    }

    /// <summary>The real window with an Architect and two Workers under it, bound as Loaded binds it.</summary>
    private static (MainWindow Window, SessionViewModel Architect, SessionViewModel One, SessionViewModel Two) Rig()
    {
        var window = new MainWindow();
        var architect = Make("Architect", sortOrder: 0);
        var one = Make("Worker one", architect, sortOrder: 1);
        var two = Make("Worker two", architect, sortOrder: 2);

        var vms = new[] { new SessionViewModel(architect), new SessionViewModel(one), new SessionViewModel(two) };
        foreach (var vm in vms) window._sessions.Add(vm);

        // Never write the running user's own config.json from a test.
        window.RememberExpandedCrews = _ => { };
        window.BindSessionRail();
        return (window, vms[0], vms[1], vms[2]);
    }

    private static string[] RowNames(MainWindow window) =>
        ((IEnumerable)window.SessionList.ItemsSource!).Cast<SessionViewModel>().Select(v => v.DisplayName).ToArray();

    private static string[] SlimDotNames(MainWindow window) =>
        ((IEnumerable)window.SlimSessionList.ItemsSource!).Cast<SessionViewModel>().Select(v => v.DisplayName).ToArray();

    [AvaloniaFact]
    public void TheSessionListDrawsTheTree_WhileTheCollapsedSidebarStillDrawsEverySession()
    {
        var (window, _, _, _) = Rig();

        // Closed by default: the crew is one row, and its two Workers are folded into it.
        Assert.Equal(new[] { "Architect" }, RowNames(window));

        // The slim strip is UNCHANGED: one dot per session, every session, crew or no crew.
        Assert.Equal(new[] { "Architect", "Worker one", "Worker two" }, SlimDotNames(window));
    }

    [AvaloniaFact]
    public void OpeningTheCrew_PutsItsSessionsInTheList_AndClosingItTakesThemBackOut()
    {
        var (window, architect, _, _) = Rig();

        window.ToggleCrew(architect);
        Assert.Equal(new[] { "Architect", "Worker one", "Worker two" }, RowNames(window));

        window.ToggleCrew(architect);
        Assert.Equal(new[] { "Architect" }, RowNames(window));

        // Neither answer moved the roster or the slim strip - opening a crew shows rows, it does not
        // create or destroy sessions.
        Assert.Equal(3, window._sessions.Count);
        Assert.Equal(3, SlimDotNames(window).Length);
    }

    [AvaloniaFact]
    public void TheCrewRowIsStampedWithWhatTheFoldSaid()
    {
        var (window, architect, _, _) = Rig();

        Assert.True(architect.HasCrew);
        Assert.False(architect.IsCrewExpanded);
        Assert.True(architect.ShowCrewLine);
        Assert.Equal("2 under it: 0 working, 2 stopped, 0 need you", architect.CrewLineText);
        // One square per session under it, each in that session's own stamped colour.
        Assert.Equal(2, architect.CrewColorBrushes.Count);

        window.ToggleCrew(architect);
        Assert.True(architect.IsCrewExpanded);
        Assert.False(architect.ShowCrewLine);
    }

    [AvaloniaFact]
    public void MovingTheOrderSwitch_RedrawsTheListWithSections()
    {
        var (window, _, _, _) = Rig();

        Assert.Equal(new[] { "Architect" }, RowNames(window));

        window.ApplyRailOrder(SessionRailOrder.Attention);

        // The same one top-level row, now under a section heading - the crew stays closed and its
        // sessions are still not rows of their own.
        Assert.Equal(new[] { "Architect" }, RowNames(window));
        Assert.True(((IEnumerable)window.SessionList.ItemsSource!).Cast<SessionViewModel>().First().ShowSectionHeader);

        window.ApplyRailOrder(SessionRailOrder.MyOrder);
        Assert.False(((IEnumerable)window.SessionList.ItemsSource!).Cast<SessionViewModel>().First().ShowSectionHeader);
    }

    [AvaloniaFact]
    public void RedrawingTheRail_KeepsTheSelectedSession()
    {
        var (window, architect, one, _) = Rig();

        window.ToggleCrew(architect);
        window.SessionList.SelectedItem = one;

        // A redraw with the same rows must not drop the selection - the rail redraws every fifteen
        // seconds to tick the crew age, and losing the selected session four times a minute would make
        // the Director unusable.
        window.RebuildRail();
        Assert.Same(one, window.SessionList.SelectedItem);

        // And a redraw that genuinely CHANGES the rows must keep it too. Adding a session redraws the
        // rail on its own - the rail subscribes to the roster - and the row the user was on is still
        // there, so they must still be on it.
        window._sessions.Add(new SessionViewModel(Make("Latecomer", sortOrder: 3)));
        Assert.Equal(new[] { "Architect", "Worker one", "Worker two", "Latecomer" }, RowNames(window));
        Assert.Same(one, window.SessionList.SelectedItem);
    }

    [AvaloniaFact]
    public void SelectingASessionInsideAClosedCrew_OpensTheCrewsAboveItRatherThanFailingQuietly()
    {
        var (window, architect, one, _) = Rig();
        Assert.Equal(new[] { "Architect" }, RowNames(window));

        // A session can be selected from somewhere other than the rail - the Cockpit creating it, a
        // message arriving. If its crew is closed the row is not there to select, and doing nothing
        // would look exactly like a broken click.
        window.SelectSession(one);

        Assert.Equal(new[] { "Architect", "Worker one", "Worker two" }, RowNames(window));
        Assert.True(architect.IsCrewExpanded);
    }

    [AvaloniaFact]
    public void ARedrawNeverSwitchesTheUserToASessionTheyDidNotPick()
    {
        var (window, architect, one, _) = Rig();
        window.ToggleCrew(architect);
        window.SessionList.SelectedItem = one;

        // Replacing the rows empties the list box's selection before refilling it, so the selection
        // handler would otherwise see a value the user never chose and switch them to it. Adding a
        // session redraws the rail, so this is the ordinary case, not a contrived one.
        var switchedTo = new List<string>();
        window.SessionList.SelectionChanged += (_, _) =>
        {
            if (window.SessionList.SelectedItem is SessionViewModel vm) switchedTo.Add(vm.DisplayName);
        };

        window._sessions.Add(new SessionViewModel(Make("Latecomer", sortOrder: 3)));

        Assert.Same(one, window.SessionList.SelectedItem);
        Assert.DoesNotContain("Architect", switchedTo);
        Assert.DoesNotContain("Latecomer", switchedTo);
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
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
