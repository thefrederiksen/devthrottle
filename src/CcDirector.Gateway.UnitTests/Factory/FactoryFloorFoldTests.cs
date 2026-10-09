using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The factory floor (owner decision, 8 October 2026): lanes are the lines each seat names, the boss works from the
/// office on top, the arrows are the published map's, and the desk holds what the factory page says needs the owner.
/// The fixture is Website Business as its map file draws it.
/// </summary>
public sealed class FactoryFloorFoldTests
{
    private static RegisteredFactorySeatDto Seat(string id, string name, string? line) =>
        new() { Id = id, Name = name, Role = name, BriefFile = $"agents/{id}.yaml", Computer = "SOREN_NORTH", Line = line };

    private static RegisteredFactoryDto Website(bool lines = true) => new()
    {
        Factory = "website-business", Title = "Website Business", Folder = @"D:\f", Computer = "SOREN_NORTH", BossSeat = "ceo",
        Seats = new()
        {
            new RegisteredFactorySeatDto { Id = "ceo", Name = "Boss", Role = "Boss", BriefFile = "agents/ceo.yaml", Computer = "SOREN_NORTH", Line = lines ? "Find and sell" : null },
            Seat("scout", "Scout", lines ? "Find and sell" : null),
            Seat("front-desk", "Front Desk", lines ? "Mailbox" : null),
            Seat("site-keeper", "Site Keeper", null),
            Seat("sender", "Sender", lines ? "Find and sell" : null),
            Seat("site-checker", "Site Checker", lines ? "Find and sell" : null),
        },
    };

    private static FactorySeatsViewDto Rows(RegisteredFactoryDto f) => new()
    {
        Rows = f.Seats.Select(s => new FactorySeatRowDto
        {
            SeatId = s.Id, Name = s.Name, Role = s.Role,
            WhenText = s.Id == "scout" ? "Weekdays 02:00" : "Not scheduled",
            LastRunText = s.Id == "scout" ? "Today 02:05 - failed" : "Not run yet",
            LastRunTone = s.Id == "scout" ? FactoryTone.Red : FactoryTone.Idle,
        }).ToList(),
    };

    private static FactoryPageViewDto Page(int failures = 0, int waiting = 0) => new()
    {
        Failures = failures == 0 ? null : new FactoryPageFailuresDto
        {
            Items = Enumerable.Range(1, failures).Select(i => new FactoryFailureItemDto { By = "Scout, today 02:05", What = $"Failure {i}" }).ToList(),
        },
        Waiting = new FactoryPageWaitingDto
        {
            Items = Enumerable.Range(1, waiting).Select(i => new FactoryWaitingItemDto { By = "Sender, today 03:00", What = $"Question {i}", Tone = FactoryTone.Amber }).ToList(),
        },
    };

    private static FactoryMapEdgeRequest Edge(string from, string to, string kind, string label, string? result = null, bool enabled = true) =>
        new() { From = from, To = to, Kind = kind, Label = label, Result = result, Enabled = enabled };

