using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
    /// The swatch is the hex the GATEWAY resolved and sent beside the name, so the legend's dot and the
    /// session's dot are the same pixel by construction - never a colour this build looked up for itself.
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
}
