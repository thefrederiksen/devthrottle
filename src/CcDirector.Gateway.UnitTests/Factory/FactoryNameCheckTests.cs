using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Talk;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE NAME CHECK (Factory Control, step 5): a factory has a boss, not a CEO, and seats are role words, never a
/// person's name. The rule is stated, not a list of names, so these tests use names no list would hold.
/// </summary>
public sealed class FactoryNameCheckTests
{
    private static RegisteredFactoryDto Factory(string title, string? boss, params RegisteredFactorySeatDto[] seats)
    {
        var f = new RegisteredFactoryDto { Factory = title.ToLowerInvariant(), Title = title, BossSeat = boss, Folder = @"D:\f\" + title };
        f.Seats.AddRange(seats);
        return f;
    }

    private static RegisteredFactorySeatDto Seat(string id, string name, string role) =>
        new() { Id = id, Name = name, Role = role, Computer = "SOREN_NORTH" };

    [Fact]
    public void Check_ABossNamedBoss_AndRoleWordSeats_Pass()
    {
        var check = FactoryNameCheck.Check(new[]
        {
            Factory("Tallyhand", "boss", Seat("boss", "Boss", "Boss"), Seat("scout", "Scout", "Scout")),
        });

        Assert.Equal(0, check.Count);
        Assert.Equal(1, check.FactoriesChecked);
        Assert.Equal("Every factory passes.", check.PassText);
        Assert.Equal(FactoryNameCheck.RuleText, check.RuleText);
    }

    [Theory]
    [InlineData("ceo", "Boss", "Boss")]
    [InlineData("boss", "CEO", "Boss")]
    [InlineData("boss", "Boss", "Ceo")]
    [InlineData("the-ceo-seat", "Boss", "Boss")]
    public void Check_ASeatThatSaysCeo_IsFlaggedAsCeo(string id, string name, string role)
    {
        var finding = Assert.Single(FactoryNameCheck.Check(new[] { Factory("Tallyhand", id, Seat(id, name, role)) }).Findings);

        Assert.Equal(FactoryNameCheck.KindCeo, finding.Kind);
        Assert.Equal(id, finding.SeatId);
        Assert.Contains("A factory has a boss, not a CEO.", finding.Text);
    }

    [Fact]
    public void Check_AWordThatOnlyContainsCeoLetters_IsNotCeo()
    {
        // Whole words only: "Lyceom" holds the letters c-e-o inside a word and is not CEO.
        Assert.False(FactoryNameCheck.SaysCeo("Lyceom Scout"));
        Assert.True(FactoryNameCheck.SaysCeo("Acting CEO"));
        Assert.True(FactoryNameCheck.SaysCeo("ceo-2"));
    }

    [Fact]
    public void Check_APersonStyleNameOnTheBossSeat_IsFlagged_WithNoListOfNames()
    {
        var finding = Assert.Single(FactoryNameCheck.Check(new[]
        {
            Factory("WarmForward", "boss", Seat("boss", "Quillon Varrick", "Boss"), Seat("scout", "Scout", "Scout")),
        }).Findings);

        Assert.Equal(FactoryNameCheck.KindNotARoleWord, finding.Kind);
        Assert.Equal("boss", finding.SeatId);
        Assert.Contains("'Quillon Varrick', which is not a role word of this factory (its role is Boss)", finding.Text);
    }

    [Fact]
    public void Check_ANameThatIsAnyRoleOfItsFactory_Passes()
    {
        var findings = FactoryNameCheck.Check(new[]
        {
            Factory("WarmForward", "lead", Seat("lead", "Boss", "Boss"), Seat("engineer", "Savings Engineer", "Savings Engineer"),
                Seat("helper", "Boss", "Helper")),
        }).Findings;

        // "Boss" is a role word of this factory (the lead's role), so the helper named Boss passes the role-word rule.
        Assert.Empty(findings);
    }

    [Fact]
    public void Check_ANameNoSeatHasAsItsRole_IsFlagged()
    {
        var finding = Assert.Single(FactoryNameCheck.Check(new[]
        {
            Factory("WarmForward", "boss", Seat("boss", "Boss", "Boss"), Seat("writer", "Scribe", "Writer")),
        }).Findings);

        Assert.Equal("writer", finding.SeatId);
        Assert.Equal(FactoryNameCheck.KindNotARoleWord, finding.Kind);
    }

    [Fact]
    public void Check_EveryFactoryIsChecked_AndFindingsSayWhichFactory()
    {
        var check = FactoryNameCheck.Check(new[]
        {
            Factory("Tallyhand", "boss", Seat("boss", "Boss", "Boss")),
            Factory("WarmForward", "ceo", Seat("ceo", "Boss", "Boss")),
        });

        Assert.Equal(2, check.FactoriesChecked);
        var finding = Assert.Single(check.Findings);
        Assert.Equal("warmforward", finding.Factory);
        Assert.Equal("WarmForward", finding.FactoryTitle);
        Assert.Null(check.PassText);
    }

    [Fact]
    public void Live_LeavesOutArchivedFactories()
    {
        var archived = Factory("Oldmill", "ceo", Seat("ceo", "CEO", "CEO"));
        archived.ArchivedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var live = Factory("Tallyhand", "boss", Seat("boss", "Boss", "Boss"));

        var check = FactoryNameCheck.Check(CcDirector.Gateway.Api.FactoryManagerEndpoints.Live(new[] { archived, live }));

        Assert.Equal(1, check.FactoriesChecked);
        Assert.Equal(0, check.Count);
    }

    // ---- the Factory Manager's briefing --------------------------------------------------------------------------

    [Fact]
    public void Compose_ListsEveryFactoryWithItsBoss_AndSaysWhoHasNone()
    {
        var seed = FactoryManagerSeed.Compose(new[]
        {
            Factory("Tallyhand", "boss", Seat("boss", "Boss", "Boss")),
            Factory("WarmForward", null, Seat("scout", "Scout", "Scout")),
        });

        Assert.StartsWith("You are this account's Factory Manager", seed);
        Assert.Contains("- Tallyhand (factory id tallyhand): boss seat boss, on SOREN_NORTH", seed);
        Assert.Contains("- WarmForward (factory id warmforward): no boss - its problems come to you", seed);
        Assert.Contains("cc-devthrottle run problems", seed);
        Assert.Contains("cc-devthrottle factory name-check", seed);
        Assert.Contains("do not run cc-devthrottle session done", seed);
        Assert.Contains("a factory has a boss, not a CEO, and seats are role words, never a person's name", seed);
    }

    [Fact]
    public void Compose_NoFactories_SaysSo()
        => Assert.Contains("- none are registered yet.", FactoryManagerSeed.Compose(Array.Empty<RegisteredFactoryDto>()));

    [Fact]
    public void ComposeForProblem_IsThePlainStartThenTheProblemInTheBossesWords()
    {
        var f = Factory("WarmForward", null, Seat("scout", "Scout", "Scout"));
        var problem = new CronRunProblemDto
        {
            RunId = "3f1c2b7a-0000-4000-8000-0000000000a1", JobId = "cj_scout", JobName = "WarmForward - Scout - sweep",
            Factory = "warmforward", Kind = CronRunProblems.DidNotReport, KindText = "did not report", Reason = "it ended",
            ShiftText = "morning shift", RaisedAtUtc = new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
            State = CronRunProblemStates.Open,
        };

        var seed = FactoryManagerSeed.ComposeForProblem(new[] { f }, f, problem, TimeZoneInfo.Utc);

        Assert.StartsWith(FactoryManagerSeed.Compose(new[] { f }), seed);
        Assert.Contains("THIS TALK IS ABOUT ONE PROBLEM OF WarmForward (factory id warmforward), which has no boss", seed);
        Assert.EndsWith(FactoryTalkSeed.ProblemBrief(problem, TimeZoneInfo.Utc), seed);
        Assert.Equal("Factory Manager - WarmForward - handle the issue (WarmForward - Scout - sweep)",
            FactoryManagerSeed.ProblemSessionName(f, problem));
    }
}
