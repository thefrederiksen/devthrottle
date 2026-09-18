using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE ROW LINE IS DRAWN ON THE DESKTOP RAIL (Message Load mission, slice 4). The Gateway folds what waits in a
/// session's fleet inbox into one string and pushes it down with the display state; the rail renders it verbatim.
/// Checked by rendering MainWindow's real row template at the rail's real width: the Gateway's words appear, whole,
/// inside the rail; they go when the Gateway clears them; and a row the Gateway said nothing about draws no line.
/// </summary>
public sealed class SessionRailInboxLineRenderTests
{
    /// <summary>The first column of MainWindow.axaml's MainLayoutGrid - the width the rail opens at.</summary>
    private const double RealRailWidth = 264;

    private const string LongLine =
        "3 messages stuck, the oldest unread for 2 hours; 2 messages waiting; 1 reply waiting; 1 notice from the Gateway waiting";

    private static (ListBox List, SessionViewModel Vm, Session Session) RenderRow(string? inboxLine)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.CustomName = "Row line";
        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine);
        var vm = new SessionViewModel(session);

        var source = new MainWindow();
        var list = new ListBox { ItemTemplate = source.SessionList.ItemTemplate, ItemsSource = new[] { vm } };
        var window = new Window { Content = list, Width = RealRailWidth, Height = 400 };
        window.Show();
        Layout(list);
        return (list, vm, session);
    }

    private static void Layout(ListBox list)
    {
        Dispatcher.UIThread.RunJobs();
        list.Measure(new Size(RealRailWidth, 400));
        list.Arrange(new Rect(0, 0, RealRailWidth, 400));
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock? Drawn(ListBox list, string text) =>
        list.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Text == text && t.IsEffectivelyVisible);

    [AvaloniaFact]
    public void TheGatewaysLine_IsDrawnVerbatim_InsideTheRail()
    {
        var (list, vm, _) = RenderRow(LongLine);
        Assert.Equal(LongLine, vm.InboxLine);

        var line = Drawn(list, LongLine);
        Assert.NotNull(line);
        var origin = line!.TranslatePoint(new Point(0, 0), list);
        Assert.NotNull(origin);
        var right = origin!.Value.X + line.Bounds.Width;
        Assert.True(right <= RealRailWidth, $"the row line runs to x={right:F0}, past the rail's {RealRailWidth} pixel edge");
        Assert.True(line.TextLayout.TextLines.Sum(l => l.Length) >= LongLine.Length, "the row line was trimmed, not wrapped");
    }

    [AvaloniaFact]
    public void AStampThatClearsTheLine_RemovesItFromTheRow()
    {
        var (list, _, session) = RenderRow("2 messages waiting");
        Assert.NotNull(Drawn(list, "2 messages waiting"));

        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine: null);
        Layout(list);

        Assert.Null(Drawn(list, "2 messages waiting"));
    }

    [AvaloniaFact]
    public void ALineArrivingLater_IsDrawnWithoutARebuild()
    {
        var (list, _, session) = RenderRow(null);
        Assert.Null(Drawn(list, "1 reply waiting"));

        session.ApplyGatewayDisplayState("blue", "Working", "active", null, null, false, inboxLine: "1 reply waiting");
        Layout(list);

        Assert.NotNull(Drawn(list, "1 reply waiting"));
    }

    [AvaloniaFact]
    public void NoLine_DrawsNoRowLine()
    {
        var (list, vm, _) = RenderRow(null);
        Assert.False(vm.HasInboxLine);
        Assert.Equal("", vm.InboxLine);
        // The row's other texts are there, so an empty answer here is about the line and not an empty row.
        Assert.NotNull(Drawn(list, "Working"));
        Assert.DoesNotContain(list.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text is { } s && s.Contains("waiting", StringComparison.Ordinal));
    }

    /// <summary>An inert backend: the Session needs one, and this test never runs a process.</summary>
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
