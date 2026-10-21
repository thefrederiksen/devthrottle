using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Stop window's two buttons, measured rather than eyeballed.
///
/// THE DEFECT THIS PINS AGAINST. The owner photographed the window on 20 October 2026: "Cancel" and
/// "Stop the session" were printed hard against the TOP edge of their buttons, ascenders touching the
/// border, with an empty band of roughly eleven pixels underneath. It reads as a label cut off by its
/// own button.
///
/// The cause is not this window's alone. Avalonia leaves <c>VerticalContentAlignment</c> at Stretch, so
/// a button given a fixed Height hands its label the whole box and the label draws its single line at
/// the TOP of what it was given. Every button in this application that sets a Height and does not set
/// the alignment does this; docs/VisualStyle.md's button recipes do not mention the property, so a
/// button written to the guide inherits the defect. ConfirmDialog sets it by hand and looks right,
/// which is the difference.
///
/// WHAT THIS COVERS, and what it does NOT. It measures where the label was arranged inside the button,
/// which is the thing that was wrong. It says nothing about colour: the owner's picture also shows the
/// blue primary button rendered grey, and that is Avalonia's pointer-over state painting the inner
/// presenter over the Background set here - a separate matter, not covered by anything below.
/// </summary>
public class StopSessionDialogButtonLayoutTests
{
    private const string SessionId = "9c41e7a2-1111-2222-3333-444455556666";

    /// <summary>How far the label's centre may sit from the button's own centre before it reads as
    /// misaligned. One device independent pixel: an odd leftover pixel is invisible, eleven is the
    /// defect.</summary>
    private const double Tolerance = 1.0;

    private static StopSessionDialog ShownDialog()
    {
        var dialog = new StopSessionDialog(
            SessionId,
            "throwaway",
            (_, _) => throw new InvalidOperationException("no stop is sent by a layout test"));
        dialog.Show();
        dialog.Measure(new Size(520, 400));
        dialog.Arrange(new Rect(0, 0, 520, 400));
        dialog.UpdateLayout();
        return dialog;
    }

    private static Button ButtonNamed(StopSessionDialog dialog, string name) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

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
    private static (double Above, double Below) GapsAroundTheLabel(Button button)
    {
        var label = button.GetVisualDescendants().OfType<TextBlock>().First();
        var topLeft = label.TranslatePoint(new Point(0, 0), button)
                      ?? throw new InvalidOperationException($"the label of '{button.Name}' was never arranged");

        var inset = button.Padding + button.BorderThickness;
        var above = topLeft.Y - inset.Top;
        var below = (button.Bounds.Height - inset.Bottom) - (topLeft.Y + label.DesiredSize.Height);
        return (above, below);
    }

    [AvaloniaTheory]
    [InlineData("BtnStop")]
    [InlineData("BtnClose")]
    public void TheLabelIsCentredInItsButton_NotPrintedAgainstTheTopEdge(string buttonName)
    {
        var dialog = ShownDialog();
        var button = ButtonNamed(dialog, buttonName);

        var (above, below) = GapsAroundTheLabel(button);

        Assert.True(
            Math.Abs(above - below) <= Tolerance,
            $"'{button.Content}' is not centred in its button: {above:F1}px above the label and " +
            $"{below:F1}px below it, in a button {button.Bounds.Height:F0}px tall. The owner's " +
            "20 October picture had the label against the top edge with the whole gap underneath.");
    }

    /// <summary>
    /// The label must also FIT. Centring a label that is taller than its button would split the
    /// overflow evenly and clip it at both edges instead of one, which is not a fix.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("BtnStop")]
    [InlineData("BtnClose")]
    public void TheLabelFitsInsideItsButton(string buttonName)
    {
        var dialog = ShownDialog();
        var button = ButtonNamed(dialog, buttonName);

        var (above, below) = GapsAroundTheLabel(button);

        Assert.True(above >= 0 && below >= 0,
            $"'{button.Content}' overflows its button: {above:F1}px above, {below:F1}px below.");
    }

    /// <summary>
    /// The label is not squeezed sideways either. This one is a GUARD, not a fix: the horizontal
    /// padding was already correct in the markup the owner photographed (12 pixels each side), and
    /// this passes before and after. It is here because ConfirmDialog carries a comment about a fixed
    /// Width silently clipping a caller's label down to "Done - I am s", and "Stop the session" is the
    /// longest label this window has.
    /// </summary>
    [AvaloniaFact]
    public void TheLongLabelKeepsItsPaddingOnBothSides()
    {
        var dialog = ShownDialog();
        var button = ButtonNamed(dialog, "BtnStop");
        var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
        var topLeft = label.TranslatePoint(new Point(0, 0), button)
                      ?? throw new InvalidOperationException("the label was never arranged");

        var left = topLeft.X;
        var right = button.Bounds.Width - (topLeft.X + label.DesiredSize.Width);

        Assert.True(left >= 10 && right >= 10,
            $"'{button.Content}' is squeezed against its edges: {left:F1}px left, {right:F1}px right.");
    }
}
