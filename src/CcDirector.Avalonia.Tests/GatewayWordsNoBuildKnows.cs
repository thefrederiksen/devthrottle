using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// A LEGEND WHOSE EVERY WORD EXISTS ONLY ON THE WIRE - the instrument that tells the Gateway's answer
/// apart from the vocabulary compiled into this Director.
///
/// WHY IT HAD TO BE WRITTEN. The Phase A read test built its fake response by serialising
/// <see cref="SessionColourLegend.Build"/> - this build's own words - and then asserted the entry count,
/// that cyan was present, and that no title was blank. An independent inspector replaced the deserialised
/// response in <c>GatewayClient.GetSessionColourLegendAsync</c> with <c>SessionColourLegend.Build()</c>,
/// throwing away every word the Gateway had sent, and all 100 targeted tests still passed. A test whose
/// expected value is the constant it is trying to rule out cannot rule it out.
///
/// So these words are chosen to appear in NO compiled constant, and
/// <c>SessionColourLegendReadTests.TheWireOnlyWords_AppearInNoCompiledConstant</c> asserts exactly that
/// against the whole of <see cref="SessionColourLegend.Build"/>, so this file cannot quietly drift into
/// agreeing with the build it is meant to distinguish from.
///
/// The explanations are deliberately LONG, because the same legend is what
/// <c>ColourLegendDialogTests</c> mounts to measure: a window that does not wrap its words carries a long
/// sentence off its right-hand edge, which is how this one shipped.
/// </summary>
internal static class GatewayWordsNoBuildKnows
{
    /// <summary>A colour this build does know, wearing words it has never held.</summary>
    internal const string KnownColour = "blue";

    internal const string KnownTitle = "Pressing ahead";

    internal const string KnownMeans =
        "The agent is putting terminal output on the screen this very second and has asked nothing of you, "
        + "so there is no decision here for you to make until it stops of its own accord and says what it did.";

    internal const string KnownAsks = "Not for the moment";

    /// <summary>A colour name a later Gateway learned and this Director never did.</summary>
    internal const string LaterColour = "kingfisher";

    internal const string LaterTitle = "Held at the ford";

    internal const string LaterMeans =
        "A state a later Gateway learned and this Director never did, written out at a length that a window "
        + "handing its words an unbounded width would lay on a single line and carry clean off the right-hand "
        + "edge, where nobody can read the end of it.";

    internal const string LaterAsks = "Yes, and at once";

    /// <summary>A short one, so a mounted test can tell a wrapped sentence from a one-line one.</summary>
    internal const string ShortColour = "slate";

    internal const string ShortTitle = "Put by";

    internal const string ShortMeans = "Set down.";

    internal const string ShortAsks = "No, not now";

    internal const string Note =
        "The reading behind these colours is switched on for this account, in words no build of this app compiled in.";

    /// <summary>Every word of the legend below that a build could otherwise have supplied itself.</summary>
    internal static IReadOnlyList<string> EveryWord =>
    [
        KnownTitle, KnownMeans, KnownAsks,
        LaterTitle, LaterMeans, LaterAsks,
        ShortTitle, ShortMeans, ShortAsks,
        Note,
        "#0A0B0C", "#0D0E0F", "#101112",
    ];

    /// <summary>The legend the Gateway is pretending to serve.</summary>
    internal static SessionColourLegendDto Legend() => new()
    {
        Entries =
        [
            new SessionColourLegendEntryDto
            {
                Colour = KnownColour, Hex = "#0A0B0C", Title = KnownTitle, Means = KnownMeans, AsksForYou = KnownAsks,
            },
            new SessionColourLegendEntryDto
            {
                Colour = LaterColour, Hex = "#0D0E0F", Title = LaterTitle, Means = LaterMeans, AsksForYou = LaterAsks,
            },
            new SessionColourLegendEntryDto
            {
                Colour = ShortColour, Hex = "#101112", Title = ShortTitle, Means = ShortMeans, AsksForYou = ShortAsks,
            },
        ],
        VerdictNote = Note,
    };

    /// <summary>That legend on the wire, serialised the way the Gateway's own route serialises it.</summary>
    internal static string AsTheRouteSerialisesIt() =>
        JsonSerializer.Serialize(Legend(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
