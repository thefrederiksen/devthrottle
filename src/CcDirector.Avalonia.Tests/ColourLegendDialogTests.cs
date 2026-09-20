using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// "What the colours mean" on the desktop RENDERS THE GATEWAY AND WRITES NOTHING.
///
/// The window's whole claim is that every colour, every swatch and every sentence it shows came from
/// <c>GET /gateway/session-colours</c>. These drive it with the REAL legend the Gateway serves
/// (<see cref="SessionColourLegend.Build"/>) and assert that each string on a row is one of the Gateway's
/// own, character for character - not that a row is merely non-empty, which would pass on a window that
/// had quietly written its own words.
/// </summary>
public sealed class ColourLegendDialogTests
{
    private static SessionColourLegendEntryDto Entry(string colour) =>
        SessionColourLegend.Build().Entries.Single(e => e.Colour == colour);

    /// <summary>Every piece of text anywhere under a control, in order.</summary>
    private static List<string> TextsOf(Control control) =>
        control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void ARow_ShowsTheGatewaysTitle_ItsAsksForYou_AndItsSentence_AndNothingElse()
    {
        var entry = Entry("cyan");

        var texts = TextsOf(ColourLegendDialog.Row(entry));

        Assert.Equal(new[] { entry.Title, $"Asks for you: {entry.AsksForYou}", entry.Means }, texts);
    }

    /// <summary>
    /// The swatch is the hex the GATEWAY resolved and sent beside the name - never a colour this build
    /// looked up for itself. It matches the session's dot for every colour this build knows; for one it does
    /// NOT know, the rail paints <see cref="StatusPalette.Neutral"/> while this swatch still shows the
    /// Gateway's hex, which is the version gap the mission exists for and the reason this window can show
    /// the real colour when the rail cannot.
    /// </summary>
    [AvaloniaFact]
    public void ARowsSwatch_IsTheHexTheGatewaySent()
    {
        var entry = Entry("error");

        var swatch = ColourLegendDialog.Row(entry).GetLogicalDescendants().OfType<Border>().First();

        Assert.Equal(Color.Parse(entry.Hex), ((ISolidColorBrush)swatch.Background!).Color);
    }

    /// <summary>
    /// Every colour the Gateway explains renders, including any this build's own palette has never heard
    /// of. The window must not filter the legend down to the names it recognises - that would give an old
    /// Director a legend missing exactly the colour it cannot paint, which is the one it is being opened
    /// to ask about.
    /// </summary>
    [AvaloniaFact]
    public void EveryColourTheGatewayExplains_Renders_EvenOneThisBuildCannotPaint()
    {
        var invented = new SessionColourLegendEntryDto
        {
            Colour = "a-colour-this-build-never-heard-of",
            Hex = "#123456",
            Title = "Something later",
            Means = "A state a later Gateway learned and this Director never did.",
            AsksForYou = SessionColourLegend.AsksNo,
        };

        var texts = TextsOf(ColourLegendDialog.Row(invented));

        Assert.Contains("Something later", texts);
        Assert.Contains("A state a later Gateway learned and this Director never did.", texts);
    }

    // ===== The window as it is actually laid out, not as a row's strings read =====

