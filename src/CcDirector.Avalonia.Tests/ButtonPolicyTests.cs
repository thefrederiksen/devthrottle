using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CcDirector.Avalonia;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The application's button policy, in Styles/ButtonStyles.axaml - the one place that decides where a
/// button's label sits and what colour the button is under the pointer.
///
/// THE TWO DEFECTS IT EXISTS FOR, both visible in the owner's screenshot of 20 October 2026:
///
///   1. THE LABEL WAS PRINTED AGAINST THE TOP EDGE. Avalonia leaves VerticalContentAlignment at
///      Stretch, so a button taller than its text - because it set a Height, or because its container
///      stretched it - handed the label the whole box, and the label drew its single line at the TOP.
///      On the Stop window that was 14 pixels of text in a 28 pixel button with all the slack below it.
///      170 buttons across 54 files were in that state.
///
///   2. A COLOURED BUTTON TURNED GREY UNDER THE POINTER. The Fluent theme's hover and pressed states
///      set a Background on the template's ContentPresenter - a different object from the button - so
///      the button's own Background, bound into that presenter at rest, was painted over and lost. A
///      #007ACC primary button became #3A3A3B on hover. 314 buttons set a colour that was being thrown
///      away like this.
///
/// WHY THESE ARE WORTH HAVING. The policy is four styles in one file; deleting it silently returns
/// every button in the application to both defects, and nothing else would notice. The harness loads
/// that same file (see HeadlessTestApp) rather than a copy, so these fail if the policy is removed,
/// renamed, or stops being applied.
///
/// WHAT THEY DO NOT COVER: colour contrast, whether 0.86 opacity reads as a hover cue to a person, and
/// anything about the Cockpit or the mobile app, which share none of this markup.
/// </summary>
public class ButtonPolicyTests
{
    /// <summary>One device independent pixel. An odd leftover pixel is invisible; the defect was 14.</summary>
    private const double Tolerance = 1.0;

    private static Window ShowHosting(Control content, double width = 400, double height = 200)
    {
        var w = new Window { Width = width, Height = height, Content = content };
        w.Show();
        w.Measure(new Size(width, height));
        w.Arrange(new Rect(0, 0, width, height));
        w.UpdateLayout();
        return w;
    }

    private static ContentPresenter PresenterOf(Button b) =>
        b.GetVisualDescendants().OfType<ContentPresenter>().First();

    private static void SetState(Button b, string pseudoClass, bool on) =>
        ((IPseudoClasses)b.Classes).Set(pseudoClass, on);

    // ---------------------------------------------------------------- the label's position

    /// <summary>
    /// The policy itself: a plain button, given more height than its text needs, centres the label.
    /// Without the style this is Stretch and the label is drawn at the top - the original defect, on a
    /// button nobody has touched.
    /// </summary>
    [AvaloniaFact]
    public void AnyButton_CentresItsLabel_WithoutBeingToldTo()
    {
        var button = new Button { Content = "Stop the session", Height = 30, Padding = new Thickness(14, 0) };
        ShowHosting(button);

        Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);

