using System.Text;
using System.Text.Json;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The turn-verdict contract's mechanical validation - CONTRACT v3, the five-field reading - proved against
/// three synthetic stops.
///
/// WHAT THIS SUITE LOOKS LIKE NOW, AND WHY. Contract v3 (the owner's ruling of 18 September 2026) cut seven
/// of the twelve fields the judge used to be asked for: spoken, summary, evidence, risk, confidence,
/// answerVia and finishedKind. This file used to assert the refusals those fields carried - that an answer
/// with no receipt was thrown away whole, that a missing confidence word refused the answer, that a finished
/// verdict without its kind was refused - and every one of those tests is GONE, because the rule it proved
/// is gone. Keeping them passing would have meant keeping the rules.
///
/// WHAT IS KEPT, EVERY LINE OF IT: the rules that decide what BYTES reach a live session. The options'
/// shape, the menu's shape, one option is not a choice, at most one recommended, no line ending inside a
/// send, the one-tap confirm whose question must be on the screen. Those guard a button that ACTS. Nothing
/// about them changed in v3 and nothing about their tests changed either.
///
/// SYNTHETIC ON PURPOSE. These are twins of three real shapes - a report, a question with a picker on
/// screen, and a turn that ended on a failure with no reply - written from scratch here. Not a byte of a
/// real session is in this repository, which is public, so a specimen is built rather than captured.
///
/// This suite is in Core.UnitTests and NOT in Core.Tests. Core.Tests is parked: it does not run in the
/// default gate, so a contract test living there would be a test that nothing runs at commit time, which is
/// the same as no test at all for the fortnight before somebody remembers to pass -Parked.
/// </summary>
public sealed class TurnVerdictContractTests
{
    private const string Model = "devthrottle/wingman";
    private static readonly DateTime ObservedAt = new(2026, 9, 14, 18, 30, 0, DateTimeKind.Utc);

    // The characters an agent draws a picker's box with, built from their code points rather than typed, so
    // this source file stays plain keyboard text. They are not decoration: the one-tap confirm's question is
    // still held to the screen through the same normalisation, and a specimen without a drawn box would
    // prove nothing about it.
    private static readonly char BoxVertical = (char)0x2502;
    private static readonly char BoxHorizontal = (char)0x2500;
    private static readonly char BoxTopLeft = (char)0x256D;
    private static readonly char BoxTopRight = (char)0x256E;
    private static readonly char BoxBottomLeft = (char)0x2570;
    private static readonly char BoxBottomRight = (char)0x256F;

    private const int BoxWidth = 44;

    private static string BoxRow(string text)
        => BoxVertical + " " + text.PadRight(BoxWidth) + BoxVertical;

    private static string BoxEdge(char left, char right)
        => left + new string(BoxHorizontal, BoxWidth + 1) + right;

    /// <summary>A picker drawn on the alternate screen: the question exists only here, inside the box.</summary>
    private static string[] MenuScreenRows => new[]
    {
        BoxEdge(BoxTopLeft, BoxTopRight),
        BoxRow("Push the deploy guard to main?"),
        BoxRow(" 1. Yes, push it now"),
        BoxRow(" 2. No, leave it on the branch"),
        BoxEdge(BoxBottomLeft, BoxBottomRight),
    };

    // ================================================================= the three specimen stops

    /// <summary>A report: the agent finished and is telling the lead so. Nothing is needed.</summary>
    private static TurnVerdictPackage ReportStop() => new()
    {
        Kind = TurnVerdictPackageKind.AgentReply,
        ConversationAvailable = true,
        SessionTitle = "devthrottle - tidy the retention timer",
        AgentKind = "ClaudeCode",
        FirstUserPrompt = "Delete the rows the retention window has passed, and add a test.",
        LatestReply =
            "The retention sweep now deletes rows older than seven days and the test covers it.\n"
            + "Nothing is needed from you - I will stop here.",
        ScreenRows = new[]
        {
            "  The retention sweep now deletes rows older than seven days and the test covers it.",
            "",
            "> ",
        },
        ScreenHash = "SPECIMEN-REPORT",
        RecentTurns = "You: add the retention sweep\n\nAgent: done, tests are green",
    };

