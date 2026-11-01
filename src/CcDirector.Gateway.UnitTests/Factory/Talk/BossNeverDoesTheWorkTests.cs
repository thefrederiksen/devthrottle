using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// A BOSS NEVER DOES THE WORK (the owner's ruling, 1 November 2026). One standing paragraph the Gateway gives every Boss
/// on every start, so no factory's seat file has to repeat it. Its words are pinned here once; every other prompt test
/// expects them by name.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class BossNeverDoesTheWorkTests
{
    private readonly ITestOutputHelper _out;
    public BossNeverDoesTheWorkTests(ITestOutputHelper output) => _out = output;

    private const string Morning = FactorySeatOpeningTests.MorningRun;
    private const string Conduct = FactorySeatOpening.BossNeverDoesTheWork;

    private static readonly FactoryGivenBrief Brief = new(3, FactorySeatOpeningTests.BriefOne);

    /// <summary>The shape five real factories are in: bossSeat names one seat, and a seat with the role Boss runs.</summary>
    private static RegisteredFactoryDto WithARunningBossSeat()
    {
        var f = FactorySeatOpeningTests.WarmForward();
        f.Seats.Add(new RegisteredFactorySeatDto { Id = "ceo", Name = "Boss", Role = "Boss", BriefFile = "agents/ceo.yaml", Schedules = { "cj_54663c" }, Computer = "devlinux" });
        return f;
    }

    private static CronJobDto Job(string seat, string factory = "warmforward") => new() { Factory = factory, Seat = seat };

    private static int Occurrences(string text)
        => (text.Length - text.Replace(Conduct, "", StringComparison.Ordinal).Length) / Conduct.Length;

    [Fact]
    public void BossNeverDoesTheWork_IsTheParagraphEveryBossReads()
    {
        Assert.Equal(
            "A BOSS NEVER DOES THE WORK. You are a true boss: picture someone running a company of a thousand employees, who "
            + "does not sit down and do the work themselves. Your main job is to delegate and to make sure the right resources "
            + "are in place to get things done. You may do your own reviews and your own strategy. As soon as anything needs "
            + "carrying out that would take any longer, you do not carry it out: you start a session to do it - cc-devthrottle "
            + "session spawn <repository path> --controlled-by self --name \"<what it does>\" --prompt \"<the whole job>\" - and "
            + "you carry on. That session gets none of your brief, so its prompt must say everything it needs. It comes back to "
            + "you when it is finished: one line tells you to run cc-devthrottle message inbox. Stay available to the owner: "
            + "never run anything long in this session, and never sit watching a session you started, so that you can always "
            + "answer at once. This holds whatever your brief, your schedule or a problem you are handed tells you to do: what "
            + "they ask for gets done, and a session does it. In a scheduled run, start only what can come back before your "
            + "shift ends, because a run still open then is recorded as a problem; end your turn to wait, and do not report the "
            + "run or close this session until the sessions you started have come back.",
            Conduct);
    }

    [Fact]
    public void BossNeverDoesTheWork_NeverSaysTheWordsAFactoryIsFlaggedFor()
    {
        // The word is Boss, never CEO and never head (the owner, 8 October 2026), and the text is plain keyboard characters.
        Assert.DoesNotContain("CEO", Conduct, StringComparison.OrdinalIgnoreCase);
        Assert.All(Conduct, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void WithBossNeverDoesTheWork_AddsTheParagraphLast_ExactlyOnce_AndAnEmptyPromptBecomesTheParagraphAlone()
    {
        var once = FactorySeatOpening.WithBossNeverDoesTheWork("Go.\n\n");

        Assert.Equal("Go.\n\n" + Conduct, once);
        Assert.Equal(once, FactorySeatOpening.WithBossNeverDoesTheWork(once));
        Assert.Equal(Conduct, FactorySeatOpening.WithBossNeverDoesTheWork(""));
    }

    [Fact]
    public void ATalk_EveryKindOfBoss_IsGivenTheConductOnce_AheadOfTheOwnersWord()
    {
        var f = WithARunningBossSeat();
        var defined = new FactoryGivenBrief(3, FactorySeatOpeningTests.BriefOne, FactoryBriefVersionKinds.Definition);

        foreach (var (seat, brief) in new[] { (f.Seats[0], (FactoryGivenBrief?)null), (f.Seats[0], Brief), (f.Seats[0], defined), (f.Seats[2], null) })
        {
            var seed = FactoryTalkSeed.Compose(f, seat, "cj_a721e6", Morning, brief).ReplaceLineEndings("\n");

            Assert.EndsWith("\n\n" + Conduct + "\n\n" + FactorySeatOpening.OwnerWordIsFinal + "\n", seed);
            Assert.Equal(1, Occurrences(seed));
        }
    }

    [Fact]
    public void AHandleTheIssueTalk_OfABoss_IsGivenTheConduct()
    {
        var f = FactorySeatOpeningTests.WarmForward();
        var problem = new CronRunProblemDto
        {
            RunId = "3f1c2b7a-0000-4000-8000-0000000000a1", JobId = "cj_a721e6", JobName = "WarmForward - morning run",
            Factory = "warmforward", Seat = "boss", Sentence = "Did not report.", ShiftText = "morning shift",
            ScheduledUtc = new DateTime(2026, 10, 11, 10, 15, 0, DateTimeKind.Utc),
            RaisedAtUtc = new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
        };

        Assert.Equal(1, Occurrences(FactoryTalkSeed.ComposeForProblem(f, f.Seats[0], "cj_a721e6", Morning, problem, TimeZoneInfo.Utc)));
    }

    [Fact]
    public void AScheduledRun_OfABossTheGatewayOpens_IsGivenTheConductOnce_AheadOfTheOwnersWord()
    {
        var f = WithARunningBossSeat();

        foreach (var seat in new[] { f.Seats[0], f.Seats[2] })
        {
            var run = ScheduledRunOpening.PromptFor(Morning, "morning", "cj_1", new ScheduledRunSeat(f, seat, Brief));

            Assert.EndsWith("\n\n" + Conduct + "\n\n" + FactorySeatOpening.OwnerWordIsFinal, run);
            Assert.Equal(1, Occurrences(run));
        }
    }

    [Fact]
    public void AScheduledRun_OfABossWithNoPublishedBrief_IsStillGivenTheConduct_AfterItsOwnPrompt()
    {
        var run = ScheduledRunOpening.PromptFor(Morning, "morning", "cj_1", seat: null, runsAsABoss: true);

        Assert.Equal(ScheduledRunClosingInstruction.PromptFor(Morning).TrimEnd('\r', '\n') + "\n\n" + Conduct, run);
    }

    [Fact]
    public void RunsAsABoss_IsReadFromTheRegistration_ForTheNamedBossAndForARoleBossSeat_AndNoOtherSeat()
    {
        var f = WithARunningBossSeat();
        RegisteredFactoryDto? Find(string id) => id == f.Factory ? f : null;

        Assert.True(ScheduledRunOpening.RunsAsABoss(Job("boss"), Find));
        Assert.True(ScheduledRunOpening.RunsAsABoss(Job("ceo"), Find));
        Assert.False(ScheduledRunOpening.RunsAsABoss(Job("savings-engineer"), Find));
        Assert.False(ScheduledRunOpening.RunsAsABoss(Job("no-such-seat"), Find));
        Assert.False(ScheduledRunOpening.RunsAsABoss(Job("boss", factory: "no-such-factory"), Find));
        Assert.False(ScheduledRunOpening.RunsAsABoss(new CronJobDto(), Find));
    }

    [Fact]
    public void AnyOtherSeat_IsNeverGivenTheConduct()
    {
        var f = WithARunningBossSeat();

        Assert.Equal(0, Occurrences(FactoryTalkSeed.Compose(f, f.Seats[1], null, null)));
        Assert.Equal(0, Occurrences(ScheduledRunOpening.PromptFor(Morning, "n", "cj_1", new ScheduledRunSeat(f, f.Seats[1], Brief))));
        Assert.Equal(0, Occurrences(ScheduledRunOpening.PromptFor(Morning, "n", "cj_1", seat: null)));
    }

    private static PublishedFactoryDefinition Published()
    {
        var read = FactoryDefinitionCheck.Read(new Dictionary<string, string>
        {
            ["factory.yaml"] = "factory: warmforward\ntitle: WarmForward\nbossSeat: boss\ndefaults:\n  machine: workstation-A\n  agent: ClaudeCode\n",
            ["seats/boss.yaml"] = "seat: boss\nname: Boss\nrole: Boss\nbrief: |\n  Run the factory.\n",
            ["seats/scout.yaml"] = "seat: scout\nname: Scout\nrole: Scout\nbrief: |\n  Find prospects.\n",
        });
        return new PublishedFactoryDefinition(4, read.Definition ?? throw new InvalidOperationException("the fixture does not parse"));
    }

    [Fact]
    public void ASeatStart_OfABoss_ACheckStartsPath_IsGivenTheConduct_AndAPromptThatHasItIsNotToldTwice()
    {
        var published = Published();
        var boss = published.Seat("boss")!;

        var check = FactorySeatStart.Build(published, boss, "check start", "New mail: 1.").Request.PrePrompt!;
        var talk = FactoryTalkSeed.Compose(FactorySeatOpeningTests.WarmForward(), FactorySeatOpeningTests.WarmForward().Seats[0], null, null);
        var again = FactorySeatStart.Build(published, boss, "talk", talk).Request.PrePrompt!;

        Assert.Equal("New mail: 1.\n\n" + Conduct + "\n\n" + FactorySeatOpening.OwnerWordIsFinal, check);
        Assert.Equal(talk, again);
    }

    [Fact]
    public void ASeatStart_OfAnyOtherSeat_IsNotGivenTheConduct()
    {
        var published = Published();

        var start = FactorySeatStart.Build(published, published.Seat("scout")!, "check start", "New mail: 1.").Request.PrePrompt!;

        Assert.Equal(0, Occurrences(start));
    }

    [Fact]
    public void TheKitsBrief_OfABoss_HoldsTheConduct_AndAnotherSeatsDoesNot()
    {
        var published = Published();
        string BriefOf(string seat) => FactoryKitComposer.Compose(new FactoryKitSource(published.Definition.Factory, published.Version,
                new Dictionary<string, string>(), published.Definition, seat, FactoryKits.Seat, DateTime.UnixEpoch,
                Array.Empty<FactoryKitJournalEntry>(), 0, Array.Empty<FactoryDocumentSummary>(), Array.Empty<FactoryKitAttachment>(), 0))
            .Single(f => f.Path == FactoryKitComposer.BriefFile).Text;

        Assert.Equal(1, Occurrences(BriefOf("boss")));
        Assert.Equal(0, Occurrences(BriefOf("scout")));
    }

    [Fact]
    public void Proof_PrintsABossTalksFirstPrompt_AsTheGatewayComposedIt()
    {
        var f = FactorySeatOpeningTests.WarmForward();
        var seed = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", Morning, Brief);

        _out.WriteLine(seed);

        Assert.Contains(Conduct, seed);
    }
}
