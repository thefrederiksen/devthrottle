using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Talk;

/// <summary>
/// THE FIRST PROMPTS, WHOLE (Factory Definition mission, phase 5; phase 4 QA report, finding 2). A defined factory's talk
/// and run are told where the seat's kit is and that a goal change is a draft of the definition - never a goal file, a
/// registration or a folder. An undefined factory's prompts are BYTE-IDENTICAL to what they were before phase 5: the
/// texts below for the undefined cases were produced by origin/main's own code (commit ac6e92ba6) from these inputs,
/// and are compared whole.
/// </summary>
[Trait("Category", "FactoryTalk")]
public sealed class FactorySeatPromptsTests
{
    internal static RegisteredFactoryDto WarmForward() => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        Computer = "SOREN_NORTH",
        BossSeat = "nora-hale",
        GoalText = "A cash engine that runs without your time.",
        GoalFile = "GOAL.md",
        GoalApprovedOn = "2026-10-04",
        Seats =
        {
            new RegisteredFactorySeatDto { Id = "nora-hale", Name = "Boss", Role = "Boss", BriefFile = "agents/ceo.yaml", Computer = "SOREN_NORTH" },
            new RegisteredFactorySeatDto { Id = "savings-engineer", Name = "Savings Engineer", Role = "Savings Engineer", BriefFile = "agents/savings.yaml", Computer = "DEVLINUX" },
        },
    };

    internal const string Seed = "Do the morning run. When done, run cc-devthrottle session done.";
    internal static readonly FactoryGivenBrief BriefVersion = new(2, "Be careful with money.\n");
    internal static readonly FactoryGivenBrief DefinitionVersion = new(3, "Be careful with money.\n", FactoryBriefVersionKinds.Definition);

    internal static string TalkText(FactoryGivenBrief? brief)
    {
        var f = WarmForward();
        return FactoryTalkSeed.Compose(f, f.Seats[0], "cj_a721e6", Seed, brief).ReplaceLineEndings("\n");
    }

    internal static string RunText(FactoryGivenBrief? brief)
    {
        var f = WarmForward();
        return ScheduledRunOpening.PromptFor(Seed, "WarmForward - Boss", "cj_a721e6",
            brief is null ? null : new ScheduledRunSeat(f, f.Seats[0], brief)).ReplaceLineEndings("\n");
    }

    // ---------- undefined: exactly as before ----------

    [Fact]
    public void Talk_UNDEFINED_FACTORY_WithNoBriefOnTheGateway_IsTheTextItWasBefore()
        => Assert.Equal(UndefinedTalkNoBrief.ReplaceLineEndings("\n"), TalkText(null));

    [Fact]
    public void Talk_UNDEFINED_FACTORY_WithABriefVersion_IsTheTextItWasBefore()
        => Assert.Equal(UndefinedTalkBriefVersion.ReplaceLineEndings("\n"), TalkText(BriefVersion));

    [Fact]
    public void Run_UNDEFINED_FACTORY_WithNoSeat_IsTheTextItWasBefore()
        => Assert.Equal(UndefinedRunNoSeat.ReplaceLineEndings("\n"), RunText(null));

    [Fact]
    public void Run_UNDEFINED_FACTORY_WithABriefVersion_IsTheTextItWasBefore()
        => Assert.Equal(UndefinedRunBriefVersion.ReplaceLineEndings("\n"), RunText(BriefVersion));

    // ---------- defined: the kit, and a goal change through the definition ----------

    [Fact]
    public void Talk_DefinedFactory_IsThisText()
        => Assert.Equal(DefinedTalk.ReplaceLineEndings("\n"), TalkText(DefinitionVersion));

    [Fact]
    public void Run_DefinedFactory_IsThisText()
        => Assert.Equal(DefinedRun.ReplaceLineEndings("\n"), RunText(DefinitionVersion));

    [Fact]
    public void Talk_DefinedFactory_NeverNamesAGoalFileARegistrationOrAFolder()
    {
        var text = TalkText(DefinitionVersion);
        Assert.DoesNotContain("GOAL.md", text);
        Assert.DoesNotContain("factory register", text);
        Assert.DoesNotContain(@"D:\ReposFred", text);
        Assert.DoesNotContain("manifest", text);
    }

    // ---------- the texts ----------

    private const string UndefinedTalkNoBrief = """
        You are the boss of WarmForward (factory id: warmforward; your agent id: nora-hale).

        The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

        Before you say anything, read:
        - your brief: D:\ReposFred\cc-consult\ideas\warmforward-factory\agents/ceo.yaml
        - the factory's goal: D:\ReposFred\cc-consult\ideas\warmforward-factory\GOAL.md
        - the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
        - the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
        - the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
          It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

        You work under the same rules as your scheduled runs.
        These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
        --- the rules of your scheduled runs ---
        Do the morning run. When done, run cc-devthrottle session done.
        --- end of the rules of your scheduled runs ---
        The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

        Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

        In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
        - write the new goal into D:\ReposFred\cc-consult\ideas\warmforward-factory\GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
        - register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

        Before the talk ends you MUST do all three of these:
        1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent nora-hale --outcome talked --what "<one line of what was decided>"
        2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
        3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
           cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
           Then tell the owner the word the factory ends on.

        """;

    private const string UndefinedTalkBriefVersion = """
        You are the boss of WarmForward (factory id: warmforward; your agent id: nora-hale).

        The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

        Before you say anything, read:
        - your brief: version 2, given to you in full below this list. The Gateway holds it; it is not a file in the factory's folder.
        - the factory's goal: D:\ReposFred\cc-consult\ideas\warmforward-factory\GOAL.md
        - the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
        - the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
        - the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
          It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

        --- your brief, version 2 ---
        Be careful with money.
        --- end of your brief, version 2 ---

        You work under the same rules as your scheduled runs.
        These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
        --- the rules of your scheduled runs ---
        Do the morning run. When done, run cc-devthrottle session done.
        --- end of the rules of your scheduled runs ---
        The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

        Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

        In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
        - write the new goal into D:\ReposFred\cc-consult\ideas\warmforward-factory\GOAL.md, with today's date and this session's id (cc-devthrottle session whoami) beside it;
        - register the factory again so the page shows it: cc-devthrottle factory register --manifest <the factory's manifest>, with goalFile GOAL.md and goalApprovedOn set to today. If you do not have the manifest, write one from cc-devthrottle factory list --json, keeping only the keys the manifest takes (docs/cli-reference.md, Factory registry and goal number).

        Before the talk ends you MUST do all three of these:
        1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent nora-hale --version 2 --outcome talked --what "<one line of what was decided>"
           Keep --version 2 exactly as written: it records which version of your brief this talk was given.
        2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
        3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
           cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
           Then tell the owner the word the factory ends on.

        """;

    private const string UndefinedRunNoSeat = """
        Do the morning run. When done, run cc-devthrottle session done.

        When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
        """;

    private const string UndefinedRunBriefVersion = """
        You are the boss of WarmForward (factory id: warmforward; your agent id: nora-hale).

        This is one of your scheduled runs: the schedule "WarmForward - Boss" (cj_a721e6). Your brief is given to you here in full, as the Gateway holds it (version 2); it is not a file in the factory's folder. With it comes what this schedule asks of this run.

        --- your brief, version 2 ---
        Be careful with money.
        --- end of your brief, version 2 ---

        Do the morning run. When done, run cc-devthrottle session done.

        When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
        """;

    private const string DefinedTalk = """
        You are the boss of WarmForward (factory id: warmforward; your agent id: nora-hale).

        The owner is here now and is talking with you. This is the owner's own session, opened from the Factories screen's Talk button. It is not one of your scheduled runs, and nobody is waiting on a report from it - the owner is reading your answers as you write them.

        Before you say anything, read:
        - your brief: definition version 3, given to you in full below this list. It is part of the factory's definition, which the Gateway holds; it is not a file in the factory's folder.
        - Your kit is in the folder the environment variable CC_FACTORY_KIT_DIR names: brief.md in it holds this same brief, and grants.md what you are allowed to do, with its limits. Read grants.md before you act on anything it covers.
        - the factory's goal: given to you in full below this list, as the factory's definition holds it.
        - the factory's memory: cc-devthrottle factory memory list, then cc-devthrottle factory memory get <name> for each note that matters
        - the factory's recent activity: cc-devthrottle factory activity --factory warmforward -n 30
        - the factory's status as the owner sees it on the Factories screen: cc-devthrottle factory status --factory warmforward
          It prints the same word the owner sees (FAILING, NEEDS YOU, PAUSED, RUNNING or TALK ONLY) and every failing and waiting item with its row id. Say that word to the owner when you open.

        --- your brief, definition version 3 ---
        Be careful with money.
        --- end of your brief, definition version 3 ---

        --- the factory's goal ---
        A cash engine that runs without your time.
        --- end of the factory's goal ---

        You work under the same rules as your scheduled runs.
        These are the rules of your scheduled runs, exactly as your schedule (cj_a721e6) starts them:
        --- the rules of your scheduled runs ---
        Do the morning run. When done, run cc-devthrottle session done.
        --- end of the rules of your scheduled runs ---
        The steps in those rules that exist only because a scheduled run is unattended do NOT apply in a talk: do not rename this session to a dated name, do not send the morning email, and do not run cc-devthrottle session done. The owner closes this session.

        Open by telling the owner, briefly, what has happened since the owner last talked with you (the newest activity line with the outcome talked, if there is one) and what you need from the owner.

        In a talk the owner and you may change anything about the factory, the goal included - the owner is here to approve it. If the goal changes:
        - save a draft of the definition with the new goal (cc-devthrottle factory definition save); the owner publishes it.

        Before the talk ends you MUST do all three of these:
        1. Record one activity line: cc-devthrottle factory record --factory warmforward --agent nora-hale --version "definition v3" --outcome talked --what "<one line of what was decided>"
           Keep --version "definition v3" exactly as written: it records which version of the factory's definition your brief came from in this talk.
        2. Write what was decided into the factory's memory: cc-devthrottle factory memory set <name> "<what was decided>"
        3. Run cc-devthrottle factory status --factory warmforward again. For every failing or waiting item that is now resolved, mark it handled with its evidence - and only if it is resolved:
           cc-devthrottle factory record --factory warmforward --agent <the item's seat> --outcome done --corrects <the item's row id> --what "Handled: <the evidence>"
           Then tell the owner the word the factory ends on.

        """;

    private const string DefinedRun = """
        You are the boss of WarmForward (factory id: warmforward; your agent id: nora-hale).

        This is one of your scheduled runs: the schedule "WarmForward - Boss" (cj_a721e6). Your brief is given to you here in full, as the Gateway holds it (definition version 3, part of the factory's definition); it is not a file in the factory's folder. With it comes what this schedule asks of this run.

        --- your brief, definition version 3 ---
        Be careful with money.
        --- end of your brief, definition version 3 ---

        Your kit is in the folder the environment variable CC_FACTORY_KIT_DIR names: brief.md in it holds this same brief, and grants.md what you are allowed to do, with its limits. Read grants.md before you act on anything it covers.

        The factory's goal is part of its definition. If it should change, save a draft of the definition with the new goal (cc-devthrottle factory definition save); the owner publishes it.

        Do the morning run. When done, run cc-devthrottle session done.

        When you have finished, the last thing you do is report how this scheduled run went: run `cc-devthrottle run result ok`, or `cc-devthrottle run result problem "<one line why>"` if something went wrong.
        """;
}
