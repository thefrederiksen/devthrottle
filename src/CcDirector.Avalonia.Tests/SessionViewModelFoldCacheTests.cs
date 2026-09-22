using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE RAIL BUILDS EACH SESSION'S WIRE OBJECT ONCE PER CHANGE (terminal slowdown plan, step 4).
///
/// Every rail getter used to rebuild the session's full Gateway transfer object, and one Gateway stamp raised
/// fifteen properties, so one stamp cost fifteen or more builds on the screen thread. These tests pin the cure:
/// one stamp costs one build even with a binding reading every raised property, a stamp that changes only the
/// colour raises only what the colour feeds, and the cache never hides a change.
/// </summary>
public sealed class SessionViewModelFoldCacheTests
{
    [AvaloniaFact]
    public void OneGatewayStamp_WithABindingReadingEveryRaise_BuildsTheWireObjectOnce()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();
        ReadEveryRaisedProperty(vm);

        var before = vm.FoldMapCount;
        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, vm.FoldMapCount - before);
        // CONTROL: the stamp really reached the row, so one build is not one build of nothing.
        Assert.Equal("Needs you", vm.ActivityLabel);
        Assert.True(vm.NeedsYou);
    }

    [AvaloniaFact]
    public void AStampChangingOnlyTheColour_RaisesTheDot_AndNotTheRoleGlyph()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        session.ApplyGatewayDisplayState("purple", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(SessionViewModel.StatusColorBrush), raised);
        Assert.DoesNotContain(nameof(SessionViewModel.RoleGlyphText), raised);
        Assert.DoesNotContain(nameof(SessionViewModel.ActivityLabel), raised);
    }

    [AvaloniaFact]
    public void TheFirstRaise_RaisesEveryProjectedProperty()
    {
        var (session, vm) = NewRow();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(15, raised.Distinct().Count());
    }

    [AvaloniaFact]
    public void AnyOtherRaise_ClearsTheCache_SoTheNextReadSeesTheChange()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();
        _ = vm.FoldInput;
        var before = vm.FoldMapCount;

        // A raise that has nothing to do with the fold, through the same one notification path.
        vm.QueueCount = vm.QueueCount + 1;
        _ = vm.FoldInput;

        Assert.Equal(1, vm.FoldMapCount - before);
    }

    [AvaloniaFact]
    public void TwoReadsWithNoChangeBetween_ShareOneBuild()
    {
        var (_, vm) = NewRow();
        _ = vm.FoldInput;
        var before = vm.FoldMapCount;

        _ = vm.ActivityLabel;
        _ = vm.NeedsYou;
        _ = vm.InboxLine;

        Assert.Equal(0, vm.FoldMapCount - before);
    }

    [AvaloniaFact]
    public void ASessionChange_IsVisibleToTheVeryNextRead_BeforeThePostedRaiseRuns()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.NeedsYou);

        // No RunJobs: the raise is still queued, and the read must already see the new stamp.
        session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false);

        Assert.True(vm.NeedsYou);
    }

    /// <summary>
    /// THE OVERLAP. A session event lands on another thread WHILE the screen thread is building the wire
    /// object. That build read the session before the change, so it must never be published and never be
    /// returned: the read throws it away and builds again, and the next read shares the second build.
    /// </summary>
    [AvaloniaFact]
    public void AnInvalidationBetweenBuildAndPublish_IsNeverLost_AndTheOldBuildIsNeverReturned()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();

        var builds = new List<SessionDto>();
        var real = vm.FoldMapper;
        vm.FoldMapper = s =>
        {
            var dto = real(s);
            builds.Add(dto);
            if (builds.Count == 1)
            {
                // The change arrives on a background thread after this build read the session and before
                // the getter publishes it - the exact window the review found.
                var t = new Thread(() =>
                    session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", DateTime.UtcNow, null, false));
                t.Start();
                t.Join();
            }
            return dto;
        };
        vm.InvalidateFold();

        var read = vm.FoldInput;

        Assert.NotNull(read);
        Assert.Equal(2, builds.Count);
        Assert.Equal("blue", builds[0].EffectiveColor);   // CONTROL: the first build really was the old one
        Assert.NotSame(builds[0], read);
        Assert.Equal("red", read.EffectiveColor);
        // The second build was published: the next read shares it.
        Assert.Same(read, vm.FoldInput);
        Assert.Equal(2, builds.Count);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The same overlap under real contention: a background thread stamps the session over and over while
    /// the screen thread reads. No read may be null, and once the writer stops the next read shows its last
    /// stamp - a build made before that stamp must not survive in the cache.
    /// </summary>
    [AvaloniaFact]
    public void ReadsDuringABackgroundStampStorm_AreNeverNull_AndEndOnTheLastStamp()
    {
        var (session, vm) = NewRow();
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false);
        Dispatcher.UIThread.RunJobs();

        var stop = 0;
        var writer = new Thread(() =>
        {
            var i = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                var colour = (i++ % 2 == 0) ? "red" : "blue";
                session.ApplyGatewayDisplayState(colour, "Working", "active", null, null, false);
            }
            session.ApplyGatewayDisplayState("purple", "Working", "active", null, null, false);
        });
        writer.Start();

        var reads = 0;
        var until = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            Assert.NotNull(vm.FoldInput);
            reads++;
        }
        Volatile.Write(ref stop, 1);
        writer.Join();

        Assert.True(reads > 100, $"only {reads} reads overlapped the writer, so this proved nothing");
        Assert.Equal("purple", vm.FoldInput.EffectiveColor);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A drag inside one crew, projected at once. The drag order is carried in the cached wire object, so the
    /// rail's stamp must drop the cache of every row it moves; otherwise the crew comes back in its old order
    /// until the cache ages out or the next 15 second rebuild.
    /// </summary>
    [AvaloniaFact]
    public void AChildReorder_IsInTheVeryNextProjection()
    {
        var architect = NewRow().Vm;
        var one = NewChild(architect, "Worker one");
        var two = NewChild(architect, "Worker two");
        var expanded = new[] { architect.Session.Id.ToString() };

        var before = SessionRailTree.ProjectInDragOrder(new[] { architect, one, two }, SessionRailOrder.MyOrder, expanded, DateTime.UtcNow);
        Assert.Equal(new[] { "Worker one", "Worker two" }, before.Skip(1).Select(r => r.Session.DisplayName).ToArray());

        // Straight away - well inside the cache's one second - the user drags worker two above worker one.
        var after = SessionRailTree.ProjectInDragOrder(new[] { architect, two, one }, SessionRailOrder.MyOrder, expanded, DateTime.UtcNow);

        Assert.Equal(new[] { "Worker two", "Worker one" }, after.Skip(1).Select(r => r.Session.DisplayName).ToArray());
    }

    /// <summary>
    /// The guard on the one rule the cache depends on: every session event reaches the row through
    /// PostChange, which clears the cache before posting. A handler that posted directly would leave a
    /// window in which the rail reads the old wire object.
    /// </summary>
    [AvaloniaFact]
    public void EveryPostInTheViewModel_GoesThroughPostChange()
    {
        var source = File.ReadAllText(Path.Combine(TestRepoRoot.Path, "src", "CcDirector.Avalonia", "SessionViewModel.cs"));

        var direct = source.Split("Dispatcher.UIThread.Post(").Length - 1;
        var helper = source.Split("PostChange(").Length - 1;

        Assert.Equal(1, direct);   // the one inside PostChange itself
        Assert.True(helper > 10, $"only {helper} PostChange uses found, so this guard is not reading the real file");
    }

    private static (Session Session, SessionViewModel Vm) NewRow()
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        return (session, new SessionViewModel(session));
    }

    private static SessionViewModel NewChild(SessionViewModel supervisor, string name)
    {
        var (session, vm) = NewRow();
        session.CustomName = name;
        session.ControllerSessionId = supervisor.Session.Id;
        session.SetGatewayResolvedRole(SessionRoles.Worker, hasLiveSupervisor: true);
        return vm;
    }

    /// <summary>Stands in for the rail's bindings: every raised property is read at once, as a binding does.</summary>
    private static void ReadEveryRaisedProperty(SessionViewModel vm)
    {
        vm.PropertyChanged += (sender, e) =>
            typeof(SessionViewModel).GetProperty(e.PropertyName!)!.GetValue(sender);
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