    /// <summary>A question: a picker is drawn on the screen and typed words cannot answer it.</summary>
    private static TurnVerdictPackage MenuStop() => new()
    {
        Kind = TurnVerdictPackageKind.AgentReply,
        ConversationAvailable = true,
        SessionTitle = "devthrottle - the deploy guard",
        AgentKind = "ClaudeCode",
        FirstUserPrompt = "Stop a deploy going out on a red commit.",
        LatestReply = "I need your decision before I can carry on.",
        ScreenRows = MenuScreenRows,
        ScreenHash = "SPECIMEN-MENU",
        IsAlternateScreen = true,
        RecentTurns = "You: add the guard\n\nAgent: written, and it needs a decision before it lands",
    };

    /// <summary>A failure: no reply at all, and the screen says why the turn ended.</summary>
    private static TurnVerdictPackage FailureStop() => new()
    {
        Kind = TurnVerdictPackageKind.TerminalFailure,
        ConversationAvailable = true,
        SessionTitle = "devthrottle - the nightly sweep",
        AgentKind = "Codex",
        FirstUserPrompt = "Run the nightly sweep and report what it found.",
        FailureText = "Error: connection reset by peer while reading the response",
        ScreenRows = new[]
        {
            "Error: connection reset by peer while reading the response",
            "",
        },
        ScreenHash = "SPECIMEN-FAILURE",
        RecentTurns = "You: run the sweep\n\nAgent: starting now",
    };

    private static readonly object[] TwoOptions =
    {
        new { key = "1. Push it now", send = "1", recommended = false, note = "Deploys to production; it cannot be taken back." },
        new { key = "2. Leave it on the branch", send = "2", recommended = true, note = "Nothing ships; the guard waits for a green commit." },
    };

    // ================================================================= the three specimens validate

