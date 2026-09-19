using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE RAIL'S COLOUR HOVER SAYS ONLY WHAT THE GATEWAY SAID - the owner's ruling on the Session Cards
/// mission, in his words: <em>"Nobody should give any local reason for anything. It should always be the
/// gateway ... if you hover over the color, it should show you what that color means."</em>
///
/// These drive <see cref="SessionDotHover"/> against the REAL legend the Gateway serves
/// (<see cref="SessionColourLegend.Build"/> - the same object <c>GET /gateway/session-colours</c>
/// returns), not a hand-made one. A hover built from a legend typed into this file would agree with
/// itself and prove nothing about the words a person actually sees.
///
/// Plain [Fact]: nothing here touches an Avalonia object.
/// </summary>
public sealed class SessionDotHoverTests
{
    private static SessionColourLegendDto Legend() => SessionColourLegend.Build();

    [Fact]
    public void TheHover_IsTheLegendsNameForTheColour_ThenTheGatewaysLabel()
    {
        // A purple row's stamped label is the Wingman's own line for the session (SessionOrdering.CalmLabel),
        // so the hover names the colour and then says what THIS session is doing.
        var hover = SessionDotHover.For("purple", "Monitor fix round 2 progress", Legend());

        Assert.Equal("Carrying on: Monitor fix round 2 progress", hover);
        // "Carrying on" is the Gateway's own title for purple - read from its legend, not typed here.
        Assert.StartsWith(Legend().Entries.Single(e => e.Colour == "purple").Title, hover, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheLabelSaysNoMoreThanTheTitle_TheHoverIsJustTheTitle()
    {
        // A red session's stamped label is usually "Needs you", which is the legend's title for red. Saying
        // it twice is noise, so the hover says it once.
        Assert.Equal("Needs you", SessionDotHover.For("red", "Needs you", Legend()));
        Assert.Equal("Needs you", SessionDotHover.For("red", "needs you", Legend()));
        Assert.Equal("Needs you", SessionDotHover.For("red", "", Legend()));
    }

    [Fact]
    public void TheFoldsUnknown_ReadsTheGreyEntry_BecauseTheGatewayExplainsThemTogether()
    {
        // The fold emits "unknown" for an activity state it could not read, and it paints the one grey; the
        // Gateway's legend covers both under the grey entry (SessionColourLegend.SharesAnEntry). The hover
        // must follow that aliasing rather than come up empty for a colour the rail is painting.
        var grey = SessionDotHover.For("grey", "", Legend());

        Assert.Equal(grey, SessionDotHover.For("unknown", "", Legend()));
        Assert.NotEqual("", grey);
    }

    [Fact]
    public void ACaseDifferentName_StillFindsItsEntry_BecauseTheNameCrossesTheWire()
    {
        Assert.Equal(SessionDotHover.For("blue", "Working", Legend()),
                     SessionDotHover.For("BLUE", "Working", Legend()));
    }

    /// <summary>
    /// A DIRECTOR OLDER THAN ITS GATEWAY - the case this whole mission exists for. The fold has learned a
    /// colour this build was never taught, so the dot goes neutral and the legend has no entry for the
    /// name. The hover must still say what the Gateway said about the SESSION, because a label is words
    /// and needs no palette to render.
    /// </summary>
    [Fact]
    public void AColourTheLegendDoesNotCarry_StillShowsTheGatewaysLabel()
    {
        Assert.Equal("Done", SessionDotHover.For("a-colour-this-build-never-heard-of", "Done", Legend()));
    }

    /// <summary>
    /// No legend read yet - a Director that has only just connected, or one with no Gateway at all. The
    /// hover falls back to the stamped label, which is still the Gateway's word. It must NOT fall back to
    /// a name this build could have compiled in: a legend baked into an old build explains the colours
    /// THAT build knows, which is the version gap this mission closes.
    /// </summary>
    [Fact]
    public void WithNoLegendYet_TheHoverIsTheGatewaysLabelAlone()
    {
        Assert.Equal("Snoozed", SessionDotHover.For("grey", "Snoozed", legend: null));
    }

    /// <summary>
    /// The Gateway has given this desktop neither a name nor a label - which is precisely what the
    /// unstamped sentinel is. The honest hover is nothing. It is the one case where the rail says less
    /// than it used to, and that is the point: what it used to say was its own.
    /// </summary>
    [Fact]
    public void WithNeitherANameNorALabel_TheHoverIsEmpty()
    {
        Assert.Equal("", SessionDotHover.For(SessionViewModel.UnstampedSentinel, "", Legend()));
        Assert.Equal("", SessionDotHover.For(null, null, Legend()));
    }
}
