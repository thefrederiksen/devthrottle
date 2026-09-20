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
/// THESE ARE THE ONLINE DOT - the colour on it IS the Gateway's stamp, so the Gateway's label for the
/// session describes the very state the dot is showing and the two may be read as one sentence. The case
/// where they are NOT about the same moment is the gateway-offline floor, and it lives in
/// <c>OfflineFloorRailColorTests</c>, next to the floor that causes it.
///
/// Plain [Fact]: nothing here touches an Avalonia object. Constructing a
/// <see cref="SessionViewModel.RailDot"/> does not run <c>SessionViewModel</c>'s static initialiser, which
/// builds brushes and must be on the dispatcher thread; CALLING <c>RailDotFor</c> does, which is why the
/// offline tests are [AvaloniaFact].
/// </summary>
public sealed class SessionDotHoverTests
{
    private static SessionColourLegendDto Legend() => SessionColourLegend.Build();

    /// <summary>The ordinary online dot: the rail is showing the colour the Gateway stamped on it.</summary>
    private static SessionViewModel.RailDot Stamped(string colour) => new(colour, ColourIsTheGatewaysStamp: true);

    [Fact]
    public void TheHover_IsTheLegendsNameForTheColour_ThenTheGatewaysLabel()
    {
        // A purple row's stamped label is the Wingman's own line for the session (SessionOrdering.CalmLabel),
        // so the hover names the colour and then says what THIS session is doing.
        var hover = SessionDotHover.For(Stamped("purple"), "Monitor fix round 2 progress", Legend());

        Assert.Equal("Carrying on: Monitor fix round 2 progress", hover);
        // "Carrying on" is the Gateway's own title for purple - read from its legend, not typed here.
        Assert.StartsWith(Legend().Entries.Single(e => e.Colour == "purple").Title, hover, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheLabelSaysNoMoreThanTheTitle_TheHoverIsJustTheTitle()
    {
        // A red session's stamped label is usually "Needs you", which is the legend's title for red. Saying
        // it twice is noise, so the hover says it once.
        Assert.Equal("Needs you", SessionDotHover.For(Stamped("red"), "Needs you", Legend()));
        Assert.Equal("Needs you", SessionDotHover.For(Stamped("red"), "needs you", Legend()));
        Assert.Equal("Needs you", SessionDotHover.For(Stamped("red"), "", Legend()));
    }

    [Fact]
    public void TheFoldsUnknown_ReadsTheGreyEntry_BecauseTheGatewayExplainsThemTogether()
    {
        // The fold emits "unknown" for an activity state it could not read, and it paints the one grey; the
        // Gateway's legend covers both under the grey entry (SessionColourLegend.SharesAnEntry). The hover
        // must follow that aliasing rather than come up empty for a colour the rail is painting.
        var grey = SessionDotHover.For(Stamped("grey"), "", Legend());

        Assert.Equal(grey, SessionDotHover.For(Stamped("unknown"), "", Legend()));
        Assert.NotEqual("", grey);
    }

    [Fact]
    public void ACaseDifferentName_StillFindsItsEntry_BecauseTheNameCrossesTheWire()
    {
        Assert.Equal(SessionDotHover.For(Stamped("blue"), "Working", Legend()),
                     SessionDotHover.For(Stamped("BLUE"), "Working", Legend()));
    }

    /// <summary>
    /// THE NAME ON THE HOVER IS THE GATEWAY'S, NOT ONE THIS BUILD COULD HAVE SUPPLIED. The legend here is a
    /// Gateway newer than this Director: a colour name it never learned, wearing a title that exists in no
    /// constant compiled into it (proved word by word by
    /// <c>SessionColourLegendReadTests.TheWireOnlyWords_AppearInNoCompiledConstant</c>). Every other test in
    /// this file reads <see cref="SessionColourLegend.Build"/>, so none of them could tell a hover reading
    /// the wire from one reading itself.
    /// </summary>
    [Fact]
    public void TheHoverCarriesTheGatewaysOwnWords_EvenWhenThisBuildKnowsNoneOfThem()
    {
        var hover = SessionDotHover.For(
            Stamped(GatewayWordsNoBuildKnows.LaterColour), "", GatewayWordsNoBuildKnows.Legend());

        Assert.Equal(GatewayWordsNoBuildKnows.LaterTitle, hover);
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
        Assert.Equal("Done", SessionDotHover.For(Stamped("a-colour-this-build-never-heard-of"), "Done", Legend()));
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
        Assert.Equal("Snoozed", SessionDotHover.For(Stamped("grey"), "Snoozed", legend: null));
    }

    /// <summary>
    /// The Gateway has given this desktop neither a name nor a label - which is precisely what the
    /// unstamped sentinel is. The honest hover is nothing. It is the one case where the rail says less
    /// than it used to, and that is the point: what it used to say was its own.
    /// </summary>
    [Fact]
    public void WithNeitherANameNorALabel_TheHoverIsEmpty()
    {
        Assert.Equal("", SessionDotHover.For(Stamped(SessionViewModel.UnstampedSentinel), "", Legend()));
        Assert.Equal("", SessionDotHover.For(Stamped(""), null, Legend()));
    }
}
