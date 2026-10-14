using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// One seat's page (Factory Seats mission, phase 1), proved without a server. The cases are the ones the mission
/// exists for: a seat with NO schedule still has every panel; a seat whose schedule the Gateway no longer has says so
/// and offers nothing to press; a paused seat is not a talk-only one; and "Take off this seat" says, before it is
/// pressed, that a switched-on schedule keeps running.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactorySeatPageFoldTests
{
    // 6 Oct 2026 10:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, params string[] schedules) =>
        new() { Id = id, Name = name, Role = role, BriefFile = $"agents/{id}.yaml", Schedules = schedules.ToList(), Computer = "SOREN_NORTH" };

    private static RegisteredFactoryDto WarmForward(params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = "/home/soren/factories/warmforward",
        Computer = "devlinux",
        BossSeat = "boss",
        GoalText = "Cut the propane bill by a third this season.",
        GoalApprovedOn = "2026-10-04",
        Seats = seats.ToList(),
    };

    private static CronJobDto Job(string id, string name, string cron = "15 6 * * *", bool enabled = true) => new()
    {
        Id = id, Name = name, Enabled = enabled, ScheduleKind = CronSchedule.KindRecurring, CronExpression = cron, TimeZoneId = "UTC",
        // A schedule that validates, so it has runs to come: its shift is read from those.
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = "/home/soren/factories/warmforward", Seed = "/run" },
    };

    private static FactoryActivityDto Row(string agent, string outcome, string what, DateTime at, string? session = null) => new()
    {
        Id = Guid.NewGuid(), Factory = "warmforward", FactoryAgent = agent, Outcome = outcome, What = what,
        OccurredUtc = at, RecordedUtc = at, SessionId = session, Actor = "session:" + session,
    };

    private static FactoriesScreenInputs Inputs(RegisteredFactoryDto f, IReadOnlyList<CronJobDto>? jobs = null,
        IReadOnlyList<FactoryActivityDto>? rows = null, IReadOnlyList<FactoryTriggerFacts>? triggers = null,
        IReadOnlyList<FactoryActivityDto>? talks = null, GoalNumberDto? number = null)
    {
        rows ??= Array.Empty<FactoryActivityDto>();
        var activity = new FactoryFoldInputs(rows, false, false, Array.Empty<FactoryActivityDto>(), Array.Empty<FactoryActivityDto>(),
            triggers ?? Array.Empty<FactoryTriggerFacts>(), new HashSet<string>(),
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), TimeZoneInfo.Utc, Now);
        var latest = new Dictionary<string, GoalNumberDto>();
        if (number is not null) latest[number.Factory] = number;
        return new FactoriesScreenInputs(new[] { f }, activity, jobs ?? Array.Empty<CronJobDto>(), latest,
            talks ?? Array.Empty<FactoryActivityDto>(), new Dictionary<string, IReadOnlyList<CronRunRecord>>());
    }

    // ---------- a seat with a schedule ----------

    [Fact]
    public void Page_ASeatWithASchedule_ShowsWhoItIs_HowItStarts_AndEveryButtonOfItsSchedule()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_scout");
        var f = WarmForward(Seat("boss", "Boss", "Boss"), scout);

        var page = FactorySeatPageFold.Page(f, scout, Inputs(f, new[] { Job("cj_scout", "WarmForward Factory - Scout - daily scan") }), memoryNotes: 3, FactoryBriefStanding.None);

        Assert.Equal("Factories / WarmForward / Seats / Scout", page.Crumb);
        Assert.Equal(new[] { "/factories", "/factories/warmforward", "/factories/warmforward/seats" }, page.CrumbHrefs);
        Assert.Equal("Scout", page.Name);
        Assert.Equal("Scout", page.Role);
        Assert.Equal("runs on SOREN_NORTH", page.ComputerText);
        Assert.Equal("Scheduled", page.StartsWord);
        Assert.Equal(FactoryTone.Ok, page.StartsTone);
        Assert.Equal("Talk to Scout", page.Talk.Label);
        Assert.Equal("scout", page.Talk.SeatId);

        Assert.Equal("How it starts", page.Starts.Heading);
        Assert.Null(page.Starts.TalkOnlyText);
        var schedule = Assert.Single(page.Starts.Schedules);
        Assert.Equal("cj_scout", schedule.JobId);
        Assert.Equal("WarmForward Factory - Scout - daily scan", schedule.Name);
        Assert.Equal("Daily 06:15", schedule.WhenText);
        Assert.Equal("Night shift", schedule.ShiftText);
        Assert.Equal("Edit schedule", schedule.EditLabel);
        Assert.Equal("Pause", schedule.ToggleLabel);
        Assert.False(schedule.Paused);
        Assert.Null(schedule.PausedText);
        Assert.Null(schedule.MissingText);
        Assert.Equal("Take off this seat", schedule.Unlink!.Label);
        Assert.Equal("cj_scout", schedule.Unlink.JobId);

        Assert.Equal("Goal", page.Goal.Heading);
        Assert.Equal("Cut the propane bill by a third this season.", page.Goal.Text);
        Assert.Equal("No number posted yet", page.GoalNumber.EmptyText);
        Assert.Equal("3 notes in the factory's memory. Memory belongs to the whole factory, not to one seat.", page.LeftBehind.MemoryText);
        Assert.Equal("/factories/warmforward/memory", page.LeftBehind.MemoryHref);
    }

    // ---------- a seat with NO schedule: the case the mission exists for ----------

    [Fact]
    public void Page_ASeatWithNoSchedule_HasEveryPanel_AndReadsTalkOnly()
    {
        var boss = Seat("boss", "Boss", "Boss");
        var f = WarmForward(boss, Seat("scout", "Scout", "Scout", "cj_scout"));
        var number = new GoalNumberDto
        {
            Factory = "warmforward", Value = "212", Unit = "litres saved", AsOf = "2026-10-05", Link = "https://example.test/m",
            PostedBy = "boss", PostedAtUtc = Now.AddHours(-3),
        };
        var talks = new[] { Row("boss", FactoryActivityOutcome.Talked, "decided: bunkie back to 13 C", Now.AddHours(-2)) };

        var page = FactorySeatPageFold.Page(f, boss, Inputs(f, new[] { Job("cj_scout", "scan") }, talks: talks, number: number), memoryNotes: 0, FactoryBriefStanding.None);

        // The head: the boss has no name of its own, and the chip says how it starts.
        Assert.Equal("Boss", page.Name);
        Assert.Equal("Factories / WarmForward / Seats / Boss", page.Crumb);
        Assert.Equal("Talk only", page.StartsWord);
        Assert.Equal(FactoryTone.Idle, page.StartsTone);
        Assert.Equal("Talk to the boss", page.Talk.Label);
        Assert.Equal("Starting the talk with the boss...", page.Talk.BusyLabel);

        // What it is told to do: where its brief is, and that the Gateway does not hold it yet.
        Assert.Equal("What this seat does", page.Brief.Heading);
        Assert.Equal("/home/soren/factories/warmforward/agents/boss.yaml", page.Brief.PathText);
        Assert.Equal("SOREN_NORTH", page.Brief.ComputerText);
        Assert.Contains("The brief is a file on SOREN_NORTH.", page.Brief.Note);
        Assert.Contains("It is not held on the Gateway yet", page.Brief.Note);

        // How it starts: talk only, said in a sentence, with nothing to edit, pause or take off.
        Assert.Equal("Talk only. Nothing is scheduled for this seat. You run it by talking to it.", page.Starts.TalkOnlyText);
        Assert.Empty(page.Starts.Schedules);
        Assert.Empty(page.Starts.Triggers);
        Assert.Null(page.Starts.TriggersPause);

        // The factory's goal and number are the factory's, so a seat with no schedule shows them all the same.
        Assert.Equal("Cut the propane bill by a third this season.", page.Goal.Text);
        Assert.Equal("Litres saved: 212", page.GoalNumber.ValueText);

        // What it has left behind: its last talk, that it never ran, and no "closes itself" verdict on runs it never had.
        Assert.Equal("Talked with you, today 08:00 - decided: bunkie back to 13 C", page.LeftBehind.LastTalkText);
        Assert.Equal("Not run yet", page.LeftBehind.LastRunText);
        Assert.Null(page.LeftBehind.ClosingText);
        Assert.Null(page.LeftBehind.ClosingTone);
        Assert.Equal("Nothing recorded in the last 7 days.", page.LeftBehind.RecentEmptyText);
        Assert.Equal("No notes in the factory's memory yet. Memory belongs to the whole factory, not to one seat.", page.LeftBehind.MemoryText);
        Assert.Equal("/factories?tab=activity&factory=warmforward&agent=boss", page.LeftBehind.AllHref);
    }

    // ---------- a seat whose schedule the Gateway no longer has ----------

    [Fact]
    public void Page_ASeatWhoseScheduleIsMissing_SaysSo_AndOffersNothingToPress()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_gone");
        var f = WarmForward(Seat("boss", "Boss", "Boss"), scout);

        var page = FactorySeatPageFold.Page(f, scout, Inputs(f), memoryNotes: 0, FactoryBriefStanding.None);

        // Not "Talk only" - something was set to run it and is gone - and not "Paused", which Resume would fix.
        Assert.Equal("Schedule missing", page.StartsWord);
        Assert.Equal(FactoryTone.Amber, page.StartsTone);
        Assert.Null(page.Starts.TalkOnlyText);
        var schedule = Assert.Single(page.Starts.Schedules);
        Assert.Equal("cj_gone", schedule.JobId);
        Assert.Equal("Schedule cj_gone is missing", schedule.WhenText);
        Assert.Equal("This seat names a schedule the Gateway does not have, so nothing runs from it.", schedule.MissingText);
        Assert.Null(schedule.Name);
        Assert.Null(schedule.EditLabel);
        Assert.Null(schedule.ToggleLabel);
        Assert.Null(schedule.Unlink);
        Assert.Equal("No run in the next 7 days", schedule.ShiftText);
    }

    [Fact]
    public void Page_OneScheduleMissingAndOneRunning_IsScheduled_AndBothAreListedInTheSeatsOrder()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_gone", "cj_scout");
        var f = WarmForward(scout);

        var page = FactorySeatPageFold.Page(f, scout, Inputs(f, new[] { Job("cj_scout", "scan") }), memoryNotes: 0, FactoryBriefStanding.None);

        Assert.Equal("Scheduled", page.StartsWord);
        Assert.Equal(new[] { "cj_gone", "cj_scout" }, page.Starts.Schedules.Select(s => s.JobId));
        Assert.Null(page.Starts.Schedules[0].Unlink);
        Assert.NotNull(page.Starts.Schedules[1].Unlink);
    }

    // ---------- paused is not talk only ----------

    [Fact]
    public void Page_ASeatWhoseOnlyScheduleIsSwitchedOff_IsPaused_WithResume()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_scout");
        var f = WarmForward(scout);

        var page = FactorySeatPageFold.Page(f, scout, Inputs(f, new[] { Job("cj_scout", "scan", enabled: false) }), memoryNotes: 0, FactoryBriefStanding.None);

        Assert.Equal("Paused", page.StartsWord);
        Assert.Equal(FactoryTone.Paused, page.StartsTone);
        Assert.Null(page.Starts.TalkOnlyText);
        var schedule = Assert.Single(page.Starts.Schedules);
        Assert.True(schedule.Paused);
        Assert.Equal("Paused", schedule.PausedText);
        Assert.Equal("Resume", schedule.ToggleLabel);
    }

    // ---------- triggers ----------

    [Fact]
    public void Page_ASeatATriggerStarts_ListsTheTrigger_AndOffersItsPause()
    {
        var desk = Seat("front-desk", "Front Desk", "Front Desk");
        var f = WarmForward(desk);
        var trigger = new FactoryTriggerFacts("t1", "New business mail", "warmforward", "front-desk", 300, false, false, null, Now.AddMinutes(-5), "nothing to do");

        var page = FactorySeatPageFold.Page(f, desk, Inputs(f, triggers: new[] { trigger }), memoryNotes: 0, FactoryBriefStanding.None);

        Assert.Equal("Triggered", page.StartsWord);
        Assert.Null(page.Starts.TalkOnlyText);
        var woken = Assert.Single(page.Starts.Triggers);
        Assert.Equal("Trigger: New business mail, every 5 min", woken.Text);
        Assert.Equal("OK", woken.StatusWord);
        Assert.Equal("pause", page.Starts.TriggersPause!.Action);

        var paused = FactorySeatPageFold.Page(f, desk, Inputs(f, triggers: new[] { trigger with { Paused = true } }), memoryNotes: 0, FactoryBriefStanding.None);
        Assert.Equal("Paused", paused.StartsWord);
        Assert.Equal("resume", paused.Starts.TriggersPause!.Action);

        var both = Seat("front-desk", "Front Desk", "Front Desk", "cj_desk");
        var g = WarmForward(both);
        Assert.Equal("Scheduled and triggered",
            FactorySeatPageFold.Page(g, both, Inputs(g, new[] { Job("cj_desk", "desk") }, triggers: new[] { trigger }), memoryNotes: 0, FactoryBriefStanding.None).StartsWord);
    }

    // ---------- take off this seat ----------

    [Fact]
    public void Unlink_ASwitchedOnSchedule_TheConfirmSaysItKeepsRunning_AndThatTheSeatBecomesTalkOnly()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_scout");
        var f = WarmForward(scout);

        var unlink = FactorySeatPageFold.Page(f, scout, Inputs(f, new[] { Job("cj_scout", "Scout - daily scan") }), memoryNotes: 0, FactoryBriefStanding.None)
            .Starts.Schedules.Single().Unlink!;

        Assert.Equal("Take this schedule off Scout?", unlink.ConfirmTitle);
        Assert.Equal("Take it off", unlink.ConfirmLabel);
        Assert.Equal("Taking it off...", unlink.BusyLabel);
        Assert.Equal(new[]
        {
            "'Scout - daily scan' stops being a schedule of Scout. The sessions it starts no longer belong to WarmForward.",
            "It stays switched on and keeps running at its times, as a plain schedule in no factory. Pause it first if it should stop.",
            "It is kept, not deleted. It is listed on the Schedule page, and cc-devthrottle schedule link puts it back on a seat.",
            "Scout will then be talk only.",
        }, unlink.ConfirmLines);
    }

    [Fact]
    public void Unlink_ASwitchedOffScheduleOfASeatWithAnother_SaysItStaysOff_AndDoesNotSayTalkOnly()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_a", "cj_b");
        var f = WarmForward(scout);

        var schedules = FactorySeatPageFold.Page(f, scout,
            Inputs(f, new[] { Job("cj_a", "morning"), Job("cj_b", "evening", enabled: false) }), memoryNotes: 0, FactoryBriefStanding.None).Starts.Schedules;

        var off = schedules.Single(s => s.JobId == "cj_b").Unlink!;
        Assert.Contains("It stays switched off, in no factory.", off.ConfirmLines);
        Assert.DoesNotContain(off.ConfirmLines, l => l.Contains("talk only"));
        Assert.DoesNotContain(schedules.Single(s => s.JobId == "cj_a").Unlink!.ConfirmLines, l => l.Contains("talk only"));
    }

    [Fact]
    public void Unlink_TheLastScheduleOfASeatATriggerAlsoStarts_DoesNotSayTalkOnly()
    {
        var desk = Seat("front-desk", "Front Desk", "Front Desk", "cj_desk");
        var f = WarmForward(desk);
        var trigger = new FactoryTriggerFacts("t1", "Mail", "warmforward", "front-desk", 300, false, false, null, null, null);

        var unlink = FactorySeatPageFold.Page(f, desk, Inputs(f, new[] { Job("cj_desk", "desk") }, triggers: new[] { trigger }), memoryNotes: 0, FactoryBriefStanding.None)
            .Starts.Schedules.Single().Unlink!;

        Assert.DoesNotContain(unlink.ConfirmLines, l => l.Contains("talk only"));
    }

    [Fact]
    public void Unlink_AScheduleTheFactorysArchiveSwitchedOff_IsNotOffered()
    {
        // Restore switches back on, by id, what the archive switched off. Taken off first, it would switch on a
        // schedule that is no longer the factory's.
        var scout = Seat("scout", "Scout", "Scout", "cj_scout", "cj_other");
        var f = WarmForward(scout);
        f.ArchivedAtUtc = Now.AddDays(-1);
        f.ArchivedSchedules = new() { "cj_scout" };

        var schedules = FactorySeatPageFold.Page(f, scout,
            Inputs(f, new[] { Job("cj_scout", "scan", enabled: false), Job("cj_other", "other", enabled: false) }), memoryNotes: 0, FactoryBriefStanding.None).Starts.Schedules;

        Assert.Null(schedules.Single(s => s.JobId == "cj_scout").Unlink);
        Assert.NotNull(schedules.Single(s => s.JobId == "cj_other").Unlink);
    }

    [Fact]
    public void UnlinkedText_SaysWhichSeat_AndWhetherTheScheduleStillRuns()
    {
        var f = WarmForward(Seat("boss", "Boss", "Boss", "cj_boss"), Seat("scout", "Scout", "Scout", "cj_scout"));
        var on = Job("cj_scout", "Scout - daily scan");
        on.Factory = "warmforward";
        on.Seat = "scout";
        var off = Job("cj_boss", "Boss - morning run", enabled: false);
        off.Factory = "warmforward";
        off.Seat = "boss";

        Assert.Equal("Taken off Scout of WarmForward. 'Scout - daily scan' is still switched on and keeps running at its times, as a plain schedule in no factory.",
            FactorySeatPageFold.UnlinkedText(on, f));
        Assert.Equal("Taken off the boss of WarmForward. 'Boss - morning run' is still switched off, and is now in no factory.",
            FactorySeatPageFold.UnlinkedText(off, f));
        // A schedule naming a factory the registry no longer has is still taken off; the sentence names what it named.
        Assert.Equal("Taken off warmforward. 'Scout - daily scan' is still switched on and keeps running at its times, as a plain schedule in no factory.",
            FactorySeatPageFold.UnlinkedText(on, factory: null));
    }

    // ---------- what it has left behind ----------

    [Fact]
    public void LeftBehind_ShowsOnlyThisSeatsRows_ItsLastRun_AndItsOwnLastTalk()
    {
        var scout = Seat("scout", "Scout", "Scout", "cj_scout");
        var f = WarmForward(Seat("boss", "Boss", "Boss"), scout);
        var rows = new[]
        {
            Row("scout", FactoryActivityOutcome.Started, "scout run 12 started", Now.AddHours(-4), "s1"),
            Row("scout", FactoryActivityOutcome.Done, "found zone 8 holding 20 C", Now.AddHours(-4).AddMinutes(9), "s1"),
            Row("boss", FactoryActivityOutcome.Done, "the boss's own line", Now.AddHours(-1), "s2"),
        };
        var talks = new[]
        {
            Row("boss", FactoryActivityOutcome.Talked, "the boss talked", Now.AddHours(-1)),
            Row("scout", FactoryActivityOutcome.Talked, "agreed to scan twice a day", Now.AddDays(-9)),
        };

        var left = FactorySeatPageFold.Page(f, scout, Inputs(f, new[] { Job("cj_scout", "scan") }, rows, talks: talks), memoryNotes: 1, FactoryBriefStanding.None).LeftBehind;

        Assert.Equal("What it has left behind", left.Heading);
        Assert.Equal("Today 06:00 - succeeded", left.LastRunText);
        Assert.Equal(FactoryTone.Ok, left.LastRunTone);
        Assert.Equal("Talked with you, 27 Sep 10:00 - agreed to scan twice a day", left.LastTalkText);
        Assert.Equal(2, left.Recent.Count);
        Assert.DoesNotContain(left.Recent, r => r.What.Contains("the boss's own line"));
        Assert.Null(left.RecentEmptyText);
        Assert.Contains(left.Numbers, n => n.Text == "1 run");
        Assert.Equal("no runs recorded yet", left.ClosingText);
        Assert.Equal(FactoryTone.Grey, left.ClosingTone);
        Assert.Equal("1 note in the factory's memory. Memory belongs to the whole factory, not to one seat.", left.MemoryText);
    }

    [Fact]
    public void LeftBehind_KeepsOnlyTheNewestLines()
    {
        var scout = Seat("scout", "Scout", "Scout");
        var f = WarmForward(scout);
        var rows = Enumerable.Range(0, FactorySeatPageFold.RecentLines + 5)
            .Select(i => Row("scout", FactoryActivityOutcome.Done, $"line {i:00}", Now.AddHours(-48).AddMinutes(i)))
            .ToList();

        var left = FactorySeatPageFold.Page(f, scout, Inputs(f, rows: rows), memoryNotes: 0, FactoryBriefStanding.None).LeftBehind;

        Assert.Equal(FactorySeatPageFold.RecentLines, left.Recent.Count);
        Assert.Contains("line 05", left.Recent[0].What);
        Assert.Contains("line 19", left.Recent[^1].What);
    }

    // ---------- the Seats tab opens every seat ----------

    [Fact]
    public void Seats_EveryRowOpensItsSeatPage_WithOrWithoutASchedule()
    {
        var f = WarmForward(Seat("boss", "Boss", "Boss"), Seat("scout", "Scout", "Scout", "cj_scout"), Seat("writer", "Writer", "Writer", "cj_gone"));

        var view = FactoriesScreenFold.Seats(f, Inputs(f, new[] { Job("cj_scout", "scan") }));

        var rows = view.Rows.ToDictionary(r => r.SeatId);
        Assert.All(view.Rows, r => Assert.Equal("Open seat", r.OpenLabel));
        Assert.Equal("/factories/warmforward/seats/boss", rows["boss"].Href);
        Assert.Equal("/factories/warmforward/seats/scout", rows["scout"].Href);
        Assert.Equal("/factories/warmforward/seats/writer", rows["writer"].Href);
        Assert.Equal("Talk only", rows["boss"].WhenText);
        // The grouped rows the Cockpit draws carry the same link, the seat with no schedule included.
        var grouped = view.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Contains(grouped, r => r.SeatId == "boss");
        Assert.All(grouped, r => Assert.Equal($"/factories/warmforward/seats/{r.SeatId}", r.Href));
    }

    [Fact]
    public void SeatHref_EscapesTheSeatId()
    {
        Assert.Equal("/factories/warm%20forward/seats/front%20desk", FactoriesScreenFold.SeatHref("warm forward", "front desk"));
    }
}
