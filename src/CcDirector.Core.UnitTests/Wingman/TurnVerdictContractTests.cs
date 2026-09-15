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
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live; a bad guard blocks every deploy." },
                new { key = "Leave it on the branch", send = "2", recommended = true, note = "Nothing changes; decide after the review." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("keys", result.AnswerVia);
        Assert.NotNull(result.Menu);
        Assert.Equal("single", result.Menu!.SelectionMode);
        Assert.Equal(2, result.Options.Count);
        // The option carries ONLY the bytes that select it. The confirm is the menu's submit, sent once
        // by the route after every selected option - never inside a send.
        Assert.Equal("1", result.Options[0].Send);
        Assert.Equal("", result.Menu!.Submit);
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
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);

        // What is STORED is the screen's own characters, not the judge's spacing. The receipt is shown to
        // the owner as the agent's own words, so it has to be them.
        Assert.Equal("Push the deploy guard to main?", result.Evidence);
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
        // Caught by the shape pass now: a member the shape declares is missing, which is a different
        // fault from a risk word nobody recognises, and says so.
        Assert.Contains("has no 'risk'", result.FailureReason);
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
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
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
        Assert.Contains("'send' is empty", result.FailureReason);
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
    public void ParseAndValidate_MissingAnswerVia_Rejected()
    {
        // It used to be written in as "reply" whenever no options were offered. It decides how bytes
        // reach a live session, so the contract choosing it is the contract deciding what gets typed.
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
        Assert.Contains("has no 'answerVia'", result.FailureReason);
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
    public void ParseAndValidate_SendIsNeverTrimmedButNeverCarriesALineEnding()
    {
        // THIS TEST USED TO PIN THE OPPOSITE. Until the contract was amended, a keys option was supposed
        // to carry its own confirm ("1\r") and a send of nothing but a carriage return was how a
        // picker's highlighted default was accepted. Three parts of the product each said something
        // different about that, and the consequence was a multiple-select nobody could answer: the route
        // took one option, re-checked the screen hash, and refused the second toggle by its own lock.
        // The one rule now is that a send carries only the bytes that CHOOSE, and the confirm is the
        // menu's submit.
        //
        // What has not changed: a send is never trimmed. Leading and trailing spaces inside a real send
        // are bytes that get typed, and tidying them is a change to what reaches the session.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = " yes, push it ", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it on the branch", send = "no", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(" yes, push it ", result.Options[0].Send);
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
        Assert.Contains("'send' is empty", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MoreThanOneRecommended_RejectedRatherThanQuietlyRepaired()
    {
        // This test used to pin the opposite: the extra flags were cleared and the answer accepted. That
        // is a silent repair of a malformed answer - the judge said two different things were the one to
        // do, nothing here can know which it meant, and keeping the first leaves the owner pressing a
        // button he believes a judge chose for him.
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

        Assert.True(result.Failed);
        Assert.Contains("marked recommended", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_ExactlyOneRecommended_Accepted()
    {
        // The control for the test above: the rule is "more than one", not "any".
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = true, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Single(result.Options, o => o.Recommended);
    }

    // ============================================ finding 3: three shapes that used to default quietly

    [Fact]
    public void ParseAndValidate_OptionsPresentButNotAList_Rejected()
    {
        // A calm-looking answer with a valid receipt and "options": {} used to be read as an answer that
        // offered nothing. It is a broken answer, and a broken answer is thrown away whole.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            optionsRaw: new Dictionary<string, object?>(StringComparer.Ordinal));

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not a list", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsExplicitlyNull_Rejected()
    {
        // This test used to pin the opposite, on the reading that null is the judge saying there are
        // none. The shape does not offer that reading: options is declared an array and, unlike menu,
        // never a nullable one, so null is simply not a list. Accepting it was the same silent repair as
        // accepting an object where a list belongs, one step quieter - and this input carries a perfectly
        // good receipt, which is exactly what makes it easy to wave through.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            optionsNull: true);

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("not a list", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionsAbsentAltogether_IsNoOptionsAndAccepted()
    {
        // The control, and the only way an answer says there are none: leave the field out. This is the
        // ordinary shape of a report, and it must stay accepted or every report would refuse.
        var answer = AnswerWithoutOptions(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Empty(result.Options);
    }

    // ================================================== an option is never cut, in either half

    [Fact]
    public void ParseAndValidate_OverLongOptionKey_RejectedRatherThanCut()
    {
        // The key is the words on the button. Cut, it is a different action from the one the judge
        // offered, and the owner presses it believing the short version.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new
                {
                    key = "Push it now" + new string('x', TurnVerdictContract.MaxOptionKeyChars),
                    send = "yes",
                    recommended = false,
                    note = "Puts the guard live.",
                },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("a shortened action", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OverLongOptionNote_RejectedRatherThanCut()
    {
        // The note is the consequence. "deletes the rows older than seven days, and the backup" cut to
        // "deletes the rows older than seven days" is a different promise, and the owner cannot ask the
        // button what the rest of it said.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new
                {
                    key = "Push it now",
                    send = "yes",
                    recommended = false,
                    note = "Puts the guard live, " + new string('y', TurnVerdictContract.MaxOptionNoteChars),
                },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("a shortened consequence", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionAtTheBound_IsKeptWholeAndNotCut()
    {
        // The control for both tests above, and the one that catches a cut that merely moved: an option
        // exactly at each bound is accepted, and what is STORED is the whole string the judge wrote.
        var key = new string('k', TurnVerdictContract.MaxOptionKeyChars);
        var note = new string('n', TurnVerdictContract.MaxOptionNoteChars);
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key, send = "yes", recommended = false, note },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal(key, result.Options[0].Key);
        Assert.Equal(note, result.Options[0].Note);
    }

    [Fact]
    public void ParseAndValidate_MenuWithNoSelectionMode_Rejected()
    {
        // A missing selection mode used to become "single". It decides whether one key answers the picker
        // or several do, and writing one in is this contract deciding how a live session gets typed into.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "1\r", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2\r", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no selectionMode", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_UnknownConfidenceWord_Rejected()
    {
        // "fairly-sure" used to be read as "ambiguous". Confidence is not a colour, which is why this
        // looked harmless; but the stored record then said the judge answered a word it never wrote.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            confidence: "fairly-sure");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("unknown confidence word", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MissingConfidence_Rejected()
    {
        // The same rule as the risk word, for the same reason: there is no default, because a default is
        // this contract answering on the judge's behalf.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            confidence: null);

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("has no 'confidence'", result.FailureReason);
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
    public void ParseAndValidate_OverLongFields_AreCutAtTheLastWholeWord()
    {
        // The three capped prose fields are READ - on a row, in a panel, out loud - so a cut one must not
        // end mid-word. "the deploy guard is bl" reads as a broken product rather than as a long answer.
        var word = "alpha ";
        var label = string.Concat(Enumerable.Repeat(word, 40));
        var summary = string.Concat(Enumerable.Repeat(word, 200));
        var spoken = string.Concat(Enumerable.Repeat(word, 400));
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: label,
            risk: "none",
            summary: summary,
            spoken: spoken);

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        foreach (var (field, cut, max) in new[]
        {
            ("label", result.Label, TurnVerdictContract.MaxLabelChars),
            ("summary", result.Summary, TurnVerdictContract.MaxSummaryChars),
            ("spoken", result.Spoken, TurnVerdictContract.MaxSpokenChars),
        })
        {
            Assert.True(cut.Length <= max, $"{field} is {cut.Length} characters, over {max}");
            Assert.EndsWith("alpha", cut);
            Assert.False(char.IsWhiteSpace(cut[^1]), $"{field} ends in whitespace");
            // The bound is still nearly reached: a word boundary is found, not the whole field thrown out.
            Assert.True(cut.Length > max - word.Length, $"{field} lost more than one word");
        }
    }

    [Fact]
    public void ParseAndValidate_OneUnbrokenRunLongerThanTheBound_IsStillCutAtTheBound()
    {
        // No boundary exists to cut at. An empty field says less to the reader than a cut one, so the
        // hard bound stands. This is the case the previous test for the caps was written with.
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


    // ============================================ the shape is proved before anything is read
    //
    // Each pair below is a member the contract used to REPAIR: it read a missing field, or a field of
    // the wrong kind, as an absence, and stored a value the judge never wrote. The rejection test says
    // the repair is gone; the control beside it says the field is still accepted when it is right, so
    // the rule cannot drift into refusing everything.

    [Fact]
    public void ParseAndValidate_MissingSummary_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            omit: new[] { "summary" });

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("has no 'summary'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_EmptySummary_Rejected()
    {
        // Present and empty is not the same fault as absent, and it gets its own sentence - but it is
        // the same outcome for the reader, who is shown a row that reports nothing.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            summary: "");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("summary is empty", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_SummaryOfOneSentence_Accepted()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            summary: "The sweep deleted the rows past the window and the test covers it.");

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("The sweep deleted the rows past the window and the test covers it.", result.Summary);
    }

    [Fact]
    public void ParseAndValidate_VerdictOfTheWrongType_Rejected()
    {
        // A number where a word belongs used to read as the empty string and be refused as an unknown
        // verdict. Same outcome, different reason - and the reason is what a reader of the stored
        // record has to act on.
        var result = TurnVerdictContract.ParseAndValidate(
            RawAnswer(@"{""verdict"": 7, ""confidence"": ""high"", ""evidence"": ""x"", ""label"": ""x"","
                + @" ""summary"": ""x"", ""answerVia"": ""reply"", ""risk"": ""none"", ""spoken"": ""x""}"),
            ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'verdict' is Number where the shape declares a string", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MenuOfTheWrongType_Rejected()
    {
        // An ARRAY where the menu belongs was discarded as no menu, so a keys answer became a reply
        // answer and the owner was offered a typing box for a picker he cannot type into.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new object[] { "single" },
            options: new object[]
            {
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'menu' is Array", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MenuExplicitlyNull_IsNoPickerAndAccepted()
    {
        // The control: menu is the one member the shape declares nullable, so null is a real answer.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            menu: null);

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Null(result.Menu);
    }

    [Fact]
    public void ParseAndValidate_AgentRecommendsOfTheWrongType_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.Finished,
            evidence: "Nothing is needed from you - I will stop here.",
            label: "Retention sweep done",
            risk: "none",
            agentRecommendsRaw: new { text = "push it" });

        var result = TurnVerdictContract.ParseAndValidate(answer, ReportStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'agentRecommends' is Object", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_AgentRecommendsNullOrAString_BothAccepted()
    {
        var withNull = TurnVerdictContract.ParseAndValidate(
            Answer(
                verdict: TurnVerdictVocabulary.Finished,
                evidence: "Nothing is needed from you - I will stop here.",
                label: "Retention sweep done",
                risk: "none",
                agentRecommends: null),
            ReportStop(), Model, ObservedAt);

        var withText = TurnVerdictContract.ParseAndValidate(
            Answer(
                verdict: TurnVerdictVocabulary.Finished,
                evidence: "Nothing is needed from you - I will stop here.",
                label: "Retention sweep done",
                risk: "none",
                agentRecommends: "I would leave the guard on the branch."),
            ReportStop(), Model, ObservedAt);

        Assert.False(withNull.Failed, withNull.FailureReason);
        Assert.Null(withNull.AgentRecommends);
        Assert.False(withText.Failed, withText.FailureReason);
        Assert.Equal("I would leave the guard on the branch.", withText.AgentRecommends);
    }

    [Fact]
    public void ParseAndValidate_RecommendedAsAString_Rejected()
    {
        // "true" is not true. It used to be read as false, which stores the OPPOSITE of what the judge
        // wrote: the option it meant to recommend arrives recommending nothing.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = "true", note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("'recommended' is String", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_RecommendedFalseOnEveryOption_Accepted()
    {
        // The control, and the one a "reject unless exactly one is recommended" slip would break: the
        // judge is allowed to recommend nothing.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.DoesNotContain(result.Options, o => o.Recommended);
    }

    [Fact]
    public void ParseAndValidate_OptionMissingItsKey_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { send = "yes", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("has no 'key'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_OptionMissingItsNote_Rejected()
    {
        // The note is the consequence. A button with no consequence is one the owner presses without
        // being told what it does, and storing an empty string is this contract writing that silence.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = false },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("has no 'note'", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_SingleSelectMenuMissingItsSubmit_Rejected()
    {
        // An absent submit used to become "", which says the picker acts on the key itself. On a picker
        // that needs Enter, that is a tap that selects and never confirms.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single" },
            options: new object[]
            {
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no submit", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MenuWithNoQuestion_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("no question", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_SingleSelectWithAnEmptySubmit_Accepted()
    {
        // The exact-boundary control for the two tests above: "" is a real submit - it says the picker
        // acts on the key itself - and it must stay accepted or every single-select would refuse.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "1", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("", result.Menu!.Submit);
        Assert.Equal("single", result.Menu.SelectionMode);
    }

    // ============================== can the route actually perform this, exactly once?

    [Fact]
    public void ParseAndValidate_ReplyOptionCarryingACarriageReturn_Rejected()
    {
        // The route appends one Enter to a reply. A send that already ends in one sends the answer
        // twice - the second time into whatever the session showed after the first.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            options: new object[]
            {
                new { key = "Push it now", send = "production\r", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("carriage return or a line feed", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_KeysOptionCarryingALineFeed_Rejected()
    {
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "\r" },
            options: new object[]
            {
                new { key = "Push it now", send = "1\n", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "2", recommended = true, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("carriage return or a line feed", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_ReplyCarryingAPickerMenu_Rejected()
    {
        // A reply is typed words and a menu is a picker. A record claiming both cannot say which one
        // the owner is doing, and the route would have to guess.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "reply",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[]
            {
                new { key = "Push it now", send = "yes", recommended = false, note = "Puts the guard live." },
                new { key = "Leave it", send = "no", recommended = false, note = "Nothing changes." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("carries a picker menu", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MultipleSelectWhoseSubmitIsNotACarriageReturn_Rejected()
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
        Assert.Contains("submit", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_MultipleSelectWithACarriageReturnSubmit_Accepted()
    {
        // The control: each option toggles with its own bytes and the submit finishes the whole set,
        // which is the only shape a checklist can be answered in.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Pick the checks to run",
            risk: "none",
            answerVia: "keys",
            menu: new { question = "Pick any that apply", selectionMode = "multiple", submit = "\r" },
            options: new object[]
            {
                new { key = "Unit tests", send = "1", recommended = false, note = "Toggles the unit tests." },
                new { key = "Gate", send = "2", recommended = false, note = "Toggles the local gate." },
            });

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Equal("multiple", result.Menu!.SelectionMode);
        Assert.Equal("\r", result.Menu.Submit);
        Assert.Equal("1", result.Options[0].Send);
    }

    [Fact]
    public void ParseAndValidate_KeysWithAMenuAndNothingToSelect_Rejected()
    {
        // A question with buttons the owner cannot see, because there are none, and nothing to confirm
        // either. The one shape that may carry no options is the already-typed reply below.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Push the deploy guard to main?",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Push the deploy guard to main?", selectionMode = "single", submit = "" },
            options: new object[0]);

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to select", result.FailureReason);
    }

    // ============================== the already-typed reply: the one keys answer with no options

    [Fact]
    public void ParseAndValidate_AlreadyTypedReply_AcceptedWithNoOptions()
    {
        // The person has typed their answer into the composer and the only action left is to send it.
        // There is nothing to toggle and something to confirm, so the submit IS the action and the
        // route sends it alone. This is the single exception to "keys must offer something to select".
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Send the reply already typed",
            risk: "irreversible",
            answerVia: "keys",
            menu: new
            {
                question = "Send the reply already typed: 'yes, push it to production'",
                selectionMode = "single",
                submit = "\r",
            },
            options: new object[0]);

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.False(result.Failed, result.FailureReason);
        Assert.Empty(result.Options);
        Assert.Equal("\r", result.Menu!.Submit);
        Assert.Contains("already typed", result.Menu.Question);
    }

    [Fact]
    public void ParseAndValidate_NoOptionsAndNothingToConfirm_Rejected()
    {
        // The first leg of the exception removed: no options AND an empty submit is an answer that
        // sends nothing at all.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Send the reply already typed",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Send the reply already typed", selectionMode = "single", submit = "" },
            options: new object[0]);

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to confirm", result.FailureReason);
    }

    [Fact]
    public void ParseAndValidate_NoOptionsAndSelectionModeMultiple_Rejected()
    {
        // The second leg removed: pick-any-that-apply, from nothing. The exception is a single confirm
        // and nothing else.
        var answer = Answer(
            verdict: TurnVerdictVocabulary.NeededYou,
            evidence: "Push the deploy guard to main?",
            label: "Send the reply already typed",
            risk: "irreversible",
            answerVia: "keys",
            menu: new { question = "Send the reply already typed", selectionMode = "multiple", submit = "\r" },
            options: new object[0]);

        var result = TurnVerdictContract.ParseAndValidate(answer, MenuStop(), Model, ObservedAt);

        Assert.True(result.Failed);
        Assert.Contains("nothing to pick", result.FailureReason);
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
    /// <summary>One answer written out by hand, for a shape the typed helpers cannot express - a member
    /// of the wrong JSON kind, where the helper would serialise a well-formed one.</summary>
    private static string RawAnswer(string json) => json;

    /// <summary>
    /// The same canned answer with the "options" field LEFT OUT of the object entirely. Absent and
    /// present-but-null are different JSON and the contract treats them differently, so a test that
    /// means "absent" cannot be written with the helper above.
    /// </summary>
    private static string AnswerWithoutOptions(
        string verdict,
        string evidence,
        string label,
        string risk)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["verdict"] = verdict,
            ["evidence"] = evidence,
            ["label"] = label,
            ["risk"] = risk,
            ["confidence"] = "high",
            ["answerVia"] = "reply",
            ["summary"] = "The retention sweep is done and the test covers it.",
            ["spoken"] = "Retention timer. The sweep is done and nothing is needed from you.",
        };
        return JsonSerializer.Serialize(fields);
    }

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
        string? confidence = "high",
        object? optionsRaw = null,
        bool optionsNull = false,
        object? agentRecommendsRaw = null,
        string[]? omit = null)
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
        // optionsRaw writes whatever is given where the list belongs - an object, a number, a string -
        // so a malformed shape can be tested. optionsNull writes an explicit JSON null there, which is
        // NOT the same as leaving the field out: see AnswerWithoutOptions for that.
        if (optionsRaw is not null) fields["options"] = optionsRaw;
        if (optionsNull) fields["options"] = null;
        // agentRecommendsRaw writes a value of the wrong KIND where a string or null belongs.
        if (agentRecommendsRaw is not null) fields["agentRecommends"] = agentRecommendsRaw;
        // omit REMOVES a member from the object entirely, which is a different thing from writing null
        // into it - the contract treats absent and null differently and so must these fixtures.
        foreach (var name in omit ?? Array.Empty<string>()) fields.Remove(name);
        if (risk is not null) fields["risk"] = risk;
        if (answerVia is not null) fields["answerVia"] = answerVia;
        if (confidence is not null) fields["confidence"] = confidence;

        return JsonSerializer.Serialize(fields);
    }
}
