using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// Factories screen round 2 (the owner's feedback on the live list, 6 Oct 2026): every status that is not RUNNING
/// says why in one line and links to its items; FAILING clears when the failure is over; PAUSED tells "Nothing
/// scheduled" from schedules switched off; and the factory's head may have any title. The rows are shaped like the
/// live Website Business record: the Sender's four "keep.page (failed)" rows at 12:02, and its "keep.recorded (done)"
/// rows for the same businesses at 12:11.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoriesScreenRound2Tests
{
    // 6 Oct 2026 14:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At1202 = new(2026, 10, 6, 12, 2, 17, DateTimeKind.Utc);
    private static readonly DateTime At1211 = new(2026, 10, 6, 12, 11, 40, DateTimeKind.Utc);

    private static readonly string[] Businesses =
    {
        "All Types Fence & Deck", "Sooner Excavation LLC", "Reynolds Pumping and Septic Services", "Alderson and Sons Tree Service",
    };

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, params string[] schedules) =>
        new() { Id = id, Name = name, Role = role, BriefFile = $"agents/{id}.yaml", Schedules = schedules.ToList(), Computer = "SOREN_NORTH" };

    private static RegisteredFactoryDto WebsiteBusiness() => new()
    {
        Factory = "website-business",
        Title = "Website Business",
        Folder = @"D:\f\website",
        Computer = "SOREN_NORTH",
        CeoSeat = "ceo",
        Seats = new() { Seat("ceo", "Malik Grant", "CEO", "cj_ceo"), Seat("sender", "Sender", "Sender"), Seat("scout", "Scout", "Scout", "cj_scout") },
    };

    private static FactoryActivityDto Row(string agent, string outcome, string what, DateTime at, string? subject = null,
        Guid? corrects = null, string factory = "website-business", string? link = null) => new()
    {
        Id = Guid.NewGuid(), Factory = factory, FactoryAgent = agent, Outcome = outcome, What = what, Subject = subject,
        OccurredUtc = at, RecordedUtc = at, SessionId = "171aef53", CorrectsId = corrects, Actor = "cc-website-factory:" + agent,
        Link = link,
    };

    private static FactoryActivityDto KeepPageFailed(string business) =>
        Row("sender", FactoryActivityOutcome.Failed,
            $"keep.page (failed): https://centerconsulting.com/websites/keep/{Slug(business)} answers 404 after 20 min", At1202, business);

    private static FactoryActivityDto KeepRecorded(string business) =>
        Row("sender", FactoryActivityOutcome.Done, $"keep.recorded (done): https://centerconsulting.com/websites/keep/{Slug(business)}", At1211, business);

    private static string Slug(string business) => business.Split(' ')[0].ToLowerInvariant();

    private static CronJobDto Job(string id, bool enabled = true) => new()
    {
        Id = id, Name = id, Enabled = enabled, ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *", TimeZoneId = "UTC",
    };

    private static CronJobDto[] Running() => new[] { Job("cj_ceo"), Job("cj_scout") };

    private static FactoriesScreenInputs Inputs(IReadOnlyList<RegisteredFactoryDto> registry, IReadOnlyList<FactoryActivityDto>? rows = null,
        IReadOnlyList<CronJobDto>? jobs = null, IReadOnlyList<FactoryTriggerFacts>? triggers = null)
    {
        rows ??= Array.Empty<FactoryActivityDto>();
        var waiting = rows.Where(r => r.Outcome is FactoryActivityOutcome.Asked or FactoryActivityOutcome.Escalated).ToList();
        var activity = new FactoryFoldInputs(rows, false, false, waiting, Array.Empty<FactoryActivityDto>(),
            triggers ?? Array.Empty<FactoryTriggerFacts>(), new HashSet<string>(),
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), TimeZoneInfo.Utc, Now);
        return new FactoriesScreenInputs(registry, activity, jobs ?? Running(), new Dictionary<string, GoalNumberDto>(),
            Array.Empty<FactoryActivityDto>());
    }

    private static FactoryListRowDto ListRow(IReadOnlyList<FactoryActivityDto> rows, IReadOnlyList<CronJobDto>? jobs = null) =>
        FactoriesScreenFold.List(Inputs(new[] { WebsiteBusiness() }, rows, jobs)).Rows.Single();

    // ---------- FAILING clears when the failure is over ----------

    [Fact]
    public void Failing_FourFailuresNotYetOver_SaysWhoHowManyAndTheNewest_AndLinksToTheFailures()
    {
        var rows = Businesses.Select(KeepPageFailed).ToList();

        var row = ListRow(rows);

        Assert.Equal("FAILING", row.StatusWord);
        Assert.StartsWith("Sender: 4 failures, newest today 12:02: keep.page (failed): https://centerconsulting.com/websites/keep/", row.StatusLine);
        Assert.True(row.StatusLine!.Length <= "Sender: 4 failures, newest today 12:02: ".Length + FactoriesScreenFold.LineWhatChars);
        Assert.Contains("answers 404 after 20 min", row.StatusReason);
        Assert.Equal("/factories/website-business#failing", row.StatusHref);
    }

    [Fact]
    public void Failing_ClearsWhenTheSameSeatLaterSucceedsAtTheSameSubject()
    {
        var rows = Businesses.Select(KeepPageFailed).Concat(Businesses.Select(KeepRecorded)).ToList();

        var row = ListRow(rows);

        Assert.Equal("RUNNING", row.StatusWord);
        Assert.Null(row.StatusLine);
        Assert.Null(row.StatusHref);
    }

    [Fact]
    public void Failing_ASuccessForOnlySomeSubjects_LeavesTheOthersCounting()
    {
        var rows = Businesses.Select(KeepPageFailed).Concat(Businesses.Take(3).Select(KeepRecorded)).ToList();

        var row = ListRow(rows);

        Assert.Equal("FAILING", row.StatusWord);
        Assert.StartsWith("Sender failed today 12:02: keep.page (failed): https://centerconsulting.com/websites/keep/alderson", row.StatusLine);
    }

    [Fact]
    public void Failing_ATriggerCheckThatFailed_ClearsOnItsNextNothingToDoCheck()
    {
        var failed = Row("Front Desk", FactoryActivityOutcome.Failed, "Checked website-new-mail - the check failed: the check timed out",
            Now.AddHours(-3), "website-new-mail");
        var next = Row("Front Desk", FactoryActivityOutcome.NothingToDo, "Checked website-new-mail - nothing to do", Now.AddHours(-2), "website-new-mail");

        Assert.Equal("FAILING", ListRow(new[] { failed }).StatusWord);
        Assert.Equal("RUNNING", ListRow(new[] { failed, next }).StatusWord);
    }

    [Theory]
    [InlineData("a success BEFORE the failure", "sender", "All Types Fence & Deck", FactoryActivityOutcome.Done, -1)]
    [InlineData("another seat's success", "scout", "All Types Fence & Deck", FactoryActivityOutcome.Done, 1)]
    [InlineData("a success about another subject", "sender", "Sooner Excavation LLC", FactoryActivityOutcome.Done, 1)]
    [InlineData("a later step it only intends", "sender", "All Types Fence & Deck", FactoryActivityOutcome.Started, 1)]
    [InlineData("a later escalation", "sender", "All Types Fence & Deck", FactoryActivityOutcome.Escalated, 1)]
    [InlineData("a later run-wide success with no subject", "sender", null, FactoryActivityOutcome.Done, 1)]
    public void Failing_DoesNotClearOn(string because, string agent, string? subject, string outcome, int minutesAfter)
    {
        var failed = KeepPageFailed("All Types Fence & Deck");
        var other = Row(agent, outcome, "something", failed.OccurredUtc.AddMinutes(minutesAfter), subject);

        var row = ListRow(new[] { failed, other });

        Assert.True(row.StatusWord == "FAILING", $"{because} must not clear the failure, but the status is {row.StatusWord}");
    }

    [Fact]
    public void Failing_ACorrectionOfAnotherRow_DoesNotCountAsTheSeatSucceeding()
    {
        // "I have handled it" on an old escalation writes a done row with the same seat and subject; it says the
        // escalation is over, not that the failed step later worked.
        var failed = KeepPageFailed("All Types Fence & Deck");
        var escalation = Row("sender", FactoryActivityOutcome.Escalated, "Gmail asks to sign in again", Now.AddDays(-2), "All Types Fence & Deck");
        var handledEscalation = Row("sender", FactoryActivityOutcome.Done, "Marked handled by owner (cockpit): Gmail asks", At1211,
            "All Types Fence & Deck", corrects: escalation.Id);

        Assert.Equal("FAILING", ListRow(new[] { failed, escalation, handledEscalation }).StatusWord);
    }

    [Fact]
    public void Failing_ClearsWhenTheOwnerMarksItHandled_ThroughTheRowHandledAppends()
    {
        var failed = KeepPageFailed("All Types Fence & Deck");
        var request = FactoriesScreenFold.FailureHandledRow("website-business", failed, Array.Empty<FactoryActivityDto>(), false,
            "owner (cockpit)", Now);
        Assert.Equal(failed.Id, request.CorrectsId);
        Assert.Equal(FactoryActivityOutcome.Done, request.Outcome);
        Assert.Equal("owner (cockpit)", request.Actor);
        Assert.StartsWith("Marked handled by owner (cockpit): keep.page (failed)", request.What);

        // What the record holds once it is appended: a NEW row; the failed row is unchanged.
        var handled = Row(request.FactoryAgent!, request.Outcome!, request.What!, request.OccurredUtc!.Value, request.Subject, request.CorrectsId);

        Assert.Equal("FAILING", ListRow(new[] { failed }).StatusWord);
        Assert.Equal("RUNNING", ListRow(new[] { failed, handled }).StatusWord);
        Assert.Equal(FactoryActivityOutcome.Failed, failed.Outcome);
    }

    [Fact]
    public void FailureHandledRow_RefusesARowThatIsNotAFailureOfThisFactory_OrIsAlreadyHandled()
    {
        var failed = KeepPageFailed("All Types Fence & Deck");
        var done = KeepRecorded("All Types Fence & Deck");
        var correction = Row("sender", FactoryActivityOutcome.Done, "Marked handled", Now, corrects: failed.Id);

        Assert.Throws<FactoryViewValidationException>(() =>
            FactoriesScreenFold.FailureHandledRow("website-business", done, Array.Empty<FactoryActivityDto>(), false, "owner", Now));
        Assert.Throws<FactoryViewValidationException>(() =>
            FactoriesScreenFold.FailureHandledRow("machine-care", failed, Array.Empty<FactoryActivityDto>(), false, "owner", Now));
        var again = Assert.Throws<FactoryViewValidationException>(() =>
            FactoriesScreenFold.FailureHandledRow("website-business", failed, new[] { correction }, false, "owner", Now));
        Assert.Equal("This failure is already handled.", again.Message);
        Assert.Throws<FactoryViewValidationException>(() =>
            FactoriesScreenFold.FailureHandledRow("website-business", failed, Array.Empty<FactoryActivityDto>(), true, "owner", Now));
    }

    [Fact]
    public void Page_TheFailuresCard_ListsWhatStillCounts_WithHandled_AndIsAbsentWhenNothingFails()
    {
        var f = WebsiteBusiness();
        var stillFailing = KeepPageFailed("Alderson and Sons Tree Service");
        var rows = new List<FactoryActivityDto> { KeepPageFailed("All Types Fence & Deck"), KeepRecorded("All Types Fence & Deck"), stillFailing };

        var page = FactoriesScreenFold.Page(f, Inputs(new[] { f }, rows));

        Assert.Equal("FAILING", page.StatusWord);
        Assert.Equal("/factories/website-business#failing", page.StatusHref);
        Assert.StartsWith("Sender failed today 12:02: ", page.StatusLine);
        var item = Assert.Single(page.Failures!.Items);
        Assert.Equal(stillFailing.Id, item.Id);
        Assert.Equal(stillFailing.What, item.What);
        Assert.Equal("Sender, today 12:02", item.By);
        Assert.Equal("Alderson and Sons Tree Service", item.Subject);
        Assert.Equal("Handled", item.HandledLabel);
        Assert.Equal("Failing", page.Failures.Heading);
        Assert.Contains("mark it handled", page.Failures.Note);

        var healthy = FactoriesScreenFold.Page(f, Inputs(new[] { f }, rows.Take(2).ToList()));
        Assert.Null(healthy.Failures);
        Assert.Null(healthy.StatusLine);
    }

    [Fact]
    public void Page_AScheduleThatCouldNotStart_IsAFailureItemThatCannotBeMarkedHandled()
    {
        var f = WebsiteBusiness();
        var jobs = new[] { new CronJobDto
        {
            Id = "cj_scout", Name = "scout", Enabled = true, ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *",
            TimeZoneId = "UTC", LastFiredUtc = Now.AddHours(-2), LastStatus = FactoriesScreenFold.ScheduleNotStarted,
        }, Job("cj_ceo") };

        var page = FactoriesScreenFold.Page(f, Inputs(new[] { f }, jobs: jobs));

        Assert.Equal("The schedule for Scout could not start its run today 12:00.", page.StatusLine);
        var item = Assert.Single(page.Failures!.Items);
        Assert.Null(item.Id);
        Assert.Null(item.HandledLabel);
        Assert.Equal("This clears when the schedule next starts its run.", item.Note);
    }

    // ---------- NEEDS YOU says what ----------

    [Fact]
    public void NeedsYou_SaysHowManySinceWhen_AndTheNewest_AndBothTheWordAndTheCountLinkToTheWaitingItems()
    {
        var rows = new[]
        {
            Row("scout", FactoryActivityOutcome.Escalated, "Dropped Septic Pros LLC: two phone numbers.", new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc)),
            Row("sender", FactoryActivityOutcome.Escalated,
                "run.finish (noted): result=needs-you: sender run 46; Gmail asks soren@centerconsulting.com to sign in again; 4 planned for 2026-10-07",
                new DateTime(2026, 10, 6, 12, 12, 59, DateTimeKind.Utc)),
        };

        var row = ListRow(rows);

        Assert.Equal("NEEDS YOU", row.StatusWord);
        Assert.StartsWith("2 decisions since 21 Sep 09:00; newest Sender, today 12:12: run.finish (noted): result=needs-you: sender run 46;", row.StatusLine);
        Assert.EndsWith("...", row.StatusLine);
        Assert.EndsWith("4 planned for 2026-10-07", row.StatusReason);
        Assert.Equal("/factories/website-business#waiting", row.StatusHref);
        Assert.Equal("/factories/website-business#waiting", row.WaitingHref);
    }

    [Fact]
    public void NeedsYou_OneItem_SaysWhoAndWhen()
    {
        var care = new RegisteredFactoryDto
        {
            Factory = "machine-care", Title = "Machine Care", Folder = @"D:\c", Computer = "SOREN_NORTH",
            Seats = new() { Seat("factory-cleaner", "Factory Cleaner", "Factory Cleaner", "cj_clean") },
        };
        var rows = new[]
        {
            Row("factory-cleaner", FactoryActivityOutcome.Escalated, "C has 55.3 GB free, below the 60 GB line.",
                new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc), factory: "machine-care"),
        };

        var row = FactoriesScreenFold.List(Inputs(new[] { care }, rows, new[] { Job("cj_clean") })).Rows.Single();

        Assert.Equal("Factory Cleaner, 23 Sep 06:00: C has 55.3 GB free, below the 60 GB line.", row.StatusLine);
    }

    [Fact]
    public void Running_HasNoLine_AndNothingWaiting_HasNoWaitingLink()
    {
        var row = ListRow(Array.Empty<FactoryActivityDto>());
        Assert.Equal("RUNNING", row.StatusWord);
        Assert.Null(row.StatusLine);
        Assert.Null(row.StatusHref);
        Assert.Equal("-", row.WaitingText);
        Assert.Null(row.WaitingHref);
    }

    // ---------- PAUSED says why ----------

    [Fact]
    public void Paused_NoScheduleAtAll_SaysNothingScheduled_AndLinksToTheSeats()
    {
        var tally = new RegisteredFactoryDto
        {
            Factory = "tallyhand", Title = "Tallyhand", Folder = @"D:\t", Computer = "SOREN_NORTH", CeoSeat = "ceo",
            Seats = new() { Seat("ceo", "Max Ridley", "CEO"), Seat("market-scout", "Market Scout", "Market Scout") },
        };

        var row = FactoriesScreenFold.List(Inputs(new[] { tally }, jobs: Array.Empty<CronJobDto>())).Rows.Single();

        Assert.Equal("PAUSED", row.StatusWord);
        Assert.Equal("Nothing scheduled", row.StatusLine);
        Assert.Equal("/factories/tallyhand/seats", row.StatusHref);
    }

    [Fact]
    public void Paused_SchedulesSwitchedOff_SaysHowMany_AndIsNotNothingScheduled()
    {
        var off = new[] { Job("cj_ceo", enabled: false), Job("cj_scout", enabled: false) };
        Assert.Equal("2 schedules switched off", ListRow(Array.Empty<FactoryActivityDto>(), off).StatusLine);
    }

    [Fact]
    public void Paused_NamesPausedTriggersAndSchedulesThatNoLongerExist()
    {
        var f = WebsiteBusiness();
        var trigger = new FactoryTriggerFacts("t1", "website-new-mail", "website-business", "sender", 300, true, false, null, null, null);
        var off = new[] { Job("cj_ceo", enabled: false) };

        var row = FactoriesScreenFold.List(Inputs(new[] { f }, jobs: off, triggers: new[] { trigger })).Rows.Single();

        Assert.Equal("1 schedule switched off, 1 trigger paused, 1 named schedule no longer exists", row.StatusLine);
    }

    // ---------- the head may have any title ----------

    [Fact]
    public void Head_IsTheCeoSeatWhateverItsTitle_AndThePageSaysItsOwnRole()
    {
        var cc = new RegisteredFactoryDto
        {
            Factory = "cc-factory", Title = "Center Consulting", Folder = @"D:\cc", Computer = "SOREN_NORTH", CeoSeat = "cfo",
            Seats = new() { Seat("cfo", "Ruth Calder", "CFO", "cj_cfo"), Seat("cost-sweep", "Cost Sweep", "Cost Sweep - the CFO's weekday run", "cj_sweep") },
        };
        var jobs = new[] { Job("cj_cfo"), Job("cj_sweep") };

        var row = FactoriesScreenFold.List(Inputs(new[] { cc }, jobs: jobs)).Rows.Single();
        var page = FactoriesScreenFold.Page(cc, Inputs(new[] { cc }, jobs: jobs));

        Assert.Equal("Talk to Ruth Calder", row.Talk!.Label);
        Assert.Equal("cfo", row.Talk.SeatId);
        Assert.Null(row.NoCeoText);
        Assert.Equal("CFO Ruth Calder", page.CeoText);
        Assert.Equal("Latest from the CFO", page.CeoLatest.Heading);
        Assert.Equal("Talk to Ruth Calder", page.Talk!.Label);
    }

    [Fact]
    public void Head_NoneNamed_SaysSo()
    {
        var cc = new RegisteredFactoryDto
        {
            Factory = "cc-factory", Title = "Center Consulting", Folder = @"D:\cc", Computer = "SOREN_NORTH",
            Seats = new() { Seat("cfo", "Ruth Calder", "CFO", "cj_cfo") },
        };
        var page = FactoriesScreenFold.Page(cc, Inputs(new[] { cc }, jobs: new[] { Job("cj_cfo") }));
        Assert.Equal("No head named", page.CeoText);
        Assert.Null(page.Talk);
        Assert.Equal("This factory has no head named.", page.CeoLatest.EmptyText);
    }
}
