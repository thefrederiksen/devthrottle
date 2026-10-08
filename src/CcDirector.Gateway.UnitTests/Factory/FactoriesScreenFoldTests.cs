using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The Factories screen fold (Factories screen mission, phase B), proved without a server: the four status words
/// and their order, what is waiting on the owner, the CEO's Talk button, the factory page's cards, and the Seats tab
/// - which lists registry seats and nothing that merely wrote an activity row.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoriesScreenFoldTests
{
    // 6 Oct 2026 10:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, params string[] schedules) =>
        new() { Id = id, Name = name, Role = role, BriefFile = $"agents/{id}.yaml", Schedules = schedules.ToList(), Computer = "SOREN_NORTH" };

    private static RegisteredFactoryDto Factory(string id, string title, string? ceo, params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = id,
        Title = title,
        Folder = $@"D:\f\{id}",
        Computer = "SOREN_NORTH",
        CeoSeat = ceo,
        Seats = seats.ToList(),
    };

    private static RegisteredFactoryDto WarmForward() => Factory("warmforward", "WarmForward", "nora-hale",
        Seat("nora-hale", "Nora Hale", "CEO", "cj_ceo"),
        Seat("savings-engineer", "Savings Engineer", "Savings Engineer", "cj_save"),
        Seat("reliability-watch", "Reliability Watch", "Reliability Watch", "cj_rel"),
        Seat("value-hunter", "Value Hunter", "Value Hunter", "cj_value"));

    private static FactoryActivityDto Row(string factory, string agent, string outcome, string what, DateTime at,
        string? session = null, Guid? corrects = null) => new()
    {
        Id = Guid.NewGuid(), Factory = factory, FactoryAgent = agent, Outcome = outcome, What = what,
        OccurredUtc = at, RecordedUtc = at, SessionId = session, CorrectsId = corrects, Actor = "session:" + session,
    };

    private static CronJobDto Job(string id, string cron, bool enabled = true, DateTime? lastFired = null, string? lastStatus = null,
        string zone = "UTC") => new()
    {
        Id = id, Name = id, Enabled = enabled, ScheduleKind = CronSchedule.KindRecurring, CronExpression = cron,
        TimeZoneId = zone, LastFiredUtc = lastFired, LastStatus = lastStatus,
    };

    /// <summary>One enabled schedule of the WarmForward CEO, so a factory is not PAUSED for want of one.</summary>
    private static CronJobDto[] Running() => new[] { Job("cj_ceo", "15 6 * * *") };

    private static FactoriesScreenInputs Inputs(IReadOnlyList<RegisteredFactoryDto> registry,
        IReadOnlyList<FactoryActivityDto>? rows = null, IReadOnlyList<CronJobDto>? jobs = null,
        IReadOnlyList<FactoryTriggerFacts>? triggers = null, GoalNumberDto? number = null, IReadOnlyList<FactoryActivityDto>? talks = null,
        TimeZoneInfo? accountZone = null)
    {
        rows ??= Array.Empty<FactoryActivityDto>();
        var waiting = rows.Where(r => r.Outcome is FactoryActivityOutcome.Asked or FactoryActivityOutcome.Escalated).ToList();
        var activity = new FactoryFoldInputs(rows, false, false, waiting, Array.Empty<FactoryActivityDto>(),
            triggers ?? Array.Empty<FactoryTriggerFacts>(), new HashSet<string>(),
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), accountZone ?? TimeZoneInfo.Utc, Now);
        var latest = new Dictionary<string, GoalNumberDto>();
        if (number is not null) latest[number.Factory] = number;
        return new FactoriesScreenInputs(registry, activity, jobs ?? Array.Empty<CronJobDto>(), latest,
            talks ?? Array.Empty<FactoryActivityDto>());
    }

    // ---------- status ----------

    [Fact]
    public void List_StatusIsWorstFirst_ThenByTitle()
    {
        var failing = Factory("mindzie-web", "mindzie Web", null, Seat("builder", "Builder", "Builder", "cj_b"));
        var needsYou = Factory("website", "Website Business", "malik", Seat("malik", "Malik Grant", "CEO"));
        var paused = Factory("tallyhand", "Tallyhand", "max", Seat("max", "Max Ridley", "CEO", "cj_t"));
        var runningB = Factory("devthrottle", "DevThrottle", "ada", Seat("ada", "Ada Brennan", "CEO", "cj_a"));
        var runningA = Factory("clickfunnels", "ClickFunnels", "hazel", Seat("hazel", "Hazel Morgan", "CEO", "cj_h"));
        var rows = new[]
        {
            Row("mindzie-web", "builder", FactoryActivityOutcome.Failed, "Build broke.", Now.AddHours(-2)),
            Row("website", "malik", FactoryActivityOutcome.Asked, "Which domain?", Now.AddHours(-3)),
        };
        var jobs = new[] { Job("cj_b", "0 6 * * *"), Job("cj_t", "0 6 * * *", enabled: false), Job("cj_a", "0 6 * * *"), Job("cj_h", "0 6 * * *") };

        var view = FactoriesScreenFold.List(Inputs(new[] { runningB, paused, runningA, needsYou, failing }, rows, jobs));

        Assert.Equal(new[] { "mindzie Web", "Website Business", "Tallyhand", "ClickFunnels", "DevThrottle" }, view.Rows.Select(r => r.Title));
        Assert.Equal(new[] { "FAILING", "NEEDS YOU", "PAUSED", "RUNNING", "RUNNING" }, view.Rows.Select(r => r.StatusWord));
        Assert.Equal(new[] { FactoryTone.Red, FactoryTone.Amber, FactoryTone.Paused, FactoryTone.Ok, FactoryTone.Ok }, view.Rows.Select(r => r.StatusTone));
        // The Cockpit sorts by the rank when the owner picks "Status (worst first)"; it must match the word.
        Assert.Equal(new[] { 0, 1, 2, 3, 3 }, view.Rows.Select(r => r.StatusRank));
        Assert.Equal(new[] { "Factories", "Activity", "Reports" }, view.Tabs.Select(t => t.Label));
    }

    [Fact]
    public void Status_AFailureOlderThan24Hours_IsNotFailing()
    {
        var f = WarmForward();
        var rows = new[] { Row("warmforward", "nora-hale", FactoryActivityOutcome.Failed, "Broke.", Now.AddHours(-25)) };
        Assert.Equal("RUNNING", FactoriesScreenFold.List(Inputs(new[] { f }, rows, jobs: Running())).Rows[0].StatusWord);
    }

    [Fact]
    public void Status_AFailureByANonSeat_IsFailing()
    {
        // Ruling of 2026-10-06: ANY failed row of the factory, whoever wrote it.
        var rows = new[] { Row("warmforward", "owner-session", FactoryActivityOutcome.Failed, "Broke.", Now.AddHours(-1)) };
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, rows)).Rows[0];
        Assert.Equal("FAILING", row.StatusWord);
        Assert.Contains("Owner Session", row.StatusReason);
    }

    [Fact]
    public void Status_AFailureOfAnotherFactory_IsNotFailing()
    {
        var rows = new[] { Row("tallyhand", "owner-session", FactoryActivityOutcome.Failed, "Broke.", Now.AddHours(-1)) };
        Assert.Equal("RUNNING", FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, rows, jobs: Running())).Rows[0].StatusWord);
    }

    [Fact]
    public void Status_ACorrectedFailure_IsNotFailing()
    {
        var failed = Row("warmforward", "nora-hale", FactoryActivityOutcome.Failed, "Broke.", Now.AddHours(-2));
        var fix = Row("warmforward", "nora-hale", FactoryActivityOutcome.Done, "It was a test row.", Now.AddHours(-1), corrects: failed.Id);
        Assert.Equal("RUNNING", FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, new[] { failed, fix }, jobs: Running())).Rows[0].StatusWord);
    }

    [Theory]
    [InlineData("not-started", "FAILING")]
    [InlineData("worklist-no-list", "FAILING")]
    [InlineData("worklist-no-director", "FAILING")]
    [InlineData("worklist-unknown", "FAILING")]
    [InlineData("started", "RUNNING")]
    [InlineData("worklist-empty", "RUNNING")]
    [InlineData("worklist-machine-busy", "RUNNING")]
    public void Status_ASeatsScheduleWhoseLastFiringFailed_IsFailing_AndSaysWhich(string lastStatus, string expected)
    {
        var jobs = new[] { Job("cj_save", "0 5 * * *", lastFired: Now.AddHours(-5), lastStatus: lastStatus) };
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, jobs: jobs)).Rows[0];
        Assert.Equal(expected, row.StatusWord);
        if (expected == "FAILING") Assert.Contains("Savings Engineer", row.StatusReason);
    }

    [Fact]
    public void Status_FailingOutranksNeedsYou()
    {
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Asked, "Is the bunkie meant to be at 20 C?", Now.AddHours(-3)),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Failed, "Feed broke.", Now.AddHours(-1)),
        };
        Assert.Equal("FAILING", FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, rows)).Rows[0].StatusWord);
    }

    [Fact]
    public void Status_PausedNeedsEveryScheduleAndTriggerOff()
    {
        var f = WarmForward();
        var allOff = new[] { Job("cj_ceo", "15 6 * * *", false), Job("cj_save", "0 5 * * *", false), Job("cj_rel", "0 6,18 * * *", false), Job("cj_value", "30 5 * * 3", false) };
        Assert.Equal("PAUSED", FactoriesScreenFold.List(Inputs(new[] { f }, jobs: allOff)).Rows[0].StatusWord);

        var oneOn = allOff.Select((j, i) => i == 0 ? Job(j.Id, j.CronExpression!, true) : j).ToArray();
        Assert.Equal("RUNNING", FactoriesScreenFold.List(Inputs(new[] { f }, jobs: oneOn)).Rows[0].StatusWord);

        var liveTrigger = new[] { new FactoryTriggerFacts("t1", "Mail", "warmforward", "nora-hale", 300, false, false, null, null, null) };
        Assert.Equal("RUNNING", FactoriesScreenFold.List(Inputs(new[] { f }, jobs: allOff, triggers: liveTrigger)).Rows[0].StatusWord);
    }

    [Fact]
    public void Status_NoScheduleAtAll_IsPaused_AndSaysSo()
    {
        // Ruling of 2026-10-06: a factory whose seats have no enabled schedule at all (Tallyhand) is PAUSED. Here every
        // seat names a schedule the Gateway does not have, and round 2 says so rather than "Nothing scheduled".
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() })).Rows[0];
        Assert.Equal("PAUSED", row.StatusWord);
        Assert.Equal("4 named schedules no longer exist", row.StatusLine);

        var unscheduled = Factory("tallyhand", "Tallyhand", "max", Seat("max", "Max Ridley", "CEO"));
        var bare = FactoriesScreenFold.List(Inputs(new[] { unscheduled })).Rows[0];
        Assert.Equal("PAUSED", bare.StatusWord);
        Assert.Equal("Nothing scheduled", bare.StatusReason);
        Assert.Equal("Nothing scheduled", bare.StatusLine);
    }

    // ---------- waiting on you ----------

    [Theory]
    [InlineData(0, 0, "-", 0)]
    [InlineData(1, 0, "1 question", 1)]
    [InlineData(0, 2, "2 decisions", 2)]
    [InlineData(1, 1, "1 question, 1 decision", 2)]
    public void WaitingText_CountsQuestionsAndDecisions(int asked, int escalated, string expected, int expectedCount)
    {
        var rows = Enumerable.Range(0, asked).Select(_ => Row("warmforward", "nora-hale", FactoryActivityOutcome.Asked, "q", Now.AddHours(-1)))
            .Concat(Enumerable.Range(0, escalated).Select(_ => Row("warmforward", "nora-hale", FactoryActivityOutcome.Escalated, "d", Now.AddHours(-1))))
            .ToList();
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, rows)).Rows[0];
        Assert.Equal(expected, row.WaitingText);
        // The number the Cockpit sorts "Waiting on you" by is the number the text describes.
        Assert.Equal(expectedCount, row.WaitingCount);
    }

    [Fact]
    public void Waiting_AQuestionFromANonSeat_StillCountsForTheFactory()
    {
        var rows = new[] { Row("warmforward", "owner-session", FactoryActivityOutcome.Asked, "q", Now.AddHours(-1)) };
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, rows)).Rows[0];
        Assert.Equal("1 question", row.WaitingText);
        Assert.Equal("NEEDS YOU", row.StatusWord);
    }

    // ---------- the CEO's button ----------

    [Fact]
    public void Talk_NamesTheCeo_TheCeoWhenTwoShareAName_AndNoCeoWhenThereIsNone()
    {
        var warm = WarmForward();
        var tally = Factory("tallyhand", "Tallyhand", "max", Seat("max", "Max Ridley", "CEO"));
        var reports = Factory("mindzie-ai-reports", "mindzie AI Reports", "max-r", Seat("max-r", "Max Ridley", "CEO"));
        var care = Factory("machine-care", "Machine Care", null, Seat("janitor", "Janitor", "Janitor"));

        var rows = FactoriesScreenFold.List(Inputs(new[] { warm, tally, reports, care })).Rows.ToDictionary(r => r.Id);

        Assert.Equal("Talk to Nora Hale", rows["warmforward"].Talk!.Label);
        Assert.Equal("Starting the talk with Nora Hale...", rows["warmforward"].Talk!.BusyLabel);
        Assert.Equal(("warmforward", "nora-hale"), (rows["warmforward"].Talk!.FactoryId, rows["warmforward"].Talk!.SeatId));
        Assert.Null(rows["warmforward"].NoCeoText);
        Assert.Equal("Talk to the CEO", rows["tallyhand"].Talk!.Label);
        Assert.Equal("Starting the talk with the CEO...", rows["tallyhand"].Talk!.BusyLabel);
        Assert.Equal("Talk to the CEO", rows["mindzie-ai-reports"].Talk!.Label);
        Assert.Null(rows["machine-care"].Talk);
        Assert.Equal("No head named", rows["machine-care"].NoCeoText);
        Assert.Equal("/factories/warmforward", rows["warmforward"].Href);
    }

    [Fact]
    public void List_NothingRegistered_SaysSo()
    {
        var view = FactoriesScreenFold.List(Inputs(Array.Empty<RegisteredFactoryDto>()));
        Assert.Empty(view.Rows);
        Assert.Contains("factory register", view.EmptyText);
    }

    // ---------- schedules outside any factory (issue #3650, point 5) ----------

    private static CronJobDto Linked(string id, string name, string? factory, string? seat, bool enabled = true)
    {
        var job = Job(id, "0 7 * * *", enabled);
        job.Name = name;
        job.Factory = factory;
        job.Seat = seat;
        job.Target = new CronJobTarget { Machine = "SOREN_NORTH" };
        return job;
    }

    [Fact]
    public void List_ShowsEveryEnabledScheduleThatIsNoSeat_WithWhy_ByName()
    {
        var jobs = new[]
        {
            Linked("cj_ceo", "WarmForward - Nora Hale", "warmforward", "nora-hale"),
            Linked("cj_mail", "Mail Desk - morning", factory: null, seat: null),
            Linked("cj_job", "Job search", factory: null, seat: null),
            Linked("cj_old", "Old reminder", factory: null, seat: null, enabled: false),
            Linked("cj_money", "Money Saver - daily", "money-saver", "daily"),
            Linked("cj_gone", "WarmForward - fired seat", "warmforward", "fired"),
        };

        var view = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, jobs: jobs));

        Assert.Equal("Schedules outside any factory", view.OutsideTitle);
        Assert.Contains("cc-devthrottle schedule link", view.OutsideText);
        Assert.Null(view.OutsideEmptyText);
        Assert.Equal(
            new[]
            {
                ("cj_job", "In no factory"),
                ("cj_mail", "In no factory"),
                ("cj_money", "Names the factory 'money-saver', which is not registered"),
                ("cj_gone", "Names the seat 'fired', which WarmForward does not have"),
            },
            view.OutsideRows.Select(r => (r.Id, r.Reason)));
        var mail = view.OutsideRows.Single(r => r.Id == "cj_mail");
        Assert.Equal(("Mail Desk - morning", "SOREN_NORTH"), (mail.Name, mail.Machine));
        Assert.False(string.IsNullOrWhiteSpace(mail.WhenText));
    }

    [Fact]
    public void List_AnEnabledScheduleOfAnArchivedFactory_IsOutside()
    {
        var archived = WarmForward();
        archived.ArchivedAtUtc = Now.AddDays(-1);

        var view = FactoriesScreenFold.List(Inputs(new[] { archived },
            jobs: new[] { Linked("cj_ceo", "CEO", "warmforward", "nora-hale") }));

        Assert.Equal("Runs for WarmForward, which is archived", Assert.Single(view.OutsideRows).Reason);
    }

    [Fact]
    public void List_WhenEveryEnabledScheduleIsASeat_SaysSo()
    {
        var view = FactoriesScreenFold.List(Inputs(new[] { WarmForward() },
            jobs: new[] { Linked("cj_ceo", "CEO", "warmforward", "nora-hale"), Linked("cj_off", "Off", null, null, enabled: false) }));

        Assert.Empty(view.OutsideRows);
        Assert.Equal("Every enabled schedule is a seat of a factory.", view.OutsideEmptyText);
    }

    // ---------- the factory page ----------

    [Fact]
    public void Page_Header_Tabs_AndTheComputerChangeIsALabelMarkedComing()
    {
        var page = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }));
        Assert.Equal("Factories / WarmForward", page.Crumb);
        Assert.Equal("CEO Nora Hale", page.CeoText);
        Assert.Equal("4 seats", page.SeatCountText);
        Assert.Equal("runs on SOREN_NORTH", page.ComputerText);
        Assert.Equal("Talk to Nora Hale", page.Talk!.Label);
        Assert.Equal(new[] { "Overview", "Seats (4)", "Activity", "Reports", "Memory", "Documents" }, page.Tabs.Select(t => t.Label));
        Assert.Contains("not on the", page.DocumentsText);
    }

    [Fact]
    public void Page_Goal_WithAndWithout()
    {
        var withGoal = WarmForward();
        withGoal.GoalText = "A cash engine of $15,000-$40,000 a year.\n";
        withGoal.GoalApprovedOn = "2026-10-04";
        var goal = FactoriesScreenFold.Page(withGoal, Inputs(new[] { withGoal })).Goal;
        Assert.Equal("A cash engine of $15,000-$40,000 a year.", goal.Text);
        Assert.Equal("Only you change the goal. Approved 4 Oct 2026.", goal.Note);
        Assert.Null(goal.EmptyText);

        var none = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() })).Goal;
        Assert.Null(none.Text);
        Assert.Null(none.Note);
        Assert.Equal("No goal set yet", none.EmptyText);
    }

    [Fact]
    public void Page_GoalNumber_ShowsWhoPostedItWhen_AndTheLink_OrSaysNone()
    {
        var number = new GoalNumberDto
        {
            Id = Guid.NewGuid(), Factory = "warmforward", Value = "not yet proven", Unit = "propane saved this season",
            AsOf = "2026-10-06", Link = "https://example.com/how", PostedBy = "nora-hale", PostedAtUtc = Now.AddHours(-3).AddMinutes(-40),
        };
        var card = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, number: number)).GoalNumber;
        Assert.Equal("Goal number - posted by Nora Hale, today 06:20", card.Heading);
        Assert.Equal("Propane saved this season: not yet proven", card.ValueText);
        Assert.Equal("As of 6 Oct 2026", card.AsOfText);
        Assert.Equal("https://example.com/how", card.LinkHref);
        Assert.Equal("How it is measured", card.LinkLabel);

        var none = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() })).GoalNumber;
        Assert.Equal("No number posted yet", none.EmptyText);
        Assert.Null(none.ValueText);
    }

    [Fact]
    public void Page_CeoLatest_IsTheCeosNewestLines_NotOtherSeatsOrStarts()
    {
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Started, "Run started.", Now.AddHours(-4)),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Done, "Feed healthy.", Now.AddHours(-3).AddMinutes(-40)),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Done, "First run. Booked the baseline.", Now.AddDays(-1).AddHours(-3).AddMinutes(-42)),
            Row("warmforward", "savings-engineer", FactoryActivityOutcome.Done, "Not the CEO.", Now.AddHours(-1)),
        };
        var latest = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, rows)).CeoLatest;
        Assert.Equal(new[] { "Today 06:20 - Feed healthy.", "Yesterday 06:18 - First run. Booked the baseline." }, latest.Lines);
        Assert.Equal("/factories?tab=activity&factory=warmforward&agent=nora-hale", latest.AllHref);
    }

    [Fact]
    public void Page_Waiting_IsThisFactorysOpenItems()
    {
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Asked, "Is the bunkie meant to be at 20 C?", Now.AddHours(-3)),
            Row("other", "x", FactoryActivityOutcome.Asked, "Not this factory.", Now.AddHours(-3)),
        };
        var waiting = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, rows)).Waiting;
        Assert.Equal("Is the bunkie meant to be at 20 C?", Assert.Single(waiting.Items).What);
        Assert.Null(waiting.EmptyText);
    }

    [Fact]
    public void Page_LastTalk_IsTheNewestTalkedRow_OrNoneYet()
    {
        var talks = new[] { Row("warmforward", "nora-hale", FactoryActivityOutcome.Talked, "decided: bunkie back to 13 C.", Now.AddHours(-1).AddMinutes(-50)) };
        Assert.Equal("Talked with you, today 08:10 - decided: bunkie back to 13 C.",
            FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, talks: talks)).LastTalk.Text);
        Assert.Equal("None yet.", FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() })).LastTalk.Text);
    }

    // ---------- the Seats tab ----------

    [Fact]
    public void Seats_ListsRegistrySeatsCeoFirst_NeverActivityOnlyNames()
    {
        var rows = new[]
        {
            Row("warmforward", "owner-session", FactoryActivityOutcome.Done, "x", Now.AddHours(-1)),
            Row("warmforward", "certifier-19", FactoryActivityOutcome.Done, "x", Now.AddHours(-1)),
        };
        var f = Factory("warmforward", "WarmForward", "nora-hale",
            Seat("savings-engineer", "Savings Engineer", "Savings Engineer"), Seat("nora-hale", "Nora Hale", "CEO"));
        var seats = FactoriesScreenFold.Seats(f, Inputs(new[] { f }, rows));
        Assert.Equal(new[] { "nora-hale", "savings-engineer" }, seats.Rows.Select(r => r.SeatId));
        Assert.Equal(new[] { "Seat", "When it runs", "Last run", "Computer" }, seats.Columns);
        Assert.Equal(("Talk", "warmforward", "nora-hale"), (seats.Rows[0].Talk.Label, seats.Rows[0].Talk.FactoryId, seats.Rows[0].Talk.SeatId));
        Assert.Equal("Starting the talk with Nora Hale...", seats.Rows[0].Talk.BusyLabel);
    }

    [Fact]
    public void Seats_WhenItRuns_AndLastRun_InPlainWords()
    {
        var jobs = new[]
        {
            Job("cj_ceo", "15 6 * * *", lastFired: Now.AddHours(-3).AddMinutes(-45), lastStatus: "started"),
            Job("cj_save", "0 5 * * *"),
            Job("cj_rel", "0 6,18 * * *"),
            Job("cj_value", "30 5 * * 3"),
        };
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Started, "Run started.", Now.AddHours(-3).AddMinutes(-44), session: "s1"),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Done, "Feed healthy.", Now.AddHours(-3).AddMinutes(-40), session: "s1"),
            Row("warmforward", "savings-engineer", FactoryActivityOutcome.Started, "Run started.", Now.AddHours(-5), session: "s2"),
            Row("warmforward", "savings-engineer", FactoryActivityOutcome.Failed, "Meter read failed.", Now.AddHours(-4).AddMinutes(-56), session: "s2"),
        };
        var seats = FactoriesScreenFold.Seats(WarmForward(), Inputs(new[] { WarmForward() }, rows, jobs)).Rows.ToDictionary(r => r.SeatId);

        Assert.Equal("Daily 06:15", seats["nora-hale"].WhenText);
        Assert.Equal("Today 06:16 - succeeded", seats["nora-hale"].LastRunText);
        Assert.Equal(FactoryTone.Ok, seats["nora-hale"].LastRunTone);
        Assert.Equal("Daily 05:00", seats["savings-engineer"].WhenText);
        Assert.Equal("Today 05:00 - failed", seats["savings-engineer"].LastRunText);
        Assert.Equal("06:00 and 18:00", seats["reliability-watch"].WhenText);
        Assert.Equal("Not run yet", seats["reliability-watch"].LastRunText);
        Assert.Equal("Wednesday 05:30", seats["value-hunter"].WhenText);
    }

    // ---------- one clock per seat (live QA, 6 Oct 2026) ----------

    private const string Toronto = "America/Toronto";

    [Fact]
    public void Seats_AScheduleInAnotherZone_TellsTheRunInThatZone_AndNamesIt()
    {
        // 06:00 Toronto (EDT, UTC-4) on 6 Oct is 10:00 UTC. The CEO runs at 05:00 Toronto, which is 09:00 UTC.
        // Live, this row read "Daily 06:15 (America/Toronto)" beside "Today 10:16 - started": two clocks on one row.
        var jobs = new[] { Job("cj_ceo", "0 5 * * *", lastFired: Now.AddHours(-1), lastStatus: "started", zone: Toronto) };
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Started, "Run started.", Now.AddHours(-1).AddMinutes(1), session: "s1"),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Done, "Feed healthy.", Now.AddHours(-1).AddMinutes(9), session: "s1"),
        };
        var row = FactoriesScreenFold.Seats(WarmForward(), Inputs(new[] { WarmForward() }, rows, jobs)).Rows.Single(r => r.SeatId == "nora-hale");

        Assert.Equal("Daily 05:00 (America/Toronto)", row.WhenText);
        Assert.Equal("Today 05:01 (America/Toronto) - succeeded", row.LastRunText);
    }

    [Fact]
    public void Seats_TheDayIsTheSchedulesDay_NotUtcs()
    {
        // 23:30 Toronto on 5 Oct is 03:30 UTC on 6 Oct: in the account's UTC it is "today", in the seat's clock it
        // is yesterday - and the schedule beside it says 23:30, so yesterday is the only reading that agrees.
        var fired = new DateTime(2026, 10, 6, 3, 30, 0, DateTimeKind.Utc);
        var jobs = new[] { Job("cj_ceo", "30 23 * * *", lastFired: fired, lastStatus: "started", zone: Toronto) };
        var row = FactoriesScreenFold.Seats(WarmForward(), Inputs(new[] { WarmForward() }, jobs: jobs)).Rows.Single(r => r.SeatId == "nora-hale");

        Assert.Equal("Daily 23:30 (America/Toronto)", row.WhenText);
        Assert.Equal("Yesterday 23:30 (America/Toronto) - started", row.LastRunText);
    }

    [Fact]
    public void Seats_AScheduleInTheAccountsZone_NamesNoZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Toronto);
        var jobs = new[] { Job("cj_ceo", "0 5 * * *", lastFired: Now.AddHours(-1), lastStatus: "started", zone: Toronto) };
        var row = FactoriesScreenFold.Seats(WarmForward(), Inputs(new[] { WarmForward() }, jobs: jobs, accountZone: zone))
            .Rows.Single(r => r.SeatId == "nora-hale");

        Assert.Equal("Daily 05:00", row.WhenText);
        Assert.Equal("Today 05:00 - started", row.LastRunText);
    }

    [Fact]
    public void Seats_ASeatWhoseSchedulesDisagreeOnAZone_IsToldInTheAccountsZone()
    {
        var f = Factory("x", "X", null, Seat("a", "A", "A", "cj_1", "cj_2"));
        var jobs = new[]
        {
            Job("cj_1", "0 5 * * *", lastFired: Now.AddHours(-1), lastStatus: "started", zone: Toronto),
            Job("cj_2", "0 18 * * *", zone: "Europe/Copenhagen"),
        };
        var row = FactoriesScreenFold.Seats(f, Inputs(new[] { f }, jobs: jobs)).Rows.Single();

        Assert.Equal("Daily 05:00 (America/Toronto); Daily 18:00 (Europe/Copenhagen)", row.WhenText);
        Assert.Equal("Today 09:00 - started", row.LastRunText);
    }

    [Fact]
    public void Page_ASeatsRunIsToldInItsScheduleZone_InTheStatusAndTheCeoCard()
    {
        var jobs = new[] { Job("cj_ceo", "0 5 * * *", lastFired: Now.AddHours(-1), lastStatus: "started", zone: Toronto) };
        var rows = new[]
        {
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Started, "Run started.", Now.AddHours(-1).AddMinutes(1), session: "s1"),
            Row("warmforward", "nora-hale", FactoryActivityOutcome.Failed, "Meter read failed.", Now.AddHours(-1).AddMinutes(9), session: "s1"),
        };
        var page = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, rows, jobs));

        Assert.Equal("Nora Hale failed today 05:09 (America/Toronto): Meter read failed.", page.StatusReason);
        Assert.Equal("Latest from the CEO (America/Toronto time)", page.CeoLatest.Heading);
        Assert.Equal(new[] { "Today 05:09 - Meter read failed." }, page.CeoLatest.Lines);
    }

    [Fact]
    public void Page_LastTalk_IsToldInTheTalkingSeatsClock_LikeItsLinesAbove()
    {
        // Live, 7 Oct 2026: the CEO card said "Today 17:18" (Toronto) and the last talk said "today 21:18" (UTC).
        var jobs = new[] { Job("cj_ceo", "0 5 * * *", zone: Toronto) };
        var talks = new[] { Row("warmforward", "nora-hale", FactoryActivityOutcome.Talked, "decided: bunkie back to 13 C.", Now.AddHours(-1).AddMinutes(-50)) };
        var page = FactoriesScreenFold.Page(WarmForward(), Inputs(new[] { WarmForward() }, jobs: jobs, talks: talks));
        Assert.Equal("Talked with you, today 04:10 (America/Toronto) - decided: bunkie back to 13 C.", page.LastTalk.Text);
    }

    [Fact]
    public void Status_AScheduleThatCouldNotStart_IsToldInItsOwnZone()
    {
        var jobs = new[] { Job("cj_save", "0 5 * * *", lastFired: Now.AddHours(-1), lastStatus: "not-started", zone: Toronto) };
        var row = FactoriesScreenFold.List(Inputs(new[] { WarmForward() }, jobs: jobs)).Rows[0];
        Assert.Equal("The schedule for Savings Engineer could not start its run today 05:00 (America/Toronto).", row.StatusReason);
    }

    [Fact]
    public void Seats_AScheduleThatCouldNotStart_SaysDidNotStart()
    {
        var jobs = new[] { Job("cj_save", "0 5 * * *", lastFired: Now.AddHours(-5), lastStatus: "not-started") };
        var row = FactoriesScreenFold.Seats(WarmForward(), Inputs(new[] { WarmForward() }, jobs: jobs)).Rows.Single(r => r.SeatId == "savings-engineer");
        Assert.Equal("Today 05:00 - did not start", row.LastRunText);
        Assert.Equal(FactoryTone.Red, row.LastRunTone);
    }

    [Fact]
    public void Seats_AMissingScheduleIsNamed_AndASeatWithNoneSaysNotScheduled()
    {
        var f = Factory("x", "X", null, Seat("a", "A", "A", "cj_gone"), Seat("b", "B", "B"));
        var rows = FactoriesScreenFold.Seats(f, Inputs(new[] { f })).Rows;
        Assert.Equal("Schedule cj_gone is missing", rows[0].WhenText);
        Assert.Equal("Not scheduled", rows[1].WhenText);
    }

    // ---------- schedule words ----------

    [Theory]
    [InlineData("15 6 * * *", "Daily 06:15")]
    [InlineData("0 6,18 * * *", "06:00 and 18:00")]
    [InlineData("0 6,12,18 * * *", "06:00, 12:00 and 18:00")]
    [InlineData("30 5 * * 3", "Wednesday 05:30")]
    [InlineData("30 5 * * WED", "Wednesday 05:30")]
    [InlineData("0 7 * * 1-5", "Weekdays 07:00")]
    [InlineData("0 7 * * 1,4", "Monday and Thursday 07:00")]
    [InlineData("0 9 * * 0,6", "Weekends 09:00")]
    [InlineData("0 9 * * 0", "Sunday 09:00")]
    [InlineData("*/15 * * * *", "Every 15 minutes")]
    [InlineData("5 * * * *", "Hourly at :05")]
    [InlineData("0 */2 * * *", "Every 2 hours at :00")]
    [InlineData("0 6 1 * *", "Cron 0 6 1 * *")]
    [InlineData("0 6-9 * * *", "Cron 0 6-9 * * *")]
    [InlineData("", "No schedule expression")]
    public void ScheduleText_NamesTheCommonShapes_AndShowsAnyOtherExactly(string cron, string expected)
        => Assert.Equal(expected, FactoryScheduleText.Cron(cron));

    [Fact]
    public void ScheduleText_PausedAndOtherZone_AreSaid()
    {
        var job = new CronJobDto { Id = "j", ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *", TimeZoneId = "Europe/Copenhagen", Enabled = false };
        Assert.Equal("Daily 06:00 (Europe/Copenhagen) (paused)", FactoryScheduleText.Describe(job, TimeZoneInfo.Utc));
    }
}
