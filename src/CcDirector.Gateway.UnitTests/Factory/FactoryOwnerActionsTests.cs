using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The owner's actions on a factory (Factories screen mission, round 2), proved without a server: the order of what
/// is waiting, Handled on every item, the bulk "mark everything older than 7 days" and its count, Archive and Restore
/// and the schedules each switches, and the list leaving an archived factory out.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoryOwnerActionsTests
{
    // 6 Oct 2026 10:00 UTC; the account is on UTC so every time is plain.
    private static readonly DateTime Now = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private static RegisteredFactorySeatDto Seat(string id, string name, string role, params string[] schedules) =>
        new() { Id = id, Name = name, Role = role, BriefFile = $"agents/{id}.yaml", Schedules = schedules.ToList(), Computer = "SOREN_NORTH" };

    private static RegisteredFactoryDto Factory(string id, string title, string? ceo, params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = id, Title = title, Folder = $@"D:\f\{id}", Computer = "SOREN_NORTH", CeoSeat = ceo, Seats = seats.ToList(),
    };

    private static RegisteredFactoryDto Website() => Factory("website-business", "Website Business", "malik",
        Seat("malik", "Malik Grant", "CEO", "cj_ceo"),
        Seat("sender", "Sender", "Sender", "cj_send", "cj_send2"));

    private static RegisteredFactoryDto Tallyhand() => Factory("tallyhand", "Tallyhand", "max", Seat("max", "Max Ridley", "CEO"));

    private static FactoryActivityDto Row(string agent, string outcome, string what, DateTime at, string factory = "website-business",
        string? link = null, Guid? corrects = null) => new()
    {
        Id = Guid.NewGuid(), Factory = factory, FactoryAgent = agent, Outcome = outcome, What = what, Link = link,
        OccurredUtc = at, RecordedUtc = at, CorrectsId = corrects, Actor = "session:s",
    };

    private static CronJobDto Job(string id, string name, bool enabled = true) => new()
    {
        Id = id, Name = name, Enabled = enabled, ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *", TimeZoneId = "UTC",
    };

    private static FactoriesScreenInputs Inputs(IReadOnlyList<RegisteredFactoryDto> registry,
        IReadOnlyList<FactoryActivityDto>? rows = null, IReadOnlyList<CronJobDto>? jobs = null)
    {
        rows ??= Array.Empty<FactoryActivityDto>();
        var waiting = rows.Where(r => r.Outcome is FactoryActivityOutcome.Asked or FactoryActivityOutcome.Escalated).ToList();
        var activity = new FactoryFoldInputs(rows, false, false, waiting, Array.Empty<FactoryActivityDto>(),
            Array.Empty<FactoryTriggerFacts>(), new HashSet<string>(),
            new FactoryWindow(FactoryAgentsFold.WindowLast7d, Now.AddDays(-7), Now), TimeZoneInfo.Utc, Now);
        return new FactoriesScreenInputs(registry, activity, jobs ?? Array.Empty<CronJobDto>(),
            new Dictionary<string, GoalNumberDto>(), Array.Empty<FactoryActivityDto>());
    }

    private static readonly FactoryActivityDto[] NoCorrections = Array.Empty<FactoryActivityDto>();

    // ---------- waiting on you ----------

    [Fact]
    public void Page_Waiting_IsDecisionsFirst_ThenQuestions_NewestFirstInEach()
    {
        var oldDecision = Row("malik", FactoryActivityOutcome.Escalated, "Old decision", Now.AddDays(-15));
        var newDecision = Row("sender", FactoryActivityOutcome.Escalated, "Gmail asks to sign in again", Now.AddHours(-2));
        var oldQuestion = Row("malik", FactoryActivityOutcome.Asked, "Old question", Now.AddDays(-9));
        var newQuestion = Row("malik", FactoryActivityOutcome.Asked, "New question", Now.AddHours(-1));

        var waiting = FactoriesScreenFold.Page(Website(), Inputs(new[] { Website() }, new[] { oldQuestion, oldDecision, newQuestion, newDecision })).Waiting;

        Assert.Equal(new[] { "Gmail asks to sign in again", "Old decision", "New question", "Old question" }, waiting.Items.Select(i => i.What));
        Assert.Equal("Decisions first, then questions; newest first in each. An item stays here until it is marked handled.", waiting.OrderText);
    }

    [Fact]
    public void Page_WaitingItem_SaysWhichSeatAndWhen_LinksItsEvidence_AndOffersHandled_OnAQuestionToo()
    {
        var asked = Row("sender", FactoryActivityOutcome.Asked, "4 keep pages answered 404", Now.AddHours(-2), link: "https://example.com/run/1");

        var item = Assert.Single(FactoriesScreenFold.Page(Website(), Inputs(new[] { Website() }, new[] { asked })).Waiting.Items);

        Assert.Equal("Sender, today 08:00", item.By);
        Assert.Equal("https://example.com/run/1", item.Link);
        Assert.Equal("Evidence", item.LinkLabel);
        Assert.Equal("Handled", item.HandledLabel);
        Assert.Equal("Marking it handled...", item.HandledBusyLabel);
    }

    [Fact]
    public void Page_WaitingItem_WithNoEvidence_HasNoLink()
    {
        var item = Assert.Single(FactoriesScreenFold.Page(Website(),
            Inputs(new[] { Website() }, new[] { Row("malik", FactoryActivityOutcome.Escalated, "C has 55.3 GB free", Now.AddDays(-13)) })).Waiting.Items);
        Assert.Null(item.Link);
        Assert.Null(item.LinkLabel);
    }

    // ---------- mark everything older than 7 days ----------

    [Fact]
    public void BulkHandled_CountsOnlyItemsOlderThan7Days_AndTheConfirmSaysExactlyHowMany()
    {
        var rows = new[]
        {
            Row("malik", FactoryActivityOutcome.Escalated, "a", Now.AddDays(-15)),
            Row("malik", FactoryActivityOutcome.Asked, "b", Now.AddDays(-8)),
            Row("sender", FactoryActivityOutcome.Escalated, "c", Now.AddDays(-7).AddMinutes(1)),
            Row("sender", FactoryActivityOutcome.Escalated, "d", Now.AddHours(-2)),
        };

        var waiting = FactoriesScreenFold.Page(Website(), Inputs(new[] { Website() }, rows)).Waiting;
        var bulk = waiting.BulkHandled;

        Assert.NotNull(bulk);
        Assert.Equal("handled-older", bulk.Action);
        Assert.Equal("Mark everything older than 7 days as handled", bulk.Label);
        Assert.Equal("Mark 2 items handled?", bulk.ConfirmTitle);
        Assert.Equal(new[]
        {
            "This marks 2 items waiting on you from Website Business as handled: every one from before 29 Sep 10:00, more than 7 days ago.",
            "Each one gets its own handled row in the activity record, and one more row records that you did this, and how many.",
            "Nothing is deleted. Items from the last 7 days stay on the list.",
        }, bulk.ConfirmLines);
        Assert.Equal("Mark 2 handled", bulk.ConfirmLabel);
        Assert.Equal(2, bulk.ExpectedCount);
        Assert.Equal(Now.AddDays(-7), bulk.CutoffUtc);
        Assert.Null(waiting.BulkHandledNote);
    }

    [Fact]
    public void BulkHandled_WhenNothingIsThatOld_IsNotOffered_AndANoteSaysWhy()
    {
        var waiting = FactoriesScreenFold.Page(Website(),
            Inputs(new[] { Website() }, new[] { Row("sender", FactoryActivityOutcome.Asked, "new", Now.AddDays(-1)) })).Waiting;
        Assert.Null(waiting.BulkHandled);
        Assert.Equal("Nothing here is older than 7 days.", waiting.BulkHandledNote);
    }

    [Fact]
    public void BulkHandled_WhenNothingWaits_HasNeitherActionNorNote()
    {
        var waiting = FactoriesScreenFold.Page(Website(), Inputs(new[] { Website() })).Waiting;
        Assert.Null(waiting.BulkHandled);
        Assert.Null(waiting.BulkHandledNote);
        Assert.Null(waiting.OrderText);
    }

    [Fact]
    public void BulkHandledRows_WritesAHandledRowPerItem_ThenOneOwnerRowWithTheCount()
    {
        var a = Row("malik", FactoryActivityOutcome.Escalated, "C has 55.3 GB free", Now.AddDays(-13));
        var b = Row("malik", FactoryActivityOutcome.Asked, "Which domain?", Now.AddDays(-9));
        var fresh = Row("sender", FactoryActivityOutcome.Escalated, "Gmail asks to sign in again", Now.AddHours(-2));
        var cutoff = Now.AddDays(-7);

        var rows = FactoryOwnerActions.BulkHandledRows(Website(), new[] { a, b, fresh }, NoCorrections, false,
            new FactoryOwnerActionRequest { CutoffUtc = cutoff, ExpectedCount = 2 }, "owner (browser)", TimeZoneInfo.Utc, Now);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new Guid?[] { a.Id, b.Id }, rows.Take(2).Select(r => r.CorrectsId));
        Assert.All(rows.Take(2), r => Assert.StartsWith("Marked handled by owner (browser) in a bulk clear of everything older than 7 days: ", r.What));
        var owner = rows[^1];
        Assert.Null(owner.CorrectsId);
        Assert.Equal("owner", owner.FactoryAgent);
        Assert.Equal("website-business", owner.Factory);
        Assert.Equal("done", owner.Outcome);
        Assert.Equal("owner (browser)", owner.Actor);
        Assert.Equal(Now, owner.OccurredUtc);
        Assert.Equal("The owner marked 2 items waiting on you as handled: everything from before 29 Sep 10:00.", owner.What);
    }

    [Fact]
    public void BulkHandledRows_WhenTheCountChangedSinceTheConfirm_IsAConflict_AndWritesNothing()
    {
        var a = Row("malik", FactoryActivityOutcome.Escalated, "a", Now.AddDays(-13));
        var ex = Assert.Throws<FactoryOwnerActionConflictException>(() => FactoryOwnerActions.BulkHandledRows(Website(), new[] { a },
            NoCorrections, false, new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-7), ExpectedCount = 2 }, "owner", TimeZoneInfo.Utc, Now));
        Assert.Equal("The confirm said 2 items, and there are now 1 from before 29 Sep 10:00. Nothing was marked. Open the factory again to see the new count.", ex.Message);
    }

    [Fact]
    public void BulkHandledRows_ACutoffInsideTheLast7Days_IsRefused()
    {
        var a = Row("malik", FactoryActivityOutcome.Escalated, "a", Now.AddDays(-2));
        var ex = Assert.Throws<FactoryViewValidationException>(() => FactoryOwnerActions.BulkHandledRows(Website(), new[] { a },
            NoCorrections, false, new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-1), ExpectedCount = 1 }, "owner", TimeZoneInfo.Utc, Now));
        Assert.Contains("Only items older than 7 days", ex.Message);
    }

    [Fact]
    public void BulkHandledRows_WithoutTheConfirmsCountAndCutoff_IsRefused()
    {
        Assert.Throws<FactoryViewValidationException>(() => FactoryOwnerActions.BulkHandledRows(Website(), Array.Empty<FactoryActivityDto>(),
            NoCorrections, false, null, "owner", TimeZoneInfo.Utc, Now));
        Assert.Throws<FactoryViewValidationException>(() => FactoryOwnerActions.BulkHandledRows(Website(), Array.Empty<FactoryActivityDto>(),
            NoCorrections, false, new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-7) }, "owner", TimeZoneInfo.Utc, Now));
    }

    [Fact]
    public void BulkHandledRows_WhenTheCorrectionsReadWasCut_IsRefused()
    {
        var a = Row("malik", FactoryActivityOutcome.Escalated, "a", Now.AddDays(-13));
        var ex = Assert.Throws<FactoryViewValidationException>(() => FactoryOwnerActions.BulkHandledRows(Website(), new[] { a },
            NoCorrections, true, new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-7), ExpectedCount = 1 }, "owner", TimeZoneInfo.Utc, Now));
        Assert.Contains("Nothing was marked", ex.Message);
    }

    // ---------- archive ----------

    [Fact]
    public void ArchiveAction_NamesTheSchedulesItSwitchesOff_AndTheOnesAlreadyOff()
    {
        var jobs = new[]
        {
            Job("cj_ceo", "Website - Malik Grant - morning run"),
            Job("cj_send", "Website - Sender - send"),
            Job("cj_send2", "Website - Sender - retry", enabled: false),
            Job("cj_other", "Not this factory"),
        };

        var action = FactoryOwnerActions.ArchiveAction(Website(), jobs);

        Assert.Equal("Archive factory", action.Label);
        Assert.Equal("Archive Website Business?", action.ConfirmTitle);
        Assert.Equal(new[]
        {
            "Website Business leaves the Factories list. It stays under Show archived, where you can restore it.",
            "These Gateway schedules are switched off: \"Website - Malik Grant - morning run\" (cj_ceo) and \"Website - Sender - send\" (cj_send).",
            "Already off, and left off: \"Website - Sender - retry\" (cj_send2).",
            "All its history, its memory and its registry entry are kept.",
            "This is recorded in its activity record as your act.",
        }, action.ConfirmLines);
        Assert.Equal("Archive Website Business", action.ConfirmLabel);
        Assert.Equal(new[] { "cj_ceo", "cj_send" }, action.Schedules);
    }

    [Fact]
    public void ArchiveAction_AFactoryWithNoSchedule_SaysNoneIsSwitchedOff()
    {
        var action = FactoryOwnerActions.ArchiveAction(Tallyhand(), Array.Empty<CronJobDto>());
        Assert.Contains("It has no Gateway schedule, so no schedule is switched off.", action.ConfirmLines);
        Assert.Empty(action.Schedules);
    }

    [Fact]
    public void ArchiveAction_AScheduleTheSeatsNameThatIsGone_IsNamedAsMissing()
    {
        var action = FactoryOwnerActions.ArchiveAction(Website(), new[] { Job("cj_ceo", "CEO run") });
        Assert.Contains("Named by its seats but not on the Gateway, so nothing to switch: cj_send, cj_send2.", action.ConfirmLines);
        Assert.Equal(new[] { "cj_ceo" }, action.Schedules);
    }

    [Fact]
    public void RequireSameSchedules_WhenTheConfirmNamedOthers_IsAConflict()
    {
        var plan = FactoryOwnerActions.ArchivePlan(Website(), new[] { Job("cj_ceo", "CEO run"), Job("cj_send", "Send") });

        FactoryOwnerActions.RequireSameSchedules(plan, new FactoryOwnerActionRequest { Schedules = new() { "cj_send", "cj_ceo" } }, "Archive");
        var ex = Assert.Throws<FactoryOwnerActionConflictException>(() =>
            FactoryOwnerActions.RequireSameSchedules(plan, new FactoryOwnerActionRequest { Schedules = new() { "cj_ceo" } }, "Archive"));
        Assert.Equal("The schedules changed after the confirm was shown: it named cj_ceo, and now it would be cj_ceo and cj_send. Nothing was done. Open it again to see what Archive does now.", ex.Message);
        Assert.Throws<FactoryViewValidationException>(() => FactoryOwnerActions.RequireSameSchedules(plan, new FactoryOwnerActionRequest(), "Archive"));
    }

    [Fact]
    public void ArchiveRow_IsTheOwnersAct_NamingWhatWasSwitchedOff()
    {
        var row = FactoryOwnerActions.ArchiveRow(Website(), new[] { Job("cj_ceo", "CEO run") }, "owner (browser)", Now);
        Assert.Equal("owner", row.FactoryAgent);
        Assert.Equal("owner (browser)", row.Actor);
        Assert.Equal("The owner archived Website Business: it left the Factories list; schedules switched off: \"CEO run\" (cj_ceo). History, memory and the registry entry are kept.", row.What);
    }

    // ---------- restore ----------

    [Fact]
    public void RestoreAction_SwitchesBackOnOnlyWhatTheArchiveSwitchedOff()
    {
        var f = Website();
        f.ArchivedAtUtc = Now.AddDays(-1);
        f.ArchivedSchedules = new() { "cj_ceo", "cj_send" };
        var jobs = new[]
        {
            Job("cj_ceo", "CEO run", enabled: false),
            Job("cj_send", "Send", enabled: true),       // someone switched it back on since
            Job("cj_send2", "Retry", enabled: false),    // off before the archive: not the archive's to switch on
        };

        var action = FactoryOwnerActions.RestoreAction(f, jobs);

        Assert.Equal("Restore", action.Label);
        Assert.Equal(new[]
        {
            "Website Business returns to the Factories list.",
            "This schedule the archive switched off is switched back on: \"CEO run\" (cj_ceo).",
            "Already on again: \"Send\" (cj_send).",
            "Schedules the archive did not switch off are not touched.",
            "This is recorded in its activity record as your act.",
        }, action.ConfirmLines);
        Assert.Equal(new[] { "cj_ceo" }, action.Schedules);
    }

    [Fact]
    public void RestoreAction_WhenTheArchiveSwitchedNothingOff_SaysSo()
    {
        var f = Tallyhand();
        f.ArchivedAtUtc = Now;
        Assert.Contains("The archive switched no schedule off, so none is switched on.", FactoryOwnerActions.RestoreAction(f, Array.Empty<CronJobDto>()).ConfirmLines);
    }

    // ---------- the list and the page ----------

    [Fact]
    public void List_LeavesOutAnArchivedFactory_AndListsItUnderShowArchived()
    {
        var tally = Tallyhand();
        tally.ArchivedAtUtc = Now.AddHours(-1);
        var failingButArchived = Factory("old-shop", "Old Shop", null, Seat("x", "X", "Worker"));
        failingButArchived.ArchivedAtUtc = Now.AddDays(-2);
        var rows = new[] { Row("x", FactoryActivityOutcome.Failed, "Broke.", Now.AddHours(-1), factory: "old-shop") };

        var view = FactoriesScreenFold.List(Inputs(new[] { Website(), tally, failingButArchived }, rows, new[] { Job("cj_ceo", "CEO run") }));

        Assert.Equal(new[] { "website-business" }, view.Rows.Select(r => r.Id));
        Assert.Equal("Show archived (2)", view.ShowArchivedLabel);
        Assert.Equal("Hide archived", view.HideArchivedLabel);
        Assert.Equal(new[] { "Old Shop", "Tallyhand" }, view.ArchivedRows.Select(r => r.Title).OrderBy(t => t));
        var archivedTally = view.ArchivedRows.Single(r => r.Id == "tallyhand");
        Assert.Equal("Archived 6 Oct 09:00 by the owner", archivedTally.ArchivedText);
        Assert.Equal("/factories/tallyhand", archivedTally.Href);
        Assert.Equal("Restore Tallyhand?", archivedTally.Restore.ConfirmTitle);
        Assert.Null(view.ArchivedEmptyText);
    }

    [Fact]
    public void List_WithNothingArchived_SaysSo()
    {
        var view = FactoriesScreenFold.List(Inputs(new[] { Website() }));
        Assert.Empty(view.ArchivedRows);
        Assert.Equal("Show archived (0)", view.ShowArchivedLabel);
        Assert.Equal("No factory is archived.", view.ArchivedEmptyText);
    }

    [Fact]
    public void List_AnArchivedFactorysCeo_NoLongerMakesALiveCeoReadAsADuplicate()
    {
        // Tallyhand and mindzie AI Reports both had Max Ridley as CEO; with Tallyhand archived the button names him.
        var tally = Tallyhand();
        tally.ArchivedAtUtc = Now;
        var reports = Factory("mindzie-ai-reports", "mindzie AI Reports", "max", Seat("max", "Max Ridley", "CEO"));

        var view = FactoriesScreenFold.List(Inputs(new[] { tally, reports }));

        Assert.Equal("Talk to Max Ridley", Assert.Single(view.Rows).Talk!.Label);
    }

    [Fact]
    public void Page_OfALiveFactory_OffersArchive_AndNoRestore()
    {
        var page = FactoriesScreenFold.Page(Website(), Inputs(new[] { Website() }));
        Assert.NotNull(page.Archive);
        Assert.Null(page.Restore);
        Assert.Null(page.ArchivedText);
    }

    [Fact]
    public void Page_OfAnArchivedFactory_SaysSo_AndOffersRestore_NotArchive()
    {
        var f = Tallyhand();
        f.ArchivedAtUtc = Now.AddHours(-1);
        var page = FactoriesScreenFold.Page(f, Inputs(new[] { f }));
        Assert.Null(page.Archive);
        Assert.Equal("Archived 6 Oct 09:00 by the owner. It is not on the Factories list.", page.ArchivedText);
        Assert.Equal("Restore Tallyhand", page.Restore!.ConfirmLabel);
    }
}
