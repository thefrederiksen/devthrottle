using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// "Watch your factory", piece 2: the floor laid out from the factory's shape, one character per seat in its own
/// colour, and the activity rows as an ordered timeline of steps, all decided on the Gateway.
/// </summary>
public sealed class FactoryWatchFoldTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc);
    private static readonly FactoryWindow Day = new(FactoryAgentsFold.WindowLast24h, Now.AddHours(-24), Now);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static RegisteredFactorySeatDto Seat(string id, string? line = null, string? name = null) =>
        new() { Id = id, Name = name ?? id, Role = id == "boss" ? "Boss" : "Worker", BriefFile = $"agents/{id}.yaml", Computer = "SOREN_NORTH", Line = line };

    private static RegisteredFactoryDto Factory(int seats, bool boss = true)
    {
        var list = new List<RegisteredFactorySeatDto>();
        if (boss) list.Add(Seat("boss"));
        for (var i = list.Count; i < seats; i++) list.Add(Seat($"seat-{i}"));
        return new RegisteredFactoryDto
        {
            Factory = "demo", Title = "Demo Website", Folder = @"D:\factories\demo", Computer = "SOREN_NORTH",
            BossSeat = boss ? "boss" : null, Seats = list,
        };
    }

    private static FactoryActivityDto Row(string agent, string outcome, string what, DateTime at, string actor = "session:abc")
        => new()
        {
            Id = Guid.NewGuid(), Factory = "demo", FactoryAgent = agent, What = what, Outcome = outcome, Actor = actor,
            SessionId = "5f8d72f4-66ab-4b03-8c37-7bc58caab6ee", Link = @"D:\factories\demo\report.html",
            OccurredUtc = at, RecordedUtc = at,
        };

    private static List<FactoryActivityDto> EmptyChecks(string agent, int count, DateTime start) =>
        Enumerable.Range(0, count)
            .Select(i => Row(agent, FactoryActivityOutcome.NothingToDo, "Checked the mail - nothing to do", start.AddMinutes(5 * i), actor: "trigger:mail-check"))
            .ToList();

    private static FactoryWatchViewDto View(RegisteredFactoryDto f, IReadOnlyList<FactoryActivityDto>? rows = null) =>
        FactoryWatchFold.View(f, rows ?? Array.Empty<FactoryActivityDto>(), false, Day, Utc, FactoryWatchAudience.Owner);

    private static bool Overlap(FactoryWatchStationDto a, FactoryWatchStationDto b) =>
        a.Gx < b.Gx + b.Width && b.Gx < a.Gx + a.Width && a.Gy < b.Gy + b.Height && b.Gy < a.Gy + a.Height;

    private static void AssertOnFloorWithoutOverlap(FactoryWatchViewDto view)
    {
        foreach (var s in view.Stations)
        {
            Assert.True(s.Gx >= 0 && s.Gy >= 0, $"{s.Id} starts off the floor");
            Assert.True(s.Gx + s.Width <= view.FloorWidth && s.Gy + s.Height <= view.FloorHeight, $"{s.Id} ends off the floor");
        }
        for (var i = 0; i < view.Stations.Count; i++)
            for (var j = i + 1; j < view.Stations.Count; j++)
                Assert.False(Overlap(view.Stations[i], view.Stations[j]), $"{view.Stations[i].Id} overlaps {view.Stations[j].Id}");
    }

    // ---------------------------------------------------------------------------------------------------- layout

    [Fact]
    public void View_OneSeat_IsAWorkshopWithOneBench()
    {
        var view = View(Factory(1));

        Assert.Equal(FactoryWatchLayout.Workshop, view.Layout);
        var station = Assert.Single(view.Stations);
        Assert.True(station.IsBoss);
        Assert.Single(view.Characters);
        Assert.Empty(view.Lines);
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_ThreeSeats_IsAWorkshopInOneRowWithTheBossFirst()
    {
        var view = View(Factory(3));

        Assert.Equal(FactoryWatchLayout.Workshop, view.Layout);
        Assert.Equal(new[] { "boss", "seat-1", "seat-2" }, view.Stations.Select(s => s.Id));
        Assert.Single(view.Stations.Select(s => s.Gy).Distinct());
        Assert.True(view.Stations[0].Gx < view.Stations[1].Gx && view.Stations[1].Gx < view.Stations[2].Gx);
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_FourSeats_IsAHubWithTheBossInTheMiddle()
    {
        var view = View(Factory(4));

        Assert.Equal(FactoryWatchLayout.Hub, view.Layout);
        var boss = view.Stations.Single(s => s.IsBoss);
        Assert.Equal(view.FloorWidth / 2, boss.Gx + boss.Width / 2, 2);
        Assert.Equal(view.FloorHeight / 2, boss.Gy + boss.Height / 2, 2);
        Assert.Equal(4, view.Stations.Count);
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_TwelveSeats_IsAHubWithOneRingAroundTheBoss()
    {
        var view = View(Factory(12));

        Assert.Equal(FactoryWatchLayout.Hub, view.Layout);
        var boss = view.Stations.Single(s => s.IsBoss);
        var (cx, cy) = (boss.Gx + boss.Width / 2, boss.Gy + boss.Height / 2);
        var radii = view.Stations.Where(s => !s.IsBoss)
            .Select(s => Math.Round(Math.Sqrt(Math.Pow(s.Gx + s.Width / 2 - cx, 2) + Math.Pow(s.Gy + s.Height / 2 - cy, 2)), 1))
            .Distinct().ToList();
        Assert.Single(radii);
        Assert.Equal(11, view.Stations.Count(s => !s.IsBoss));
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_TwentySeats_IsAHubWithASecondRing()
    {
        var view = View(Factory(20));

        Assert.Equal(FactoryWatchLayout.Hub, view.Layout);
        var boss = view.Stations.Single(s => s.IsBoss);
        var (cx, cy) = (boss.Gx + boss.Width / 2, boss.Gy + boss.Height / 2);
        var radii = view.Stations.Where(s => !s.IsBoss)
            .Select(s => Math.Round(Math.Sqrt(Math.Pow(s.Gx + s.Width / 2 - cx, 2) + Math.Pow(s.Gy + s.Height / 2 - cy, 2)), 1))
            .Distinct().OrderBy(r => r).ToList();
        Assert.Equal(2, radii.Count);
        Assert.Equal(20, view.Stations.Count);
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_OneToSixtySeatsWithAndWithoutABoss_NoStationOverlapsOrLeavesTheFloor()
    {
        foreach (var boss in new[] { true, false })
            for (var seats = 1; seats <= 60; seats++)
            {
                var view = View(Factory(seats, boss));

                Assert.Equal(seats, view.Stations.Count);
                Assert.Equal(seats <= FactoryWatchFold.WorkshopMaxSeats ? FactoryWatchLayout.Workshop : FactoryWatchLayout.Hub, view.Layout);
                AssertOnFloorWithoutOverlap(view);
            }
    }

    [Fact]
    public void View_FactoryWithLines_HasOneHallPerLineInSeatOrderAndOtherLast()
    {
        var f = Factory(1);
        f.Seats.Add(Seat("writer", "Write"));
        f.Seats.Add(Seat("loose"));
        f.Seats.Add(Seat("editor", "Write"));
        f.Seats.Add(Seat("poster", "Publish"));

        var view = View(f);

        Assert.Equal(FactoryWatchLayout.Lines, view.Layout);
        Assert.Equal(new[] { "Write", "Publish", FactoryFloorFold.OtherLane }, view.Lines.Select(l => l.Name));
        Assert.Equal(new[] { 0, 1, 2 }, view.Lines.Select(l => l.Index));
        foreach (var station in view.Stations.Where(s => !s.IsBoss))
        {
            var hall = view.Lines.Single(l => l.Name == station.Line);
            Assert.True(station.Gx >= hall.Gx && station.Gx + station.Width <= hall.Gx + hall.Width
                        && station.Gy >= hall.Gy && station.Gy + station.Height <= hall.Gy + hall.Height, $"{station.Id} is outside its hall");
        }
        var bossStation = view.Stations.Single(s => s.IsBoss);
        Assert.Null(bossStation.Line);
        Assert.True(bossStation.Gy + bossStation.Height <= view.Lines.Min(l => l.Gy), "the Boss's office stands above the halls");
        AssertOnFloorWithoutOverlap(view);
    }

    [Fact]
    public void View_TwoSeatsWithALine_IsLinesNotAWorkshop()
    {
        var f = Factory(1);
        f.Seats.Add(Seat("writer", "Write"));

        Assert.Equal(FactoryWatchLayout.Lines, View(f).Layout);
    }

    [Fact]
    public void View_EveryStation_HasOneCharacterStandingAtIt()
    {
        var view = View(Factory(7));

        Assert.Equal(view.Stations.Select(s => s.Id), view.Characters.Select(c => c.Seat));
        foreach (var c in view.Characters)
        {
            var st = view.Stations.Single(s => s.Id == c.Station);
            Assert.InRange(c.HomeGx, st.Gx, st.Gx + st.Width);
            Assert.True(c.HomeGy >= st.Gy + st.Height, $"{c.Seat} stands in front of its bench");
        }
    }

    // ---------------------------------------------------------------------------------------------------- colour

    [Fact]
    public void View_SameSeatAcrossCallsAndReorderedSeats_KeepsItsColour()
    {
        var f = Factory(6);
        var first = View(f, new[] { Row("seat-2", FactoryActivityOutcome.Done, "x", Now.AddHours(-1)) }).Characters.ToDictionary(c => c.Seat, c => c.Colour);

        var reordered = Factory(6);
        reordered.Seats.Reverse();
        var second = FactoryWatchFold.View(reordered, Array.Empty<FactoryActivityDto>(), false,
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), Utc, FactoryWatchAudience.Owner)
            .Characters.ToDictionary(c => c.Seat, c => c.Colour);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Colours_AnotherSeatAddedBeforeItInIdOrder_SeatKeepsItsColour()
    {
        // Colouring by sorted position would give seat-1 the first colour alone and the second beside seat-0.
        var alone = FactoryWatchFold.Colours(new[] { "seat-1" })["seat-1"];
        var withAnEarlierSeat = FactoryWatchFold.Colours(new[] { "seat-0", "seat-1" })["seat-1"];

        Assert.Equal(alone, withAnEarlierSeat);
        Assert.NotEqual(FactoryWatchFold.Colours(new[] { "seat-0", "seat-1" })["seat-0"], withAnEarlierSeat);
    }

    [Fact]
    public void View_TwelveSeats_EverySeatHasItsOwnColourFromThePalette()
    {
        var colours = View(Factory(12)).Characters.Select(c => c.Colour).ToList();

        Assert.Equal(12, colours.Distinct().Count());
        Assert.All(colours, c => Assert.Contains(c, FactoryWatchFold.Palette));
    }

    // ---------------------------------------------------------------------------------------------------- steps

    [Fact]
    public void StepKind_EveryActivityOutcome_MapsToExactlyOneKnownStepKind()
    {
        var expected = new Dictionary<string, string>
        {
            [FactoryActivityOutcome.Started] = FactoryWatchStepKind.Woke,
            [FactoryActivityOutcome.Done] = FactoryWatchStepKind.Delivered,
            [FactoryActivityOutcome.Screened] = FactoryWatchStepKind.Delivered,
            [FactoryActivityOutcome.Talked] = FactoryWatchStepKind.Talked,
            [FactoryActivityOutcome.Asked] = FactoryWatchStepKind.Talked,
            [FactoryActivityOutcome.Escalated] = FactoryWatchStepKind.Talked,
            [FactoryActivityOutcome.Allowed] = FactoryWatchStepKind.Talked,
            [FactoryActivityOutcome.NothingToDo] = FactoryWatchStepKind.CheckedNothing,
            [FactoryActivityOutcome.Paused] = FactoryWatchStepKind.CheckedNothing,
            [FactoryActivityOutcome.Skipped] = FactoryWatchStepKind.CheckedNothing,
            [FactoryActivityOutcome.Blocked] = FactoryWatchStepKind.Hiccup,
            [FactoryActivityOutcome.SentBack] = FactoryWatchStepKind.Hiccup,
            [FactoryActivityOutcome.Failed] = FactoryWatchStepKind.Hiccup,
        };

        // A new outcome added to the closed list without a step fails here, by name.
        Assert.Equal(FactoryActivityOutcome.All.OrderBy(o => o), expected.Keys.OrderBy(o => o));
        foreach (var outcome in FactoryActivityOutcome.All)
        {
            Assert.Equal(expected[outcome], FactoryWatchFold.StepKind(outcome));
            Assert.Contains(FactoryWatchFold.StepKind(outcome), FactoryWatchStepKind.All);
        }
    }

    [Fact]
    public void StepKind_UnknownOutcome_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => FactoryWatchFold.StepKind("exploded"));
    }

    [Fact]
    public void View_RunOfEmptyChecks_BecomesOneStepCarryingItsCount()
    {
        var start = Now.AddHours(-10);
        var rows = new List<FactoryActivityDto> { Row("seat-1", FactoryActivityOutcome.Started, "Started on the request", start.AddMinutes(-30)) };
        rows.AddRange(EmptyChecks("seat-1", 25, start));
        rows.Add(Row("seat-1", FactoryActivityOutcome.Done, "Version 2 is live", start.AddHours(3)));

        var view = View(Factory(3), rows);

        Assert.Equal(new[] { FactoryWatchStepKind.Woke, FactoryWatchStepKind.CheckedNothing, FactoryWatchStepKind.Delivered }, view.Steps.Select(s => s.Kind));
        var quiet = view.Steps[1];
        Assert.Equal(25, quiet.EmptyChecks);
        Assert.Equal(start, quiet.Utc);
        Assert.Equal(start.AddMinutes(5 * 24), quiet.ToUtc);
        Assert.Equal("Checked 25 times - nothing to do", quiet.Words);
        Assert.Equal("22:00-00:00", quiet.Time);
        Assert.Equal(0, view.Steps[0].EmptyChecks);
        Assert.Null(view.Steps[0].ToUtc);
    }

    [Fact]
    public void View_RunOfEmptyChecks_IsGroupedExactlyAsTheActivityTabGroupsIt()
    {
        var start = Now.AddHours(-10);
        var rows = EmptyChecks("seat-1", 4, start);
        rows.AddRange(EmptyChecks("seat-2", 3, start.AddMinutes(1)).Select(r => { r.Actor = "trigger:other"; return r; }));
        rows = rows.OrderBy(r => r.OccurredUtc).ToList();

        var steps = View(Factory(3), rows).Steps;
        var lines = FactoryAgentsFold.Collapse(rows, Day, Utc, Array.Empty<FactoryActivityDto>(), rows);

        Assert.Equal(lines.Select(l => l.Key), steps.Select(s => s.Key));
        Assert.Equal(lines.Select(l => l.What), steps.Select(s => s.Words));
    }

    [Fact]
    public void View_RowsOutOfOrder_StepsComeOutOldestFirst()
    {
        var rows = new[]
        {
            Row("seat-2", FactoryActivityOutcome.Done, "third", Now.AddHours(-1)),
            Row("boss", FactoryActivityOutcome.Started, "first", Now.AddHours(-5)),
            Row("seat-1", FactoryActivityOutcome.Talked, "second", Now.AddHours(-3)),
        };

        var view = View(Factory(3), rows);

        Assert.Equal(new[] { "first", "second", "third" }, view.Steps.Select(s => s.Words));
        Assert.Equal(new[] { "boss", "seat-1", "seat-2" }, view.Steps.Select(s => s.Seat));
    }

    [Fact]
    public void View_EmptyWindow_GivesAnEmptyTimelineThatSaysSo()
    {
        var view = View(Factory(3));

        Assert.Empty(view.Steps);
        Assert.Equal("Nothing happened in this factory in the last 24 hours.", view.EmptyText);
        Assert.Equal(3, view.Stations.Count);
    }

    [Fact]
    public void View_StepsInTheWindow_HaveNoEmptyText()
    {
        Assert.Null(View(Factory(3), new[] { Row("boss", FactoryActivityOutcome.Started, "x", Now.AddHours(-1)) }).EmptyText);
    }

    [Fact]
    public void View_RowForAnUnregisteredWorker_IsLeftOffAndCountedInNotes()
    {
        var rows = new[]
        {
            Row("ghost", FactoryActivityOutcome.Done, "x", Now.AddHours(-2)),
            Row("boss", FactoryActivityOutcome.Done, "y", Now.AddHours(-1)),
        };

        var view = View(Factory(3), rows);

        Assert.Equal(new[] { "y" }, view.Steps.Select(s => s.Words));
        Assert.Contains(view.Notes, n => n.StartsWith("1 row names a worker this factory has not registered", StringComparison.Ordinal));
    }

    [Fact]
    public void View_OwnerActs_AreNotSeatStepsAndNotCountedAsUnregistered()
    {
        var rows = new[]
        {
            Row("seat-1", FactoryActivityOutcome.Done, "Marked handled by owner (device:abc): the question", Now.AddHours(-2), actor: "owner (device:abc)"),
            Row("owner", FactoryActivityOutcome.Done, "The owner archived Demo Website", Now.AddHours(-1), actor: "owner (device:abc)"),
        };

        var view = View(Factory(3), rows);

        Assert.Empty(view.Steps);
        Assert.DoesNotContain(view.Notes, n => n.Contains("not registered", StringComparison.Ordinal));
    }

    [Fact]
    public void View_RowOfAnotherFactory_Throws()
    {
        var row = Row("boss", FactoryActivityOutcome.Done, "x", Now.AddHours(-1));
        row.Factory = "someone-else";

        Assert.Throws<ArgumentException>(() => View(Factory(3), new[] { row }));
    }

    [Fact]
    public void View_ContributorAudienceWithoutAScope_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FactoryWatchFold.View(Factory(3), Array.Empty<FactoryActivityDto>(), false, Day, Utc, FactoryWatchAudience.Contributor));
    }

    [Fact]
    public void View_TruncatedWindow_SaysSo()
    {
        var view = FactoryWatchFold.View(Factory(3), new[] { Row("boss", FactoryActivityOutcome.Done, "x", Now.AddHours(-1)) }, true, Day, Utc, FactoryWatchAudience.Owner);

        Assert.Equal("This window holds more than 1 rows; only the first 1 are shown. Choose a shorter window.", view.TruncatedText);
    }

    [Fact]
    public void View_DisplayFields_NeverCarryAMachineFolderSessionOrLink()
    {
        var rows = new List<FactoryActivityDto> { Row("seat-1", FactoryActivityOutcome.Failed, "it broke", Now.AddHours(-3)) };
        rows.AddRange(EmptyChecks("seat-1", 3, Now.AddHours(-2)));

        var view = View(Factory(3), rows);

        var shown = view.Stations.SelectMany(s => new[] { s.Label, s.Sub, s.Line })
            .Concat(view.Characters.Select(c => c.Name))
            .Concat(view.Steps.SelectMany(s => new[] { s.Words, s.Time }))
            .Concat(view.Lines.Select(l => l.Name))
            .Concat(view.Notes)
            .Concat(new[] { view.Title, view.EmptyText, view.TruncatedText })
            .Where(t => t is not null)
            .ToList();
        foreach (var forbidden in new[] { "SOREN_NORTH", @"D:\factories", "5f8d72f4", "session:", "trigger:" })
            Assert.DoesNotContain(shown, t => t!.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }
}
