using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Factory;

/// <summary>
/// The factory map (issue #3383): a factory publishes the map its own tool drew, the Gateway keeps the latest one per
/// factory and refuses a broken one whole, and the Map tab adds each agent's status from the same card the
/// Factories tab shows.
/// </summary>
public sealed class FactoryMapTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTime Now = new(2026, 9, 24, 18, 2, 0, DateTimeKind.Utc);

    internal static PublishFactoryMapRequest Map(string factory = "website-business") => new()
    {
        Factory = factory,
        Title = "Website Business",
        Source = "factory.yaml in cc-consult at 687a1e9",
        Width = 800,
        Height = 440,
        Nodes = new()
        {
            new() { Id = "scout", Kind = FactoryMapNodeKind.Agent, Title = "Scout", Lines = new() { "daily 07:00" },
                X = 100, Y = 200, Width = 90, Height = 40,
                Spec = new() { new() { Label = "Inputs", Text = "market: small businesses on Facebook only" } } },
            new() { Id = "sender", Kind = FactoryMapNodeKind.Agent, Title = "Sender", Lines = new() { "after a chain" },
                X = 300, Y = 200, Width = 90, Height = 40 },
            new() { Id = "bookkeeper", Kind = FactoryMapNodeKind.Agent, Title = "Bookkeeper", Built = false,
                X = 500, Y = 100, Width = 90, Height = 40 },
            new() { Id = "owner", Kind = FactoryMapNodeKind.Owner, Title = "You", X = 500, Y = 300, Width = 120, Height = 40 },
        },
        Edges = new()
        {
            Edge("scout", "sender", FactoryMapEdgeKind.Chain, FactoryMapResults.Succeeded),
            Edge("scout", "owner", FactoryMapEdgeKind.Notify, FactoryMapResults.Failed),
            Edge("sender", "owner", FactoryMapEdgeKind.Asks, null),
        },
    };

    private static FactoryMapEdgeRequest Edge(string from, string to, string kind, string? result) => new()
    {
        From = from, To = to, Kind = kind, Result = result, Label = kind,
        Points = new() { new[] { 145.0, 200 }, new[] { 180.0, 200 }, new[] { 220.0, 200 }, new[] { 245.0, 200 } },
        Tip = new[] { 255.0, 200 }, LabelX = 200, LabelY = 190,
    };

    [Fact]
    public void Publish_KeepsTheLatestMapPerFactory_AndLeavesOtherFactoriesAndAccountsAlone()
    {
        using var h = new GatewayDbTestHarness();
        var store = new FactoryMapStore(new TenantSettingsStore(h.Open()));

        store.Publish(TenantA, Map(), "session s1", Now);
        store.Publish(TenantA, Map("factory-cleaner"), "session s2", Now);
        var second = Map();
        second.Title = "Website Business (new)";
        store.Publish(TenantA, second, "session s3", Now.AddMinutes(5));

        var kept = store.Find(TenantA, "website-business")!;
        Assert.Equal("Website Business (new)", kept.Map.Title);
        Assert.Equal("session s3", kept.PublishedBy);
        Assert.Equal(Now.AddMinutes(5), kept.PublishedUtc);
        Assert.Equal(4, kept.Map.Nodes.Count);
        Assert.NotNull(store.Find(TenantA, "factory-cleaner"));
        Assert.Null(store.Find(TenantB, "website-business"));
    }

    public static TheoryData<string, Action<PublishFactoryMapRequest>, string> Broken => new()
    {
        { "factory id", m => m.Factory = "Website Business", "must be lower-case" },
        { "reserved id", m => m.Factory = "waiting", "cannot be called" },
        { "no title", m => m.Title = " ", "title is missing" },
        { "no source", m => m.Source = "", "source is missing" },
        { "no boxes", m => m.Nodes.Clear(), "at least one box" },
        { "two boxes one id", m => m.Nodes[1].Id = "scout", "Two boxes are called 'scout'" },
        { "unknown box kind", m => m.Nodes[0].Kind = "robot", "has kind 'robot'" },
        { "a box off the drawing", m => m.Nodes[0].X = -5, "box 'scout' x" },
        { "a box with no size", m => m.Nodes[0].Width = 0, "box 'scout' width" },
        { "not a number", m => m.Nodes[0].Y = double.NaN, "box 'scout' y" },
        { "arrow to nothing", m => m.Edges[0].To = "janitor", "names a box that is not on the map" },
        { "unknown arrow kind", m => m.Edges[0].Kind = "teleport", "has kind 'teleport'" },
        { "a result that is not one of the four", m => m.Edges[0].Result = "partly", "a run finishes only as" },
        { "half a curve", m => m.Edges[0].Points.RemoveAt(3), "1 + 3n points" },
        { "a point with three numbers", m => m.Edges[0].Points[0] = new[] { 1.0, 2, 3 }, "not an x and a y" },
        { "half a label position", m => m.Edges[0].LabelY = null, "half a label position" },
        { "too many lines", m => m.Nodes[0].Lines = new() { "a", "b", "c", "d", "e" }, "more than 4 lines" },
        { "a long title", m => m.Nodes[0].Title = new string('x', 121), "longer than 120" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Publish_RefusesABrokenMapWhole_AndStoresNothing(string what, Action<PublishFactoryMapRequest> breakIt, string reason)
    {
        using var h = new GatewayDbTestHarness();
        var store = new FactoryMapStore(new TenantSettingsStore(h.Open()));
        var map = Map();
        breakIt(map);

        var ex = Assert.Throws<FactoryViewValidationException>(() => store.Publish(TenantA, map, "session s1", Now));
        Assert.Contains(reason, ex.Message);
        Assert.True(store.Find(TenantA, "website-business") is null, $"{what}: a refused map was stored");
    }

    [Fact]
    public void View_NoMapPublished_SaysHowAMapArrives_AndStillListsTheAgents()
    {
        var card = Card(("scout", "IDLE", FactoryTone.Idle));
        var view = FactoryMapFold.View("website-business", null, card, TimeZoneInfo.Utc);

        Assert.NotNull(view.EmptyText);
        Assert.Contains("has not published a map yet", view.EmptyText);
        Assert.Empty(view.Nodes);
        Assert.Equal("Map", view.Tabs[0].Label);
        Assert.Equal("Agents (1)", view.Tabs[1].Label);
        Assert.Single(view.Agents);
    }

    [Fact]
    public void View_AgentBoxesCarryTheirCardStatus_AndThePlannedOneIsDashedAndGrey()
    {
        var stored = new StoredFactoryMap(Map(), Now, "session s1");
        var card = Card(("scout", "FAULT", FactoryTone.Red), ("sender", "WORKING", FactoryTone.Working));
        var zone = TimeZoneInfo.CreateCustomTimeZone("minus-four", TimeSpan.FromHours(-4), "minus-four", "minus-four");

        var view = FactoryMapFold.View("website-business", stored, card, zone);

        Assert.Null(view.EmptyText);
        Assert.Equal("Drawn from factory.yaml in cc-consult at 687a1e9. Published 24 Sep 14:02 by session s1.", view.SourceText);
        var scout = view.Nodes.Single(n => n.Id == "scout");
        Assert.Equal("FAULT", scout.StatusWord);
        Assert.Equal(FactoryTone.Red, scout.Tone);
        Assert.Equal("/factory-agents/website-business/scout", scout.Href);
        Assert.Equal("Status", scout.Spec[0].Label);
        Assert.Equal("Inputs", scout.Spec[^1].Label);          // the factory's own rows follow the live ones
        var planned = view.Nodes.Single(n => n.Id == "bookkeeper");
        Assert.True(planned.Dashed);
        Assert.Equal(FactoryTone.Grey, planned.Tone);
        Assert.Null(planned.Href);
        Assert.Equal(FactoryTone.Amber, view.Nodes.Single(n => n.Id == "owner").Tone);
    }

    [Fact]
    public void View_ArrowsGetTheirToneAndLineFromWhatTheyAre()
    {
        var map = Map();
        map.Edges.Add(Edge("scout", "bookkeeper", FactoryMapEdgeKind.Call, null));
        var off = Edge("sender", "bookkeeper", FactoryMapEdgeKind.Trigger, null);
        off.Enabled = false;
        map.Edges.Add(off);

        var view = FactoryMapFold.View("website-business", new StoredFactoryMap(map, Now, "x"), null, TimeZoneInfo.Utc);

        FactoryMapEdgeDto E(string from, string to) => view.Edges.Single(e => e.From == from && e.To == to);
        Assert.Equal((FactoryTone.Ok, "solid"), (E("scout", "sender").Tone, E("scout", "sender").Line));
        Assert.Equal((FactoryTone.Red, "solid"), (E("scout", "owner").Tone, E("scout", "owner").Line));
        Assert.Equal((FactoryTone.Amber, "solid"), (E("sender", "owner").Tone, E("sender", "owner").Line));
        Assert.Equal((FactoryTone.Neutral, "dashed"), (E("scout", "bookkeeper").Tone, E("scout", "bookkeeper").Line));
        Assert.Equal((FactoryTone.Grey, "dotted"), (E("sender", "bookkeeper").Tone, E("sender", "bookkeeper").Line));
        Assert.Equal("M 145 200 C 180 200 220 200 245 200", E("scout", "sender").Path);
        Assert.Equal("255,200 245,203.5 245,196.5", E("scout", "sender").Head);
    }

    [Fact]
    public void View_AnAgentTheGatewayHasNeverSeen_SaysSoInsteadOfAStatus()
    {
        var view = FactoryMapFold.View("website-business", new StoredFactoryMap(Map(), Now, "x"), Card(), TimeZoneInfo.Utc);

        var sender = view.Nodes.Single(n => n.Id == "sender");
        Assert.Null(sender.StatusWord);
        Assert.Null(sender.Href);
        Assert.Contains("Nothing recorded yet", sender.Spec[0].Text);
    }

    private static FactoryCardDto Card(params (string Agent, string Word, string Tone)[] agents) => new()
    {
        Id = "website-business",
        Title = "Website Business",
        Agents = agents.Select(a => new FactoryAgentRowDto
        {
            FactoryId = "website-business", FactoryTitle = "Website Business", AgentId = a.Agent,
            Name = FactoryAgentsFold.Humanize(a.Agent), WokenBy = "No trigger names it", LastRun = "No run in the last 24 hours",
            StatusWord = a.Word, StatusTone = a.Tone, Href = FactoryAgentsFold.AgentHref("website-business", a.Agent),
        }).ToList(),
    };
}