        var (above, below) = GlyphGaps(button);
        Assert.True(Math.Abs(above - below) <= Tolerance,
            $"the label is not centred: {above:F1}px above, {below:F1}px below.");
    }

    /// <summary>A button that asks for something else still gets it - a policy, not a straitjacket.</summary>
    [AvaloniaFact]
    public void AButtonThatSetsItsOwnAlignment_Wins()
    {
        var button = new Button
        {
            Content = "Top please",
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ShowHosting(button);

        Assert.Equal(VerticalAlignment.Top, button.VerticalContentAlignment);
    }

    // ---------------------------------------------------------------- the colour under the pointer

    /// <summary>
    /// The decisive one for defect 2. The button is blue; hovering it must leave it blue. Before the
    /// policy the presenter's Background became the theme's own colour and the blue was gone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(":pointerover")]
    [InlineData(":pressed")]
    public void AColouredButton_KeepsItsColour_WhenThePointerIsOnIt(string state)
    {
        var blue = Color.Parse("#007ACC");
        var button = new Button { Content = "Stop the session", Height = 30, Background = new SolidColorBrush(blue) };
        var window = ShowHosting(button);

        SetState(button, state, true);
        window.UpdateLayout();

        var painted = Assert.IsAssignableFrom<ISolidColorBrush>(PresenterOf(button).Background);
        Assert.Equal(blue, painted.Color);
    }

    /// <summary>The state still SAYS something - a button that looks identical hovered is its own defect.</summary>
    [AvaloniaFact]
    public void AColouredButton_StillShowsThatThePointerIsOnIt()
    {
        var button = new Button { Content = "Stop", Height = 30, Background = new SolidColorBrush(Color.Parse("#007ACC")) };
        var window = ShowHosting(button);
        var atRest = button.Opacity;

        SetState(button, ":pointerover", true);
        window.UpdateLayout();

        Assert.True(button.Opacity < atRest,
            $"hovering changed nothing: opacity {atRest} at rest and {button.Opacity} under the pointer.");
    }

    /// <summary>
    /// The exception that has to be said out loud. A transparent button has no colour to brighten, so
    /// re-asserting its own background would leave a three-dot menu or an icon button with NO hover cue
    /// at all. The 42 of them carry Classes="ghost" and keep a visible wash instead.
    /// </summary>
    [AvaloniaFact]
    public void AGhostButton_GetsAVisibleWash_NotItsOwnTransparent()
    {
        var button = new Button { Content = "...", Background = Brushes.Transparent, Height = 26 };
        button.Classes.Add("ghost");
        var window = ShowHosting(button);

        SetState(button, ":pointerover", true);
        window.UpdateLayout();

        // The POLICY'S OWN wash, not merely "something visible". The Fluent theme also washes a hovered
        // button, so asserting "not transparent" would pass with the policy deleted and guard nothing.
        // #505050 is the ButtonHover docs/VisualStyle.md already defines.
        var painted = Assert.IsAssignableFrom<ISolidColorBrush>(PresenterOf(button).Background);
        Assert.Equal(Color.Parse("#505050"), painted.Color);
        Assert.Equal(1.0, button.Opacity);
    }

    /// <summary>
    /// THE SHIPPED APPLICATION loads the policy, not just the test harness.
    ///
    /// Everything above runs against HeadlessTestApp, which includes Styles/ButtonStyles.axaml by the
    /// same avares path App.axaml uses. That proves the policy works and proves nothing about whether
    /// the real application still points at it: a StyleInclude with a wrong or stale Source compiles
    /// happily and fails at RUNTIME, where no test above would be looking. This resolves App.axaml's
    /// own include and makes that failure a red build instead of a shipped regression.
    ///
    /// Initialize() only calls AvaloniaXamlLoader.Load; the application's real startup work is in
    /// OnFrameworkInitializationCompleted, which is not called here.
    /// </summary>
    [AvaloniaFact]
    public void TheApplicationItself_CarriesTheButtonPolicy()
    {
        var app = new App();
        app.Initialize();

        // The XAML compiler resolves a StyleInclude into the Styles object it points at, so the policy
        // arrives nested inside App.Styles rather than as a StyleInclude to look up by name. Walk it and
        // look for the setter itself - that survives the include being inlined, renamed or re-nested.
        var centresButtonLabels = Flatten(app.Styles).Any(style =>
            style.Selector?.ToString().Contains("Button", StringComparison.OrdinalIgnoreCase) == true &&
            style.Setters.OfType<Setter>().Any(setter =>
                setter.Property?.Name == nameof(Button.VerticalContentAlignment) &&
                Equals(setter.Value, VerticalAlignment.Center)));

        Assert.True(centresButtonLabels,
            "App.axaml no longer carries the button policy - every button in the application has " +
            "quietly returned to a top-aligned label and a grey hover. Check the StyleInclude for " +
            "Styles/ButtonStyles.axaml.");
    }

    /// <summary>Every Style in a styles tree, however deeply the includes nested it.</summary>
    private static IEnumerable<Style> Flatten(IEnumerable<IStyle> styles)
    {
        foreach (var style in styles)
        {
            if (style is Style s) yield return s;
            if (style is IEnumerable<IStyle> nested)
                foreach (var child in Flatten(nested)) yield return child;
        }
    }

    // ---------------------------------------------------------------- the real windows

    /// <summary>
    /// The sweep. Every text label on every button of these real windows, measured where it was actually
    /// arranged. The unit tests above prove the policy; this proves the windows the owner opens get it.
    /// </summary>
    public static TheoryData<string> Windows() => new()
    {
        nameof(StopSessionDialog), nameof(ConfirmDialog), nameof(RenameSessionDialog),
        nameof(MessageDialog), nameof(WorkflowVariableNameDialog), nameof(ColourLegendDialog),
        nameof(HostedAiUnavailableDialog),
    };

    private static Window Build(string name) => name switch
    {
        nameof(StopSessionDialog) => new StopSessionDialog(
            "9c41e7a2-1111-2222-3333-444455556666", "cc-consult - test",
            (_, _) => throw new InvalidOperationException("no stop is sent by a layout test")),
        nameof(ConfirmDialog) => new ConfirmDialog("Director", "Remove this repository?"),
        nameof(RenameSessionDialog) => new RenameSessionDialog("cc-consult - test"),
        nameof(MessageDialog) => new MessageDialog("Director", "Something worth saying."),
        nameof(WorkflowVariableNameDialog) => new WorkflowVariableNameDialog("key", "value"),
        nameof(ColourLegendDialog) => new ColourLegendDialog(),
        nameof(HostedAiUnavailableDialog) => new HostedAiUnavailableDialog(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no builder"),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Windows))]
    public void EveryLabelInTheWindow_IsCentredInItsButton(string windowName)
    {
        var window = Build(windowName);
        window.Show();
        window.Measure(new Size(640, 700));
        window.Arrange(new Rect(0, 0, 640, 700));
        window.UpdateLayout();

        var offenders = new List<string>();
        var measured = 0;
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            // Only buttons whose content is one run of text. A button holding a panel has its own
            // layout and is not what this measures.
            var labels = button.GetVisualDescendants().OfType<TextBlock>().ToList();
            if (labels.Count != 1 || button.Bounds.Height <= 0) continue;

            measured++;
            var (above, below) = GlyphGaps(button);
            if (Math.Abs(above - below) > Tolerance)
                offenders.Add($"'{labels[0].Text}' in a {button.Bounds.Height:F0}px button: " +
                              $"{above:F1}px above, {below:F1}px below");
        }

        // An empty sweep is a broken instrument, not a clean result.
        Assert.True(measured > 0, $"{windowName} offered no text button to measure - the sweep proved nothing.");
        Assert.True(offenders.Count == 0,
            $"{windowName} has {offenders.Count} label(s) off centre:\n  " + string.Join("\n  ", offenders));
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// Where the GLYPHS sit in the space the button gives them, as the gap above and the gap below.
    ///
    /// MEASURE THE GLYPH BAND, NEVER THE LABEL'S BOUNDS. A label left at the default Stretch is arranged
    /// to fill the button, so its Bounds ARE the button's: that reports zero above and zero below while
    /// the text is visibly against the top edge, and a check reading it certifies the defect as fixed.
    /// The text is drawn at the top of whatever box it is given and occupies DesiredSize, so the gaps
    /// are measured from that. The reading is correct either way - centred, the box shrinks to the glyphs.
    ///
    /// AND MEASURE AGAINST THE CONTENT BOX, not the button's outer edges. Fluent's default button padding
    /// is asymmetric - 5 above and 6 below - so a button that sets no padding of its own is centred
    /// correctly and still sits one pixel off its outer centre. Subtracting the border and padding asks
    /// the question that actually matters, "is the label centred in the room it was given", and keeps the
    /// tolerance at a single pixel instead of loosening it to swallow the difference.
    /// </summary>
    private static (double Above, double Below) GlyphGaps(Button button)
    {
        var label = button.GetVisualDescendants().OfType<TextBlock>().First();
        var topLeft = label.TranslatePoint(new Point(0, 0), button)
                      ?? throw new InvalidOperationException($"the label of '{button.Name}' was never arranged");

        var inset = button.Padding + button.BorderThickness;
        var above = topLeft.Y - inset.Top;
        var below = (button.Bounds.Height - inset.Bottom) - (topLeft.Y + label.DesiredSize.Height);
        return (above, below);
    }
}
