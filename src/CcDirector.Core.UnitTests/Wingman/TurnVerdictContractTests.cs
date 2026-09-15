using System.Text;
using System.Text.Json;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The turn-verdict contract's mechanical validation, proved against three synthetic stops.
///
/// SYNTHETIC ON PURPOSE. These are twins of three real shapes - a report, a question with a picker on
/// screen, and a turn that ended on a failure with no reply - written from scratch here. Not a byte of
/// a real session is in this repository, which is public, so a specimen is built rather than captured.
///
/// This suite is in Core.UnitTests and NOT in Core.Tests. Core.Tests is parked: it does not run in the
/// default gate, so a contract test living there would be a test that nothing runs at commit time,
/// which is the same as no test at all for the fortnight before somebody remembers to pass -Parked.
/// </summary>
public sealed class TurnVerdictContractTests
{
    private const string Model = "devthrottle/wingman";
    private static readonly DateTime ObservedAt = new(2026, 9, 14, 18, 30, 0, DateTimeKind.Utc);

    // The characters an agent draws a picker's box with, built from their code points rather than
    // typed, so this source file stays plain keyboard text. They are not decoration here: a question
    // that only exists inside a drawn box is exactly the case the receipt check has to see through,
    // and a specimen without them would prove nothing about it.
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

    // ================================================================= the three specimens validate

