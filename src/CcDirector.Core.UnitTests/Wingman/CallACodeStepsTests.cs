using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Call A's code steps (contract v4, design v2): each step fires on its own case, answers its word with a reason a
/// person can read, and the first to fire decides. The cases are the shapes the phase 1 measurement's code_first.py
/// was written against, rewritten here as synthetic replies - no text from a real session is in this repository.
/// </summary>
public sealed class CallACodeStepsTests
{
    private static readonly IReadOnlyList<CallAToolUse> NoTools = Array.Empty<CallAToolUse>();

    private static TurnVerdictPackage Stop(string? reply, params string[] screen) => new()
    {
        Kind = TurnVerdictPackageKind.AgentReply,
        AgentKind = "ClaudeCode",
        LatestReply = reply,
        ScreenRows = screen.Length > 0 ? screen : new[] { "  " + (reply ?? "").Split('\n')[^1], "", "> " },
    };

    /// <summary>A permission prompt, drawn the way Claude Code draws one.</summary>
    private static readonly string[] PermissionPromptScreen =
    {
        " Bash command",
        "   rm -rf build/out",
        " Do you want to proceed?",
        "   1. Yes",
        "   2. No, and tell Claude what to do differently (esc)",
        "",
    };

    // ================================================================= step 1: the picker

    [Fact]
    public void Picker_APermissionPromptOnTheScreen_IsNeedsYou_WithTheSignsInTheReason()
    {
        var decision = CallACodeSteps.Decide(Stop("I will clean the build folder.", PermissionPromptScreen), NoTools);

        Assert.NotNull(decision);
        Assert.Equal(CallACodeSteps.PickerStep, decision!.Step);
        Assert.Equal("needs-you", decision.Word);
        Assert.Contains("proceed-prompt", decision.Reason);
    }

    [Fact]
    public void Picker_AnEnterToConfirmFooter_IsNeedsYou()
    {
        var screen = new[] { " Which model?", "   1. Fast", "   2. Thorough", "", " Enter to confirm - Esc to cancel" };

        var decision = CallACodeSteps.Decide(Stop("Pick one.", screen), NoTools);

        Assert.Equal(CallACodeSteps.PickerStep, decision!.Step);
    }

    [Fact]
    public void Picker_ComesFirst_EvenWhenTheReplyAlsoAsksAQuestion()
    {
        var decision = CallACodeSteps.Decide(Stop("Shall I delete it?", PermissionPromptScreen), NoTools);

        Assert.Equal(CallACodeSteps.PickerStep, decision!.Step);
    }

    // ================================================================= step 2: the agent's own verdict

    [Fact]
    public void AgentVerdict_ACcDismissBlockSayingNeedsHuman_IsNeedsYou_WithItsReason()
    {
        var reply = "The sweep ran and flagged two rows.\n\nCC-DISMISS\nverdict: needs-human\nreason: two rows need a person to confirm the delete";

        var decision = CallACodeSteps.Decide(Stop(reply), NoTools);

        Assert.Equal(CallACodeSteps.AgentVerdictStep, decision!.Step);
        Assert.Equal("needs-you", decision.Word);
        Assert.Contains("two rows need a person", decision.Reason);
    }

    [Fact]
    public void AgentVerdict_ACcDismissBlockSayingDone_DecidesNothing()
    {
        var reply = "All clean.\n\nCC-DISMISS\nverdict: done\nreason: nothing found";

        Assert.Null(CallACodeSteps.Decide(Stop(reply), NoTools));
    }

    // ================================================================= step 3: a real question

    [Theory]
    [InlineData("The tests pass and the branch is pushed.\n\nWant me to also open the pull request?")]
    [InlineData("Two ways to do this. Which one do you prefer?")]
    [InlineData("Done with the parser. Shall I go ahead with the second half? I have not started it.")]
    [InlineData("**Should I merge it now?**")]
    public void Question_AReplyThatAsksThePerson_IsNeedsYou_QuotingTheQuestion(string reply)
    {
        var decision = CallACodeSteps.Decide(Stop(reply), NoTools);

        Assert.NotNull(decision);
        Assert.Equal(CallACodeSteps.QuestionStep, decision!.Step);
        Assert.Equal("needs-you", decision.Word);
        Assert.StartsWith("the reply asks a question: \"", decision.Reason);
        Assert.Contains("?", decision.Reason);
    }

    [Fact]
    public void Question_NearTheTopOfALongReply_StillCounts_WhenItHasScrolledOffTheScreen()
    {
        // The known risk of a screen-only Call A: a question that scrolled off. The reply is read whole.
        var reply = "Before I go on - do you want the old rows kept?\n\n" + string.Join("\n", Enumerable.Range(1, 80).Select(i => $"Line {i} of the report."));
        var screen = new[] { "  Line 79 of the report.", "  Line 80 of the report.", "", "> " };

        var decision = CallACodeSteps.Decide(Stop(reply, screen), NoTools);

        Assert.Equal(CallACodeSteps.QuestionStep, decision!.Step);
        Assert.Contains("do you want the old rows kept?", decision.Reason);
    }

