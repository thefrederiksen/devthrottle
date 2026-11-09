using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// "Watch your factory", piece 3: the contributor's watch view, the security piece. A contributor never sees a
/// machine, and the view is assembled only from an allow-list. Each rule has its own test that goes red when the rule
/// is removed; the canary test seeds every internal field with a unique string and finds none of them in the view.
/// </summary>
public sealed class FactoryWatchContributorTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private static readonly FactoryWindow Day = new(FactoryAgentsFold.WindowLast24h, Now.AddHours(-24), Now);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly string[] Machines = { "SOREN_NORTH", "devlinux" };
    private static readonly FactoryWatchContributorScope Theirs = new(true, Machines);
    private static readonly FactoryWatchContributorScope NotTheirs = new(false, Machines);

    private static RegisteredFactorySeatDto Seat(string id, string? name = null, string? role = null, string? line = null) =>
        new() { Id = id, Name = name ?? id, Role = role ?? "Worker", BriefFile = $"agents/{id}.yaml", Computer = "SOREN_NORTH", Line = line };

    private static RegisteredFactoryDto Factory(params RegisteredFactorySeatDto[] seats) => new()
    {
        Factory = "demo", Title = "Demo Website", Folder = @"D:\factories\demo", Computer = "SOREN_NORTH", BossSeat = "boss",
        Seats = seats.Length > 0 ? seats.ToList() : new() { Seat("boss", "Boss", "Boss"), Seat("desk", "Website Desk", "Builds the site") },
    };

    private static FactoryActivityDto Row(string agent, string outcome, DateTime at, string actor = "session:abc",
        string what = "the row's own text", string? forContributor = null, string? subject = null) => new()
    {
        Id = Guid.NewGuid(), Factory = "demo", FactoryAgent = agent, What = what, Outcome = outcome, Actor = actor,
        Subject = subject, ForContributor = forContributor, OccurredUtc = at, RecordedUtc = at,
    };

    private static FactoryWatchViewDto View(RegisteredFactoryDto f, IReadOnlyList<FactoryActivityDto> rows,
        FactoryWatchContributorScope? scope = null, bool truncated = false) =>
        FactoryWatchFold.View(f, rows, truncated, Day, Utc, FactoryWatchAudience.Contributor, scope ?? Theirs);

    private static FactoryWatchStepDto OneStep(FactoryActivityDto row, FactoryWatchContributorScope? scope = null) =>
        Assert.Single(View(Factory(), new[] { row }, scope).Steps);

    // ------------------------------------------------------------------------------------------------ the canary

    /// <summary>
    /// Every internal field of every seat and every row - and every allowed text field that carries a machine name, a
    /// path or an email - holds a unique canary. None of them may appear anywhere in the serialized contributor view,
    /// whether the work is theirs or not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void View_EveryInternalFieldSeededWithACanary_NoCanaryAppearsInTheContributorJson(bool wholeFactory)
    {
        var canaries = new List<string>();
        string C(string what) { var c = $"CANARY{canaries.Count:D3}{what}"; canaries.Add(c); return c; }

        var seats = new List<RegisteredFactorySeatDto>();
        foreach (var (i, line) in new[] { (0, (string?)null), (1, C("line") + " on SOREN_NORTH"), (2, C("line2") + @" D:\work"), (3, null) })
        {
            seats.Add(new RegisteredFactorySeatDto
            {
                Id = C("seatid"),
                Name = C("seatname") + (i % 2 == 0 ? " SOREN_NORTH" : " devlinux"),
                Role = C("role") + " in /home/soren/",
                BriefFile = C("brief"),
                Schedules = new() { C("schedule") },
                Computer = C("computer"),
                Line = line,
            });
        }
        var factoryId = C("factoryid");
        var f = new RegisteredFactoryDto
        {
            Factory = factoryId, Title = C("title") + " SOREN_NORTH", Folder = C("folder"), Computer = C("factorycomputer"),
            BossSeat = seats[0].Id, GoalText = C("goal"), GoalFile = C("goalfile"), GoalApprovedOn = C("approved"),
            Purpose = C("purpose"), Seats = seats, RegisteredBy = C("registeredby"), ArchivedBy = C("archivedby"),
            ArchivedSchedules = new() { C("archived") },
        };

        FactoryActivityDto Seeded(string agent, string outcome, int minutes, string actor) => new()
        {
            Id = Guid.NewGuid(), Factory = factoryId, FactoryAgent = agent, FactoryAgentVersion = C("version"),
            SessionId = C("session"), What = C("what") + " C:\\Users\\soren", Outcome = outcome, Subject = C("subject"),
            Link = C("link"), Actor = actor, CorrectsId = Guid.NewGuid(),
            ForContributor = C("forcontributor") + " on devlinux, mail soren@example.com",
            OccurredUtc = Now.AddMinutes(-600 + minutes), RecordedUtc = Now.AddMinutes(-600 + minutes),
        };

        var rows = new List<FactoryActivityDto>();
        var minute = 0;
        foreach (var outcome in FactoryActivityOutcome.All)
            foreach (var seat in seats)
                rows.Add(Seeded(seat.Id, outcome, minute++, "session:" + C("actor")));
        rows.Add(Seeded(seats[1].Id, FactoryActivityOutcome.Failed, minute++, "trigger:" + C("trigger")));
        rows.Add(Seeded(seats[1].Id, FactoryActivityOutcome.Failed, minute++, "schedule:" + C("job")));
        for (var i = 0; i < 4; i++)
            rows.Add(Seeded(seats[2].Id, FactoryActivityOutcome.NothingToDo, minute++, "trigger:" + "CANARYrun"));
        canaries.Add("CANARYrun");
        rows.Add(Seeded(seats[3].Id, FactoryActivityOutcome.Done, minute++, "owner (device:" + C("device") + ")"));
        rows.Add(Seeded("owner", FactoryActivityOutcome.Done, minute++, "owner (device:" + C("device2") + ")"));
        rows.Add(Seeded(C("unregistered"), FactoryActivityOutcome.Done, minute++, "session:" + C("actor2")));

        var view = FactoryWatchFold.View(f, rows, true, Day, Utc, FactoryWatchAudience.Contributor, new FactoryWatchContributorScope(wholeFactory, Machines));
        var json = JsonSerializer.Serialize(view);

        Assert.NotEmpty(view.Steps);
        foreach (var canary in canaries)
            Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        foreach (var leak in new[] { "SOREN_NORTH", "devlinux", @"D:\\", "/home/", "@example.com", "session:", "trigger:", "schedule:", "device:" })
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
        foreach (var row in rows)
            Assert.DoesNotContain(row.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------ the words

    [Fact]
    public void Fixed_IsExactlyTheApprovedSentenceList()
    {
        Assert.Equal(new Dictionary<string, string>
        {
            [FactoryWatchStepKind.Woke] = "Started work.",
            [FactoryWatchStepKind.Delivered] = "Finished a piece of work.",
            [FactoryWatchStepKind.Talked] = "Talked it over.",
            [FactoryWatchStepKind.CheckedNothing] = "Checked for new work. Nothing yet.",
            [FactoryWatchStepKind.Waiting] = "Your request is waiting its turn.",
        }, FactoryWatchWords.Fixed);
    }

    [Fact]
    public void View_OwnStepWithAPassingContributorSentence_ShowsThatSentenceExactly()
    {
        var step = OneStep(Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-1), forContributor: "Your bakery's new home page is live."));

        Assert.Equal("Your bakery's new home page is live.", step.Words);
        Assert.True(step.Mine);
    }

    [Theory]
    [InlineData("Built it on SOREN_NORTH.")]
    [InlineData("Built it on devlinux.")]
    [InlineData(@"Saved it in D:\sites\bakery.")]
    [InlineData("Sent it to soren@example.com.")]
    public void View_ContributorSentenceRefusedAtReadTime_ShowsTheFixedSentenceInstead(string sentence)
    {
        // A row can predate a machine being registered, so the sentence is checked again when it is read.
        var step = OneStep(Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-1), forContributor: sentence));

        Assert.Equal("Finished a piece of work.", step.Words);
    }

    [Theory]
    [InlineData(FactoryActivityOutcome.Started, FactoryWatchStepKind.Woke, "Started work.")]
    [InlineData(FactoryActivityOutcome.Done, FactoryWatchStepKind.Delivered, "Finished a piece of work.")]
    [InlineData(FactoryActivityOutcome.Talked, FactoryWatchStepKind.Talked, "Talked it over.")]
    [InlineData(FactoryActivityOutcome.NothingToDo, FactoryWatchStepKind.CheckedNothing, "Checked for new work. Nothing yet.")]
    public void View_OwnStepWithoutAContributorSentence_ShowsTheFixedSentenceNeverTheRowText(string outcome, string kind, string words)
    {
        var step = OneStep(Row("desk", outcome, Now.AddHours(-1), what: "Internal: rebuilt the cache"));

        Assert.Equal(kind, step.Kind);
        Assert.Equal(words, step.Words);
    }

    [Theory]
    [InlineData(FactoryActivityOutcome.Failed)]
    [InlineData(FactoryActivityOutcome.Blocked)]
    [InlineData(FactoryActivityOutcome.SentBack)]
    public void View_HiccupWrittenByTheSeat_HasNoWordsEvenWithAContributorSentence(string outcome)
    {
        var step = OneStep(Row("desk", outcome, Now.AddHours(-1), forContributor: "Oops, starting again."));

        Assert.Equal(FactoryWatchStepKind.Hiccup, step.Kind);
        Assert.Equal("", step.Words);
    }

    [Theory]
    [InlineData("trigger:3f2a")]
    [InlineData("schedule:nightly-build")]
    public void View_FailureWrittenByTheGatewaysMachinery_IsOnlyWaitingItsTurn(string actor)
    {
        var step = OneStep(Row("desk", FactoryActivityOutcome.Failed, Now.AddHours(-1), actor,
            what: "Checked mail - the session could not be started: no Director on SOREN_NORTH", forContributor: "Something broke."));

        Assert.Equal(FactoryWatchStepKind.Waiting, step.Kind);
        Assert.Equal("Your request is waiting its turn.", step.Words);
    }

    [Fact]
    public void View_ContributorSteps_UseOnlyTheContributorKinds()
    {
        var rows = FactoryActivityOutcome.All.Select((o, i) => Row("desk", o, Now.AddMinutes(-100 + i))).ToList();
        rows.Add(Row("desk", FactoryActivityOutcome.Failed, Now.AddMinutes(-10), "trigger:x"));

        var view = View(Factory(), rows);

        Assert.All(view.Steps, s => Assert.Contains(s.Kind, FactoryWatchStepKind.AllForContributor));
        Assert.Contains(view.Steps, s => s.Kind == FactoryWatchStepKind.Waiting);
    }

    [Fact]
    public void View_RunOfEmptyChecks_IsOneStepWithItsCountAndTheFixedSentence()
    {
        var rows = Enumerable.Range(0, 6)
            .Select(i => Row("desk", FactoryActivityOutcome.NothingToDo, Now.AddHours(-3).AddMinutes(5 * i), "trigger:mail", forContributor: "Looked."))
            .ToList();

        var step = Assert.Single(View(Factory(), rows).Steps);

        Assert.Equal(6, step.EmptyChecks);
        Assert.Equal("Checked for new work. Nothing yet.", step.Words);
    }

    // ------------------------------------------------------------------------------------------------ their own work

    [Fact]
    public void View_WorkThatIsNotTheirs_IsAPlainCrateWithNoWordsAndNoSubject()
    {
        var rows = new[]
        {
            Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-2), forContributor: "Another customer's site is live.", subject: "Other Customer Ltd"),
            Row("desk", FactoryActivityOutcome.Started, Now.AddHours(-1), subject: "Other Customer Ltd"),
            Row("desk", FactoryActivityOutcome.Failed, Now.AddMinutes(-30), "trigger:x", subject: "Other Customer Ltd"),
        };

        var view = View(Factory(), rows, NotTheirs);
        var json = JsonSerializer.Serialize(view);

        Assert.Equal(3, view.Steps.Count);
        Assert.All(view.Steps, s => Assert.False(s.Mine));
        Assert.All(view.Steps, s => Assert.Equal("", s.Words));
        Assert.DoesNotContain("Other Customer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Another customer", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_FailsSafe_NothingIsTheirsUnlessTheScopeSaysSo()
    {
        var row = Row("desk", FactoryActivityOutcome.Done, Now);

        Assert.False(NotTheirs.IsMine(row));
        Assert.True(Theirs.IsMine(row));
    }

    // ------------------------------------------------------------------------------------------------ labels

    [Fact]
    public void View_SeatLabelsNamingAMachine_AreReplacedByTheNeutralWordForTheirPlace()
    {
        var f = Factory(Seat("boss", "Boss on SOREN_NORTH", "Boss at devlinux"), Seat("desk", "devlinux desk", @"Builds in D:\sites"), Seat("ok", "Website Desk", "Builds the site"));

        var view = View(f, Array.Empty<FactoryActivityDto>());

        Assert.Equal(new[] { "Boss", "Worker", "Website Desk" }, view.Stations.Select(s => s.Label));
        Assert.Equal(new[] { "Boss", "Worker", "Builds the site" }, view.Stations.Select(s => s.Sub));
        Assert.Equal(new[] { "Boss", "Worker", "Website Desk" }, view.Characters.Select(c => c.Name));
    }

    [Fact]
    public void View_TitleNamingAMachine_IsYourFactory()
    {
        var f = Factory();
        f.Title = "SOREN_NORTH builds";

        Assert.Equal("Your factory", View(f, Array.Empty<FactoryActivityDto>()).Title);
        Assert.Equal("Demo Website", View(Factory(), Array.Empty<FactoryActivityDto>()).Title);
    }

    [Fact]
    public void View_LineNameNamingAMachine_IsLineAndItsNumber()
    {
        var f = Factory(Seat("boss", "Boss", "Boss"), Seat("a", line: "Write"), Seat("b", line: "devlinux night shift"));

        var view = View(f, Array.Empty<FactoryActivityDto>());

        Assert.Equal(new[] { "Write", "Line 2" }, view.Lines.Select(l => l.Name));
        Assert.Equal(new[] { null, "Write", "Line 2" }, view.Stations.Select(s => s.Line));
    }

    // ------------------------------------------------------------------------------------------------ what is absent

    [Fact]
    public void View_SeatsAndSteps_HaveOpaqueKeysNeverTheSeatIdOrTheRowId()
    {
        var rows = new[] { Row("desk", FactoryActivityOutcome.Started, Now.AddHours(-2)), Row("boss", FactoryActivityOutcome.Done, Now.AddHours(-1)) };

        var view = View(Factory(), rows);

        Assert.Equal(new[] { "seat-1", "seat-2" }, view.Stations.Select(s => s.Id));
        Assert.Equal(new[] { "seat-1", "seat-2" }, view.Characters.Select(c => c.Seat));
        Assert.Equal(new[] { "seat-2", "seat-1" }, view.Steps.Select(s => s.Seat));
        Assert.Equal(new[] { "step-1", "step-2" }, view.Steps.Select(s => s.Key));
    }

    [Fact]
    public void View_SameSeat_KeepsTheOwnersColourInTheContributorView()
    {
        var owner = FactoryWatchFold.View(Factory(), Array.Empty<FactoryActivityDto>(), false, Day, Utc, FactoryWatchAudience.Owner);

        var contributor = View(Factory(), Array.Empty<FactoryActivityDto>());

        Assert.Equal(owner.Characters.Select(c => c.Colour), contributor.Characters.Select(c => c.Colour));
    }

    [Fact]
    public void View_ContributorView_HasNoNotesNoTruncationTextAndNoFactoryId()
    {
        var rows = new[] { Row("ghost", FactoryActivityOutcome.Done, Now.AddHours(-1)), Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-1)) };

        var view = View(Factory(), rows, truncated: true);

        Assert.Empty(view.Notes);
        Assert.Null(view.TruncatedText);
        Assert.Equal("", view.FactoryId);
    }

    [Fact]
    public void View_OwnerActsAndUnregisteredWorkers_AreNotOnTheContributorTimeline()
    {
        var rows = new[]
        {
            Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-3), "owner (device:abc)", "Marked handled by owner (device:abc): x"),
            Row("owner", FactoryActivityOutcome.Done, Now.AddHours(-2), "owner (device:abc)", "The owner archived Demo"),
            Row("ghost", FactoryActivityOutcome.Done, Now.AddHours(-1)),
        };

        var view = View(Factory(), rows);

        Assert.Empty(view.Steps);
        Assert.Empty(view.Notes);
    }

    [Fact]
    public void View_EmptyWindow_SaysNothingHappenedInYourFactory()
    {
        Assert.Equal("Nothing happened in your factory in the last 24 hours.", View(Factory(), Array.Empty<FactoryActivityDto>()).EmptyText);
    }

    // ------------------------------------------------------------------------------------------------ the owner view

    [Fact]
    public void View_OwnerView_NeverEmitsWaitingAndKeepsTheRowsOwnWords()
    {
        var rows = new[]
        {
            Row("desk", FactoryActivityOutcome.Failed, Now.AddHours(-2), "trigger:x", "the session could not be started: no Director"),
            Row("desk", FactoryActivityOutcome.Done, Now.AddHours(-1), what: "Site live", forContributor: "Your site is live."),
        };

        var view = FactoryWatchFold.View(Factory(), rows, false, Day, Utc, FactoryWatchAudience.Owner);

        Assert.Equal(new[] { FactoryWatchStepKind.Hiccup, FactoryWatchStepKind.Delivered }, view.Steps.Select(s => s.Kind));
        Assert.Equal(new[] { "the session could not be started: no Director", "Site live" }, view.Steps.Select(s => s.Words));
        Assert.Equal(rows.Select(r => r.Id.ToString()), view.Steps.Select(s => s.Key));
        Assert.Equal(new[] { "desk", "desk" }, view.Steps.Select(s => s.Seat));
        Assert.All(view.Steps, s => Assert.False(s.Mine));
        Assert.Equal("demo", view.FactoryId);
        Assert.NotEmpty(view.Notes);
    }

    [Fact]
    public void StepKind_NoActivityOutcome_MapsToWaiting()
    {
        Assert.DoesNotContain(FactoryWatchStepKind.Waiting, FactoryActivityOutcome.All.Select(FactoryWatchFold.StepKind));
        Assert.DoesNotContain(FactoryWatchStepKind.Waiting, FactoryWatchStepKind.All);
    }

    [Fact]
    public void View_OwnerViewWithAContributorScope_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            FactoryWatchFold.View(Factory(), Array.Empty<FactoryActivityDto>(), false, Day, Utc, FactoryWatchAudience.Owner, Theirs));
    }
}
