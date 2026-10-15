using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// WHAT A PUBLISH WILL DO TO THE LIVE FACTORY (Factory Definition, phase 4; design sections 3 and 4), on the pure
/// function alone: a definition and the live rows in, the plan or the refusals out. No database, no clock.
///
/// The fixture's publishable definition has three seats: boss (a start at 08:00 on weekdays), outreach (a start at
/// 09:00 on weekdays, its own machine and line) and writer (no starts). The factory is registered in
/// <c>D:\ReposFred\devthrottle</c>.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryPublishPlanTests
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string Folder = @"D:\ReposFred\devthrottle";

    private static FactoryDefinition Definition(IReadOnlyDictionary<string, string>? content = null)
    {
        var read = FactoryDefinitionCheck.Read(content ?? DefinitionFixture.Publishable());
        Assert.True(read.Definition is not null, string.Join("\n", read.Findings.Select(f => $"{f.File}:{f.Line} {f.Message}")));
        return read.Definition!;
    }

    private static FactoryRegistryEntity Registry()
    {
        var row = FactoryRegistryStore.Validate(DefinitionFixture.Registration());
        row.TenantId = "acct-a";
        row.RegisteredBy = "session x";
        return row;
    }

    private static CronJobEntity Live(string id, string? seat, string cron = "0 8 * * 1-5", string prompt = "Morning round.",
        string factory = TheFactory, bool enabled = true, string zone = "Europe/Copenhagen", string? name = null) => new()
    {
        Id = id,
        TenantId = "acct-a",
        Name = name ?? $"old name {id}",
        Enabled = enabled,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = cron,
        TimeZoneId = zone,
        Factory = factory,
        Seat = seat,
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = Folder, Seed = prompt },
        PreventOverlap = true,
        NotifyOn = CronNotify.None,
    };

    private static TriggerEntity Trigger(string name, string factory = TheFactory) => new()
    {

        TenantId = "acct-a",
        Name = name,
        Factory = factory,
        Machine = "SOREN_NORTH",
        CheckCommand = "python check.py",
        IntervalSeconds = 600,
        Prompt = "Go.",
    };

    private static PublishPlan Plan(FactoryDefinition? definition = null, FactoryRegistryEntity? registry = null,
        IReadOnlyList<CronJobEntity>? schedules = null, IReadOnlyList<TriggerEntity>? triggers = null)
    {
        var composed = FactoryPublishPlan.Compose(definition ?? Definition(), registry ?? Registry(),
            schedules ?? Array.Empty<CronJobEntity>(), triggers ?? Array.Empty<TriggerEntity>());
        Assert.True(composed.Refusal is null, composed.Refusal?.Sentence + " " +
            string.Join(" | ", composed.Refusal?.Findings.Select(f => $"{f.File}: {f.Message}") ?? Array.Empty<string>()));
        return composed.Plan!;
    }

    private static FactoryPublishRefusal Refusal(FactoryDefinition? definition = null, FactoryRegistryEntity? registry = null,
        bool noRegistry = false, IReadOnlyList<CronJobEntity>? schedules = null)
    {
        var composed = FactoryPublishPlan.Compose(definition ?? Definition(), noRegistry ? null : registry ?? Registry(),
            schedules ?? Array.Empty<CronJobEntity>(), Array.Empty<TriggerEntity>());
        Assert.Null(composed.Plan);
        return composed.Refusal!;
    }

    private static FactoryDefinition WithSeat(FactoryDefinition d, string seat, Func<SeatDefinition, SeatDefinition> change)
        => d with { Seats = d.Seats.Select(s => s.Seat == seat ? change(s) : s).ToList() };

    private static SeatStartDefinition Start(string cron, string prompt, string zone = "Europe/Copenhagen", bool overlap = true)
        => new(cron, zone, null, null, null, Array.Empty<string>(), prompt, overlap);

    // ---------- the registry row ----------

    [Fact]
    public void Compose_TheRegistryRow_ComesFromFactoryYamlAndTheSeatFiles_WithTheFolderKept()
    {
        var plan = Plan();

        var row = plan.Registry;
        Assert.Equal(TheFactory, row.Factory);
        Assert.Equal("DevThrottle", row.Title);
        Assert.Equal("boss", row.BossSeat);
        Assert.Equal("Ship the product.\n", row.GoalText);
        Assert.Equal("2026-10-10", row.GoalApprovedOn);
        Assert.Equal("Builds DevThrottle", row.Purpose);
        Assert.Equal("SOREN_NORTH", row.Computer);
        Assert.Equal(Folder, row.Folder);
        Assert.Null(row.GoalFile);
        var seats = FactoryRegistryStore.SeatsOf(row);
        Assert.Equal(new[] { "boss", "outreach", "writer" }, seats.Select(s => s.Id));
        var outreach = seats[1];
        Assert.Equal("Outreach", outreach.Name);
        Assert.Equal("Sales", outreach.Role);
        Assert.Equal("Find and sell", outreach.Line);
        Assert.Equal("SOREN_SOUTH", outreach.Computer);
        Assert.Equal("seats/outreach.yaml", outreach.BriefFile);
        Assert.Equal("SOREN_NORTH", seats[2].Computer);
        Assert.Equal("seats/writer.yaml", seats[2].BriefFile);
    }

    [Fact]
    public void Compose_APurposeAbsentFromFactoryYaml_IsNull()
    {
        var plan = Plan(Definition(DefinitionFixture.PublishableEdit("factory.yaml", "purpose: Builds DevThrottle\n", "")));

        Assert.Null(plan.Registry.Purpose);
        Assert.Contains("Registry: purpose set to \"Builds DevThrottle\".", Plan(registry: Registry()).RegistrySentences);
    }

    [Fact]
    public void Compose_ARenamedSeat_IsSaidOfTheRegistry_AndEveryScheduleOfTheSeatTakesTheNewName()
    {
        var writerRenamed = WithSeat(Definition(), "writer", s => s with
        {
            Name = "Author",
            Starts = new[] { Start("0 7 * * *", "Write."), Start("0 19 * * *", "Write more.") },
        });
        var live = new[] { Live("cj_w1", "writer", "0 7 * * *", "Write.", name: "DevThrottle - Writer"),
                           Live("cj_w2", "writer", "0 19 * * *", "Write more.", name: "DevThrottle - Writer (2)") };

        var plan = Plan(writerRenamed, schedules: live);

        Assert.Contains("Registry: seat writer renamed Writer -> Author.", plan.RegistrySentences);
        var writer = plan.Schedules.Where(s => s.Seat == "writer").ToList();
        Assert.Equal(new[] { "cj_w1", "cj_w2" }, writer.Select(s => s.Id));
        Assert.All(writer, s => Assert.Equal(PlannedScheduleAction.Update, s.Action));
        Assert.Equal(new[] { "DevThrottle - Author", "DevThrottle - Author (2)" }, writer.Select(s => s.Job!.Name));
        Assert.Equal("Schedule cj_w1 (DevThrottle - Author) changed: name \"DevThrottle - Writer\" -> \"DevThrottle - Author\".",
            writer[0].Sentence());
    }

    // ---------- the schedules: what a start makes ----------

    [Fact]
    public void Compose_AStartWithNoLiveSchedule_IsCreated_WithEveryFieldOfDesignSection3()
    {
        var plan = Plan();

        var boss = Assert.Single(plan.Schedules, s => s.Seat == "boss");
        Assert.Equal(PlannedScheduleAction.Create, boss.Action);
        var job = boss.Job!;
        Assert.Equal("DevThrottle - Boss", job.Name);
        Assert.True(job.Enabled);
        Assert.Equal(CronSchedule.KindRecurring, job.ScheduleKind);
        Assert.Equal("0 8 * * 1-5", job.CronExpression);
        Assert.Equal("Europe/Copenhagen", job.TimeZoneId);
        Assert.Equal(TheFactory, job.Factory);
        Assert.Equal("boss", job.Seat);
        Assert.Equal("SOREN_NORTH", job.Target.Machine);
        Assert.Equal("Morning round.", job.Action.Seed);
        Assert.Equal(Folder, job.Action.RepoPath);
        Assert.True(job.PreventOverlap);
        Assert.Equal("A new schedule (DevThrottle - Boss) for seat boss: Weekdays 08:00 Europe/Copenhagen.", boss.Sentence());
        Assert.Equal("Schedule cj_abc (DevThrottle - Boss) created for seat boss: Weekdays 08:00 Europe/Copenhagen.", boss.Sentence("cj_abc"));

        var outreach = Assert.Single(plan.Schedules, s => s.Seat == "outreach");
        Assert.Equal("SOREN_SOUTH", outreach.Job!.Target.Machine);
        Assert.Equal("New mail: {count}. {context}", outreach.Job.Action.Seed);
        Assert.DoesNotContain(plan.Schedules, s => s.Seat == "writer");
    }

    [Fact]
    public void Compose_ASeatWithSeveralStarts_NamesThemInStartOrder()
    {
        var d = WithSeat(Definition(), "writer", s => s with
        {
            Starts = new[] { Start("0 19 * * *", "Evening."), Start("0 7 * * *", "Morning."), Start("0 12 * * *", "Noon.") },
        });

        var writer = Plan(d).Schedules.Where(s => s.Seat == "writer").ToList();

        Assert.Equal(new[] { "DevThrottle - Writer", "DevThrottle - Writer (2)", "DevThrottle - Writer (3)" }, writer.Select(s => s.Job!.Name));
        Assert.Equal(new[] { "Evening.", "Morning.", "Noon." }, writer.Select(s => s.Job!.Action.Seed));
    }

    // ---------- the schedules: pairing ----------

    [Fact]
    public void Compose_ALiveScheduleWrittenExactlyAsTheStart_IsKept_AndNothingIsWritten()
    {
        var live = Live("cj_boss", "boss", name: "DevThrottle - Boss");

        var boss = Assert.Single(Plan(schedules: new[] { live }).Schedules, s => s.Seat == "boss");

        Assert.Equal(PlannedScheduleAction.Keep, boss.Action);
        Assert.Equal("cj_boss", boss.Id);
        Assert.Equal("Schedule cj_boss (DevThrottle - Boss) kept as it is.", boss.Sentence());
    }

    [Fact]
    public void Compose_ALiveScheduleAtAnotherTime_IsPairedByCloseness_AndMovedInPlace_KeepingWhatIsThePersons()
    {
        var live = Live("cj_boss", "boss", cron: "0 6 * * 1-5", enabled: false, name: "DevThrottle - Boss");
        live.NotifyOn = CronNotify.Always;
        live.NotifyWebhookUrl = "https://example.com/hook";
        live.Action.AutoDismiss = true;

        var boss = Assert.Single(Plan(schedules: new[] { live }).Schedules, s => s.Seat == "boss");

        Assert.Equal(PlannedScheduleAction.Update, boss.Action);
        Assert.Equal("cj_boss", boss.Id);
        Assert.Equal("0 8 * * 1-5", boss.Job!.CronExpression);
        Assert.False(boss.Job.Enabled);
        Assert.Equal(CronNotify.Always, boss.Job.NotifyOn);
        Assert.Equal("https://example.com/hook", boss.Job.NotifyWebhookUrl);
        Assert.True(boss.Job.Action.AutoDismiss);
        Assert.Equal("Schedule cj_boss (DevThrottle - Boss) moved: Weekdays 06:00 Europe/Copenhagen -> Weekdays 08:00 Europe/Copenhagen.",
            boss.Sentence());
    }

    [Fact]
    public void Compose_ExactPairingWinsOverPosition_AndClosenessPrefersTheSamePrompt()
    {
        var d = WithSeat(Definition(), "writer", s => s with
        {
            Starts = new[] { Start("0 7 * * *", "Morning."), Start("0 19 * * *", "Evening.") },
        });
        // cj_a is written exactly as the SECOND start; cj_b has the first start's prompt at another time.
        var live = new[] { Live("cj_a", "writer", "0 19 * * *", "Evening."), Live("cj_b", "writer", "0 6 * * *", "Morning.") };

        var writer = Plan(d, schedules: live).Schedules.Where(s => s.Seat == "writer").ToDictionary(s => s.Id!);

        Assert.Equal(1, writer["cj_a"].StartIndex);
        Assert.Equal(0, writer["cj_b"].StartIndex);
        Assert.Equal("0 7 * * *", writer["cj_b"].Job!.CronExpression);
    }

    [Fact]
    public void Compose_EnabledIsNeverChangedByAPublish()
    {
        var off = Live("cj_off", "boss", cron: "0 9 * * *", enabled: false);
        var on = Live("cj_on", "outreach", cron: "0 10 * * *", prompt: "New mail: {count}. {context}", enabled: true);

        var plan = Plan(schedules: new[] { off, on });

        Assert.False(plan.Schedules.Single(s => s.Id == "cj_off").Job!.Enabled);
        Assert.True(plan.Schedules.Single(s => s.Id == "cj_on").Job!.Enabled);
    }

    [Fact]
    public void Compose_ALiveScheduleNoStartNames_IsDeleted_SayingItsRunsAreKept()
    {
        var spare = Live("cj_spare", "boss", cron: "0 22 * * *", prompt: "Late.");
        var paired = Live("cj_boss", "boss");

        var plan = Plan(schedules: new[] { spare, paired });

        var deleted = Assert.Single(plan.Schedules, s => s.Action == PlannedScheduleAction.Delete);
        Assert.Equal("cj_spare", deleted.Id);
        Assert.Equal("Schedule cj_spare (old name cj_spare) deleted: no start in the definition names it; its runs are kept.", deleted.Sentence());
    }

    [Fact]
    public void Compose_ALiveScheduleWithNoSeat_OrASeatTheDefinitionHasNot_IsDeleted()
    {
        var noSeat = Live("cj_none", null);
        var gone = Live("cj_gone", "retired");

        var plan = Plan(schedules: new[] { noSeat, gone });

        Assert.Equal("Schedule cj_none (old name cj_none) deleted: it names no seat; its runs are kept.",
            plan.Schedules.Single(s => s.Id == "cj_none").Sentence());
        Assert.Equal("Schedule cj_gone (old name cj_gone) deleted: its seat 'retired' is not in the definition; its runs are kept.",
            plan.Schedules.Single(s => s.Id == "cj_gone").Sentence());
    }

    [Fact]
    public void Compose_AnotherFactorysScheduleAndTrigger_AreNeverInThePlan()
    {
        var theirs = Live("cj_theirs", "boss", factory: "invoice-factory");
        var plain = Live("cj_plain", null, factory: null!);
        plain.Factory = null;

        var plan = Plan(schedules: new[] { theirs, plain }, triggers: new[] { Trigger("their-inbox", "invoice-factory") });

        Assert.DoesNotContain(plan.Schedules, s => s.Id is "cj_theirs" or "cj_plain");
        Assert.Empty(plan.Triggers);
    }

    [Fact]
    public void Compose_EveryLiveTriggerOfTheFactory_IsDeleted_AndNamed()
    {
        var plan = Plan(triggers: new[] { Trigger("inbox-watch"), Trigger("another"), Trigger("theirs", "invoice-factory") });

        Assert.Equal(new[] { "another", "inbox-watch" }, plan.Triggers.Select(t => t.Name));
        Assert.All(plan.Triggers, t => Assert.Equal(PlannedTriggerAction.Delete, t.Action));
        Assert.Equal("Trigger inbox-watch deleted: its check is a command, and a defined factory's triggers come from its definition; its run history is kept.",
            plan.Triggers[1].Sentence());
    }

    [Fact]
    public void Compose_TheSentences_ComeInTheOrderAPublishTakesThem()
    {
        var plan = Plan(schedules: new[] { Live("cj_spare", "writer", "0 22 * * *", "Late.") }, triggers: new[] { Trigger("inbox-watch") });

        var sentences = plan.Sentences();

        Assert.StartsWith("Registry: ", sentences[0]);
        var firstSchedule = sentences.ToList().FindIndex(s => !s.StartsWith("Registry: ", StringComparison.Ordinal));
        Assert.StartsWith("A new schedule (DevThrottle - Boss)", sentences[firstSchedule]);
        Assert.StartsWith("A new schedule (DevThrottle - Outreach)", sentences[firstSchedule + 1]);
        Assert.StartsWith("Schedule cj_spare", sentences[firstSchedule + 2]);
        Assert.StartsWith("Trigger inbox-watch deleted", sentences[^1]);
    }

    // ---------- the triggers (phase 5b, design section 4) ----------

    /// <summary>A live definition trigger of the outreach seat, as a publish of the valid definition writes it.</summary>
    private static TriggerEntity DefTrigger(string name, string seat = "outreach", int interval = 600,
        string check = "checks/new_mail.py", string prompt = "New mail: {count}. {context}",
        string secrets = """["outreach-imap-password"]""", string machine = "SOREN_SOUTH", string factory = TheFactory) => new()
    {
        TenantId = "acct-a",
        Name = name,
        Factory = factory,
        FactoryAgent = seat,
        Machine = machine,
        RepoPath = Folder,
        CheckCommand = "",
        IntervalSeconds = interval,
        Prompt = prompt,
        Seat = seat,
        DefinitionVersion = 1,
        CheckPath = check,
        SecretsJson = secrets,
    };

    [Fact]
    public void Compose_AnIntervalStartWithACheck_IsANewDefinitionTrigger_NotASchedule()
    {
        var plan = Plan(Definition(DefinitionFixture.Valid()));

        var t = Assert.Single(plan.Triggers);
        Assert.Equal(PlannedTriggerAction.Create, t.Action);
        Assert.Equal("devthrottle-outreach-start-1", t.Name);
        Assert.Equal(new TriggerRow("devthrottle-outreach-start-1", "outreach", "SOREN_SOUTH", Folder, 600,
            "New mail: {count}. {context}", "checks/new_mail.py", new[] { "outreach-imap-password" }), t.Row);
        Assert.DoesNotContain(plan.Schedules, s => s.Seat == "outreach");
        Assert.Equal("A new trigger devthrottle-outreach-start-1 for seat outreach: every 10 minutes, check checks/new_mail.py with secret outreach-imap-password, on SOREN_SOUTH.",
            t.Sentence());
        Assert.Equal("Trigger devthrottle-outreach-start-1 created for seat outreach: every 10 minutes, check checks/new_mail.py with secret outreach-imap-password, on SOREN_SOUTH.",
            t.Sentence(2));
    }

    [Fact]
    public void Compose_ALiveDefinitionTriggerThatMatches_IsKept_ByItsId_AndOnlyItsVersionMoves()
    {
        var live = DefTrigger("devthrottle-outreach-start-1");

        var t = Assert.Single(Plan(Definition(DefinitionFixture.Valid()), triggers: new[] { live }).Triggers);

        Assert.Equal((PlannedTriggerAction.Keep, live.Id), (t.Action, t.Id));
        Assert.Equal("Trigger devthrottle-outreach-start-1 kept as it is; its check is read from version 3.", t.Sentence(3));
    }

    [Fact]
    public void Compose_AnIntervalEdited_ChangesTheTriggerInPlace_AndSaysWhat()
    {
        var live = DefTrigger("devthrottle-outreach-start-1", interval: 300, check: "checks/old_mail.py", secrets: """["old-entry"]""",
            prompt: "Old.", machine: "SOREN_NORTH");

        var t = Assert.Single(Plan(Definition(DefinitionFixture.Valid()), triggers: new[] { live }).Triggers);

        Assert.Equal((PlannedTriggerAction.Update, live.Id), (t.Action, t.Id));
        Assert.Equal("devthrottle-outreach-start-1", t.Row!.Name);
        Assert.Equal("Trigger devthrottle-outreach-start-1 changed: every 5 minutes -> every 10 minutes; check checks/old_mail.py -> checks/new_mail.py; " +
                     "secrets old-entry -> outreach-imap-password; prompt changed; machine SOREN_NORTH -> SOREN_SOUTH; its check is read from version 2.",
            t.Sentence(2));
    }

    [Fact]
    public void Compose_ADefinitionTriggerNoStartNames_AndOneOfAGoneSeat_AreDeleted_AndACommandTriggerToo()
    {
        var spare = DefTrigger("devthrottle-outreach-start-2", interval: 3600, check: "checks/other.py");
        var gone = DefTrigger("devthrottle-retired", seat: "retired");
        var command = Trigger("inbox-watch");
        var kept = DefTrigger("devthrottle-outreach-start-1");

        var plan = Plan(Definition(DefinitionFixture.Valid()), triggers: new[] { spare, gone, command, kept });

        Assert.Equal(PlannedTriggerAction.Keep, plan.Triggers[0].Action);
        var deletes = plan.Triggers.Where(t => t.Action == PlannedTriggerAction.Delete).ToDictionary(t => t.Name, t => t.Sentence());
        Assert.Equal("Trigger devthrottle-outreach-start-2 deleted: no start in the definition names it; its run history is kept.", deletes["devthrottle-outreach-start-2"]);
        Assert.Equal("Trigger devthrottle-retired deleted: its seat 'retired' is not in the definition; its run history is kept.", deletes["devthrottle-retired"]);
        Assert.Equal("Trigger inbox-watch deleted: its check is a command, and a defined factory's triggers come from its definition; its run history is kept.", deletes["inbox-watch"]);
    }

    [Fact]
    public void Compose_ATriggerNameAnotherFactoryHolds_IsRefused_OnTheSeatFile_InWords()
    {
        // Arrange: another factory's trigger already holds devthrottle-outreach-start-1, ignoring case.
        var theirs = Trigger("DevThrottle-Outreach-Start-1", "invoice-factory");

        // Act
        var composed = FactoryPublishPlan.Compose(Definition(DefinitionFixture.Valid()), Registry(),
            Array.Empty<CronJobEntity>(), new[] { theirs });

        // Assert
        Assert.Null(composed.Plan);
        var finding = Assert.Single(composed.Refusal!.Findings);
        Assert.Equal("seats/outreach.yaml", finding.File);
        Assert.Equal("The trigger this start makes is named devthrottle-outreach-start-1, and that name is taken by the trigger " +
                     "'DevThrottle-Outreach-Start-1' of invoice-factory. A trigger's name is unique in the account; rename or delete " +
                     "that trigger, then publish.", finding.Message);
    }

    [Fact]
    public void Compose_ATriggerNameThisFactorysCommandTriggerHeld_IsTakenOver_ByTheDefinitionTrigger()
    {
        var plan = Plan(Definition(DefinitionFixture.Valid()), triggers: new[] { Trigger("devthrottle-outreach-start-1") });

        Assert.Equal(new[] { (PlannedTriggerAction.Create, "devthrottle-outreach-start-1"), (PlannedTriggerAction.Delete, "devthrottle-outreach-start-1") },
            plan.Triggers.Select(t => (t.Action, t.Name)));
    }

    [Fact]
    public void Compose_AKeptTriggerWhoseStartMovedInTheList_IsRenamedWithIt()
    {
        // Arrange: the hourly check was the seat's second start and is now its only one.
        var live = DefTrigger("devthrottle-outreach-start-2", interval: 3600, prompt: "Hourly.", secrets: "[]");
        var d = WithSeat(Definition(DefinitionFixture.Valid()), "outreach", s => s with
        {
            Starts = new[] { new SeatStartDefinition(null, null, "1h", 3600, "checks/new_mail.py", Array.Empty<string>(), "Hourly.", true) },
        });

        // Act
        var t = Assert.Single(Plan(d, triggers: new[] { live }).Triggers);

        // Assert
        Assert.Equal((PlannedTriggerAction.Update, live.Id, "devthrottle-outreach-start-1"), (t.Action, t.Id, t.Name));
        Assert.Equal("Trigger devthrottle-outreach-start-1 changed: renamed devthrottle-outreach-start-2 -> devthrottle-outreach-start-1; its check is read from version 2.",
            t.Sentence(2));
    }

    [Fact]
    public void Compose_TwoIntervalStartsOnASeat_AreNamedByTheirStartNumbers()
    {
        var d = WithSeat(Definition(DefinitionFixture.Valid()), "outreach", s => s with
        {
            Starts = new[]
            {
                s.Starts[0],
                new SeatStartDefinition(null, null, "1h", 3600, "checks/new_mail.py", Array.Empty<string>(), "Hourly.", true),
            },
        });

        var plan = Plan(d);

        Assert.Equal(new[] { "devthrottle-outreach-start-1", "devthrottle-outreach-start-2" }, plan.Triggers.Select(t => t.Name));
        Assert.Equal(new[] { 600, 3600 }, plan.Triggers.Select(t => t.Row!.IntervalSeconds));
    }

    [Fact]
    public void Compose_AnIntervalStartAfterATimedStart_IsNumberedAmongEveryStart()
    {
        // Arrange: a timed start first, so the interval start is the seat's SECOND start.
        var d = WithSeat(Definition(DefinitionFixture.Valid()), "outreach", s => s with
        {
            Starts = new[]
            {
                new SeatStartDefinition("0 9 * * 1-5", "Europe/Copenhagen", null, null, null, Array.Empty<string>(), "Morning.", true),
                s.Starts[0],
            },
        });

        var plan = Plan(d);

        Assert.Equal("devthrottle-outreach-start-2", Assert.Single(plan.Triggers).Name);
    }

    [Fact]
    public void CheckStateFold_NumbersAStartByItsPlaceAmongEveryStart_TheNumberInItsTriggersName()
    {
        // Arrange: the interval start is the seat's second start; its trigger is the one the plan writes for it.
        var d = WithSeat(Definition(DefinitionFixture.Valid()), "outreach", s => s with
        {
            Starts = new[]
            {
                new SeatStartDefinition("0 9 * * 1-5", "Europe/Copenhagen", null, null, null, Array.Empty<string>(), "Morning.", true),
                s.Starts[0],
            },
        });
        var row = Assert.Single(Plan(d).Triggers).Row!;
        var live = new TriggerEntity
        {
            Name = row.Name, Factory = TheFactory, FactoryAgent = row.FactoryAgent, Machine = row.Machine,
            RepoPath = row.RepoPath, CheckCommand = "", IntervalSeconds = row.IntervalSeconds, Prompt = row.Prompt,
            Seat = "outreach", DefinitionVersion = 1, CheckPath = row.CheckPath,
            SecretsJson = System.Text.Json.JsonSerializer.Serialize(row.Secrets),
        };

        // Act
        var listed = CcDirector.Gateway.Factory.Triggers.FactoryCheckStateFold.List(d, new[] { live }, _ => null);

        // Assert
        var only = Assert.Single(listed);
        Assert.Equal(("outreach", 2, "devthrottle-outreach-start-2"), (only.Seat, only.Start, only.Name));
    }

    [Fact]
    public void DuplicateNames_TwoPlannedTriggersSharingAName_AreRefusedOnEachSeatFile_InWords()
    {
        // Arrange: two planned triggers whose names differ only in case - a name is unique in the account ignoring case.
        var planned = new[]
        {
            new PlannedTrigger(PlannedTriggerAction.Create, null, "f-a-start-1", "a", 0, null, null),
            new PlannedTrigger(PlannedTriggerAction.Create, null, "F-A-start-1", "b", 2, null, null),
            new PlannedTrigger(PlannedTriggerAction.Create, null, "f-c-start-1", "c", 0, null, null),
        };

        // Act
        var findings = FactoryPublishPlan.DuplicateNames(planned).ToList();

        // Assert
        Assert.Equal(new[] { "seats/a.yaml", "seats/b.yaml" }, findings.Select(f => f.File));
        Assert.Equal("Start 1 of this seat makes a trigger named f-a-start-1, and so does start 3 of seat b. " +
                     "A trigger's name is unique in the account; rename one of the seats, then publish.", findings[0].Message);
        Assert.All(findings, f => Assert.Equal(DefinitionSeverity.Error, f.Severity));
    }

    [Fact]
    public void Compose_ATimedStartBesideAnIntervalStart_IsNumberedAmongTheTimedStartsOnly()
    {
        var d = WithSeat(Definition(DefinitionFixture.Valid()), "outreach", s => s with
        {
            Starts = new[] { s.Starts[0], Start("0 9 * * 1-5", "Morning.") },
        });

        var plan = Plan(d);

        Assert.Equal("DevThrottle - Outreach", plan.Schedules.Single(s => s.Seat == "outreach").Job!.Name);
        Assert.Single(plan.Triggers);
    }

    [Fact]
    public void Compose_AnUndefinedFactorysTriggers_AreNeverInThePlan_EvenADefinitionShapedOne()
    {
        var plan = Plan(Definition(DefinitionFixture.Valid()),
            triggers: new[] { DefTrigger("invoice-clerk", seat: "outreach", factory: "invoice-factory") });

        Assert.Equal(new[] { (PlannedTriggerAction.Create, "devthrottle-outreach-start-1") }, plan.Triggers.Select(t => (t.Action, t.Name)));
    }

    // ---------- refusals (design section 4) ----------

    [Fact]
    public void Compose_AFactoryTheRegistryDoesNotHold_IsRefused_409()
    {
        var refusal = Refusal(noRegistry: true);

        Assert.Equal(FactoryDefinitionOutcome.NotRegistered, refusal.Outcome);
        Assert.Equal("devthrottle is not registered, and until phase 5 a defined factory runs in its registered folder. Register it first, then publish.",
            refusal.Sentence);
    }

    [Fact]
    public void Compose_AnArchivedFactory_IsRefused()
    {
        var archived = Registry();
        archived.ArchivedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        var refusal = Refusal(registry: archived);

        Assert.Equal(FactoryDefinitionOutcome.Archived, refusal.Outcome);
        Assert.Equal("devthrottle is archived. Restore it, then publish.", refusal.Sentence);
    }

    [Fact]
    public void Compose_ATimedStartWithACheck_IsRefused_OnItsSeatFile_UntilTheDueCheckLands()
    {
        var refusal = Refusal(Definition(DefinitionFixture.TimedChecked()));

        Assert.Equal(FactoryDefinitionOutcome.PlanRefused, refusal.Outcome);
        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("seats/outreach.yaml", finding.File);
        Assert.Equal(FactoryDefinitionCheck.TimedCheckStartNotYet, finding.Message);
    }

    [Fact]
    public void Compose_AnIntervalStartWithNoCheck_IsRefusedInItsOwnWords_NotAsAStartWithACheck()
    {
        // Arrange: the writer starts on an interval with no check, as a version stored before the check's interval
        // rule could hold.
        var d = WithSeat(Definition(), "writer", s => s with
        {
            Starts = new[] { new SeatStartDefinition(null, null, "1h", 3600, null, Array.Empty<string>(), "Write.", true) },
        });

        // Act
        var refusal = Refusal(d);

        // Assert
        Assert.Equal(FactoryDefinitionOutcome.PlanRefused, refusal.Outcome);
        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("seats/writer.yaml", finding.File);
        Assert.Equal("This start runs on an interval and has no check. An interval start is a check that runs often and starts a " +
                     "session only when it counts work, so give it a check, or a cron time, or publish it without the start.", finding.Message);
    }

    [Fact]
    public void Compose_ATitleTheRegistryRefuses_IsRefused_OnFactoryYaml_InTheRegistrysWords()
    {
        var refusal = Refusal(Definition() with { Title = new string('T', FactoryRegistryStore.MaxTitleChars + 1) });

        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("factory.yaml", finding.File);
        Assert.Equal($"The title is {FactoryRegistryStore.MaxTitleChars + 1} characters; it takes at most {FactoryRegistryStore.MaxTitleChars}.", finding.Message);
        Assert.StartsWith("The live factory cannot take this definition: 1 row would break its rule", refusal.Sentence);
    }

    [Fact]
    public void Compose_ASeatTheRegistryRefuses_IsRefused_OnThatSeatsFile()
    {
        var refusal = Refusal(WithSeat(Definition(), "writer", s => s with { Name = new string('N', FactoryRegistryStore.MaxTitleChars + 1) }));

        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("seats/writer.yaml", finding.File);
        Assert.Equal($"The name of seat 'writer' is {FactoryRegistryStore.MaxTitleChars + 1} characters; it takes at most {FactoryRegistryStore.MaxTitleChars}.", finding.Message);
    }

    [Fact]
    public void Compose_MoreStartsOnASeatThanTheRegistryAllowsSchedules_IsRefused_InTheRegistrysWords()
    {
        var starts = Enumerable.Range(0, FactoryRegistryStore.MaxSchedulesPerSeat + 1)
            .Select(i => Start($"{i} 7 * * *", $"Write {i}.")).ToList();

        var refusal = Refusal(WithSeat(Definition(), "writer", s => s with { Starts = starts }));

        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("seats/writer.yaml", finding.File);
        Assert.Equal($"Seat 'writer' names {FactoryRegistryStore.MaxSchedulesPerSeat + 1} schedules; a seat names at most {FactoryRegistryStore.MaxSchedulesPerSeat}.", finding.Message);
    }

    [Fact]
    public void Compose_AScheduleTheScheduleRulesRefuse_IsRefused_OnItsSeatFile_InTheirWords()
    {
        var refusal = Refusal(WithSeat(Definition(), "writer", s => s with { Starts = new[] { Start("0 7 * * *", "") } }));

        var finding = Assert.Single(refusal.Findings);
        Assert.Equal("seats/writer.yaml", finding.File);
        Assert.Equal("The schedule this start makes is refused: action requires either a seed or a workListName.", finding.Message);
    }

    [Fact]
    public void Compose_ReadsNothingButWhatItIsGiven_TheSameInputGivesTheSamePlan()
    {
        var live = new[] { Live("cj_boss", "boss", cron: "0 6 * * 1-5") };

        Assert.Equal(Plan(schedules: live).Sentences(), Plan(schedules: live).Sentences());
    }
}