    [Theory]
    [InlineData("## Can we see it? Yes.\nThe page renders.")]
    [InlineData("- **Speed** - is it fast? It is.\nAll measured.")]
    [InlineData("1. Does it build? It builds.\nAll green.")]
    [InlineData("I rewrote the \"What is Codex?\" explainer.")]
    [InlineData("I answered *what do the rules need?* in the doc.")]
    [InlineData("**Do I need anything? No blockers.**")]
    [InlineData("Is the cache warm? yes, it was primed at start.")]
    public void Question_AHeadingListItemQuotedOrSelfAnsweredQuestion_DoesNotCount(string reply)
    {
        Assert.Empty(CallACodeSteps.AskedQuestions(reply));
        Assert.Null(CallACodeSteps.Decide(Stop(reply), NoTools));
    }

    [Fact]
    public void Question_AnOverLongQuestion_IsQuotedOnlyInPart()
    {
        var reply = "Should I " + string.Join(" ", Enumerable.Repeat("really", 80)) + " merge it?";

        var decision = CallACodeSteps.Decide(Stop(reply), NoTools);

        Assert.True(decision!.Reason.Length < CallACodeSteps.MaxQuotedQuestionChars + 60, decision.Reason);
        Assert.EndsWith("...\"", decision.Reason);
    }

    // ================================================================= step 4: a way back

    [Theory]
    [InlineData("ScheduleWakeup", "{\"delaySeconds\":1200}", "a ScheduleWakeup call")]
    [InlineData("Monitor", "{\"until\":\"build done\"}", "a Monitor call")]
    [InlineData("Bash", "{\"command\":\"cc-devthrottle session spawn D:/repo --name worker\"}", "a session spawn")]
    [InlineData("Bash", "{\"command\":\"dotnet test\", \"run_in_background\": true}", "a background run")]
    public void WayBack_TheAgentSetItselfAWayBack_IsCarryingOn(string tool, string input, string named)
    {
        var decision = CallACodeSteps.Decide(Stop("Started it; I will pick it up when it lands."), new[] { new CallAToolUse(tool, input) });

        Assert.NotNull(decision);
        Assert.Equal(CallACodeSteps.WayBackStep, decision!.Step);
        Assert.Equal("carrying-on", decision.Word);
        Assert.Equal("the agent set itself a way back in its last turn: " + named, decision.Reason);
    }

    [Fact]
    public void WayBack_AnOrdinaryToolUse_DecidesNothing()
    {
        var tools = new[] { new CallAToolUse("Bash", "{\"command\":\"dotnet test\",\"run_in_background\":false}"), new CallAToolUse("Read", "{\"file_path\":\"a.cs\"}") };

        Assert.Null(CallACodeSteps.Decide(Stop("The tests pass."), tools));
    }

    [Fact]
    public void Order_AQuestionAndAWayBack_IsNeedsYou()
    {
        var decision = CallACodeSteps.Decide(
            Stop("I scheduled a check in twenty minutes. Do you want me to merge it when it is green?"),
            new[] { new CallAToolUse("ScheduleWakeup", "{\"delaySeconds\":1200}") });

        Assert.Equal(CallACodeSteps.QuestionStep, decision!.Step);
        Assert.Equal("needs-you", decision.Word);
    }

    [Fact]
    public void WayBack_OnAFailureWithNoReply_DoesNotFire_BecauseAFailureIsNeverCalm()
    {
        var failure = new TurnVerdictPackage
        {
            Kind = TurnVerdictPackageKind.TerminalFailure,
            AgentKind = "ClaudeCode",
            FailureText = "API Error: 529 overloaded",
            ScreenRows = new[] { "API Error: 529 overloaded", "" },
        };

        Assert.Null(CallACodeSteps.Decide(failure, new[] { new CallAToolUse("ScheduleWakeup", "{}") }));
    }

    // ================================================================= what is deliberately NOT a step

    [Theory]
    [InlineData("The design is written up. Your call on which option ships.")]
    [InlineData("Everything is staged and waiting on you.")]
    [InlineData("Let me know if the numbers look wrong.")]
    public void ThePhraseList_IsNotAStep_SoTheseReachTheModel(string reply)
    {
        // The owner: the phrase list comes in only after it is checked against his own labels.
        Assert.Null(CallACodeSteps.Decide(Stop(reply), NoTools));
    }

    [Fact]
    public void APlainReport_WithNoToolUse_ReachesTheModel()
    {
        Assert.Null(CallACodeSteps.Decide(Stop("The retention sweep now deletes rows older than seven days. Tests are green."), NoTools));
    }

    [Fact]
    public void NoReplyAndNoScreen_ReachesTheModel()
    {
        var package = new TurnVerdictPackage { Kind = TurnVerdictPackageKind.AgentReply, AgentKind = "ClaudeCode" };

        Assert.Null(CallACodeSteps.Decide(package, NoTools));
    }

    [Fact]
    public void Steps_AreInTheirOrder_ThenTheModel()
    {
        Assert.Equal(new[] { "picker", "agent-verdict", "question", "way-back", "model" }, CallACodeSteps.Steps);
    }
}