    /// <summary>
    /// Fill a MOUNTED window and let it lay itself out, so what follows measures the real thing.
    /// </summary>
    private static ColourLegendDialog Mounted(SessionColourLegendDto legend)
    {
        var dialog = new ColourLegendDialog();
        // Showing runs the window's own Loaded handler, which finds no running Director here and writes its
        // "still starting" line into the status row - so the legend goes in after, exactly as a real read
        // landing late would put it there.
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        dialog.Render(legend);
        dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static List<TextBlock> WordsOnScreen(ColourLegendDialog dialog) =>
        dialog.ColourRows.GetLogicalDescendants().OfType<TextBlock>().ToList();

    /// <summary>
    /// THE WINDOW'S WORDS FIT INSIDE THE WINDOW. This is the one thing "What the colours mean" exists to do,
    /// and it shipped unable to do it: the dot and the words column sat in a HORIZONTAL StackPanel, which
    /// measures its children with unbounded width, so <c>TextWrapping.Wrap</c> had no width to wrap at.
    /// Measured by an independent inspector, nine of the ten explanations were wider than the 580-pixel rows
    /// viewport, the widest at 1570 - the ends of the Gateway's sentences were simply off the screen.
    ///
    /// THE TEST THAT MISSED IT enumerated the strings on an UNMOUNTED row. Every string was the Gateway's
    /// and every string was correct; none of them fitted. So this one mounts the window, lets it lay itself
    /// out and asserts measured bounds - the only kind of assertion that could have caught it.
    ///
    /// These are headless font measurements rather than a photograph of a real screen, so they do not claim
    /// exact native pixel widths. What they do establish is the mechanism: the text is handed a finite width
    /// and wraps at it.
    /// </summary>
    [AvaloniaFact]
    public void EveryExplanationFitsInsideTheWindow_RatherThanRunningOffIt()
    {
        var dialog = Mounted(GatewayWordsNoBuildKnows.Legend());
        try
        {
            var viewport = dialog.ColourRows.Bounds.Width;
            Assert.True(viewport > 0, "the rows panel was never laid out, so nothing below measured anything");

            var words = WordsOnScreen(dialog);
            Assert.NotEmpty(words);

            foreach (var block in words)
                Assert.True(block.Bounds.Width <= viewport + 0.5,
                    $"\"{block.Text}\" measures {block.Bounds.Width:F0} wide in a {viewport:F0} viewport - "
                    + "its end is off the right-hand edge of the window");
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// And it fits because it WRAPPED, not because it was cut short. A long explanation takes more vertical
    /// room than a short one; if the fix above had been a clip or an ellipsis, both would be one line high
    /// and this fails.
    /// </summary>
    [AvaloniaFact]
    public void ALongExplanationWraps_InsteadOfBeingCutShort()
    {
        var dialog = Mounted(GatewayWordsNoBuildKnows.Legend());
        try
        {
            var words = WordsOnScreen(dialog);
            var oneLine = words.Single(t => t.Text == GatewayWordsNoBuildKnows.ShortMeans);
            var many = words.Single(t => t.Text == GatewayWordsNoBuildKnows.LaterMeans);

            Assert.True(oneLine.Bounds.Height > 0, "the short explanation was never laid out");
            Assert.True(many.Bounds.Height > oneLine.Bounds.Height * 1.5,
                $"the long explanation is {many.Bounds.Height:F0} tall against the short one's "
                + $"{oneLine.Bounds.Height:F0} - it did not wrap onto further lines");
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// What is on the screen is what came down the wire, character for character - including a colour name
    /// this build has never heard of and an explanation no constant in it contains. The words are proved
    /// wire-only in <c>SessionColourLegendReadTests.TheWireOnlyWords_AppearInNoCompiledConstant</c>.
    /// </summary>
    [AvaloniaFact]
    public void TheWordsOnTheWindowAreTheGatewaysOwn_EvenWhenThisBuildKnowsNoneOfThem()
    {
        var dialog = Mounted(GatewayWordsNoBuildKnows.Legend());
        try
        {
            var texts = WordsOnScreen(dialog).Select(t => t.Text ?? "").ToList();

            Assert.Equal(
                new[]
                {
                    GatewayWordsNoBuildKnows.KnownTitle, $"Asks for you: {GatewayWordsNoBuildKnows.KnownAsks}", GatewayWordsNoBuildKnows.KnownMeans,
                    GatewayWordsNoBuildKnows.LaterTitle, $"Asks for you: {GatewayWordsNoBuildKnows.LaterAsks}", GatewayWordsNoBuildKnows.LaterMeans,
                    GatewayWordsNoBuildKnows.ShortTitle, $"Asks for you: {GatewayWordsNoBuildKnows.ShortAsks}", GatewayWordsNoBuildKnows.ShortMeans,
                },
                texts);
            Assert.Equal(GatewayWordsNoBuildKnows.Note, dialog.VerdictNoteText.Text);
        }
        finally
        {
            dialog.Close();
        }
    }
}