    [Fact]
    public void ParseAndValidate_ReportStop_Accepted()
    {
        var answer = Answer(state: TurnVerdictStates.FinishedReport, label: "Retention sweep done, nothing needed");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictStates.FinishedReport, result.State);
        Assert.Equal(TurnVerdictVocabulary.Finished, result.Verdict);
        Assert.Equal("report", result.FinishedKind);
        Assert.Equal("Retention sweep done, nothing needed", result.Label);
        Assert.Equal("agent-reply", result.PackageKind);
        Assert.Equal(TurnVerdictContract.Version, result.ContractVersion);
        Assert.Equal(Model, result.Model);
        Assert.Equal(ObservedAt, result.TurnEndObservedAtUtc);
        Assert.Equal("SPECIMEN-REPORT", result.ScreenHash);
        Assert.NotEqual("", result.VerdictId);
    }

    [Fact]
    public void ParseAndValidate_MenuStop_AcceptedWithAMenuAndKeysDerivedFromIt()
    {
        var answer = Answer(
            state: TurnVerdictStates.NeedsYou,
            label: "Decide whether the deploy guard goes to main",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: TwoOptions);

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictStates.NeedsYou, result.State);
        // DERIVED, not asked (contract v3): a menu on the record means keys, and no menu means a reply.
        Assert.Equal("keys", result.AnswerVia);
        Assert.NotNull(result.Menu);
        Assert.Equal("Push the deploy guard to main?", result.Menu!.Question);
        Assert.Equal(2, result.Options.Count);
        Assert.True(result.Options[1].Recommended);
    }

    [Fact]
    public void ParseAndValidate_NoMenu_DerivesAReply()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Retention sweep done"), ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("reply", result.AnswerVia);
        Assert.Null(result.Menu);
    }

    [Fact]
    public void ParseAndValidate_FailureStop_AcceptedWhenTheStateIsNotCalm()
    {
        var answer = Answer(state: TurnVerdictStates.StuckRecoverable, label: "Connection reset; the sweep can be retried");

        var result = TurnVerdictContract.ParseAndValidate(answer, FailureStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictStates.StuckRecoverable, result.State);
        Assert.Equal("terminal-failure", result.PackageKind);
    }

    // ================================================================= the state word

    [Theory]
    [InlineData("needs-you", "needed-you", null)]
    [InlineData("finished-done", "finished", "done")]
    [InlineData("finished-report", "finished", "report")]
    [InlineData("carrying-on", "continues-alone", null)]
    [InlineData("stuck-recoverable", "stuck-recoverable", null)]
    [InlineData("stuck-needs-person", "stuck-needs-person", null)]
    [InlineData("cannot-tell", "cannot-tell", null)]
    public void ParseAndValidate_EveryStateWord_IsAcceptedAndStoredAsItsPair(string state, string verdict, string? kind)
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: state, label: "Something happened here"), ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(kind, result.FinishedKind);
        // And the record reads back as the word the judge answered with: the two spellings are one fact.
        Assert.Equal(state, result.State);
    }

    [Theory]
    [InlineData("finished")]          // the STORED spelling, which is not a state word
    [InlineData("continues-alone")]   // ditto
    [InlineData("not-a-turn-end")]    // the detector's seventh word, which the Wingman never answers
    [InlineData("needs-attention")]
    [InlineData("")]
    public void ParseAndValidate_AWordThatIsNotOneOfTheSeven_Rejected(string state)
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: state, label: "Retention sweep done"), ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("state word", result.FailureReason);
        Assert.Equal("", result.Verdict);
    }

    [Theory]
    [InlineData("finished-done")]
    [InlineData("finished-report")]
    [InlineData("carrying-on")]
    public void ParseAndValidate_TerminalFailurePackageWithACalmState_Rejected(string state)
    {
        // A turn that ended on a failure with NO REPLY cannot mean the session finished or is carrying on.
        // This is the one rule that leans hardest away from quiet, and v3 did not touch it.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: state, label: "The sweep finished"), FailureStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("is calm", result.FailureReason);
    }

    // ================================================================= the label

    [Fact]
    public void ParseAndValidate_MissingLabel_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "", omit: new[] { "label" }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'label'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_EmptyLabel_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: ""), ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no label", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongLabel_IsCutAtTheLastWholeWord()
    {
        var words = string.Join(" ", Enumerable.Repeat("retention", 40));
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: words), ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.True(result.Label.Length <= TurnVerdictContract.MaxLabelChars);
        Assert.EndsWith("retention", result.Label);
    }

    // ================================================================= what the agent recommends

    [Fact]
    public void ParseAndValidate_AgentRecommendsNullOrAString_BothAccepted()
    {
        var withNothing = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done"), ReportStop(), Model, ObservedAt);
        var withOne = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done",
                agentRecommends: "Turn the daily schedule off - the list is complete."),
            ReportStop(), Model, ObservedAt);

        Assert.False(withNothing.Failed, withNothing.FailureReason);
        Assert.Null(withNothing.AgentRecommends);
        Assert.False(withOne.Failed, withOne.FailureReason);
        Assert.Equal("Turn the daily schedule off - the list is complete.", withOne.AgentRecommends);
    }

    [Fact]
    public void ParseAndValidate_AgentRecommendsOfTheWrongType_Rejected()
    {
        // Reading an object here as "no recommendation" would store a silence the judge did not answer with.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done", agentRecommendsRaw: new { text = "do it" }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("agentRecommends", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongAgentRecommends_IsCutRatherThanRefused()
    {
        // It is a sentence in a panel: a reader who can see most of it is better served than one shown nothing.
        var words = string.Join(" ", Enumerable.Repeat("schedule", 120));
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done", agentRecommends: words),
            ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.NotNull(result.AgentRecommends);
        Assert.True(result.AgentRecommends!.Length <= TurnVerdictContract.MaxAgentRecommendsChars);
    }

    // ================================================================= THE CUT FIELDS ARE NO LONGER ASKED FOR
    //
    // These are the tests that say what v3 DID. Each one is an answer the old contract refused and the new one
    // accepts, and each names the refusal it replaces.

    [Fact]
    public void ParseAndValidate_NoReceipt_IsAccepted_BecauseTheVerbatimRuleIsGone()
    {
        // The rule this replaces refused about one reading in seven on the live fleet, including a complete and
        // correct briefing, because the agent's reply carries Markdown and the judge quoted through it.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedReport, label: "Retention sweep done, nothing needed"),
            ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("", result.Evidence);
    }

    [Fact]
    public void ParseAndValidate_NoConfidenceNoRiskNoSummaryNoSpoken_AllAccepted()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the deploy guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: TwoOptions),
            MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("", result.Confidence);
        Assert.Equal("", result.Risk);
        Assert.Equal("", result.Summary);
        Assert.Equal("", result.Spoken);
        // The narration fills Summary and Spoken when the reading completes - see TurnVerdictService.
        Assert.Null(result.Narration);
    }

    [Fact]
    public void ParseAndValidate_AnExtraMemberFromTheOldContract_IsIgnoredRatherThanRefused()
    {
        // A model that has seen the old shape may still answer with some of it. Those members are not read and
        // they do not refuse the answer: every one of them is a field this contract no longer has an opinion on.
        var raw = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = TurnVerdictStates.FinishedReport,
            ["label"] = "Retention sweep done, nothing needed",
            ["options"] = Array.Empty<object>(),
            ["menu"] = null,
            ["agentRecommends"] = null,
            ["confidence"] = "ambiguous",
            ["risk"] = "spends-money",
            ["evidence"] = "a sentence that is nowhere in the reply",
            ["finishedKind"] = "done",
            ["answerVia"] = "keys",
        });

        var result = TurnVerdictContract.ParseAndValidate(raw, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        // The state word decides, and the stale members decide nothing: finishedKind "done" beside a
        // finished-report state does NOT make the record a "done" one.
        Assert.Equal("report", result.FinishedKind);
        Assert.Equal("reply", result.AnswerVia);   // derived from the absent menu, never from the stale word
        Assert.Equal("", result.Risk);
        Assert.Equal("", result.Confidence);
        Assert.Equal("", result.Evidence);
    }

    [Fact]
    public void ParseAndValidate_RefusedAnswer_CarriesNoProseAtAll()
    {
        // From v3 there is no prose on this call to salvage. A refusal is a reading that could not be read.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: "not-a-state", label: "Something", spokenFromTheOldContract: "words the old contract kept"),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Equal("", result.Spoken);
        Assert.Equal("", result.Summary);
        Assert.Equal("", result.Label);
    }

    // ================================================================= the shape

    [Fact]
    public void ParseAndValidate_StateOfTheWrongType_Rejected()
    {
        var raw = RawAnswer("{\"state\": 7, \"label\": \"x\", \"options\": []}");

        var result = TurnVerdictContract.ParseAndValidate(raw, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'state'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NotJson_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate("I think it finished", ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not valid JSON", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NothingAtAll_RejectedRatherThanTreatedAsCalm()
    {
        var result = TurnVerdictContract.ParseAndValidate("", ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Equal("", result.Verdict);
    }

    [Fact]
    public void ParseAndValidate_AnswerWrappedInFencesAndProse_Accepted()
    {
        var json = Answer(state: TurnVerdictStates.FinishedDone, label: "Retention sweep done");

        var fenced = TurnVerdictContract.ParseAndValidate("```json\n" + json + "\n```", ReportStop(), Model, ObservedAt);
        var prefaced = TurnVerdictContract.ParseAndValidate("Here is my answer: " + json, ReportStop(), Model, ObservedAt);

        Assert.False(fenced.Failed, fenced.FailureReason);
        Assert.False(prefaced.Failed, prefaced.FailureReason);
    }

    [Fact]
    public void IsReadableJsonObject_AbsorbsFencesAndPreamble_ExactlyAsValidationDoes()
    {
        var json = Answer(state: TurnVerdictStates.FinishedDone, label: "Retention sweep done");

        Assert.True(TurnVerdictContract.IsReadableJsonObject(json));
        Assert.True(TurnVerdictContract.IsReadableJsonObject("```json\n" + json + "\n```"));
        Assert.True(TurnVerdictContract.IsReadableJsonObject("Here is my answer: " + json));
        Assert.False(TurnVerdictContract.IsReadableJsonObject("I think it finished"));
        Assert.False(TurnVerdictContract.IsReadableJsonObject(""));
    }

    // ================================================================= THE OPTIONS AND THE MENU
    //
    // Everything from here to the end of the section guards what BYTES reach a live session. v3 changed none
    // of it, and none of these tests changed either.

    [Fact]
    public void ParseAndValidate_ExactlyOneOption_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: new object[]
                {
                    new { key = "1. Yes", send = "1", recommended = false, note = "Deploys to production." },
                }),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("one option is not a choice", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MoreThanOneRecommended_RejectedRatherThanQuietlyRepaired()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: new object[]
                {
                    new { key = "1. Yes", send = "1", recommended = true, note = "Deploys to production." },
                    new { key = "2. No", send = "2", recommended = true, note = "Nothing ships." },
                }),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("recommended", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_RecommendedAsAString_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: new object[]
                {
                    new { key = "1. Yes", send = "1", recommended = "true", note = "Deploys to production." },
                    new { key = "2. No", send = "2", recommended = false, note = "Nothing ships." },
                }),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("recommended", result.FailureReason);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("send")]
    [InlineData("note")]
    public void ParseAndValidate_OptionMissingAnyOfItsParts_Rejected(string missing)
    {
        var first = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = "1. Yes",
            ["send"] = "1",
            ["note"] = "Deploys to production.",
            ["recommended"] = false,
        };
        first.Remove(missing);

        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: new object[]
                {
                    first,
                    new { key = "2. No", send = "2", recommended = false, note = "Nothing ships." },
                }),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains(missing, result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_SendOfNothingButSpaces_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: new object[]
                {
                    new { key = "1. Yes", send = "   ", recommended = false, note = "Deploys to production." },
                    new { key = "2. No", send = "2", recommended = false, note = "Nothing ships." },
                }),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ParseAndValidate_SendIsNeverTrimmed()
    {
        // The send is typed into a live session verbatim, so what looks like tidying is a change to what is typed.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Answer the question",
                options: new object[]
                {
                    new { key = "Yes", send = " yes please ", recommended = false, note = "Confirms the change." },
                    new { key = "No", send = "no", recommended = false, note = "Leaves it alone." },
                }),
            ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(" yes please ", result.Options[0].Send);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    public void ParseAndValidate_OptionCarryingALineEnding_Rejected(string ending)
    {
        // A reply has one Enter appended by the route, and a picker is confirmed by the menu's submit. A line
        // ending inside a send is either sent twice or confirms before the person has finished choosing.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Answer the question",
                options: new object[]
                {
                    new { key = "Yes", send = "yes" + ending, recommended = false, note = "Confirms the change." },
                    new { key = "No", send = "no", recommended = false, note = "Leaves it alone." },
                }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("carriage return", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongOptionKey_RejectedRatherThanCut()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Answer the question",
                options: new object[]
                {
                    new { key = new string('k', TurnVerdictContract.MaxOptionKeyChars + 1), send = "1", recommended = false, note = "Does the thing." },
                    new { key = "2. No", send = "2", recommended = false, note = "Nothing ships." },
                }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("a different action", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongOptionNote_RejectedRatherThanCut()
    {
        // A shortened consequence is a different promise, and an option is pressed once and cannot be asked
        // what the rest of it said.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Answer the question",
                options: new object[]
                {
                    new { key = "1. Yes", send = "1", recommended = false, note = new string('n', TurnVerdictContract.MaxOptionNoteChars + 1) },
                    new { key = "2. No", send = "2", recommended = false, note = "Nothing ships." },
                }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("a different promise", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsAbsentAltogether_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done", omit: new[] { "options" }),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'options'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsExplicitlyNull_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done", optionsNull: true),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("options", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsPresentAndEmpty_IsTheOneWayToSayThereAreNone()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.FinishedDone, label: "Sweep done"), ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Empty(result.Options);
    }

    [Fact]
    public void ParseAndValidate_MenuOfTheWrongType_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            RawAnswer("{\"state\": \"needs-you\", \"label\": \"x\", \"options\": [], \"menu\": [1,2]}"),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'menu'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MenuWithNoQuestion_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { selectionMode = "single", submit = "" }, options: TwoOptions),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("question", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MenuWithNoSelectionMode_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", submit = "" }, options: TwoOptions),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("selectionMode", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MultipleSelectWhoseSubmitIsNotACarriageReturn_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Pick the sweeps to run",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "multiple", submit = "" },
                options: TwoOptions),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("submit", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MultipleSelectWithACarriageReturnSubmit_Accepted()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Pick the sweeps to run",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "multiple", submit = "\r" },
                options: TwoOptions),
            MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("multiple", result.Menu!.SelectionMode);
        Assert.Equal("\r", result.Menu.Submit);
    }

    [Fact]
    public void ParseAndValidate_KeysWithAMenuAndNothingToSelectAndNothingToConfirm_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
                options: Array.Empty<object>()),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to confirm", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NoOptionsAndSelectionModeMultiple_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Send the reply already typed",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "multiple", submit = "\r" },
                options: Array.Empty<object>()),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to pick", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_AlreadyTypedReply_AcceptedWithNoOptions()
    {
        // THE ONE SHAPE in which a menu may carry no options: the person has already typed their reply and the
        // only action left is to send it. The question must BE the parked text, found on the screen.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Send the reply already typed",
                menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "\r" },
                options: Array.Empty<object>()),
            MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Empty(result.Options);
        Assert.Equal("\r", result.Menu!.Submit);
    }

    [Fact]
    public void ParseAndValidate_OneTapConfirmWhoseQuestionIsNotOnTheScreen_Rejected()
    {
        // Without this leg a judge could offer a one-tap Enter under any question at all, and the owner would
        // confirm a send he cannot see.
        var result = TurnVerdictContract.ParseAndValidate(
            Answer(state: TurnVerdictStates.NeedsYou, label: "Send the reply already typed",
                menu: new { question = "Send the reply already typed: ship it", selectionMode = "single", submit = "\r" },
                options: Array.Empty<object>()),
            MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not on the screen", result.FailureReason);
    }

    // ================================================================= the vocabulary pin

    [Fact]
    public void Vocabulary_IsTheSixWordsPlusTheDetectorsSeventh()
    {
        // UNCHANGED BY v3, AND DELIBERATELY SO. These six are one half of a pair with the labelling tool in the
        // internal repository (tools/turn-log/verdicts.py, VERSION 2), and every stored record carries one.
        // v3 renamed what the JUDGE is asked for, not what is stored, so a corpus graded on these words still
        // means what it meant.
        Assert.Equal(
            new[] { "needed-you", "finished", "continues-alone", "stuck-recoverable", "stuck-needs-person", "cannot-tell" },
            TurnVerdictVocabulary.WingmanVerdicts);

        Assert.Equal(
            new[]
            {
                "needed-you", "finished", "stuck-recoverable", "stuck-needs-person",
                "continues-alone", "not-a-turn-end", "cannot-tell",
            },
            TurnVerdictVocabulary.AllVerdicts);

        Assert.Equal(2, TurnVerdictVocabulary.Version);
        Assert.DoesNotContain(TurnVerdictVocabulary.NotATurnEnd, TurnVerdictVocabulary.WingmanVerdicts);
        Assert.All(TurnVerdictVocabulary.AllVerdicts,
            word => Assert.True(TurnVerdictVocabulary.VerdictMeanings.ContainsKey(word), $"no meaning for '{word}'"));
        Assert.Equal(TurnVerdictVocabulary.AllVerdicts.Count, TurnVerdictVocabulary.VerdictMeanings.Count);
        Assert.Equal(new[] { "single", "multiple" }, TurnVerdictVocabulary.SelectionModes);
        Assert.Equal(new[] { "finished", "continues-alone" }, TurnVerdictVocabulary.Calm);
    }

    [Fact]
    public void States_AreTheSevenWords_AndAgreeWithTheStoredSpellingInBothDirections()
    {
        Assert.Equal(
            new[]
            {
                "needs-you", "finished-done", "finished-report", "carrying-on",
                "stuck-recoverable", "stuck-needs-person", "cannot-tell",
            },
            TurnVerdictVocabulary.WingmanStates);

        Assert.All(TurnVerdictVocabulary.WingmanStates,
            word => Assert.True(TurnVerdictVocabulary.StateMeanings.ContainsKey(word), $"no meaning for '{word}'"));

        // THE PIN. TurnVerdictStates lives in the Contracts assembly and writes the six verdict words as
        // literals because it may not reference Core. This is what stops the two spellings drifting apart.
        Assert.Null(TurnVerdictVocabulary.Tests.StatesMatchVerdictWords());
    }

    [Fact]
    public void StateOf_AFinishedRecordWithNoKind_ReadsAsAReport()
    {
        // Every finished record stored before 15 September 2026 carries no kind. Reading it as a report offers
        // the reader the body rather than telling him there is nothing there.
        Assert.Equal("finished-report", TurnVerdictVocabulary.StateOf("finished", null));
        Assert.Equal("finished-done", TurnVerdictVocabulary.StateOf("finished", "done"));
        // A refused record has no state at all, and neither has a word nothing recognises.
        Assert.Equal("", TurnVerdictVocabulary.StateOf("", null));
        Assert.Equal("", TurnVerdictVocabulary.StateOf("not-a-turn-end", null));
    }

    // ================================================================= the prompt is ONE file

    [Fact]
    public void EmbeddedPrompt_EqualsTheResourceFile_ByteForByte()
    {
        var path = ResolvePromptFile();
        var onDisk = File.ReadAllBytes(path);
        var embedded = Encoding.UTF8.GetBytes(TurnVerdictContract.PromptTemplate);

        Assert.True(
            onDisk.SequenceEqual(embedded),
            $"The embedded prompt and {TurnVerdictContract.PromptResourcePath} differ. They are the SAME "
            + "bytes the grading tool reads off disk, so a difference means the product ships one prompt "
            + "and the grading measures another.");

        // The pin is only true on every machine if the file's line endings cannot be rewritten by a checkout.
        // .gitattributes holds it to line feeds; this says so out loud.
        Assert.DoesNotContain((byte)'\r', onDisk);
    }

    [Fact]
    public void ContractVersion_NamesThePromptFileByItsSHAPE()
    {
        // THE MAJOR PART NAMES THE FILE: it is the SHAPE - the JSON a verdict must be, and what validation
        // accepts. The grading tool reads that file off disk by path, so renaming it moves the grader's ground
        // truth, and nothing but a shape change may do that. v3 IS a shape change: twelve fields became five.
        Assert.Equal("v3", TurnVerdictContract.Version);

        var major = TurnVerdictContract.Version.Split('.')[0];
        Assert.Equal("v3", major);
        Assert.EndsWith($"/turn-verdict-{major}.txt", TurnVerdictContract.PromptResourcePath);
        Assert.EndsWith($".turn-verdict-{major}.txt", TurnVerdictContract.PromptResourceName);
    }

    [Fact]
    public void Prompt_AsksForTheFiveFieldsAndForNothingThatWasCut()
    {
        var prompt = TurnVerdictContract.PromptTemplate;

        foreach (var asked in new[] { "\"state\"", "\"label\"", "\"agentRecommends\"", "\"menu\"", "\"options\"" })
            Assert.Contains(asked, prompt);

        // THE SEVEN CUT FIELDS ARE NOT ASKED FOR ANYWHERE. A prompt that still asked for one would produce a
        // longer answer on a model where a quarter of calls already timed out, for a field nothing reads.
        foreach (var cut in new[] { "\"spoken\"", "\"summary\"", "\"evidence\"", "\"risk\"", "\"confidence\"", "\"answerVia\"", "\"finishedKind\"" })
            Assert.DoesNotContain(cut, prompt);

        // And every one of the seven state words is defined for the judge, not just listed in the shape.
        foreach (var state in TurnVerdictVocabulary.WingmanStates)
            Assert.Contains($"- \"{state}\":", prompt);
    }

    [Fact]
    public void BuildPrompt_FillsEveryPlaceholderAndLeavesNone()
    {
        var prompt = TurnVerdictContract.BuildPrompt(MenuStop());

        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("Shape: agent-reply", prompt);
        Assert.Contains("A stored conversation was available: yes", prompt);
        Assert.Contains("devthrottle - the deploy guard", prompt);
        Assert.Contains("I need your decision before I can carry on.", prompt);
        // Absent facts are named rather than left blank, so "we did not look" and "there was nothing" never
        // look the same to the judge.
        Assert.Contains("Why the turn was called ended: (none)", prompt);
        Assert.Contains("Next wake-up the session announced: (none)", prompt);
    }

    [Fact]
    public void BuildPrompt_OwnedSessions_AreCountedForAnOwner_AndOwningNoneIsSaidInWords()
    {
        var owner = TurnVerdictContract.BuildPrompt(ReportStop() with { OwnedSessions = new OwnedSessionCounts(Working: 3, Stopped: 2, NeedYou: 1) });
        var solo = TurnVerdictContract.BuildPrompt(ReportStop());

        Assert.Contains("Sessions this session owns: 3 working, 2 stopped, 1 need a person", owner);
        Assert.Contains("Sessions this session owns: none - this session owns no other session", solo);
        Assert.Contains("When its reply waits on its own working sessions and asks a person", owner);
    }

    [Fact]
    public void BuildPrompt_ScreenTextThatLooksLikeAPlaceholder_IsNotSubstituted()
    {
        // The screen is written by somebody else. A row that reads like a placeholder is inert text, because
        // the template is filled in one pass and a filled value is never rescanned.
        var package = MenuStop() with { ScreenRows = new[] { "{{SESSION_TITLE}}", "{{RECENT_TURNS}}" } };

        var prompt = TurnVerdictContract.BuildPrompt(package);

        Assert.Contains("{{SESSION_TITLE}}", prompt);
        Assert.Contains("{{RECENT_TURNS}}", prompt);
    }

    // ================================================================= the salvaged narration decision

    [Fact]
    public void SalvageNarrationDecision_ReadsTheStateAndTheMenu_AndDerivesHowThePersonAnswers()
    {
        var raw = Answer(state: TurnVerdictStates.NeedsYou, label: "Decide on the guard",
            agentRecommends: "Leave it on the branch until the commit is green.",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: TwoOptions);

        var decision = TurnVerdictContract.SalvageNarrationDecision(raw);

        Assert.NotNull(decision);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, decision!.Verdict);
        Assert.Equal("keys", decision.AnswerVia);
        Assert.Equal("Push the deploy guard to main?", decision.Menu!.Question);
        Assert.Equal(2, decision.Options.Count);
        Assert.Equal("Leave it on the branch until the commit is green.", decision.AgentRecommends);
    }

    [Fact]
    public void SalvageNarrationDecision_AnswerThatIsNotAJsonObject_IsNothing()
    {
        Assert.Null(TurnVerdictContract.SalvageNarrationDecision("I think it finished"));
        Assert.Null(TurnVerdictContract.SalvageNarrationDecision(""));
        Assert.Null(TurnVerdictContract.SalvageNarrationDecision("[1,2,3]"));
    }

    private static string ResolvePromptFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, TurnVerdictContract.PromptResourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"Could not find {TurnVerdictContract.PromptResourcePath} by walking up from {AppContext.BaseDirectory}.");
    }

    // ================================================================= building a canned answer

    /// <summary>One answer written out by hand, for a shape the typed helper cannot express - a member of the
    /// wrong JSON kind, where the helper would serialise a well-formed one.</summary>
    private static string RawAnswer(string json) => json;

    /// <summary>
    /// One canned model answer as JSON, in the v3 shape. Every field is written explicitly so a test says
    /// exactly what the judge is pretending to have said; <paramref name="omit"/> REMOVES a member entirely,
    /// which is a different thing from writing null into it - the contract treats absent and null differently
    /// and so must these fixtures.
    /// </summary>
    private static string Answer(
        string state,
        string label,
        string? agentRecommends = null,
        object? menu = null,
        object[]? options = null,
        object? optionsRaw = null,
        bool optionsNull = false,
        object? agentRecommendsRaw = null,
        string[]? omit = null,
        string? spokenFromTheOldContract = null)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = state,
            ["label"] = label,
            ["options"] = options ?? Array.Empty<object>(),
            ["menu"] = menu,
            ["agentRecommends"] = agentRecommends,
        };
        // optionsRaw writes whatever is given where the list belongs - an object, a number, a string - so a
        // malformed shape can be tested. optionsNull writes an explicit JSON null there, which is NOT the same
        // as leaving the field out.
        if (optionsRaw is not null) fields["options"] = optionsRaw;
        if (optionsNull) fields["options"] = null;
        if (agentRecommendsRaw is not null) fields["agentRecommends"] = agentRecommendsRaw;
        // A member from the OLD contract, for the tests that prove a stale field changes nothing.
        if (spokenFromTheOldContract is not null) fields["spoken"] = spokenFromTheOldContract;
        foreach (var name in omit ?? Array.Empty<string>()) fields.Remove(name);

        return JsonSerializer.Serialize(fields);
    }
}
