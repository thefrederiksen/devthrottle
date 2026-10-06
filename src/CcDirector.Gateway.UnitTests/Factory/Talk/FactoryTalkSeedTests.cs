using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// The first prompt of a talk (Factories screen mission, phase C). Each test is one of the owner's rulings about what
/// a talk must do, read off the composed text: seated as the agent, under its scheduled run's rules without the
/// unattended steps, opening with what happened, and leaving the talked line and the memory note before it ends.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactoryTalkSeedTests
{
    private const string MorningRun =
        "You are Nora Hale, CEO of WarmForward. Rename this session to \"WarmForward - Nora Hale - <date>\". "
        + "Read agents/ceo.yaml and do the morning run. Email the owner the morning summary. When done, run cc-devthrottle session done.";

    private static RegisteredFactoryDto WarmForward(string? goalFile = "GOAL.md", string? goalText = "A cash engine that runs without your time.") => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        Computer = "SOREN_NORTH",
        CeoSeat = "nora-hale",
        GoalText = goalText,
        GoalFile = goalFile,
        Seats =
        {
            new RegisteredFactorySeatDto { Id = "nora-hale", Name = "Nora Hale", Role = "CEO", BriefFile = "agents/ceo.yaml", Schedules = { "cj_a721e6" }, Computer = "SOREN_NORTH" },
        },
    };

    private static RegisteredFactorySeatDto Nora(RegisteredFactoryDto f) => f.Seats[0];

    [Fact]
    public void SessionName_IsFactoryThenSeatThenTalkWithTheOwner()
    {
        var f = WarmForward();
        Assert.Equal("WarmForward - Nora Hale - talk with the owner", FactoryTalkSeed.SessionName(f, Nora(f)));
    }

    [Fact]
    public void Compose_SeatsTheAgent_ByNameRoleAndFactory_AndSaysTheOwnerIsHere()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.StartsWith("You are Nora Hale, CEO of WarmForward (factory id: warmforward; your agent id: nora-hale).", seed);
        Assert.Contains("The owner is here now and is talking with you. This is the owner's own session", seed);
    }

    [Fact]
    public void Compose_NamesWhatToReadFirst_TheBriefGoalMemoryAndRecentActivity()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains(@"- your brief: D:\ReposFred\cc-consult\ideas\warmforward-factory\agents/ceo.yaml", seed);
        Assert.Contains(@"- the factory's goal: D:\ReposFred\cc-consult\ideas\warmforward-factory\GOAL.md", seed);
        Assert.Contains("cc-devthrottle factory memory list", seed);
        Assert.Contains("cc-devthrottle factory activity --factory warmforward -n 30", seed);
    }

    [Fact]
    public void Compose_CarriesTheScheduledRunsRules_AndSaysTheUnattendedStepsDoNotApply()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains("You work under the same rules as your scheduled runs.", seed);
        Assert.Contains("--- the rules of your scheduled runs ---\n" + MorningRun + "\n--- end of the rules of your scheduled runs ---",
            seed.Replace("\r\n", "\n"));
        Assert.Contains("do not rename this session to a dated name, do not send the morning email, and do not run "
                        + "cc-devthrottle session done", seed);
    }

    [Fact]
    public void Compose_OpensWithWhatHappenedSinceTheLastTalk_AndWhatItNeeds()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains("Open by telling the owner, briefly, what has happened since the owner last talked with you", seed);
        Assert.Contains("and what you need from the owner.", seed);
    }

    [Fact]
    public void Compose_BeforeTheTalkEnds_RequiresTheTalkedLineAndTheMemoryNote()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains("Before the talk ends you MUST do both of these:", seed);
        Assert.Contains("cc-devthrottle factory record --factory warmforward --agent nora-hale --outcome talked --what \"<one line of what was decided>\"", seed);
        Assert.Contains("cc-devthrottle factory memory set <name> \"<what was decided>\"", seed);
    }

    [Fact]
    public void Compose_AGoalChange_IsWrittenToTheGoalFileWithDateAndSessionId_AndReRegistered()
    {
        var f = WarmForward(goalFile: "docs/GOAL.md");
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains("the goal included", seed);
        Assert.Contains(@"write the new goal into D:\ReposFred\cc-consult\ideas\warmforward-factory\docs/GOAL.md, with today's date and this session's id (cc-devthrottle session whoami)", seed);
        Assert.Contains("cc-devthrottle factory register --manifest", seed);
        Assert.Contains("with goalFile docs/GOAL.md and goalApprovedOn set to today", seed);
    }

    [Fact]
    public void Compose_AFactoryWithNoGoalFile_IsToldToWriteGoalMdInItsFolder()
    {
        var f = WarmForward(goalFile: null, goalText: null);
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains(@"warmforward-factory\GOAL.md (the registry holds no goal for this factory yet, so there may be none)", seed);
    }

    [Fact]
    public void Compose_ASeatNoScheduleRuns_SaysItsBriefIsTheWholeOfItsRules()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), scheduleId: null, scheduleSeed: null);

        Assert.Contains("No schedule runs this seat yet, so your brief is the whole of your rules.", seed);
        Assert.DoesNotContain("--- the rules of your scheduled runs ---", seed);
    }

    [Fact]
    public void Compose_AScheduleThatIsNotOnTheGateway_IsNamedAsMissing_NeverInvented()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_gone", scheduleSeed: null);

        Assert.Contains("The schedule that runs you (cj_gone) is not on the Gateway, so its rules could not be read here.", seed);
        Assert.DoesNotContain("--- the rules of your scheduled runs ---", seed);
    }

    [Fact]
    public void Compose_AFactoryOnLinux_JoinsPathsWithItsOwnSeparator()
    {
        var f = WarmForward();
        f.Folder = "/home/soren/factories/warmforward/";
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.Contains("- your brief: /home/soren/factories/warmforward/agents/ceo.yaml", seed);
    }

    [Fact]
    public void Compose_IsPlainAscii()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, Nora(f), "cj_a721e6", MorningRun);

        Assert.All(seed, c => Assert.True(c < 128, $"character U+{(int)c:X4} is not ASCII"));
    }
}
