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
        Assert.Equal("Name check: nothing flagged", check.HeadingText);
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
        Assert.Equal("Name check: 1 seat flagged", check.HeadingText);
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

        var check = FactoryNameCheck.Check(CcDirector.Gateway.Api.FactoryNameCheckEndpoints.Live(new[] { archived, live }));

        Assert.Equal(1, check.FactoriesChecked);
        Assert.Equal(0, check.Count);
    }

    // ---- what the Fleet Manager is told ---------------------------------------------------------------------------

    [Fact]
    public void FleetManagerProblemBrief_StandsInForTheMissingBoss_ThenTheProblemInTheBossesWords()
    {
        var f = Factory("WarmForward", null, Seat("scout", "Scout", "Scout"));
        var problem = new CronRunProblemDto
        {
            RunId = "3f1c2b7a-0000-4000-8000-0000000000a1", JobId = "cj_scout", JobName = "WarmForward - Scout - sweep",
            Factory = "warmforward", Kind = CronRunProblems.DidNotReport, KindText = "did not report", Reason = "it ended",
            ShiftText = "morning shift", RaisedAtUtc = new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
            State = CronRunProblemStates.Open,
        };

        var brief = FleetManagerProblemBrief.Compose(f, problem, TimeZoneInfo.Utc);

        Assert.StartsWith("WarmForward (factory id warmforward) has no boss", brief);
        Assert.Contains("You stand in for the missing boss", brief);
        // Rule 1 still binds it: the fix goes to a session it starts.
        Assert.Contains("start a session of your own in the factory's folder", brief);
        Assert.EndsWith(FactoryTalkSeed.ProblemBrief(problem, TimeZoneInfo.Utc), brief);
    }
}
