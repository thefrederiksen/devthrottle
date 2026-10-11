using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Talk;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// Handle the issue (Factory Control, step 1b), without a server: the factory page's Open problems card laid out from
/// the one problems fold's answer, who a problem's talk goes to (the boss, or the Factory Manager that does not exist
/// yet), and the briefing the boss is opened with.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactoryProblemsFoldTests
{
    // Sun 11 Oct 2026 12:00 UTC; the account is on UTC so "today" is plain.
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static RegisteredFactoryDto Factory(string? boss) => new()
    {
        Factory = "tallyhand",
        Title = "Tallyhand",
        Folder = @"D:\f\tallyhand",
        Computer = "SOREN_NORTH",
        BossSeat = boss,
        Seats =
        {
            new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml", Schedules = { "cj_boss" }, Computer = "SOREN_NORTH" },
            new RegisteredFactorySeatDto { Id = "scout", Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml", Schedules = { "cj_scout" }, Computer = "SOREN_NORTH" },
        },
    };

    private static CronRunProblemDto Problem(string state = CronRunProblemStates.Open, string? session = "5b0e9d1c-1111-4000-8000-0000000000b2",
        string factory = "tallyhand") => new()
    {
        RunId = "3f1c2b7a-0000-4000-8000-0000000000a1",
        JobId = "cj_scout",
        JobName = "Tallyhand - Scout - daily scan",
        Factory = factory,
        Seat = "scout",
        ScheduledUtc = Now.AddHours(-3),
        StartedUtc = session is null ? null : Now.AddHours(-3).AddMinutes(1),
        Shift = "morning",
        ShiftText = "morning shift, Sun 11 Oct, 08:00-16:00",
        Kind = session is null ? CronRunProblems.DidNotRun : CronRunProblems.DidNotReport,
        KindText = session is null ? "did not run" : "did not report",
        Reason = session is null ? "it was due and never started a session" : "its session ended without reporting how the run went",
        Sentence = session is null
            ? "Did not run: it was due and never started a session."
            : "Did not report: its session ended without reporting how the run went.",
        WhenText = session is null ? "Due Sun 11 Oct 09:00" : "Started Sun 11 Oct 09:01",
        RaisedAtUtc = Now.AddHours(-2).AddMinutes(-30),
        OpenForText = state == CronRunProblemStates.Open ? "open 2 hours" : null,
        SessionId = session,
        State = state,
    };

    private static CronRunProblemsDto Answer(params CronRunProblemDto[] problems) => new()
    {
        Count = problems.Length, Factory = "tallyhand", TimeZone = "UTC", Problems = problems.ToList(),
    };

    // ---- the card ----------------------------------------------------------------------------------------------

    [Fact]
    public void Card_AnOpenProblem_IsLaidOutInTheGatewaysWords()
    {
        var card = FactoryProblemsFold.Card(Factory("boss"), Answer(Problem()));

        var item = Assert.Single(card.Items);
        Assert.Equal("Open problems", card.Heading);
        Assert.Null(card.EmptyText);
        Assert.Equal("3f1c2b7a-0000-4000-8000-0000000000a1", item.RunId);
        Assert.Equal("Scout - Tallyhand - Scout - daily scan", item.SeatText);
        Assert.Equal("Did not report: its session ended without reporting how the run went.", item.What);
        Assert.Equal("Started Sun 11 Oct 09:01", item.WhenText);
        Assert.Equal("morning shift, Sun 11 Oct, 08:00-16:00", item.ShiftText);
        Assert.Equal("open 2 hours", item.OpenForText);
        Assert.Equal("5b0e9d1c-1111-4000-8000-0000000000b2", item.SessionId);
        Assert.NotNull(item.SessionLabel);
        Assert.Null(item.NoSessionText);
        Assert.Equal("Resolve", item.ResolveLabel);
        Assert.Equal("Why is it resolved? One line.", item.ResolvePrompt);
    }

    [Fact]
    public void Card_ARunThatNeverStarted_SaysSoAndHasNoSession()
    {
        var item = Assert.Single(FactoryProblemsFold.Card(Factory("boss"), Answer(Problem(session: null))).Items);

        Assert.Equal("Due Sun 11 Oct 09:00", item.WhenText);
        Assert.Null(item.SessionId);
        Assert.Null(item.SessionLabel);
        Assert.Equal("No session - the run never started one.", item.NoSessionText);
    }

    [Fact]
    public void Card_OnlyOpenProblemsOfThisFactory_AreListed()
    {
        var card = FactoryProblemsFold.Card(Factory("boss"),
            Answer(Problem(state: CronRunProblemStates.Resolved), Problem(state: CronRunProblemStates.Cleared), Problem(factory: "warmforward")));

        Assert.Empty(card.Items);
        Assert.Equal("No open problems.", card.EmptyText);
    }

    [Fact]
    public void Card_AFactoryWithABoss_OffersHandleTheIssue()
    {
        var handle = Assert.Single(FactoryProblemsFold.Card(Factory("boss"), Answer(Problem())).Items).Handle;

        Assert.True(handle.Available);
        Assert.Null(handle.UnavailableText);
        Assert.Equal("Handle the issue", handle.Label);
        Assert.Equal("tallyhand", handle.FactoryId);
        Assert.Equal("3f1c2b7a-0000-4000-8000-0000000000a1", handle.RunId);
    }

    [Fact]
    public void Card_AFactoryWithNoBoss_OffersHandleTheIssue_WithTheFactoryManager()
    {
        var handle = Assert.Single(FactoryProblemsFold.Card(Factory(null), Answer(Problem())).Items).Handle;

        Assert.True(handle.Available);
        Assert.Null(handle.UnavailableText);
        Assert.Equal("Handle the issue", handle.Label);
        Assert.Equal("Starting the talk with the Factory Manager...", handle.BusyLabel);
    }

    // ---- who it goes to ----------------------------------------------------------------------------------------

    [Fact]
    public void Route_AFactoryWithABoss_GoesToTheBoss()
    {
        var target = FactoryProblemHandoff.Route(Factory("BOSS"));

        Assert.True(target.Available);
        Assert.Equal("boss", target.Seat!.Id);
    }

    [Fact]
    public void Route_ABossSeatTheFactoryDoesNotHave_GoesToTheFactoryManager()
    {
        var target = FactoryProblemHandoff.Route(Factory("ceo"));

        Assert.True(target.Available);
        Assert.True(target.ToFactoryManager);
        Assert.Null(target.Seat);
    }

    // ---- the briefing ------------------------------------------------------------------------------------------

    [Fact]
    public void ComposeForProblem_IsTheOrdinaryTalkThenTheProblemAndHowToResolveIt()
    {
        var f = Factory("boss");
        var boss = f.Seats[0];
        var toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

        var seed = FactoryTalkSeed.ComposeForProblem(f, boss, "cj_boss", "Do the morning run.", Problem(), toronto);

        // The ordinary talk first: seated as the boss, under the rules of its scheduled runs.
        Assert.StartsWith(FactoryTalkSeed.Compose(f, boss, "cj_boss", "Do the morning run."), seed);
        Assert.Contains("THIS TALK IS ABOUT ONE PROBLEM.", seed);
        Assert.Contains("- the run: 3f1c2b7a-0000-4000-8000-0000000000a1", seed);
        Assert.Contains("- the schedule: Tallyhand - Scout - daily scan (cj_scout), run as the seat scout", seed);
        Assert.Contains("- what went wrong: did not report - its session ended without reporting how the run went", seed);
        // Times in the account's zone, named.
        Assert.Contains("- when: due Sun 11 Oct 05:00 (America/Toronto), started Sun 11 Oct 05:01 (America/Toronto), in the morning shift, Sun 11 Oct, 08:00-16:00", seed);
        Assert.Contains("- the session to read: 5b0e9d1c-1111-4000-8000-0000000000b2.", seed);
        Assert.Contains("cc-devthrottle session buffer 5b0e9d1c-1111-4000-8000-0000000000b2", seed);
        Assert.Contains("cc-devthrottle run resolve 3f1c2b7a-0000-4000-8000-0000000000a1 \"<one line: what fixed it>\"", seed);
        Assert.Contains("If it is not fixed when the talk ends, do not resolve it", seed);
    }

    [Fact]
    public void ComposeForProblem_ARunThatNeverStarted_SaysThereIsNoSessionToRead()
    {
        var f = Factory("boss");

        var seed = FactoryTalkSeed.ComposeForProblem(f, f.Seats[0], "cj_boss", "Do the morning run.", Problem(session: null), TimeZoneInfo.Utc);

        Assert.Contains("never started", seed);
        Assert.Contains("- the session to read: none - this run never started a session", seed);
        Assert.DoesNotContain("session buffer", seed);
    }

    [Fact]
    public void ProblemSessionName_IsTheFactoryTheBossAndTheSchedule()
    {
        var f = Factory("boss");

        Assert.Equal("Tallyhand - Boss - handle the issue (Tallyhand - Scout - daily scan)",
            FactoryTalkSeed.ProblemSessionName(f, f.Seats[0], Problem()));
    }

    // ---- a seat is named by its role, never its id -----------------------------------------------------------

    [Fact]
    public void Card_AProblemOfASeatWithNoRole_IsRefusedNotNamedByItsId()
    {
        var f = Factory("boss");
        f.Seats[1].Role = " ";

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryProblemsFold.Card(f, Answer(Problem())));
        Assert.Contains("has no role", ex.Message);
    }

    [Fact]
    public void Card_AProblemOfASeatTheFactoryNoLongerHas_SaysSoWithoutTheOldId()
    {
        var f = Factory("boss");
        f.Seats.RemoveAt(1);

        var item = Assert.Single(FactoryProblemsFold.Card(f, Answer(Problem())).Items);

        Assert.Equal("A seat no longer on this factory - Tallyhand - Scout - daily scan", item.SeatText);
    }
}
