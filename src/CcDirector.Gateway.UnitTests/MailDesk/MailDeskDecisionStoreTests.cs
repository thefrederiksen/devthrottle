using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// WHAT THE MAIL DESK DECIDED FOR EACH EMAIL (Mail Desk mission, phase 1, contract sections 2 and 5): the insert phase 3
/// writes through, every filter of <c>GET /decisions</c>, the counts by decision with zeros, a day's counts per channel
/// between two instants, and - first - that there is nowhere to put an email's body. Over a throwaway SQLite file.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskDecisionStoreTests : IDisposable
{
    private const string Support = "email@devthrottle.com";
    private const string Other = "other@example.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    private readonly GatewayDbTestHarness _h = new();
    public void Dispose() => _h.Dispose();

    private MailDeskDecisionStore Store() => new(_h.Open());

    private Guid Channel(TenantId tenant, string address = Support)
        => new MailDeskChannelStore(_h.Open()).Add(tenant, address, "outlook", new[] { "devlinux" }, Owner, "screen it", Now).Channel!.Id;

    /// <summary>One decision as phase 3 would record it: a junked sender, observe mode, decided at <paramref name="decided"/>.</summary>
    internal static MailDeskDecisionInput Decision(Guid channel, string key, string layer = MailDeskWords.Layers.Block,
        string decision = MailDeskWords.Decisions.Junk, DateTime? decided = null)
        => new(channel, key, (decided ?? Now).AddMinutes(-1), "Sales@Spammy.Example", "Grow your revenue", layer, decision,
            decision == MailDeskWords.Decisions.Route ? "devthrottle" : null, decision == MailDeskWords.Decisions.Route ? "mail-desk" : null,
            "block entry *@spammy.example", null, null, MailDeskWords.Modes.Observe, null, null, decided ?? Now);

    // ---------- no body text ----------

    [Fact]
    public void MailDeskDecisionEntity_Properties_HoldNoBodyText()
    {
        // The table has no column for the body or its preview, on purpose: the preview is used for the model question
        // and thrown away. This list is exact, so a body column cannot be added without this test failing.
        var properties = typeof(MailDeskDecisionEntity).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "ChannelId", "Confidence", "CostMicros", "DecidedUtc", "Decision", "Id", "Layer", "MessageKey", "Mode", "Outcome",
            "OutcomeDetail", "Reason", "ReceivedUtc", "RouteFactory", "RouteSeat", "SenderAddress", "Subject", "TenantId",
        }, properties);
        Assert.DoesNotContain(typeof(MailDeskDecisionInput).GetProperties(), p =>
            p.Name.Contains("Body", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Database_TheDecisionsTable_HasNoBodyColumn()
    {
        using var ctx = _h.Open().CreateContext(A);
        var columns = ctx.Model.FindEntityType(typeof(MailDeskDecisionEntity))!.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.DoesNotContain(columns, c => c.Contains("body", StringComparison.OrdinalIgnoreCase) || c.Contains("preview", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("mail_desk_decisions", ctx.Model.FindEntityType(typeof(MailDeskDecisionEntity))!.GetTableName());
    }

    // ---------- insert ----------

    [Fact]
    public void Insert_ADecision_IsKeptWithTheSenderLowerCase()
    {
        // Arrange
        var channel = Channel(A);
        var input = Decision(channel, "m-1") with { Confidence = 0.92, CostMicros = 41, Layer = MailDeskWords.Layers.SenderScreen };

        // Act
        var insert = Store().Insert(A, input);

        // Assert
        Assert.True(insert.Inserted);
        var kept = Store().Get(A, insert.Decision.Id)!;
        Assert.Equal("sales@spammy.example", kept.SenderAddress);
        Assert.Equal((0.92, 41L), (kept.Confidence!.Value, kept.CostMicros!.Value));
        Assert.Equal((MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Junk, MailDeskWords.Modes.Observe), (kept.Layer, kept.Decision, kept.Mode));
        Assert.Null(kept.RouteFactory);
    }

    [Fact]
    public void Insert_TheSameMessageTwice_KeepsOneRowAndReturnsIt()
    {
        var channel = Channel(A);
        var store = Store();
        var first = store.Insert(A, Decision(channel, "m-1"));

        var second = store.Insert(A, Decision(channel, "m-1", decision: MailDeskWords.Decisions.Archive, layer: MailDeskWords.Layers.Allow));

        Assert.False(second.Inserted);
        Assert.Equal(first.Decision.Id, second.Decision.Id);
        Assert.Equal(MailDeskWords.Decisions.Junk, second.Decision.Decision);
        Assert.Equal(1, store.List(A, new MailDeskDecisionFilter()).Total);
    }

    [Fact]
    public void Insert_TheSameMessageKeyOnTwoChannels_IsTwoRows()
    {
        var store = Store();
        store.Insert(A, Decision(Channel(A), "m-1"));

        var other = store.Insert(A, Decision(Channel(A, Other), "m-1"));

        Assert.True(other.Inserted);
    }

    [Fact]
    public void Insert_ARouteWithItsFactoryAndSeat_KeepsThem()
    {
        var insert = Store().Insert(A, Decision(Channel(A), "m-1", MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route));

        Assert.Equal(("devthrottle", "mail-desk"), (insert.Decision.RouteFactory, insert.Decision.RouteSeat));
    }

    [Fact]
    public void Insert_ALongSubject_IsCutToTheColumnAndKept()
    {
        var insert = Store().Insert(A, Decision(Channel(A), "m-1") with { Subject = new string('s', MailDeskDecisionStore.MaxSubjectLength + 50) });

        Assert.Equal(MailDeskDecisionStore.MaxSubjectLength, insert.Decision.Subject.Length);
    }

    [Fact]
    public void Insert_UnknownWordsOrARouteWithNoSeat_Throw()
    {
        var channel = Channel(A);
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Layer = "spam-filter" }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Decision = "delete" }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Mode = "auto" }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Outcome = "deleted" }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Decision = MailDeskWords.Decisions.Route }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, "m-1") with { Confidence = 1.5 }));
        Assert.Throws<ArgumentException>(() => store.Insert(A, Decision(channel, " ")));
    }

    [Fact]
    public void Insert_OnAnotherAccountsChannel_Throws()
    {
        var bChannel = Channel(B);

        Assert.Throws<ArgumentException>(() => Store().Insert(A, Decision(bChannel, "m-1")));
    }

    [Fact]
    public void Database_ASecondRowForOneMessageOnOneChannel_IsRefusedByTheUniqueIndex()
    {
        var channel = Channel(A);
        Store().Insert(A, Decision(channel, "m-1"));

        using var ctx = _h.Open().CreateContext(A);
        ctx.MailDeskDecisions.Add(new MailDeskDecisionEntity
        {
            TenantId = A.Value, ChannelId = channel, MessageKey = "m-1", SenderAddress = "x@example.com", Subject = "",
            Layer = "block", Decision = "junk", Reason = "", Mode = "observe", ReceivedUtc = Now, DecidedUtc = Now,
        });
        Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
    }

    // ---------- the filters ----------

    private (Guid Support, Guid Other) Seed()
    {
        var support = Channel(A);
        var other = Channel(A, Other);
        var store = Store();
        store.Insert(A, Decision(support, "s-1", MailDeskWords.Layers.Block, MailDeskWords.Decisions.Junk, Now.AddHours(-3)));
        store.Insert(A, Decision(support, "s-2", MailDeskWords.Layers.Allow, MailDeskWords.Decisions.Archive, Now.AddHours(-2)));
        store.Insert(A, Decision(support, "s-3", MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route, Now.AddHours(-1)));
        store.Insert(A, Decision(other, "o-1", MailDeskWords.Layers.Allow, MailDeskWords.Decisions.KeepForOwner, Now.AddMinutes(-30)));
        store.Insert(A, Decision(other, "o-2", MailDeskWords.Layers.SenderScreen, MailDeskWords.Decisions.Junk, Now));
        return (support, other);
    }

    [Fact]
    public void List_NoFilter_IsEveryDecisionNewestFirstWithCountsOfEveryDecisionIncludingZeros()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter());

        Assert.Equal(new[] { "o-2", "o-1", "s-3", "s-2", "s-1" }, list.Decisions.Select(d => d.MessageKey));
        Assert.Equal(5, list.Total);
        Assert.Equal(MailDeskWords.Decisions.All, list.ByDecision.ByDecision.Keys);
        Assert.Equal(new[] { 1, 1, 2, 1, 0, 0 }, MailDeskWords.Decisions.All.Select(d => list.ByDecision.ByDecision[d]));
    }

    [Fact]
    public void List_N_CutsThePageButNotTheTotalOrTheCounts()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(N: 2));

        Assert.Equal(new[] { "o-2", "o-1" }, list.Decisions.Select(d => d.MessageKey));
        Assert.Equal(5, list.Total);
        Assert.Equal(2, list.ByDecision.ByDecision[MailDeskWords.Decisions.Junk]);
    }

    [Fact]
    public void List_Since_KeepsOnlyDecisionsAtOrAfterIt()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(SinceUtc: Now.AddHours(-1)));

        Assert.Equal(new[] { "o-2", "o-1", "s-3" }, list.Decisions.Select(d => d.MessageKey));
    }

    [Fact]
    public void List_Layer_KeepsOnlyThatLayer()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(Layer: MailDeskWords.Layers.Allow));

        Assert.Equal(new[] { "o-1", "s-2" }, list.Decisions.Select(d => d.MessageKey));
        Assert.Equal(2, list.Total);
    }

    [Fact]
    public void List_Decision_KeepsOnlyThatDecisionAndCountsOnlyIt()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(Decision: MailDeskWords.Decisions.Junk));

        Assert.Equal(new[] { "o-2", "s-1" }, list.Decisions.Select(d => d.MessageKey));
        Assert.Equal(2, list.ByDecision.Total);
        Assert.Equal(0, list.ByDecision.ByDecision[MailDeskWords.Decisions.Archive]);
    }

    [Fact]
    public void List_Channel_KeepsOnlyThatAddressInAnySpelling()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(ChannelAddress: " EMAIL@devthrottle.com "));

        Assert.Equal(new[] { "s-3", "s-2", "s-1" }, list.Decisions.Select(d => d.MessageKey));
    }

    [Fact]
    public void List_AChannelAddressTheAccountNeverHad_IsEmptyWithZeroCounts()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(ChannelAddress: "nobody@example.net"));

        Assert.Equal(0, list.Total);
        Assert.Empty(list.Decisions);
        Assert.All(list.ByDecision.ByDecision.Values, v => Assert.Equal(0, v));
    }

    [Fact]
    public void List_EveryFilterTogether_AppliesEachOne()
    {
        Seed();

        var list = Store().List(A, new MailDeskDecisionFilter(SinceUtc: Now.AddHours(-2.5), Layer: MailDeskWords.Layers.Allow,
            Decision: MailDeskWords.Decisions.Archive, ChannelAddress: Support, N: 10));

        Assert.Equal("s-2", list.Decisions.Single().MessageKey);
    }

    [Fact]
    public void List_AnUnknownLayerOrDecisionOrAnOutOfRangeN_Throws()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.List(A, new MailDeskDecisionFilter(Layer: "spam")));
        Assert.Throws<ArgumentException>(() => store.List(A, new MailDeskDecisionFilter(Decision: "delete")));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(A, new MailDeskDecisionFilter(N: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(A, new MailDeskDecisionFilter(N: MailDeskDecisionStore.MaxList + 1)));
    }

    // ---------- a day's counts per channel ----------

    [Fact]
    public void CountsByChannel_BetweenTwoInstants_CountsEachChannelsDecisionsWithTheEndLeftOut()
    {
        // Arrange
        var (support, other) = Seed();

        // Act: from two hours ago up to (not including) now.
        var counts = Store().CountsByChannel(A, Now.AddHours(-2), Now);

        // Assert
        Assert.Equal(2, counts[support].Total);
        Assert.Equal(1, counts[support].ByDecision[MailDeskWords.Decisions.Archive]);
        Assert.Equal(1, counts[support].ByDecision[MailDeskWords.Decisions.Route]);
        Assert.Equal(0, counts[support].ByDecision[MailDeskWords.Decisions.Junk]);
        Assert.Equal(1, counts[other].Total);
        Assert.Equal(1, counts[other].ByDecision[MailDeskWords.Decisions.KeepForOwner]);
        var day = MailDeskDecisionCounts.Sum(counts.Values);
        Assert.Equal(3, day.Total);
        Assert.Equal(MailDeskWords.Decisions.All, day.ByDecision.Keys);
    }

    [Fact]
    public void CountsByChannel_AChannelWithNothingInTheSpan_IsNotInTheMap()
    {
        var (support, _) = Seed();

        var counts = Store().CountsByChannel(A, Now.AddMinutes(-45), Now.AddMinutes(1));

        Assert.False(counts.ContainsKey(support));
        Assert.Equal(0, MailDeskDecisionCounts.Zero.Total);
    }

    [Fact]
    public void CountsByChannel_AnEndBeforeTheStart_Throws()
    {
        Assert.Throws<ArgumentException>(() => Store().CountsByChannel(A, Now, Now.AddHours(-1)));
    }

    // ---------- accounts ----------

    [Fact]
    public void Store_AccountA_NeverSeesAccountBsDecisions()
    {
        // Arrange
        var bChannel = Channel(B);
        var bDecision = Store().Insert(B, Decision(bChannel, "m-1")).Decision;
        Channel(A);

        // Act and Assert
        Assert.Equal(0, Store().List(A, new MailDeskDecisionFilter()).Total);
        Assert.Equal(0, Store().List(A, new MailDeskDecisionFilter(ChannelAddress: Support)).Total);
        Assert.Null(Store().Get(A, bDecision.Id));
        Assert.Empty(Store().CountsByChannel(A, Now.AddDays(-1), Now.AddDays(1)));
        Assert.Equal(1, Store().List(B, new MailDeskDecisionFilter()).Total);
    }

    // ---------- the routed table (phase 4 writes it) ----------

    [Fact]
    public void Database_TheRoutedTable_ExistsAndKeepsARowForItsOwnAccountOnly()
    {
        // Arrange
        var decision = Store().Insert(A, Decision(Channel(A), "m-1", MailDeskWords.Layers.RouteModel, MailDeskWords.Decisions.Route)).Decision;
        var db = _h.Open();

        // Act
        using (var ctx = db.CreateContext(A))
        {
            ctx.MailDeskRouted.Add(new MailDeskRoutedEntity
            {
                TenantId = A.Value, DecisionId = decision.Id, Factory = "devthrottle", Seat = "mail-desk",
                State = MailDeskWords.RoutedStates.Open, CreatedUtc = Now, ChangedUtc = Now,
            });
            ctx.SaveChanges();
        }

        // Assert
        using (var ctx = db.CreateContext(A))
        {
            var row = ctx.MailDeskRouted.Single();
            Assert.Equal((decision.Id, "open", 0, (string?)null), (row.DecisionId, row.State, row.ReturnedCount, row.SessionId));
            Assert.Equal("mail_desk_routed", ctx.Model.FindEntityType(typeof(MailDeskRoutedEntity))!.GetTableName());
        }
        using (var ctx = db.CreateContext(B))
            Assert.Empty(ctx.MailDeskRouted.ToList());
    }
}