    private static StoredFactoryMap Map(params FactoryMapEdgeRequest[] edges) => new(new PublishFactoryMapRequest
    {
        Factory = "website-business",
        Nodes = new()
        {
            new() { Id = "owner", Kind = FactoryMapNodeKind.Owner, Title = "You" },
            new() { Id = "mail", Kind = FactoryMapNodeKind.Source, Title = "Business email", Lines = new() { "(replies)" } },
            new() { Id = "scout", Kind = FactoryMapNodeKind.Agent, Title = "Scout" },
            new() { Id = "site-keeper", Kind = FactoryMapNodeKind.Agent, Title = "Site Keeper", Built = false },
        },
        Edges = edges.ToList(),
    }, new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc), "session s1");

    [Fact]
    public void Lanes_AreEachSeatsLine_InSeatOrder_OtherLast_AndTheBossWorksFromTheOfficeNotABay()
    {
        var f = Website();

        var view = FactoryFloorFold.View(f, Rows(f), Page(), null);

        Assert.Equal(new[] { "Find and sell", "Mailbox", FactoryFloorFold.OtherLane }, view.Lanes.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2 }, view.Lanes.Select(l => l.Hue));
        Assert.DoesNotContain(view.Bays, b => b.Id == "ceo");
        Assert.NotNull(view.Office);
        Assert.Equal("Boss's office", view.Office!.Title);
        // The bays of a lane sit inside it, in the factory's own seat order.
        var find = view.Lanes[0];
        var inFind = view.Bays.Where(b => b.Y >= find.Y && b.Y + b.Height <= find.Y + find.Height).OrderBy(b => b.X).Select(b => b.Id);
        Assert.Equal(new[] { "scout", "sender", "site-checker" }, inFind);
        // The office runs above the first lane; the desk stands to the right of every lane.
        Assert.True(view.Office.Y + view.Office.Height <= find.Y);
        Assert.True(view.Desk.X >= view.Lanes.Max(l => l.X + l.Width));
        Assert.True(view.Width >= view.Desk.X + view.Desk.Width);
        Assert.True(view.Height >= view.Lanes.Max(l => l.Y + l.Height));
    }

    [Fact]
    public void NoLineNamed_PutsEverySeatInOneLane_AndSaysHowToNameThem()
    {
        var f = Website(lines: false);

        var view = FactoryFloorFold.View(f, Rows(f), Page(), null);

        Assert.Equal(FactoryFloorFold.OneLane, Assert.Single(view.Lanes).Name);
        Assert.Contains(view.Notes, n => n.Contains("has not named its lines yet") && n.Contains("\"line\": \"Night shift\""));
        Assert.Contains(view.Notes, n => n.Contains("has not published its map"));
        Assert.Empty(view.Arrows);
    }

    [Fact]
    public void ABaysLight_IsItsSeatsLastRun_AndASeatTheMapCallsUnbuiltIsDashed()
    {
        var f = Website();

        var view = FactoryFloorFold.View(f, Rows(f), Page(), Map());

        var scout = view.Bays.Single(b => b.Id == "scout");
        Assert.Equal(FactoryTone.Red, scout.Tone);
        Assert.Equal("Today 02:05 - failed", scout.ToneText);
        Assert.Equal("Weekdays 02:00", scout.Sub);
        Assert.Equal("/factories/website-business/seats", scout.Href);
        var keeper = view.Bays.Single(b => b.Id == "site-keeper");
        Assert.True(keeper.Dashed);
        Assert.Equal("not built yet", keeper.Sub);
        Assert.Equal(FactoryTone.Grey, keeper.Tone);
    }

    [Fact]
    public void Arrows_AreTheMapsOwn_StyledByWhatTheyMean_AndAMailboxStandsInTheLaneOfTheSeatItWakes()
    {
        var f = Website();
        var map = Map(
            Edge("scout", "sender", FactoryMapEdgeKind.Chain, "succeeded\nstarts", FactoryMapResults.Succeeded),
            Edge("scout", "site-checker", FactoryMapEdgeKind.Call, "calls"),
            Edge("mail", "front-desk", FactoryMapEdgeKind.Trigger, "trigger"),
            Edge("front-desk", "owner", FactoryMapEdgeKind.Escalate, "escalates"),
            Edge("sender", "owner", FactoryMapEdgeKind.Asks, "needs-you:\nasks only when stuck"),
            Edge("site-keeper", "site-checker", FactoryMapEdgeKind.Call, "calls", enabled: false));

        var view = FactoryFloorFold.View(f, Rows(f), Page(), map);

        var chain = view.Arrows.Single(a => a.From == "scout" && a.To == "sender");
        Assert.Equal((FactoryTone.Ok, FactoryFloorFold.Solid, "succeeded: starts"), (chain.Tone, chain.Line, chain.Label));
        Assert.StartsWith("M ", chain.Path);
        Assert.Contains(" C ", chain.Path);
        Assert.Equal(3, chain.Head.Split(' ').Length);
        Assert.Equal(FactoryFloorFold.Dashed, view.Arrows.Single(a => a.From == "scout" && a.To == "site-checker").Line);
        Assert.Equal(FactoryTone.Grey, view.Arrows.Single(a => a.From == "site-keeper").Tone);
        Assert.Equal("needs-you: asks only when stuck", view.Arrows.Single(a => a.From == "sender").Label);

        // The mailbox is a bay of its own, at the front of Front Desk's lane, and its arrow is dotted.
        var mail = view.Bays.Single(b => b.Id == "mail");
        Assert.Equal("source", mail.Kind);
        Assert.Null(mail.Href);
        var mailbox = view.Lanes.Single(l => l.Name == "Mailbox");
        Assert.InRange(mail.Y, mailbox.Y, mailbox.Y + mailbox.Height);
        Assert.True(mail.X < view.Bays.Single(b => b.Id == "front-desk").X);
        Assert.Equal(FactoryFloorFold.Dotted, view.Arrows.Single(a => a.From == "mail").Line);

        // An arrow to the owner ends at the desk, and needs-you arrows are amber.
        var escalates = view.Arrows.Single(a => a.From == "front-desk");
        Assert.Equal(FactoryTone.Amber, escalates.Tone);
        var tipX = double.Parse(escalates.Head.Split(' ')[0].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(view.Desk.X, tipX, 2);
        Assert.Contains(view.Notes, n => n.StartsWith("The arrows are the ones the factory published in its map"));
        Assert.Equal(new[] { "On success, starts the next", "Needs you", "Calls another seat", "Woken from outside", "Not switched on" },
            view.Legend.Select(l => l.Text));
    }

    [Fact]
    public void FailuresThatEmailTheOwner_AreSaidOnceOnTheDesk_NotDrawnAsArrows_AndAnArrowToAnUnregisteredSeatIsCounted()
    {
        var f = Website();
        var map = Map(
            Edge("scout", "owner", FactoryMapEdgeKind.Notify, "failed\nemails you"),
            Edge("sender", "owner", FactoryMapEdgeKind.Notify, "failed\nemails you"),
            Edge("ceo", "owner", FactoryMapEdgeKind.Notify, "failed\nemails you"),
            Edge("scout", "bookkeeper", FactoryMapEdgeKind.Call, "calls"));

        var view = FactoryFloorFold.View(f, Rows(f), Page(), map);

        Assert.Empty(view.Arrows);
        Assert.Equal("Failures also reach you by email, from 3 seats.", view.Desk.Note);
        Assert.Contains("1 arrow names a box the factory has not registered as a seat, so it is not drawn.", view.Notes);
    }

    [Fact]
    public void TheDesk_ListsFailuresThenQuestions_CapsTheList_AndSaysNothingWaitsWhenNothingDoes()
    {
        var f = Website();

        var empty = FactoryFloorFold.View(f, Rows(f), Page(), null);
        Assert.Empty(empty.Desk.Items);
        Assert.Equal("Nothing is waiting on you.", empty.Desk.EmptyText);

        var some = FactoryFloorFold.View(f, Rows(f), Page(failures: 1, waiting: 1), null);
        Assert.Equal(new[] { "Scout, today 02:05", "Sender, today 03:00" }, some.Desk.Items.Select(i => i.Title));
        Assert.Equal(new[] { "Failure 1", "Question 1" }, some.Desk.Items.Select(i => i.Text));
        Assert.Equal(new[] { FactoryTone.Red, FactoryTone.Amber }, some.Desk.Items.Select(i => i.Tone));
        Assert.Null(some.Desk.EmptyText);

        var many = FactoryFloorFold.View(f, Rows(f), Page(failures: 4, waiting: 4), null);
        Assert.Equal(FactoryFloorFold.MaxDeskItems, many.Desk.Items.Count);
        Assert.Equal("and 3 more on this page", many.Desk.Items[^1].Text);
        Assert.True(many.Desk.Height >= FactoryFloorFold.DeskItemsTop + many.Desk.Items.Count * FactoryFloorFold.DeskItemStep);
    }

    [Fact]
    public void AFactoryWithNoBoss_HasNoOffice_AndItsLanesStartAtTheTop()
    {
        var f = Website();
        f.BossSeat = null;

        var view = FactoryFloorFold.View(f, Rows(f), Page(), null);

        Assert.Null(view.Office);
        Assert.Equal(FactoryFloorFold.Margin, view.Lanes[0].Y);
        Assert.Contains(view.Bays, b => b.Id == "ceo");
    }
}
