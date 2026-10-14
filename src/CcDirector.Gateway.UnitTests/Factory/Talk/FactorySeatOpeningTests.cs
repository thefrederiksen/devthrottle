using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// How the Gateway opens a seat's session (Factory Seats mission, phase 2c), as wording: who the seat is, and the
/// brief, for a talk and for a scheduled run.
///
/// THE PINS. <see cref="TodaysTalkToTheBoss"/>, <see cref="TodaysTalkToASeat"/> and <see cref="TodaysScheduledRun"/>
/// were written out by the code as it stood BEFORE this phase changed it (commit 4ebd3e9c2), for the fixture below. A
/// seat with no brief on the Gateway - every seat that existed before this phase - must be given exactly those words.
/// A talk's lines end the way the computer ends lines, as they always did, and this file's own lines end the way the
/// checkout ends them, so each pin is compared after both sides are brought to one line end. A scheduled run the
/// Gateway does not open is also compared, as it is, with what the closing instruction alone gives.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactorySeatOpeningTests
{
    private readonly ITestOutputHelper _out;
    public FactorySeatOpeningTests(ITestOutputHelper output) => _out = output;

    internal const string MorningRun =
        "Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.";

    internal const string BriefOne = "character: You run WarmForward. You are careful with money and plain with the owner.\nduties:\n  - read the overnight numbers\n  - tell the owner what changed";

    internal static RegisteredFactoryDto WarmForward(string bossRole = "Boss", string engineerName = "Savings Engineer") => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = "/home/soren/factories/warmforward",
        Computer = "devlinux",
        BossSeat = "boss",
        GoalText = "A cash engine that runs without your time.",
        GoalFile = "GOAL.md",
        Seats =
        {
            new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = bossRole, BriefFile = "agents/boss.yaml", Schedules = { "cj_a721e6" }, Computer = "devlinux" },
            new RegisteredFactorySeatDto { Id = "savings-engineer", Name = engineerName, Role = "Engineer", BriefFile = "agents/savings.yaml", Computer = "devlinux" },
        },
    };

    private static string OneLineEnd(string text) => text.ReplaceLineEndings("\n");

    internal const string TodaysTalkToTheBoss =
        """
You are the boss of WarmForward (factory id: warmforward; your agent id: boss).

The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

Before you say anything, read:
- your brief: /home/soren/factories/warmforward/agents/boss.yaml
- the factory's goal: /home/soren/factories/warmforward/GOAL.md
- the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
- the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
- the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
  It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

You work under the same rules as your scheduled runs.
These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
--- the rules of your scheduled runs ---
Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.
--- end of the rules of your scheduled runs ---
The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
- write the new goal into /home/soren/factories/warmforward/GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
- register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

Before the talk ends you MUST do all three of these:
1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent boss --outcome talked --what "<one line of what was decided>"
2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
   cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
   Then tell the owner the word the factory ends on.
""";

    internal const string TodaysTalkToASeat =
        """
You are Savings Engineer, Engineer of WarmForward (factory id: warmforward; your agent id: savings-engineer).

The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

Before you say anything, read:
- your brief: /home/soren/factories/warmforward/agents/savings.yaml
- the factory's goal: /home/soren/factories/warmforward/GOAL.md
- the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
- the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
- the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
  It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

You work under the same rules as your scheduled runs.
No schedule runs this seat yet, so your brief is the whole of your rules.
The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
- write the new goal into /home/soren/factories/warmforward/GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
- register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

Before the talk ends you MUST do all three of these:
1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent savings-engineer --outcome talked --what "<one line of what was decided>"
2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
   cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
   Then tell the owner the word the factory ends on.
""";

    internal const string TodaysScheduledRun =
        """
Do the morning run: read the overnight numbers and write the summary. When done, run cc-devthrottle session done.

When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
""";

    // ---- a seat with no brief on the Gateway: exactly today's words ---------------------------------------------

    [Fact]
    public void Compose_ATalkToABossWithNoBriefOnTheGateway_IsWordForWordWhatItWasBeforeThisPhase()
    {
        var f = WarmForward();

        var seed = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, brief: null);

        Assert.Equal(OneLineEnd(TodaysTalkToTheBoss) + "\n", OneLineEnd(seed));
        Assert.Equal(seed, FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun));
    }

    [Fact]
    public void Compose_ATalkToASeatWithNoBriefAndNoSchedule_IsWordForWordWhatItWasBeforeThisPhase()
    {
        var f = WarmForward();

        var seed = FactoryTalkSeed.Compose(f, f.Seats[1], scheduleId: null, scheduleSeed: null, brief: null);

        Assert.Equal(OneLineEnd(TodaysTalkToASeat) + "\n", OneLineEnd(seed));
    }

    [Fact]
    public void PromptFor_AScheduledRunTheGatewayDoesNotOpen_IsByteForByteWhatItWasBeforeThisPhase()
    {
        var given = ScheduledRunOpening.PromptFor(MorningRun, "WarmForward - morning run", "cj_a721e6", seat: null);

        Assert.Equal(OneLineEnd(TodaysScheduledRun), given);
        Assert.Equal(ScheduledRunClosingInstruction.PromptFor(MorningRun), given);
    }

    [Theory]
    [InlineData("/mail-desk\n\n")]
    [InlineData("Sort the inbox. When done: cc-devthrottle run result ok")]
    [InlineData("  leading and trailing space  ")]
    public void PromptFor_AScheduledRunTheGatewayDoesNotOpen_IsTheClosingInstructionsTextForEveryPrompt(string prompt)
    {
        Assert.Equal(ScheduledRunClosingInstruction.PromptFor(prompt), ScheduledRunOpening.PromptFor(prompt, "any", "cj_1", seat: null));
    }

    // ---- who the seat is ----------------------------------------------------------------------------------------

    [Fact]
    public void WhoYouAre_TheBoss_IsTheBossOfItsFactory_AndNoRoleIsSaidBesideIt()
    {
        // The boss's role is Boss (the owner, 13 October 2026). A factory registered before that day may still hold
        // another role word; it is not said, because the sentence no longer has a place for one.
        foreach (var role in new[] { "Boss", "CFO", "" })
        {
            var f = WarmForward(bossRole: role);
            Assert.Equal("You are the boss of WarmForward (factory id: warmforward; your agent id: boss).",
                FactorySeatOpening.WhoYouAre(f, f.Seats[0]));
        }
    }

    [Fact]
    public void WhoYouAre_AnyOtherSeat_IsIntroducedByItsRegisteredNameAndRole()
    {
        var f = WarmForward();

        Assert.Equal("You are Savings Engineer, Engineer of WarmForward (factory id: warmforward; your agent id: savings-engineer).",
            FactorySeatOpening.WhoYouAre(f, f.Seats[1]));
    }

    [Fact]
    public void ATalkAndAScheduledRun_OfOneSeat_AreToldWhoTheyAreInTheSameWords()
    {
        var f = WarmForward();
        var brief = new FactoryGivenBrief(3, BriefOne);
        var who = FactorySeatOpening.WhoYouAre(f, f.Seats[1]);

        Assert.StartsWith(who + Environment.NewLine, FactoryTalkSeed.Compose(f, f.Seats[1], null, null, brief));
        Assert.StartsWith(who + "\n", ScheduledRunOpening.PromptFor(MorningRun, "nightly", "cj_1", new ScheduledRunSeat(f, f.Seats[1], brief)));
    }

    // ---- a boss stored with another role word, and a goal change (review 1, finding 2) --------------------------

    [Fact]
    public void Compose_AFactoryWhoseBossIsStoredWithAnotherRoleWord_IsToldToRegisterTheBossAsBoss_SoAGoalChangeCanComplete()
    {
        var f = WarmForward(bossRole: "CFO");
        const string line = "- in that manifest, set the role of the boss seat boss to \"Boss\". It is registered today as \"CFO\", and the "
                            + "boss's role is Boss: a registration that gives the boss any other role word is refused, and the goal would not be registered.";

        // Said to the boss, and to any other seat of that factory: either may change the goal in a talk.
        Assert.Contains(line, FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun));
        Assert.Contains(line, FactoryTalkSeed.Compose(f, f.Seats[1], null, null));
        // It is the ONE thing that differs from a factory whose boss's role is Boss.
        var asBoss = WarmForward();
        Assert.Equal(FactoryTalkSeed.Compose(asBoss, asBoss.Seats[0], "cj_a721e6", MorningRun),
            FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun).Replace(line + Environment.NewLine, ""));
        // A factory with no boss has no boss role to correct.
        asBoss.BossSeat = null;
        Assert.DoesNotContain("set the role of the boss seat", FactoryTalkSeed.Compose(asBoss, asBoss.Seats[1], null, null));
    }

    // ---- a talk of a seat with a published brief ----------------------------------------------------------------

    [Fact]
    public void Compose_ATalkOfASeatWithAPublishedBrief_IsGivenTheBriefsTextAndVersion_InPlaceOfTheFilePath()
    {
        var f = WarmForward();
        var seed = OneLineEnd(FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, new FactoryGivenBrief(3, BriefOne)));

        Assert.Contains("- your brief: version 3, given to you in full below this list. The Gateway holds it; it is not a file in the factory's folder.\n", seed);
        Assert.Contains("--- your brief, version 3 ---\n" + BriefOne + "\n--- end of your brief, version 3 ---\n", seed);
        // The path of the brief file is gone: nothing tells this talk to read a file for its brief.
        Assert.DoesNotContain("agents/boss.yaml", seed);
        Assert.DoesNotContain("- your brief: /", seed);
    }

    [Fact]
    public void Compose_ATalkOfASeatWithAPublishedBrief_DiffersFromTodaysWordsOnlyByTheBriefAndItsVersion()
    {
        var f = WarmForward();
        var withBrief = OneLineEnd(FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, new FactoryGivenBrief(3, BriefOne)));

        // Take the three things the brief adds back out, and what is left is today's talk.
        var withoutIt = withBrief
            .Replace("- your brief: version 3, given to you in full below this list. The Gateway holds it; it is not a file in the factory's folder.\n",
                "- your brief: /home/soren/factories/warmforward/agents/boss.yaml\n")
            .Replace("--- your brief, version 3 ---\n" + BriefOne + "\n--- end of your brief, version 3 ---\n\n", "")
            .Replace(" --agent boss --version 3 --outcome talked", " --agent boss --outcome talked")
            .Replace("   Keep --version 3 exactly as written: it records which version of your brief this talk was given.\n", "");

        Assert.Equal(OneLineEnd(TodaysTalkToTheBoss) + "\n", withoutIt);
    }

    [Fact]
    public void Compose_ATalkOfASeatWithAPublishedBrief_IsToldToRecordItsVersionOnTheTalkedLine()
    {
        var f = WarmForward();
        var seed = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, new FactoryGivenBrief(7, BriefOne));

        Assert.Contains("cc-devthrottle factory record --factory warmforward --agent boss --version 7 --outcome talked --what \"<one line of what was decided>\"", seed);
        // A seat with no brief on the Gateway has no version to record.
        Assert.DoesNotContain("--version", FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun));
    }

    [Fact]
    public void ComposeForProblem_ATalkOpenedByHandleTheIssue_IsGivenTheBriefToo()
    {
        var f = WarmForward();
        var brief = new FactoryGivenBrief(2, BriefOne);
        var problem = new CronRunProblemDto
        {
            RunId = "3f1c2b7a-0000-4000-8000-0000000000a1", JobId = "cj_a721e6", JobName = "WarmForward - morning run",
            Factory = "warmforward", Seat = "boss", Sentence = "Did not report.", ShiftText = "morning shift",
            ScheduledUtc = new DateTime(2026, 10, 11, 10, 15, 0, DateTimeKind.Utc),
            RaisedAtUtc = new DateTime(2026, 10, 11, 10, 30, 0, DateTimeKind.Utc),
        };

        var seed = FactoryTalkSeed.ComposeForProblem(f, f.Seats[0], "cj_a721e6", MorningRun, problem, TimeZoneInfo.Utc, brief);

        Assert.StartsWith(FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, brief), seed);
        Assert.Contains("--- your brief, version 2 ---", seed);
    }

    // ---- a seat renamed in the registration ---------------------------------------------------------------------

    [Fact]
    public void ASeatRenamedInTheRegistration_TheNextTalkAndTheNextRun_SayTheNewNameAndNothingElseChanges()
    {
        var brief = new FactoryGivenBrief(3, BriefOne);
        var before = WarmForward(engineerName: "Savings Engineer");
        var after = WarmForward(engineerName: "Cost Cutter");

        var talkBefore = FactoryTalkSeed.Compose(before, before.Seats[1], null, null, brief);
        var talkAfter = FactoryTalkSeed.Compose(after, after.Seats[1], null, null, brief);
        var runBefore = ScheduledRunOpening.PromptFor(MorningRun, "nightly", "cj_1", new ScheduledRunSeat(before, before.Seats[1], brief));
        var runAfter = ScheduledRunOpening.PromptFor(MorningRun, "nightly", "cj_1", new ScheduledRunSeat(after, after.Seats[1], brief));

        Assert.StartsWith("You are Cost Cutter, Engineer of WarmForward", talkAfter);
        Assert.StartsWith("You are Cost Cutter, Engineer of WarmForward", runAfter);
        Assert.DoesNotContain("Savings Engineer", talkAfter);
        Assert.DoesNotContain("Savings Engineer", runAfter);
        // The name is the ONLY thing that moved: put the old name back and each text is what it was.
        Assert.Equal(talkBefore, talkAfter.Replace("Cost Cutter", "Savings Engineer"));
        Assert.Equal(runBefore, runAfter.Replace("Cost Cutter", "Savings Engineer"));
        // And the session a talk opens is named for the new name.
        Assert.Equal("WarmForward - Cost Cutter - talk with the owner", FactoryTalkSeed.SessionName(after, after.Seats[1]));
    }

    // ---- a scheduled run of a seat with a published brief -------------------------------------------------------

    [Fact]
    public void PromptFor_AScheduledRunOfASeatWithAPublishedBrief_IsOpenedAheadOfTheSchedulesOwnPrompt()
    {
        var f = WarmForward();

        var given = ScheduledRunOpening.PromptFor(MorningRun, "WarmForward - morning run", "cj_a721e6",
            new ScheduledRunSeat(f, f.Seats[0], new FactoryGivenBrief(3, BriefOne)));

        Assert.Equal(
            "You are the boss of WarmForward (factory id: warmforward; your agent id: boss).\n\n"
            + "This is one of your scheduled runs: the schedule \"WarmForward - morning run\" (cj_a721e6). Your brief is given "
            + "to you here in full, as the Gateway holds it (version 3); it is not a file in the factory's folder. With it "
            + "comes what this schedule asks of this run.\n\n"
            + "--- your brief, version 3 ---\n" + BriefOne + "\n--- end of your brief, version 3 ---\n\n"
            + MorningRun + "\n\n"
            + ScheduledRunClosingInstruction.Text,
            given);
    }

    [Fact]
    public void PromptFor_ASchedulesPromptThatIsACommand_StaysFirst_AndTheOpeningFollowsIt()
    {
        // A prompt that begins with a slash is a command the agent runs only when it is the first thing it is given.
        var f = WarmForward();
        var seat = new ScheduledRunSeat(f, f.Seats[0], new FactoryGivenBrief(3, BriefOne));

        var given = ScheduledRunOpening.PromptFor("/mail-desk\n\n", "Mail Desk", "cj_mail", seat);

        Assert.Equal("/mail-desk\n\n" + ScheduledRunOpening.Opening("Mail Desk", "cj_mail", seat) + "\n\n" + ScheduledRunClosingInstruction.Text, given);
    }

    [Fact]
    public void PromptFor_TheClosingInstruction_IsDecidedOnTheSchedulesOwnPrompt_NeverOnTheBrief()
    {
        var f = WarmForward();
        // The brief names the command; the schedule's prompt does not. The run is still told to report.
        var briefNamesIt = new ScheduledRunSeat(f, f.Seats[0], new FactoryGivenBrief(1, "In a talk, never run cc-devthrottle run result."));
        Assert.EndsWith("\n\n" + ScheduledRunClosingInstruction.Text, ScheduledRunOpening.PromptFor(MorningRun, "n", "cj_1", briefNamesIt));

        // The schedule's prompt names the command: the owner's own wording wins, and nothing is added after it.
        const string ownersWording = "Sort the inbox. When done: cc-devthrottle run result ok";
        var plain = new ScheduledRunSeat(f, f.Seats[0], new FactoryGivenBrief(1, BriefOne));
        Assert.EndsWith("\n\n" + ownersWording, ScheduledRunOpening.PromptFor(ownersWording, "n", "cj_1", plain));
    }

    [Fact]
    public void BriefBlock_GivesTheTextWholeAndUnchanged_DroppingOnlyTheLineEndsAtItsVeryEnd()
    {
        const string text = "  indented first line\n\nmiddle\ttab\n  trailing spaces stay  \n\r\n";

        var block = FactorySeatOpening.BriefBlock(new FactoryGivenBrief(12, text), "\n");

        Assert.Equal("--- your brief, version 12 ---\n  indented first line\n\nmiddle\ttab\n  trailing spaces stay  \n--- end of your brief, version 12 ---", block);
    }

    // ---- the proof: what the Gateway composed, printed for docs/qa/factory-seats/phase-2c ------------------------

    [Fact]
    public void Proof_PrintsTheFourOpeningsAsTheGatewayComposedThem()
    {
        var f = WarmForward();
        var brief = new FactoryGivenBrief(3, BriefOne);
        var talkWithout = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun);
        var talkWith = FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", MorningRun, brief);
        var runWithout = ScheduledRunOpening.PromptFor(MorningRun, "WarmForward - morning run", "cj_a721e6", seat: null);
        var runWith = ScheduledRunOpening.PromptFor(MorningRun, "WarmForward - morning run", "cj_a721e6", new ScheduledRunSeat(f, f.Seats[0], brief));

        foreach (var (title, text) in new[]
                 {
                     ("TALK, SEAT WITH NO BRIEF ON THE GATEWAY", talkWithout), ("TALK, SEAT WITH A PUBLISHED BRIEF", talkWith),
                     ("SCHEDULED RUN, SEAT WITH NO BRIEF ON THE GATEWAY", runWithout), ("SCHEDULED RUN, SEAT WITH A PUBLISHED BRIEF", runWith),
                 })
        {
            _out.WriteLine($"=====BEGIN {title}=====");
            _out.WriteLine(text.TrimEnd('\r', '\n'));
            _out.WriteLine($"=====END {title}=====");
        }

        Assert.NotEqual(talkWithout, talkWith);
        Assert.NotEqual(runWithout, runWith);
    }
}
