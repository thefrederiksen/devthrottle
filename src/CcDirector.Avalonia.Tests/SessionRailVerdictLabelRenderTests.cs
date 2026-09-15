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
/// THE WINGMAN'S LABEL IS DRAWN WHOLE ON THE DESKTOP RAIL (the Wingman-on-every-turn mission, slice D).
///
/// The desktop gets a verdict as colour and label only, over the display push. Every label the rail showed
/// before was a word or two ("Needs you", "Working"); a verdict line is up to eighty characters, the cap the
/// turn-verdict contract puts on it, and a finished (cyan) row's label leads it with "Done - " or "Report - ". The rail is
/// 264 pixels wide. So the claim "the desktop shows the label" is a rendered claim, and it is checked by rendering:
/// MainWindow's real row template, at the rail's real width, carrying the longest label the Gateway can stamp,
/// with the drawn text measured against the rail's edge.
/// </summary>
public sealed class SessionRailVerdictLabelRenderTests
{
    /// <summary>The first column of MainWindow.axaml's MainLayoutGrid - the width the rail opens at.</summary>
    private const double RealRailWidth = 264;

    /// <summary>Exactly eighty characters: the contract's cap on a verdict label.</summary>
    private const string LongestVerdictLine = "The pull request is open and the branch is pushed; nothing is waiting on you now";

    /// <summary>The longest label the fold can stamp: the longer of the two leading words, then the longest line.</summary>
    private const string LongestLabel = "Report - " + LongestVerdictLine;

    [AvaloniaFact]
    public void TheLongestVerdictLabel_IsDrawnWhole_InsideTheRailWidth()
    {
        Assert.Equal(80, LongestVerdictLine.Length);
        Assert.Equal(89, LongestLabel.Length);

        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.CustomName = "Wingman label";
        session.ApplyGatewayDisplayState("cyan", LongestLabel, "active", null, null, false);
        var vm = new SessionViewModel(session);
        // CONTROL: the row really carries the label, so a pass below is about the drawing and not an empty row.
        Assert.Equal(LongestLabel, vm.ActivityLabel);

        var source = new MainWindow();
        var list = new ListBox { ItemTemplate = source.SessionList.ItemTemplate, ItemsSource = new[] { vm } };
        var window = new Window { Content = list, Width = RealRailWidth, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        list.Measure(new Size(RealRailWidth, 400));
        list.Arrange(new Rect(0, 0, RealRailWidth, 400));
        Dispatcher.UIThread.RunJobs();

        var label = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == LongestLabel);
        Assert.True(label.IsEffectivelyVisible, "the verdict label is not visible on the rail row at all");

        var origin = label.TranslatePoint(new Point(0, 0), list);
        Assert.NotNull(origin);
        var right = origin!.Value.X + label.Bounds.Width;
        Assert.True(right <= RealRailWidth,
            $"the verdict label runs to x={right:F0} (starts at {origin.Value.X:F0}, {label.Bounds.Width:F0} wide, list {list.Bounds.Width:F0} wide), " +
            $"past the rail's {RealRailWidth} pixel edge, so its end is clipped off the screen");

        // Every drawn line fits the block it was given. A line's width here leaves out the space it wrapped at,
        // which is never drawn; the width that includes it overstates a wrapped line by one space.
        var lines = label.TextLayout.TextLines;
        foreach (var line in lines)
            Assert.True(line.Width <= label.Bounds.Width + 0.5,
                $"a drawn line of the label is {line.Width:F0} pixels inside a {label.Bounds.Width:F0} pixel block");
        // And the lines together carry every character of the label: it wrapped, it was not trimmed. At least,
        // not exactly: the layout counts one end-of-paragraph character on its last line, so a whole label measures
        // one more than its length here, and a trimmed one would measure less than its length.
        Assert.True(lines.Sum(l => l.Length) >= LongestLabel.Length,
            $"the label's lines carry {lines.Sum(l => l.Length)} characters of its {LongestLabel.Length}, so part of it was trimmed");
        Assert.True(lines.Count > 1, "the longest label fitted on one line, so this test no longer exercises wrapping");
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
