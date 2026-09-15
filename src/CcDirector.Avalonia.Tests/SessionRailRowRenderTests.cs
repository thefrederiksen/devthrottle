using Avalonia;
using Avalonia.Layout;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE RAIL ROW AS IT IS ACTUALLY DRAWN. These take the REAL item template out of MainWindow.axaml and
/// render it, because the row projection being right says nothing about whether the markup that reads it
/// puts a chevron on the screen.
///
/// A fold test cannot see a rendered defect, and a build that succeeds says only that the bindings
/// compile - not that the crew line appears when the crew is closed, that it goes away when the crew is
/// open, that a child is indented, or that an ordinary row is left exactly as it was. Those are four
/// separate visible claims and each one is asserted here.
///
/// The template is lifted off a real MainWindow and mounted in a plain window on purpose. MainWindow's
/// own Loaded handler reaches for the running Director (App.SessionManager, the Gateway monitor, the rail
/// timers), which does not exist in a headless test - so the window is never shown. What is under test is
/// the template, and this is the template, not a copy of it.
/// </summary>
public sealed class SessionRailRowRenderTests
{
    private static SessionViewModel Vm(string name)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.CustomName = name;
        return new SessionViewModel(session);
    }

    private static readonly IReadOnlyList<ISolidColorBrush> NoCrew = Array.Empty<ISolidColorBrush>();

    private static IReadOnlyList<ISolidColorBrush> Squares(int count) =>
        Enumerable.Range(0, count).Select(_ => StatusPalette.BrushFor("blue")).ToList();

    /// <summary>
    /// The width the Director's rail actually opens at: the first column of MainWindow.axaml's
    /// MainLayoutGrid. The drawn design is 545 wide and says it is "the rail's real width"; it is not,
    /// and that gap is why the crew line's squares moved onto their own row.
    /// </summary>
    private const double RealRailWidth = 264;

    /// <summary>The width the plain render helper opens at, wide enough that nothing is squeezed.</summary>
    private const double RenderWidth = 300;

    /// <summary>Render these view models through MainWindow's own SessionList item template.</summary>
    private static ListBox Render(params SessionViewModel[] rows) => RenderAt(RenderWidth, rows);

    private static ListBox RenderAt(double width, params SessionViewModel[] rows)
    {
        var source = new MainWindow();
        var list = new ListBox
        {
            ItemTemplate = source.SessionList.ItemTemplate,
            Styles = { },
            ItemsSource = rows,
        };
        var window = new Window { Content = list, Width = width, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        list.Measure(new Size(width, 700));
        list.Arrange(new Rect(0, 0, width, 700));
        Dispatcher.UIThread.RunJobs();
        return list;
    }

    private static IReadOnlyList<string> VisibleText(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToList();

    // ===== An ordinary row is unchanged =====

    [AvaloniaFact]
    public void AnOrdinaryRow_DrawsNoChevronAndNoCrewLine()
    {
        var solo = Vm("Solo session");
        solo.ApplyRailRow(0, hasCrew: false, isExpanded: false, "", "", NoCrew, "", false);

        var list = Render(solo);

        Assert.Contains("Solo session", VisibleText(list));
        // Nothing of the tree is drawn on a session that has nobody under it.
        Assert.DoesNotContain(VisibleText(list), t => t.Contains("under it"));
        Assert.Empty(VisibleChevrons(list));
        Assert.Empty(VisibleGuideLines(list));
    }

    // ===== A closed crew carries its crew line =====

    [AvaloniaFact]
    public void AClosedCrewRow_DrawsAChevron_TheCrewLineAndTheAge()
    {
        var crew = Vm("Architect");
        crew.ApplyRailRow(0, hasCrew: true, isExpanded: false,
            "9 under it: 4 working, 5 stopped, 0 need you", "5h 29m", Squares(9), "", false);

        var list = Render(crew);
        var text = VisibleText(list);

        Assert.Contains("9 under it: 4 working, 5 stopped, 0 need you", text);
        Assert.Contains("5h 29m", text);
        Assert.Single(VisibleChevrons(list));
        // Closed, so it points RIGHT: there is something folded away behind it.
        Assert.Equal("right", ChevronDirection(list));

        // One square per session under it - the strip is what stops a closed crew hiding a colour, so
        // the count of drawn squares is the claim, not merely that the control exists.
        Assert.Equal(9, DrawnCrewSquares(list));
    }

    [AvaloniaFact]
    public void AnOpenCrewRow_DropsTheCrewLine_BecauseItsSessionsAreThereToRead()
    {
        var crew = Vm("Architect");
        crew.ApplyRailRow(0, hasCrew: true, isExpanded: true,
            "9 under it: 4 working, 5 stopped, 0 need you", "5h 29m", Squares(9), "", false);

        var list = Render(crew);

        Assert.DoesNotContain(VisibleText(list), t => t.Contains("under it"));
        Assert.DoesNotContain("5h 29m", VisibleText(list));
        // The chevron stays - it is how the crew is closed again - and it now points DOWN.
        Assert.Single(VisibleChevrons(list));
        Assert.Equal("down", ChevronDirection(list));
        Assert.Equal(0, DrawnCrewSquares(list));
    }

    /// <summary>
    /// The crew's squares are a STRIP - side by side, in order, with a gap - not a pile. Counting them
    /// would not notice nine squares drawn on top of each other, which is what an items panel that did
    /// not take the horizontal override would produce: a single square, and eight sessions invisible.
    /// </summary>
    [AvaloniaFact]
    public void TheCrewSquaresSitSideBySide_NotOnTopOfEachOther()
    {
        var crew = Vm("Architect");
        crew.ApplyRailRow(0, true, false, "3 under it: 0 working, 3 stopped, 0 need you", "5h 29m",
            Squares(3), "", false);

        var list = Render(crew);

        var xs = list.GetVisualDescendants().OfType<Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width is 8.0 && b.Height is 8.0)
            .Select(b => b.TranslatePoint(new Point(0, 0), list)?.X ?? -1)
            .ToList();

        Assert.Equal(3, xs.Count);
        // Each one begins 10 pixels after the last: an 8 pixel square and a 2 pixel gap.
        Assert.Equal(xs[0] + 10, xs[1]);
        Assert.Equal(xs[1] + 10, xs[2]);
    }

    /// <summary>
    /// THE COUNTS SURVIVE THE REAL RAIL WIDTH. Architect's ruling, 15 September 2026, after the drawn
    /// design turned out to be 545 pixels wide against a rail that opens at 264.
    ///
    /// What this asserts is that the counts are GIVEN the width they asked for - arranged at least as
    /// wide as they measured - which is a layout fact, not a font fact, and is exactly what went wrong
    /// when the strip of squares shared their row: the squares took their space first and the counts
    /// were arranged NARROWER than they wanted, which is what trimming is.
    ///
    /// IT IS NOT A PROOF ABOUT TEXT, AND THE WIDTH ASSERTION IS THE WEAK HALF. Headless Avalonia has no
    /// real font metrics: a 43-character line measures 56 pixels here, so at these metrics everything
    /// fits on one row and nothing is squeezed. I revert-proved this test by putting the strip back
    /// beside the counts - the drawn layout - and the width assertion stayed GREEN. What went red was
    /// the POSITION assertion at the end ("squares at 58, counts at 58").
    ///
    /// So be clear about what each half does. The position assertion is the load-bearing one: it holds
    /// down the structural cause of the trimming, which is the strip sharing the counts' row. The width
    /// assertion cannot see real text and will not catch a font that is merely wider than the stub; it
    /// catches only a layout greedy enough to squeeze the counts even at stub metrics. Whether the
    /// counts trim on a real screen at 264 pixels is UNPROVEN here and needs a human looking at the
    /// running Director.
    /// </summary>
    [AvaloniaFact]
    public void AtTheRealRailWidth_TheCountsAreNotSqueezed_AndTheAgeAndEverySquareStillShow()
    {
        var crew = Vm("Rule Factory - Architect - Epic 9171 implement the BPMN rule catalogue");
        crew.ApplyRailRow(0, true, false,
            "9 under it: 4 working, 5 stopped, 0 need you", "5h 29m", Squares(9), "", false);

        var list = RenderAt(RealRailWidth, crew);

        var counts = list.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "9 under it: 4 working, 5 stopped, 0 need you");
        Assert.True(counts.IsEffectivelyVisible);
        Assert.True(
            counts.Bounds.Width >= WantedWidth(counts),
            $"the counts were squeezed: arranged {counts.Bounds.Width:F0} against a wanted " +
            $"{WantedWidth(counts):F0} - something on their row is taking the width first");

        // The age is still there. It is the only thing saying how long the crew has run, so it was
        // never a candidate for dropping.
        var age = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "5h 29m");
        Assert.True(age.IsEffectivelyVisible);
        Assert.True(age.Bounds.Width >= WantedWidth(age),
            $"the age was squeezed: arranged {age.Bounds.Width:F0} against a wanted {WantedWidth(age):F0}");

        // And the strip is uncapped: every session under the crew still has a square, on its own row.
        Assert.Equal(9, DrawnCrewSquares(list));
        var squareTop = list.GetVisualDescendants().OfType<Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width is 8.0 && b.Height is 8.0)
            .Select(b => b.TranslatePoint(new Point(0, 0), list)!.Value.Y)
            .Min();
        var countsTop = counts.TranslatePoint(new Point(0, 0), list)!.Value.Y;
        Assert.True(squareTop > countsTop,
            $"the squares are meant to sit BELOW the counts, not beside them: squares at {squareTop:F0}, " +
            $"counts at {countsTop:F0}");
    }

    /// <summary>
    /// A CREW TOO WIDE FOR THE RAIL WRAPS - it does not lose sessions off the end. The strip is
    /// deliberately uncapped, so on a rail 264 pixels wide a crew of forty is wider than the row it sits
    /// in, and the question is what happens to the overflow. Clipping would hide sessions, which is the
    /// one thing the strip exists to prevent.
    /// </summary>
    [AvaloniaFact]
    public void ACrewTooWideForTheRail_WrapsOntoAnotherRow_RatherThanLosingSessions()
    {
        var crew = Vm("Architect");
        crew.ApplyRailRow(0, true, false, "40 under it: 10 working, 30 stopped, 0 need you", "2d 3h",
            Squares(40), "", false);

        var list = RenderAt(RealRailWidth, crew);

        // Every one of the forty is drawn.
        Assert.Equal(40, DrawnCrewSquares(list));

        // And they are on more than one row: a 264 pixel rail cannot hold forty 10-pixel squares.
        var rows = list.GetVisualDescendants().OfType<Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width is 8.0 && b.Height is 8.0)
            .Select(b => b.TranslatePoint(new Point(0, 0), list)!.Value.Y)
            .Distinct()
            .ToList();
        Assert.True(rows.Count > 1,
            $"forty squares were laid out on {rows.Count} row(s) at {RealRailWidth} pixels - they are " +
            "being clipped or overflowing rather than wrapping, and sessions are being lost");
    }

    /// <summary>
    /// A ROW ALREADY ON THE SCREEN FOLLOWS ITS NEXT STAMP. Every other test in this file stamps the row
    /// BEFORE the first render, which is the one case where the change notifications in
    /// <c>SessionViewModel.ApplyRailRow</c> do not matter at all - the bindings read the values on their
    /// way up, notified or not. Deleting all twelve of those notifications left the whole shipped suite
    /// green, which made the half of that method the slice added for exactly this purpose a guard
    /// nothing held down.
    ///
    /// In production, stamping before the first render is the case that never happens. <c>RebuildRail</c>
    /// re-stamps rows that are ALREADY DRAWN - every fifteen seconds to tick the crew age, and on every
    /// roster change - and the fast path deliberately keeps the same containers, so nothing else would
    /// move the drawn row. Without the notifications the rail would go silently stale: yesterday's
    /// counts, yesterday's age, a chevron pointing the wrong way, and no sign at all that it had stopped
    /// following.
    /// </summary>
    [AvaloniaFact]
    public void ARowAlreadyOnTheScreen_FollowsItsNextStamp_RatherThanKeepingThePreviousTicksWords()
    {
        var crew = Vm("Architect");
        crew.ApplyRailRow(0, hasCrew: true, isExpanded: false,
            "2 under it: 2 working, 0 stopped, 0 need you", "5h 29m", Squares(2), "", false);

        var list = Render(crew);
        Assert.Contains("2 under it: 2 working, 0 stopped, 0 need you", VisibleText(list));
        Assert.Equal("5h 29m", VisibleText(list).Single(t => t.EndsWith("29m", StringComparison.Ordinal)));
        Assert.Equal(2, DrawnCrewSquares(list));

        // The tick: a third session joined the crew and stopped, and the age moved on.
        crew.ApplyRailRow(0, hasCrew: true, isExpanded: false,
            "3 under it: 2 working, 1 stopped, 0 need you", "5h 44m", Squares(3), "", false);
        Relayout(list);

        var ticked = VisibleText(list);
        Assert.Contains("3 under it: 2 working, 1 stopped, 0 need you", ticked);
        Assert.Contains("5h 44m", ticked);
        Assert.DoesNotContain("2 under it: 2 working, 0 stopped, 0 need you", ticked);
        Assert.DoesNotContain("5h 29m", ticked);
        Assert.Equal(3, DrawnCrewSquares(list));

        // The parts that APPEAR AND DISAPPEAR follow the stamp too, not only the words: opening the crew
        // must take the crew line off a row that is already drawn and turn its chevron down.
        crew.ApplyRailRow(0, hasCrew: true, isExpanded: true,
            "3 under it: 2 working, 1 stopped, 0 need you", "5h 44m", Squares(3), "", false);
        Relayout(list);

        Assert.DoesNotContain(VisibleText(list), t => t.Contains("under it"));
        Assert.Equal(0, DrawnCrewSquares(list));
        Assert.Equal("down", ChevronDirection(list));
    }

    /// <summary>
    /// Run the layout again over a list that is already on the screen, the way the rail's next tick
    /// reaches a row that never left it. This is NOT a re-render: the same containers stay, so anything
    /// that changes has to arrive through a change notification.
    /// </summary>
    private static void Relayout(ListBox list)
    {
        Dispatcher.UIThread.RunJobs();
        list.Measure(new Size(RenderWidth, 700));
        list.Arrange(new Rect(0, 0, RenderWidth, 700));
        Dispatcher.UIThread.RunJobs();
    }

    // ===== A child is indented, behind a guide line, further at each level =====

    [AvaloniaFact]
    public void AChildRow_IsIndentedBehindAGuideLine_AndAGrandchildIsIndentedFurther()
    {
        var child = Vm("Worker");
        child.ApplyRailRow(1, hasCrew: false, isExpanded: false, "", "", NoCrew, "", false);
        var grandchild = Vm("Sub worker");
        grandchild.ApplyRailRow(2, hasCrew: false, isExpanded: false, "", "", NoCrew, "", false);

        var list = Render(child, grandchild);

        // Two guide lines, one per row under a parent.
        Assert.Equal(2, VisibleGuideLines(list).Count);

        // And the indent grows with the depth, so a crew's own crew reads as one level further in.
        Assert.Equal(18.0, child.RailIndentWidth);
        Assert.Equal(36.0, grandchild.RailIndentWidth);
        var indents = list.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Width is 18.0 or 36.0)
            .Select(b => b.Width)
            .OrderBy(w => w)
            .ToList();
        Assert.Equal(new[] { 18.0, 36.0 }, indents);
    }

    // ===== The attention section heading =====

    [AvaloniaFact]
    public void TheFirstRowOfAnAttentionSection_DrawsItsHeading_AndTheRestDoNot()
    {
        var first = Vm("Needs you longest");
        first.ApplyRailRow(0, false, false, "", "", NoCrew, "NEEDS YOU 2", true);
        var second = Vm("Needs you recently");
        second.ApplyRailRow(0, false, false, "", "", NoCrew, "", false);

        var list = Render(first, second);

        // Counted by the heading SLOT, not by its words: a heading whose visibility stopped following
        // the row would render as an EMPTY heading above the second row, which is a blank gap in the
        // rail and would be invisible to a test that only looked for the text.
        var headingSlots = list.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Margin == new Thickness(8, 8, 8, 2))
            .ToList();
        Assert.Single(headingSlots);
        Assert.Equal("NEEDS YOU 2", headingSlots[0].Text);

        // The needs-you heading is red; the others are the muted grey. The rail reads the colour off the
        // view model, so this is the one place the sections are told apart.
        var heading = list.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "NEEDS YOU 2");
        Assert.Equal(Color.Parse(StatusPalette.Red), ((ISolidColorBrush)heading.Foreground!).Color);
    }

    /// <summary>
    /// How wide an element asked to be, in the same terms as its arranged <c>Bounds</c>. DesiredSize
    /// INCLUDES the element's margin and Bounds does not, so comparing the two raw makes any element
    /// with a margin look squeezed when it was given exactly what it wanted.
    /// </summary>
    private static double WantedWidth(Layoutable element) =>
        Math.Max(0, element.DesiredSize.Width - element.Margin.Left - element.Margin.Right);

    // ===== helpers that name what is being counted =====

    /// <summary>
    /// The crew chevrons on screen, found by the tooltip that says what they do rather than by their
    /// geometry text - a Geometry's ToString is a toolkit detail and asserting on it tests the toolkit.
    /// </summary>
    private static IReadOnlyList<Button> VisibleChevrons(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible
                        && ToolTip.GetTip(b) as string == "Open or close the sessions under this one")
            .ToList();

    /// <summary>
    /// Which way the one drawn chevron points, read off the drawn shape rather than off the view model
    /// the shape is supposed to be following. The closed chevron points RIGHT, so it is taller than it is
    /// wide; the open one points DOWN, so it is wider than it is tall. Two paths share the gutter and
    /// exactly one of them is ever visible.
    /// </summary>
    private static string ChevronDirection(Visual root)
    {
        var drawn = VisibleChevrons(root)
            .SelectMany(b => b.GetVisualDescendants().OfType<ShapePath>())
            .Where(p => p.IsEffectivelyVisible && p.Data is not null)
            .ToList();
        Assert.Single(drawn);
        var bounds = drawn[0].Data!.Bounds;
        return bounds.Height > bounds.Width ? "right" : "down";
    }

    /// <summary>The vertical guide lines a crew's sessions hang behind: a 1 pixel wide border.</summary>
    private static IReadOnlyList<Border> VisibleGuideLines(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width is 1.0)
            .ToList();

    /// <summary>The small squares on a crew line: 8 by 8 borders.</summary>
    private static int DrawnCrewSquares(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Border>()
            .Count(b => b.IsEffectivelyVisible && b.Width is 8.0 && b.Height is 8.0);

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
