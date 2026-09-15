using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The rail's own remembered state - which order the switch is on, and which crews the user has opened -
/// round-tripped through config.json's shape.
///
/// These drive the PURE halves (<see cref="SessionRailConfig.ReadFrom"/> and
/// <see cref="SessionRailConfig.WriteInto"/>) rather than the file, on purpose: a test that writes the
/// real config.json writes the CURRENT USER'S config.json, and the suite runs in parallel. The reading
/// and writing rules are the part that can be wrong; the file access is the same three lines
/// <c>SidebarConfig</c> has always used beside it.
/// </summary>
public sealed class SessionRailConfigTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ACleanConfig_OpensOnMyOrder_WithEveryCrewClosed()
    {
        // The Director's default, settled in the design: my order, nothing expanded. A user who has
        // never touched the switch must not be handed the phone's default.
        var (order, crews) = SessionRailConfig.ReadFrom(Parse("{}"));

        Assert.Equal(SessionRailConfig.MyOrder, order);
        Assert.Empty(crews);
    }

    [Fact]
    public void TheOrderAndTheOpenCrews_SurviveARoundTrip()
    {
        var root = new JsonObject();
        SessionRailConfig.WriteInto(root, SessionRailConfig.Attention, new[] { "crew-b", "crew-a" });

        var (order, crews) = SessionRailConfig.ReadFrom(Parse(root.ToJsonString()));

        Assert.Equal(SessionRailConfig.Attention, order);
        Assert.Equal(new[] { "crew-a", "crew-b" }, crews.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WritingTheRailState_LeavesEverythingElseInConfigAlone()
    {
        // config.json is shared with the sidebar's own collapsed state and whatever else lives there.
        var root = (JsonObject)JsonNode.Parse("""{"sidebar_collapsed": true, "something_else": 7}""")!;

        SessionRailConfig.WriteInto(root, SessionRailConfig.MyOrder, Array.Empty<string>());

        Assert.True(root["sidebar_collapsed"]!.GetValue<bool>());
        Assert.Equal(7, root["something_else"]!.GetValue<int>());
    }

    [Fact]
    public void AnOrderNameThisBuildDoesNotKnow_ReadsAsMyOrder()
    {
        // A value written by a newer build, or a hand-edited file. My order is what the rail has always
        // opened on, so an unknown name lands there rather than on a guess.
        var (order, _) = SessionRailConfig.ReadFrom(Parse("""{"session_rail_order": "by-machine"}"""));
        Assert.Equal(SessionRailConfig.MyOrder, order);
    }

    [Fact]
    public void AMalformedOpenCrewList_IsNotFatal_AndKeepsTheEntriesItCanRead()
    {
        // A wrong type where the list should be, and rubbish inside a list that is the right type. A
        // remembered chevron is not worth failing a Director's startup over.
        var (_, none) = SessionRailConfig.ReadFrom(Parse("""{"session_rail_expanded_crews": "crew-a"}"""));
        Assert.Empty(none);

        var (_, some) = SessionRailConfig.ReadFrom(
            Parse("""{"session_rail_expanded_crews": ["crew-a", 4, null, "  ", "crew-b"]}"""));
        Assert.Equal(new[] { "crew-a", "crew-b" }, some.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
