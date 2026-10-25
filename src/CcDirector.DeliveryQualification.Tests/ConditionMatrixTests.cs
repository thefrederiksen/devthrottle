using System.Text;
using System.Text.Json;
using CcDirector.DeliveryQualification;
using Xunit;

namespace CcDirector.DeliveryQualification.Tests;

/// <summary>
/// The harness additions before the phase-2 runs (issue #4378, the mission's 11.5): the condition cells' judgements, the
/// agent's accepted trim of edge spaces, Claude Code's mid-turn prompt record, the tool-call reader the Codex approval ask is
/// read from, the second run of every cell, and the screen saved beside a refused send. A judge here that passed a destroyed
/// draft, or took a prompt's own words for an approval ask, would certify the faults phase 2 is meant to fix.
/// </summary>
public sealed class ConditionMatrixTests
{
    private const string Body = "   Token Q1, with spaces before and after. Reply with exactly: ACK Q1   ";

    // ===== the agent's own trim of edge spaces (widened in issue #4379) =====

    /// <summary>An echo run's results holding one cell per entry, each with its verdict, loaded as the proof.</summary>
    private static EchoProof Proof(params (string Route, string As, string Text, string Verdict)[] cells)
    {
        var file = Path.Combine(Path.GetTempPath(), "echo-proof-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(file, cells.Select(c => JsonSerializer.Serialize(new EchoResult(c.Route, c.As, c.Text, 10, "OK", "",
                new EchoVerdict(c.Verdict, "x", false, 1, 1, 1, "")))));
            return EchoProof.Load(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static EchoProof WroteIt(string agent) => Proof(("prompt", agent, "edge-spaces", "EXACT"), ("prompt", agent, "edge-spaces", "EXACT"));

    /// <summary>The trim each agent really makes of a text with spaces at both ends, as its records held it on 23 October
    /// 2026: Claude Code the trailing edge, Codex and Pi BOTH edges (the baseline note said leading only - measured too
    /// narrowly). With the echo cell proving the Director wrote the spaces, it passes, and the reason names that cell.</summary>
    [Theory]
    [InlineData("claude", "   Token Q1, with spaces before and after. Reply with exactly: ACK Q1", "Claude Code trims trailing spaces")]
    [InlineData("codex", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1", "Codex trims the white space at both edges")]
    [InlineData("pi", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1", "Pi trims the white space at both edges")]
    public void AgentTrim_TheAgentsOwnTrimAndTheDirectorWroteTheSpaces_IsAcceptedNamingTheProof(string agent, string got, string why)
    {
        var accepted = AcceptedDifference.AgentTrim(agent, Body, got, WroteIt(agent), "prompt", "edge-spaces");

        Assert.NotNull(accepted);
        Assert.StartsWith(why, accepted);
        Assert.Contains("the echo cell prompt as " + agent + " edge-spaces shows the Director wrote them", accepted);
    }

    /// <summary>The same trim with NO proof the Director wrote the spaces - no echo run given, the echo cell for another
    /// route, or an echo cell that was not EXACT - may be the Director's own loss, so it fails.</summary>
    [Fact]
    public void AgentTrim_NoEchoCellProvesTheDirectorWroteTheSpaces_IsNotAccepted()
    {
        const string got = "Token Q1, with spaces before and after. Reply with exactly: ACK Q1";

        Assert.Null(AcceptedDifference.AgentTrim("codex", Body, got, EchoProof.None, "prompt", "edge-spaces"));
        Assert.Null(AcceptedDifference.AgentTrim("codex", Body, got, Proof(("desktop-paste", "codex", "edge-spaces", "EXACT")), "prompt", "edge-spaces"));
        Assert.Null(AcceptedDifference.AgentTrim("codex", Body, got,
            Proof(("prompt", "codex", "edge-spaces", "EXACT"), ("prompt", "codex", "edge-spaces", "ALTERED")), "prompt", "edge-spaces"));
    }

    [Theory]
    // An edge the agent does not trim, one edge where it trims both, and any change inside: none is the agent's own trim,
    // even with the proof.
    [InlineData("claude", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1   ")]
    [InlineData("claude", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1")]
    [InlineData("codex", "   Token Q1, with spaces before and after. Reply with exactly: ACK Q1")]
    [InlineData("codex", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1   ")]
    [InlineData("pi", "Token Q1, with spaces before and after.Reply with exactly: ACK Q1")]
    [InlineData("gemini", "Token Q1, with spaces before and after. Reply with exactly: ACK Q1")]
    public void AgentTrim_AnyOtherDifference_IsNotAccepted(string agent, string got)
    {
        Assert.Null(AcceptedDifference.AgentTrim(agent, Body, got, WroteIt(agent), "prompt", "edge-spaces"));
    }

    [Fact]
    public void AgentTrim_AnExactRecord_NeedsNothingAccepted()
    {
        Assert.Null(AcceptedDifference.AgentTrim("claude", "Token Q1", "Token Q1", WroteIt("claude"), "prompt", "edge-spaces"));
    }

    [Fact]
    public void Verdict_TheAgentsTrimWithProof_ArrivesAndPassesNamingWhy()
    {
        var found = new RecordsVerdict(true, false, false, false, "f");

        var (truth, exact, accepted, _) = AgentMatrix.Verdict("codex", Body, found, Body.Trim(), WroteIt("codex"), "prompt", "edge-spaces");
        var result = new AgentMatrixResult("codex", "prompt", "edge-spaces", Body.Length, "OK", "", truth, exact, false, 1, null, null, Accepted: accepted);

        Assert.Equal("ARRIVED", truth);
        Assert.False(exact);
        Assert.True(result.Pass);
    }

    /// <summary>Without the proof the cell is ALTERED, and its difference says the trim was seen and why it was not
    /// accepted - visible, never silent.</summary>
    [Fact]
    public void Verdict_TheAgentsTrimWithoutProof_IsAlteredAndSaysWhy()
    {
        var found = new RecordsVerdict(true, false, false, false, "f");

        var (truth, _, accepted, difference) = AgentMatrix.Verdict("codex", Body, found, Body.Trim(), EchoProof.None, "prompt", "edge-spaces");

        Assert.Equal("ALTERED", truth);
        Assert.Null(accepted);
        Assert.Contains("NOT accepted", difference);
    }

    [Fact]
    public void Verdict_TheEdgeSpacesTextChangedInside_IsStillAltered()
    {
        var found = new RecordsVerdict(true, false, false, false, "f");

        var (truth, _, accepted, difference) = AgentMatrix.Verdict("claude", Body, found, Body.Replace("before and", "beforeand").TrimEnd(),
            WroteIt("claude"), "prompt", "edge-spaces");

        Assert.Equal("ALTERED", truth);
        Assert.Null(accepted);
        Assert.NotNull(difference);
    }

    // ===== Claude Code's mid-turn prompt record =====

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void UserTurn_ClaudesQueuedPromptAttachment_IsTheUsersTurn()
    {
        var root = Json("""{"type":"attachment","attachment":{"type":"queued_command","prompt":"Token Q1. hi","commandMode":"prompt","origin":{"kind":"human"}},"rendered":[{"content":"<system-reminder>The user sent a new message while you were working: Token Q1. hi</system-reminder>"}]}""");

        Assert.True(AgentRecords.IsUserTurn("claude", root));
        Assert.Equal("Token Q1. hi", AgentRecords.TurnText("claude", root));
    }

    [Fact]
    public void UserTurn_AnAttachmentThatIsNotAQueuedPrompt_IsNotATurn()
    {
        Assert.False(AgentRecords.IsUserTurn("claude", Json("""{"type":"attachment","attachment":{"type":"queued_command","prompt":"ls","commandMode":"bash"}}""")));
        Assert.False(AgentRecords.IsUserTurn("claude", Json("""{"type":"attachment","attachment":{"type":"date","prompt":"Token Q1"}}""")));
    }

    [Fact]
    public void CountUserTurns_CodexWritingOnePromptInTwoKinds_IsOneTurn()
    {
        var lines = new[]
        {
            """{"type":"event_msg","payload":{"type":"user_message","message":"Token Q1. hi"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Token Q1. hi"}]}}""",
        };

        Assert.Equal(1, AgentRecords.CountUserTurns("codex", lines, "Q1"));
    }

    [Fact]
    public void CountUserTurns_ClaudesQueuedPromptAndAUserLine_IsTwoTurns()
    {
        var lines = new[]
        {
            """{"type":"attachment","attachment":{"type":"queued_command","prompt":"Token Q1. hi","commandMode":"prompt"}}""",
            """{"type":"user","message":{"role":"user","content":"Token Q1. hi"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ACK Q1"}]}}""",
        };

        Assert.Equal(2, AgentRecords.CountUserTurns("claude", lines, "Q1"));
    }

    // ===== the tool-call reader and the Codex approval ask =====

    [Theory]
    [InlineData("codex", """{"type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":"touch approval-Q1.txt"}}""", true)]
    [InlineData("codex", """{"type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{\"command\":[\"touch\",\"approval-Q1.txt\"]}"}}""", true)]
    [InlineData("codex", """{"type":"event_msg","payload":{"type":"exec_approval_request","command":["touch","approval-Q1.txt"]}}""", true)]
    // Codex echoes the prompt in an item_completed event that is not a user turn: it is not the ask.
    [InlineData("codex", """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"touch approval-Q1.txt"}]}}}""", false)]
    [InlineData("codex", """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"touch approval-Q1.txt"}]}}""", false)]
    [InlineData("claude", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","input":{"command":"touch approval-Q1.txt"}}]}}""", true)]
    [InlineData("claude", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I will touch approval-Q1.txt"}]}}""", false)]
    [InlineData("pi", """{"type":"message","message":{"role":"assistant","content":[{"type":"toolCall","name":"bash","arguments":{"command":"touch approval-Q1.txt"}}]}}""", true)]
    [InlineData("pi", """{"type":"message","message":{"role":"user","content":[{"type":"text","text":"touch approval-Q1.txt"}]}}""", false)]
    public void IsToolCall_EachAgentsToolCallRecord_IsTheAskAndNothingElseIs(string agent, string json, bool isCall)
    {
        Assert.Equal(isCall, AgentRecords.IsToolCall(agent, Json(json)));
    }

    [Fact]
    public void AskRecordedAt_CodexEchoesThePromptFirst_TheAskIsItsToolCall()
    {
        var since = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var lines = new[]
        {
            """{"timestamp":"2026-10-23T14:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Token Q1. Use your shell tool to run exactly: touch approval-Q1.txt"}]}}""",
            """{"timestamp":"2026-10-23T14:00:01.100Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"touch approval-Q1.txt"}]}}}""",
            """{"timestamp":"2026-10-23T14:00:04.500Z","type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":"tools.exec_command({cmd:\"touch approval-Q1.txt\"})"}}""",
        };

        var at = StateMatrix.AskRecordedAt("codex", lines, since, "Q1", "approval");

        Assert.Equal(new DateTime(2026, 10, 23, 14, 0, 4, 500, DateTimeKind.Utc), at);
    }

    [Fact]
    public void AskRecordedAt_NoToolCallYet_IsNoAsk()
    {
        var since = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var lines = new[] { """{"timestamp":"2026-10-23T14:00:01.100Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"touch approval-Q1.txt"}]}}}""" };

        Assert.Null(StateMatrix.AskRecordedAt("codex", lines, since, "Q1", "approval"));
    }

    [Theory]
    [InlineData("""{"type":"api-request-blob","message":{"role":"user","content":[{"type":"tool_result","content":"<system-reminder>The user sent a new message while you were working: Token Q1</system-reminder>"}]}}""", true)]
    [InlineData("""{"type":"queue-operation","operation":"enqueue","content":"Token Q1"}""", true)]
    [InlineData("""{"type":"last-prompt","prompt":"Token Q1"}""", true)]
    [InlineData("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"Token Q1"}]}}}""", true)]
    [InlineData("""{"type":"attachment","attachment":{"type":"queued_command","prompt":"Token Q1","commandMode":"prompt"}}""", false)]
    [InlineData("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"ACK Q1"}]}}}""", false)]
    public void IsCopyOfPrompt_RecordsThatOnlyCopyThePrompt_AreKnown(string json, bool copy)
    {
        Assert.Equal(copy, AgentRecords.IsCopyOfPrompt(Json(json)));
    }

    [Fact]
    public void AnswerRecordedAt_CodexsEchoOfThePrompt_IsNotTheAnswer()
    {
        // The prompt asks for "ACK Q1", and Codex echoes it at once: the answer is the agent's own message, later.
        var since = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        var echo = """{"timestamp":"2026-10-23T14:00:01.000Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"Text","text":"Then reply with exactly: ACK Q1"}]}}}""";
        var answer = """{"timestamp":"2026-10-23T14:00:30.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ACK Q1"}]}}""";

        Assert.Null(StateMatrix.AnswerRecordedAt("codex", [echo], since, "Q1"));
        Assert.Equal(new DateTime(2026, 10, 23, 14, 0, 30, DateTimeKind.Utc), StateMatrix.AnswerRecordedAt("codex", [echo, answer], since, "Q1"));
    }

    [Fact]
    public void LongTool_NamesTheSleepTheMidTurnCellWaitsFor()
    {
        Assert.Contains($"time.sleep({ConditionMatrix.MidTurnToolSeconds})", StateMatrix.LongTool(ConditionMatrix.MidTurnToolSeconds));
        Assert.DoesNotContain("sleep 25", StateMatrix.LongTool(25));
    }

    // ===== mid-turn: the tool's own proof that it was running (review of pull request 4395, finding 1) =====

    private static readonly DateTime Sent = new(2026, 10, 23, 19, 0, 10, DateTimeKind.Utc);

    [Fact]
    public void MidTurnSetup_StartedBeforeAndFinishedAfterTheSend_IsMidTurn()
    {
        var (midTurn, why) = ConditionMatrix.MidTurnSetup(Sent.AddSeconds(-3), Sent.AddSeconds(20), Sent);

        Assert.True(midTurn, why);
        Assert.Contains("started 3.0s before", why);
    }

    [Fact]
    public void MidTurnSetup_StartedBeforeAndStillRunning_IsMidTurn()
    {
        Assert.True(ConditionMatrix.MidTurnSetup(Sent.AddSeconds(-3), null, Sent).MidTurn);
    }

    [Fact]
    public void MidTurnSetup_TheToolStartedOnlyAfterTheSend_IsNotMidTurn()
    {
        // The reviewer's case: the call was recorded, but a consent pause or a slow start held the tool until after the send.
        var (midTurn, why) = ConditionMatrix.MidTurnSetup(Sent.AddSeconds(4), Sent.AddSeconds(29), Sent);

        Assert.False(midTurn);
        Assert.Contains("after the send", why);
    }

    [Fact]
    public void MidTurnSetup_TheToolFinishedBeforeTheSend_IsNotMidTurn()
    {
        Assert.False(ConditionMatrix.MidTurnSetup(Sent.AddSeconds(-30), Sent.AddSeconds(-5), Sent).MidTurn);
    }

    [Fact]
    public void MidTurnTool_MarksItsStartBeforeTheSleepAndItsEndAfter()
    {
        var (command, started, done) = ConditionMatrix.MidTurnTool("/repo", "Q1");

        Assert.Equal(Path.Combine("/repo", ".temp/midturn-Q1.started"), started);
        Assert.Equal(Path.Combine("/repo", ".temp/midturn-Q1.done"), done);
        var startAt = command.IndexOf("midturn-Q1.started", StringComparison.Ordinal);
        var sleepAt = command.IndexOf($"time.sleep({ConditionMatrix.MidTurnToolSeconds})", StringComparison.Ordinal);
        var doneAt = command.IndexOf("midturn-Q1.done", StringComparison.Ordinal);
        Assert.True(startAt >= 0 && startAt < sleepAt && sleepAt < doneAt, command);
    }

    // ===== owner-draft =====

    private const string Text = "Token Q2. Reply with exactly: ACK Q2";
    private static readonly string Draft = ConditionMatrix.DraftFor("QD");

    [Theory]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("")]
    public void DraftVerdict_TheDraftThenTheText_ArrivesAfterTheDraft(string join)
    {
        var (truth, exact, _, _) = ConditionMatrix.DraftVerdict("codex", Draft, Text, Draft + join + Text, 1, false, EchoProof.None, "prompt", "ascii");

        Assert.Equal("ARRIVED-AFTER-DRAFT", truth);
        Assert.True(exact);
    }

    [Fact]
    public void DraftVerdict_TheTextAloneAndTheDraftGone_IsDraftLost()
    {
        Assert.Equal("DRAFT-LOST", ConditionMatrix.DraftVerdict("claude", Draft, Text, Text, 1, draftOnScreen: false, EchoProof.None, "prompt", "ascii").Truth);
    }

    [Fact]
    public void DraftVerdict_TheTextAloneAndTheDraftLeftInTheComposer_IsDraftLeft()
    {
        Assert.Equal("DRAFT-LEFT", ConditionMatrix.DraftVerdict("claude", Draft, Text, Text, 1, draftOnScreen: true, EchoProof.None, "prompt", "ascii").Truth);
    }

    [Fact]
    public void DraftVerdict_TheTextChangedAfterTheDraft_IsAltered()
    {
        var (truth, _, _, difference) = ConditionMatrix.DraftVerdict("claude", Draft, Text, Draft + " " + Text.Replace("exactly", "exact"), 1, false, EchoProof.None, "prompt", "ascii");

        Assert.Equal("ALTERED", truth);
        Assert.NotNull(difference);
    }

    [Fact]
    public void DraftVerdict_TheDraftChanged_IsAltered()
    {
        Assert.Equal("ALTERED", ConditionMatrix.DraftVerdict("claude", Draft, Text, "My draft QD " + Text, 1, false, EchoProof.None, "prompt", "ascii").Truth);
    }

    [Fact]
    public void DraftVerdict_NoTurnOrTwoTurns_IsLostOrDoubled()
    {
        Assert.Equal("LOST", ConditionMatrix.DraftVerdict("pi", Draft, Text, null, 0, true, EchoProof.None, "prompt", "ascii").Truth);
        Assert.Equal("DOUBLED", ConditionMatrix.DraftVerdict("pi", Draft, Text, Draft + " " + Text, 2, false, EchoProof.None, "prompt", "ascii").Truth);
    }

    [Fact]
    public void DraftVerdict_ClaudeTrimmingTheTextsTrailingSpaces_IsAccepted()
    {
        var (truth, exact, accepted, _) = ConditionMatrix.DraftVerdict("claude", Draft, Text + "  ", Draft + " " + Text, 1, false,
            Proof(("prompt", "claude", "ascii", "EXACT")), "prompt", "ascii");

        Assert.Equal("ARRIVED-AFTER-DRAFT", truth);
        Assert.False(exact);
        Assert.StartsWith("Claude Code trims trailing spaces", accepted);
    }

    // ===== menu-open =====

    [Theory]
    [InlineData("LOST")]
    [InlineData("ANSWERED-MENU")]
    [InlineData("ALTERED")]
    public void MenuCellPass_ADesktopRoute_JudgesOnlyTheNextText(string firstTruth)
    {
        Assert.True(ConditionMatrix.MenuCellPass("desktop-type", firstTruth, "OK", false, "ARRIVED", nextPass: true).Pass);
        Assert.False(ConditionMatrix.MenuCellPass("desktop-type", firstTruth, "OK", false, "ALTERED", nextPass: false).Pass);
    }

    [Fact]
    public void MenuCellPass_ThePromptRouteRefusedInWordsThenTheNextArrives_Passes()
    {
        Assert.Equal((true, "LOST/LOST"), ConditionMatrix.MenuCellPass("prompt", "LOST", "FAILED", false, "ARRIVED", nextPass: true));
    }

    [Fact]
    public void MenuCellPass_ThePromptRouteTypedIntoTheMenuAndSaidDelivered_Fails()
    {
        Assert.False(ConditionMatrix.MenuCellPass("prompt", "ANSWERED-MENU", "OK", false, "ARRIVED", nextPass: true).Pass);
        Assert.False(ConditionMatrix.MenuCellPass("prompt", "LOST", "OK", false, "ARRIVED", nextPass: true).Pass);
    }

    [Fact]
    public void MenuCellPass_ThePromptRouteRefusedButItArrivedLate_FailsAsLate()
    {
        Assert.Equal((false, "LOST/LATE"), ConditionMatrix.MenuCellPass("prompt", "LOST", "FAILED", true, "ARRIVED", nextPass: true));
    }

    [Fact]
    public void MenuShowing_ClaudesQuestionMenuUnderAFramedPromptRow_IsAMenu()
    {
        // The screen the first run failed to read (the composer reader saw the prompt row above as a composer with text).
        string[] rows =
        [
            "\u276f Token Q157A0D2CF5. Use the AskUserQuestion tool right now to ask me one question: \"Which colour?\" with the two",
            "  options Red and Blue. Do nothing else.",
            "\u2500\u2500\u2500\u2500\u2500\u2500",
            " \u2610 Colour", "", "Which colour?", "", "\u276f 1. Red", "     The colour red", "  2. Blue", "     The colour blue",
            "  3. Type something.", "\u2500\u2500\u2500\u2500\u2500\u2500", "  4. Chat about this", "",
            "Enter to select \u00b7 \u2191/\u2193 to navigate \u00b7 Esc to cancel", "", "",
        ];

        Assert.True(ConditionMatrix.MenuShowing(rows));
    }

    [Fact]
    public void MenuShowing_AnAnsweredMenuInTheHistory_IsNoMenu()
    {
        string[] rows = ["  \u23bf  \u00b7 Which colour? \u2192 Red", "Enter to select \u00b7 Esc to cancel", "a", "b", "c", "d", "e", "f", "\u276f "];

        Assert.False(ConditionMatrix.MenuShowing(rows));
    }

    [Fact]
    public void LetterToken_HasNoDigitToPickAMenuOption()
    {
        for (var i = 0; i < 50; i++)
        {
            var t = ConditionMatrix.LetterToken();
            Assert.Equal(11, t.Length);
            Assert.DoesNotContain(t, char.IsDigit);
        }
    }

    // ===== usage-limit, the echo agent's dialog =====

    [Fact]
    public void UsageDialog_DrawnByTheEchoAgent_IsReadAsShowingByTheDirectorsOwnCheck()
    {
        var drawn = ("echo-agent ready\r\n> " + EchoAgent.UsageDialog()).Split("\r\n");
        var screen = drawn.Concat(Enumerable.Repeat("", Math.Max(0, 40 - drawn.Length))).Take(40).ToList();

        Assert.True(CcDirector.Core.Sessions.UsageLimitDialog.IsShowing(screen));
    }

    [Fact]
    public void Analyze_OnlyTheEnterArrived_IsDropped()
    {
        var v = EchoAnalysis.Analyze([new EchoRead(0, Convert.ToHexString(Encoding.ASCII.GetBytes("\r")))], "Token Q1. hello");

        Assert.Equal("DROPPED", v.Verdict);
        Assert.False(v.Pass);
    }

    // ===== two runs, and what an unsteady cell is =====

    [Fact]
    public void Options_EachMatrixCellRunsTwiceAndEachScenarioOnce_UnlessRepeatSays()
    {
        var plain = Options.Parse([]);
        var three = Options.Parse(["--repeat", "3"]);

        Assert.Equal((2, 1), (plain.MatrixRepeat, plain.Repeat));
        Assert.Equal((3, 3), (three.MatrixRepeat, three.Repeat));
    }

    [Fact]
    public void Options_AnUnknownCondition_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--condition-matrix", "--conditions", "mid-turn,sideways"]));
        Assert.Equal(["mid-turn", "owner-draft", "menu-open", "usage-limit"], Options.Parse(["--conditions", "all"]).ConditionNames);
    }

    [Fact]
    public void Unsteady_CountsCellsWhoseRunsDisagree()
    {
        var results = new[] { ("a", true), ("a", false), ("b", true), ("b", true), ("c", false), ("c", false) };

        Assert.Equal(1, AgentMatrix.Unsteady(results, r => r.Item1, r => r.Item2));
    }

    // ===== the screen beside a refused send =====

    [Fact]
    public void ScreenDump_WritesEveryRowNumberedUnderScreens()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dq-screen-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var rel = ScreenDump.Write(dir, "text-claude-prompt-emoji-run1", ["first row  ", "", "❯ composer"], "claude prompt emoji run 1: FAILED");

            Assert.Equal(Path.Combine("screens", "text-claude-prompt-emoji-run1.txt"), rel);
            var lines = File.ReadAllLines(Path.Combine(dir, rel));
            Assert.StartsWith("# claude prompt emoji run 1: FAILED", lines[0]);
            Assert.Equal(["00 | first row", "01 | ", "02 | ❯ composer"], lines[1..]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("menu-open-claude-desktop-type-ascii-run1-menu", "menu-open-claude-desktop-type-ascii-run1-menu.txt")]
    [InlineData("a b/c:d", "a-b-c-d.txt")]
    public void ScreenDump_FileName_KeepsOnlySafeCharacters(string name, string file)
    {
        Assert.Equal(file, ScreenDump.FileName(name));
    }

    [Theory]
    [InlineData("OK", false)]
    [InlineData("DELIVERING", false)]
    [InlineData("REFUSED", true)]
    [InlineData("FAILED", true)]
    [InlineData("HUNG", true)]
    public void ScreenDump_IsRefusal_EverySendThatWasNotDelivered(string director, bool refused)
    {
        Assert.Equal(refused, ScreenDump.IsRefusal(director));
    }
}
