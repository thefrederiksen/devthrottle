using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// Factory Control, step 2: every seat says whether its sessions close themselves, and a factory with a seat that does
/// not is flagged on the Factories list. Folded on the Gateway from how each scheduled run's session ended
/// (<see cref="CronRunEndingFold"/>), with the same ruling on each run as the Schedule page's record.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactorySeatMarkerTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A run already stamped with its ending, fired <paramref name="hoursAgo"/> hours before now.</summary>
    private static CronRunRecord Run(string ending, double hoursAgo) => new()
    {
        ScheduledUtc = Now.AddHours(-hoursAgo),
        FiredUtc = Now.AddHours(-hoursAgo),
        Machine = "SOREN_NORTH",
        SessionId = ending == CronRunEndings.NoSession ? null : Guid.NewGuid().ToString(),
        InfraStatus = ending == CronRunEndings.NoSession ? "not-started" : "started",
        TaskStatus = "unknown",
        Ending = ending,
    };

    private static IReadOnlyList<CronRunRecord> Runs(params CronRunRecord[] runs) => runs;

    private static CronRunRecordSummaryDto Marker(params IReadOnlyList<CronRunRecord>[] bySchedule) =>
        CronRunEndingFold.SeatMarker(bySchedule, Now);

    // ---- one seat's marker -----------------------------------------------------------------------------------

    [Fact]
    public void SeatMarker_EveryRunClosedItself_ClosesItself()
    {
        var marker = Marker(Runs(
            Run(CronRunEndings.ClosedItself, 2), Run(CronRunEndings.ClosedItself, 26), Run(CronRunEndings.ClosedItself, 50)));

        Assert.Equal("ok", marker.Verdict);
        Assert.Equal("closes itself - its last 3 runs", marker.Text);
        Assert.Equal(3, marker.Runs);
    }

    [Fact]
    public void SeatMarker_SomeRunsLeftOrStopped_StayedOpenNOfM()
    {
        // Stopped by you, stopped by another session and left open past an hour all mean someone else had to end it.
        var marker = Marker(Runs(
            Run(CronRunEndings.StillOpen, 3),
            Run(CronRunEndings.StoppedByYou, 27),
            Run(CronRunEndings.ClosedItself, 51),
            Run(CronRunEndings.StoppedBySession, 75),
            Run(CronRunEndings.ClosedItself, 99),
            Run(CronRunEndings.ClosedItself, 123),
            Run(CronRunEndings.ClosedItself, 147)));

        Assert.Equal("bad", marker.Verdict);
        Assert.Equal("stayed open 3 of its last 7 runs", marker.Text);
    }

    [Fact]
    public void SeatMarker_NoRuns_SaysNothingIsRecorded()
    {
        var marker = Marker();

        Assert.Equal("none", marker.Verdict);
        Assert.Equal("no runs recorded yet", marker.Text);
    }

    [Fact]
    public void SeatMarker_OnlyRunsThatSayNothing_SaysNothingIsRecordedRatherThanClaimingEither()
    {
        // Still young, closer not recorded (every run before 9 October), ended with its Director, did not start.
        var marker = Marker(Runs(
            Run(CronRunEndings.StillOpen, 0.25),
            Run(CronRunEndings.ClosedNotRecorded, 24),
            Run(CronRunEndings.DirectorStopped, 48),
            Run(CronRunEndings.NoSession, 72)));

        Assert.Equal("none", marker.Verdict);
        Assert.Equal("no runs recorded yet", marker.Text);
    }

    [Fact]
    public void SeatMarker_AFireThatStartedNoSession_IsNotCountedAsStayingOpen()
    {
        var marker = Marker(Runs(Run(CronRunEndings.NoSession, 1), Run(CronRunEndings.ClosedItself, 25)));

        Assert.Equal("ok", marker.Verdict);
        Assert.Equal("closes itself - its last run", marker.Text);
    }

    [Fact]
    public void SeatMarker_SeveralSchedules_FoldsAcrossThemAndSaysSo()
    {
        var morning = Runs(Run(CronRunEndings.ClosedItself, 2), Run(CronRunEndings.ClosedItself, 26));
        var evening = Runs(Run(CronRunEndings.StoppedByYou, 14), Run(CronRunEndings.ClosedItself, 38));

        var marker = Marker(morning, evening);

        Assert.Equal("bad", marker.Verdict);
        Assert.Equal("stayed open 1 of its last 4 runs, across its 2 schedules", marker.Text);
    }

    [Fact]
    public void SeatMarker_TheWindowIsTheNewestTenRunsAcrossItsSchedules()
    {
        // Ten newer runs that closed themselves push an old left-open run out of the window.
        var newer = Enumerable.Range(1, 10).Select(h => Run(CronRunEndings.ClosedItself, h)).ToArray();
        var old = Runs(Run(CronRunEndings.StoppedByYou, 100));

        var marker = Marker(newer, old);

        Assert.Equal("ok", marker.Verdict);
        Assert.Equal("closes itself - its last 10 runs", marker.Text);
    }

    [Fact]
    public void Judge_IsTheSameRulingTheScheduleRecordUses()
    {
        // A schedule whose record is "bad" for a left-open run gives its seat a "stayed open" marker on the same run.
        var runs = Runs(Run(CronRunEndings.StillOpen, 2), Run(CronRunEndings.ClosedItself, 26));

        Assert.Equal("bad", CronRunEndingFold.Summarize(runs, new Dictionary<string, CcDirector.Gateway.History.SessionEndingFact>(), Now).Verdict);
        Assert.Equal("bad", Marker(runs).Verdict);
        Assert.Equal(CronRunEndingFold.RunJudgement.LeftOpen, CronRunEndingFold.Judge(runs[0], Now));
    }

    // ---- the Factories screen --------------------------------------------------------------------------------

    private static RegisteredFactorySeatDto Seat(string id, string role, params string[] schedules) =>
        new() { Id = id, Name = role, Role = role, BriefFile = $"agents/{id}.yaml", Schedules = schedules.ToList(), Computer = "SOREN_NORTH" };

    private static RegisteredFactoryDto Factory(string id, string title, params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = id, Title = title, Folder = $@"D:\f\{id}", Computer = "SOREN_NORTH", BossSeat = seats[0].Id, Seats = seats.ToList(),
    };

    private static CronJobDto Job(string id) => new()
    {
        Id = id, Name = id, Enabled = true, ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *", TimeZoneId = "UTC",
    };

    private static FactoriesScreenInputs Inputs(IReadOnlyList<RegisteredFactoryDto> registry, Dictionary<string, IReadOnlyList<CronRunRecord>> runs)
    {
        var activity = new FactoryFoldInputs(Array.Empty<FactoryActivityDto>(), false, false, Array.Empty<FactoryActivityDto>(),
            Array.Empty<FactoryActivityDto>(), Array.Empty<FactoryTriggerFacts>(), new HashSet<string>(),
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), TimeZoneInfo.Utc, Now);
        var jobs = registry.SelectMany(f => f.Seats).SelectMany(s => s.Schedules).Select(Job).ToList();
        return new FactoriesScreenInputs(registry, activity, jobs, new Dictionary<string, GoalNumberDto>(),
            Array.Empty<FactoryActivityDto>(), runs);
    }

    private static readonly IReadOnlyList<CronRunRecord> Closes = Runs(Run(CronRunEndings.ClosedItself, 2), Run(CronRunEndings.ClosedItself, 26));
    private static readonly IReadOnlyList<CronRunRecord> StaysOpen = Runs(Run(CronRunEndings.StillOpen, 2), Run(CronRunEndings.ClosedItself, 26));

    [Fact]
    public void Seats_EachSeatCarriesItsMarkerAndTone()
    {
        var f = Factory("tallyhand", "Tallyhand", Seat("boss", "Boss", "cj_boss"), Seat("scout", "Scout", "cj_scout"), Seat("writer", "Writer"));
        var input = Inputs(new[] { f }, new() { ["cj_boss"] = Closes, ["cj_scout"] = StaysOpen });

        var rows = FactoriesScreenFold.Seats(f, input).Rows.ToDictionary(r => r.SeatId);

        Assert.Equal("closes itself - its last 2 runs", rows["boss"].ClosingText);
        Assert.Equal(FactoryTone.Ok, rows["boss"].ClosingTone);
        Assert.Equal("stayed open 1 of its last 2 runs", rows["scout"].ClosingText);
        Assert.Equal(FactoryTone.Amber, rows["scout"].ClosingTone);
        Assert.Equal("no runs recorded yet", rows["writer"].ClosingText);
        Assert.Equal(FactoryTone.Grey, rows["writer"].ClosingTone);
    }

    [Fact]
    public void List_AFactoryWithOneSeatThatStaysOpen_IsFlaggedNamingTheSeatByRole()
    {
        var bad = Factory("tallyhand", "Tallyhand", Seat("boss", "Boss", "cj_boss"), Seat("scout", "Scout", "cj_scout"));
        var good = Factory("warmforward", "WarmForward", Seat("lead", "Boss", "cj_lead"));
        var input = Inputs(new[] { bad, good }, new() { ["cj_boss"] = Closes, ["cj_scout"] = StaysOpen, ["cj_lead"] = Closes });

        var rows = FactoriesScreenFold.List(input).Rows.ToDictionary(r => r.Id);

        Assert.Equal("Does not close itself: Scout - stayed open 1 of its last 2 runs", rows["tallyhand"].LeftOpenText);
        Assert.Equal("/factories/tallyhand/seats", rows["tallyhand"].LeftOpenHref);
        Assert.Null(rows["warmforward"].LeftOpenText);
        Assert.Null(rows["warmforward"].LeftOpenHref);
    }

    [Fact]
    public void List_EverySeatClosesItselfOrHasNoRecord_IsNotFlagged()
    {
        var f = Factory("tallyhand", "Tallyhand", Seat("boss", "Boss", "cj_boss"), Seat("scout", "Scout", "cj_scout"), Seat("writer", "Writer"));
        var input = Inputs(new[] { f }, new() { ["cj_boss"] = Closes, ["cj_scout"] = Closes });

        var row = FactoriesScreenFold.List(input).Rows.Single();

        Assert.Null(row.LeftOpenText);
        Assert.Null(row.LeftOpenHref);
    }

    [Fact]
    public void List_TwoSeatsThatStayOpen_NamesBoth()
    {
        var f = Factory("tallyhand", "Tallyhand", Seat("boss", "Boss", "cj_boss"), Seat("scout", "Scout", "cj_scout"));
        var input = Inputs(new[] { f }, new() { ["cj_boss"] = StaysOpen, ["cj_scout"] = StaysOpen });

        var row = FactoriesScreenFold.List(input).Rows.Single();

        Assert.Equal("2 seats do not close themselves: Boss - stayed open 1 of its last 2 runs; Scout - stayed open 1 of its last 2 runs",
            row.LeftOpenText);
    }

    [Fact]
    public void Page_CarriesTheSameFlagAsTheList()
    {
        var f = Factory("tallyhand", "Tallyhand", Seat("boss", "Boss", "cj_boss"), Seat("scout", "Scout", "cj_scout"));
        var input = Inputs(new[] { f }, new() { ["cj_boss"] = Closes, ["cj_scout"] = StaysOpen });

        var page = FactoriesScreenFold.Page(f, input);

        Assert.Equal(FactoriesScreenFold.List(input).Rows.Single().LeftOpenText, page.LeftOpenText);
        Assert.Equal("/factories/tallyhand/seats", page.LeftOpenHref);
    }
}
