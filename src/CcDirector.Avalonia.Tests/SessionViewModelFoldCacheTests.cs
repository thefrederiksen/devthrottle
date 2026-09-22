using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
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