    [Fact]
    public void ParseAndValidate_ReportStop_Accepted()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done, nothing needed",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictVocabulary.Finished, result.Verdict);
        Assert.Equal("Nothing is needed from you - I will stop here.", result.Evidence);
        Assert.Equal("agent-reply", result.PackageKind);
        Assert.Equal(TurnVerdictContract.Version, result.ContractVersion);
        Assert.Equal(Model, result.Model);
        Assert.Equal(ObservedAt, result.TurnEndObservedAtUtc);
        Assert.Equal("SPECIMEN-REPORT", result.ScreenHash);
        Assert.NotEqual("", result.VerdictId);
    }

    [Fact]
    public void ParseAndValidate_MenuStop_AcceptedWithKeysAndMenu()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "1\r", recommended = false, note = "Puts the guard live; a bad guard blocks every deploy." },
                new { key = "Leave it on the branch", send = "2\r", recommended = true, note = "Nothing changes; decide after the review." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("keys", result.AnswerVia);
        Assert.NotNull(result.Menu);
        Assert.Equal("single", result.Menu!.SelectionMode);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal("1\r", result.Options[0].Send);
        Assert.Single(result.Options, o => o.Recommended);
        Assert.Equal("irreversible", result.Risk);
    }

    [Fact]
    public void ParseAndValidate_FailureStop_AcceptedWhenTheVerdictIsNotCalm()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.StuckRecoverable,
            evidence: "Error: connection reset by peer while reading the response",
            label: "Connection dropped; it would carry on",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, FailureStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictVocabulary.StuckRecoverable, result.Verdict);
        Assert.Equal("terminal-failure", result.PackageKind);
    }

    // ================================================================= the receipt

    [Fact]
    public void ParseAndValidate_EvidenceNotVerbatim_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "The agent said there was nothing left for you to do.",
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not found verbatim", result.FailureReason);
        Assert.Equal("", result.Verdict);
    }

    [Fact]
    public void ParseAndValidate_EvidenceVerbatimAfterWhitespaceNormalisation_Accepted()
    {
        // The quote came off a screen row drawn inside a box and with the spacing the box imposed. It is
        // the same sentence, so it is accepted; the box edges and the run of spaces are the only
        // differences tolerated.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy    guard\n to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "1\r", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2\r", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_EvidenceOneWordDifferent_Rejected()
    {
        // "the" instead of "this". Every other character matches, and it is still a different sentence:
        // the receipt is what proves the answer is the agent's words rather than the judge's.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop now.",
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not found verbatim", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_CannotTellNeedsNoEvidence_Accepted()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.CannotTell,
            evidence: "",
            label: "Cannot tell what this stop means",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictVocabulary.CannotTell, result.Verdict);
    }

    [Fact]
    public void ParseAndValidate_EvidenceFromTheScreenWhenThereIsNoConversation_Accepted()
    {
        var package = ReportStop() with
        {
            ConversationAvailable = false,
            LatestReply = null,
        };
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "The retention sweep now deletes rows older than seven days and the test covers it.",
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, package, Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
    }

    // ================================================================= the closed word lists

    [Fact]
    public void ParseAndValidate_UnknownVerdict_Rejected()
    {
        var answer = Answer(
            verdict: "all-good",
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("unknown verdict word", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_TheDetectorsSeventhWord_Rejected()
    {
        // not-a-turn-end is a real word in the shared vocabulary and belongs to the DETECTOR. The
        // Wingman is only ever asked about stops that already happened, so it may not answer with it.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NotATurnEnd,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Still working",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("unknown verdict word", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_UnknownRisk_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "low");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("unknown risk word", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MissingRisk_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: null);

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no risk word", result.FailureReason);
    }

    // ================================================================= the answering shape

    [Fact]
    public void ParseAndValidate_KeysWithoutMenu_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            options: new object[]
            {
                new { key = "Push it now", send = "1\r", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2\r", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("carries no menu", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_ExactlyOneOption_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes, push it", recommended = true, note = "Puts the guard live." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("one option is not a choice", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionWithNothingToSend_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes, push it", recommended = true, note = "Puts the guard live." },
                new { key = "Leave it", send = "", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to send", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MultipleChoiceMenuWithNoSubmit_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Pick the checks to run",
            risk: "none",
            answerVia: "keys",
            menu: new { question = "Pick any that apply", selectionMode = "multiple", submit = "" },
            options: new object[]
            {
                new { key = "Unit tests", send = "1", recommended = false, note = "Toggles the unit tests." },
                new { key = "Gate", send = "2", recommended = false, note = "Toggles the local gate." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no way to submit", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsWithNoAnswerVia_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: null,
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no answerVia", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_UnknownAnswerVia_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "voice");

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("unknown answerVia", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_SendKeepsItsCarriageReturn_AndALoneOneIsARealSend()
    {
        // The send is what gets typed into a live session. A picker is confirmed by the carriage return
        // carried inside the send, and nothing appends one for a keys option - so trimming "1\r" to "1"
        // would select the option and never confirm it. A send of nothing BUT a carriage return is how a
        // picker's highlighted default is accepted, so it is a real send rather than an empty one.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Accept what is highlighted", send = "\r", recommended = false, note = "Takes the default." },
                new { key = "Leave it on the branch", send = "2\r", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("\r", result.Options[0].Send);
        Assert.Equal("2\r", result.Options[1].Send);
    }

    [Fact]
    public void ParseAndValidate_SendOfNothingButSpaces_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = true, note = "Puts the guard live." },
                new { key = "Leave it", send = "   ", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to send", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MoreThanOneRecommended_KeepsOnlyTheFirst()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = true, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Single(result.Options, o => o.Recommended);
        Assert.True(result.Options[0].Recommended);
    }

    // ================================================================= calm can never come from a failure

    [Fact]
    public void ParseAndValidate_TerminalFailurePackageWithFinished_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Error: connection reset by peer while reading the response",
            label: "Sweep finished",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, FailureStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("is calm", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_TerminalFailurePackageWithContinuesAlone_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.ContinuesAlone,
            evidence: "Error: connection reset by peer while reading the response",
            label: "Sweep carrying on",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, FailureStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("is calm", result.FailureReason);
    }

    // ================================================================= the spoken section and the caps

    [Fact]
    public void ParseAndValidate_SpokenMissing_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            spoken: "");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no spoken section", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongFields_Capped()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: new string('a', TurnVerdictContract.MaxLabelChars + 50),
            risk: "none",
            summary: new string('b', TurnVerdictContract.MaxSummaryChars + 50),
            spoken: new string('c', TurnVerdictContract.MaxSpokenChars + 50),
            agentRecommends: new string('d', TurnVerdictContract.MaxAgentRecommendsChars + 50));

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(TurnVerdictContract.MaxLabelChars, result.Label.Length);
        Assert.Equal(TurnVerdictContract.MaxSummaryChars, result.Summary.Length);
        Assert.Equal(TurnVerdictContract.MaxSpokenChars, result.Spoken.Length);
        Assert.Equal(TurnVerdictContract.MaxAgentRecommendsChars, result.AgentRecommends!.Length);
    }

    [Fact]
    public void ParseAndValidate_OverLongEvidence_RejectedRatherThanCut()
    {
        // A receipt cannot be truncated: a cut receipt would no longer be what the agent said, and the
        // check that it IS what the agent said is the whole value of the field.
        var longQuote = new string('e', TurnVerdictContract.MaxEvidenceChars + 1);
        var package = ReportStop() with { LatestReply = longQuote };
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: longQuote,
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, package, Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("cannot be cut", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MissingLabel_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no label", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NotJson_Rejected()
    {
        var result = TurnVerdictContract.ParseAndValidate(
            "I think the session is finished.", ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not valid JSON", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NothingAtAll_RejectedRatherThanTreatedAsCalm()
    {
        var result = TurnVerdictContract.ParseAndValidate("", ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Equal("", result.Verdict);
        Assert.Equal(ObservedAt, result.TurnEndObservedAtUtc);
        Assert.Equal(TurnVerdictContract.Version, result.ContractVersion);
    }

    [Fact]
    public void ParseAndValidate_AnswerWrappedInFencesAndProse_Accepted()
    {
        var inner = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none");
        var raw = "Here is the answer:\n```json\n" + inner + "\n```";

        var result = TurnVerdictContract.ParseAndValidate(raw, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
    }

    // ================================================================= the vocabulary pin

    [Fact]
    public void Vocabulary_IsTheSixWordsPlusTheDetectorsSeventh()
    {
        Assert.Equal(
            new[] { "needed-you", "finished", "continues-alone", "stuck-recoverable", "stuck-needs-person", "cannot-tell" },
            TurnVerdictVocabulary.WingmanVerdicts);

        // The whole shared list, which the labelling tool's verdicts.py must equal word for word.
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

        Assert.Equal(new[] { "none", "irreversible", "standing-grant", "spends-money" }, TurnVerdictVocabulary.Risks);
        Assert.Equal(new[] { "high", "ambiguous" }, TurnVerdictVocabulary.Confidences);
        Assert.Equal(new[] { "reply", "keys" }, TurnVerdictVocabulary.AnswerVias);
        Assert.Equal(new[] { "single", "multiple" }, TurnVerdictVocabulary.SelectionModes);
        Assert.Equal(new[] { "finished", "continues-alone" }, TurnVerdictVocabulary.Calm);
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

        // The pin is only true on every machine if the file's line endings cannot be rewritten by a
        // checkout. .gitattributes holds it to line feeds; this says so out loud, because a carriage
        // return would make the file and the embedded copy differ on every line.
        Assert.DoesNotContain((byte)'\r', onDisk);
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
        // Absent facts are named rather than left blank, so "we did not look" and "there was nothing"
        // never look the same to the judge.
        Assert.Contains("Why the turn was called ended: (none)", prompt);
        Assert.Contains("Next wake-up the session announced: (none)", prompt);
    }

    [Fact]
    public void BuildPrompt_ScreenTextThatLooksLikeAPlaceholder_IsNotSubstituted()
    {
        // The screen is written by somebody else. A row that reads like a placeholder is inert text,
        // because the template is filled in one pass and a filled value is never rescanned.
        var package = MenuStop() with { ScreenRows = new[] { "{{SESSION_TITLE}}", "{{RECENT_TURNS}}" } };

        var prompt = TurnVerdictContract.BuildPrompt(package);

        Assert.Contains("{{SESSION_TITLE}}", prompt);
        Assert.Contains("{{RECENT_TURNS}}", prompt);
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

    /// <summary>
    /// One canned model answer as JSON. Every field is written explicitly so a test says exactly what
    /// the judge is pretending to have said; null means the field is absent from the object entirely,
    /// which is a different thing from present and empty.
    /// </summary>
    private static string Answer(
        string verdict,
        string evidence,
        string label,
        string? risk,
        string? summary = "The retention sweep is done and the test covers it.",
        string? spoken = "Retention timer. The sweep now deletes rows older than seven days and the test covers it. Nothing is needed from you.",
        string? agentRecommends = null,
        string? answerVia = "reply",
        object? menu = null,
        object[]? options = null,
        string? confidence = "high")
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["verdict"] = verdict,
            ["evidence"] = evidence,
            ["label"] = label,
            ["summary"] = summary,
            ["spoken"] = spoken,
            ["options"] = options ?? Array.Empty<object>(),
            ["menu"] = menu,
            ["agentRecommends"] = agentRecommends,
        };
        if (risk is not null) fields["risk"] = risk;
        if (answerVia is not null) fields["answerVia"] = answerVia;
        if (confidence is not null) fields["confidence"] = confidence;

        return JsonSerializer.Serialize(fields);
    }
}
