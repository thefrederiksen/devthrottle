using System.Text;
using System.Text.Json;
using CcDirector.DeliveryQualification;
using Xunit;

namespace CcDirector.DeliveryQualification.Tests;

/// <summary>
/// The parity harness's own judgement (issue #4378): how the echo layer reads the bytes a route wrote, how the strict
/// witness reads an agent's user turn, and that the texts sit exactly on the routing limits they are named after. A
/// verdict is only as good as these - a reader that took CR LF in a paste for two line breaks would report a fault the
/// product does not have, and one that took an Enter for a line break would hide the fault it does.
/// </summary>
public sealed class ParityMatrixTests
{
    private static List<EchoRead> Reads(params string[] chunks) =>
        chunks.Select((c, i) => new EchoRead(i * 10, Convert.ToHexString(Encoding.UTF8.GetBytes(c)))).ToList();

    [Fact]
    public void Analyze_TypedLineThenEnter_IsExact()
    {
        var v = EchoAnalysis.Analyze(Reads("Token Q1. hello", "\r"), "Token Q1. hello");

        Assert.Equal("EXACT", v.Verdict);
        Assert.False(v.Framed);
        Assert.Equal(1, v.Enters);
    }

    [Fact]
    public void Analyze_CarriageReturnInsideAnUnframedPaste_IsASplitSubmit()
    {
        // What the desktop paste writes for copied Windows text: the CR is an Enter, so the agent submits line one alone.
        var v = EchoAnalysis.Analyze(Reads("line one\r\nline two", "\r"), "line one\r\nline two");

        Assert.Equal("SPLIT", v.Verdict);
        Assert.Equal(2, v.Enters);
        Assert.Equal("line one", v.Received);
    }

    [Fact]
    public void Analyze_CrLfInsideABracketedPaste_IsOneLineBreak()
    {
        var v = EchoAnalysis.Analyze(Reads("\u001b[200~line one\r\nline two\u001b[201~", "\r"), "line one\r\nline two");

        Assert.Equal("EXACT", v.Verdict);
        Assert.True(v.Framed);
    }

    [Fact]
    public void Analyze_ATabInsideAPaste_IsText_ButAnUnframedTabIsTheTabKey()
    {
        Assert.Equal("EXACT", EchoAnalysis.Analyze(Reads("\u001b[200~a\tb\u001b[201~", "\r"), "a\tb").Verdict);
        Assert.Equal("ALTERED", EchoAnalysis.Analyze(Reads("a\tb", "\r"), "a\tb").Verdict);
    }

    [Fact]
    public void Routes_ATextWithATab_IsNeverTyped()
    {
        Assert.False(Routes.AppliesTo("desktop-type", "a\tb"));
        Assert.True(Routes.AppliesTo("desktop-paste", "a\tb"));
    }

    [Fact]
    public void Analyze_TheWholeTextThenASecondEnter_IsNotAPass()
    {
        var v = EchoAnalysis.Analyze(Reads("expected", "\r\r"), "expected");

        Assert.Equal("EXTRA-SUBMIT", v.Verdict);
        Assert.False(v.Pass);
    }

    [Theory]
    [InlineData("  spaced  ", "  spaced  ")]
    [InlineData("<pasted_content id=\"1\">  body\n</pasted_content>", "  body\n")]
    // Claude Code's own form for a long paste (issue #4379): the breaks round the wrapper, and the one after its opening
    // tag, are the wrapper's; the text keeps its own leading spaces.
    [InlineData("\n\n<pasted_content id=\"e942\">\n  Token body\n</pasted_content id=\"e942\">\n", "  Token body\n")]
    public void StripPasteWrapper_KeepsBoundaryWhitespace(string record, string expected)
    {
        Assert.Equal(expected, AgentRecords.StripPasteWrapper(record));
    }

    [Fact]
    public void Analyze_NoEnter_IsNotSubmitted()
    {
        Assert.Equal("NOT-SUBMITTED", EchoAnalysis.Analyze(Reads("typed but never sent"), "typed but never sent").Verdict);
    }

    [Fact]
    public void Analyze_NoBytes_IsNothing()
    {
        Assert.Equal("NOTHING", EchoAnalysis.Analyze([], "x").Verdict);
    }

    [Fact]
    public void Analyze_ALostCharacter_IsAlteredAndNamesWhere()
    {
        var v = EchoAnalysis.Analyze(Reads("Caf  au lait", "\r"), "Café au lait");

        Assert.Equal("ALTERED", v.Verdict);
        Assert.Contains("character 3", v.Detail);
    }

    [Fact]
    public void Analyze_AnAtReference_IsTheFileRoute()
    {
        var v = EchoAnalysis.Analyze(Reads("@.temp/input_20261023_101500_ab12.txt", "\r"), "a long text");

        Assert.Equal("VIA-FILE", v.Verdict);
        Assert.False(v.Pass);
    }

    [Fact]
    public void Analyze_Win32KeyEvents_AreReadAsTheirCharacters()
    {
        // The send path's form for a character outside ASCII on Windows: key down then key up, the UTF-16 unit in field 3.
        var e = "\u001b[0;0;233;1;0;1_\u001b[0;0;233;0;0;1_";
        var v = EchoAnalysis.Analyze(Reads("Caf" + e, "\r"), "Café");

        Assert.Equal("EXACT", v.Verdict);
    }

    [Fact]
    public void Analyze_BackspaceAndCtrlU_EditTheComposer()
    {
        var v = EchoAnalysis.Analyze(Reads("old draft\u0015new tex", "x\u007f", "t", "\r"), "new text");

        Assert.Equal("EXACT", v.Verdict);
    }

    [Theory]
    [InlineData("size-199", 199)]
    [InlineData("size-200", 200)]
    [InlineData("size-201", 201)]
    [InlineData("size-300", 300)]
    [InlineData("size-2000", 2000)]
    [InlineData("size-2001", 2001)]
    [InlineData("size-50k", 51200)]
    public void SizedTexts_AreExactlyTheirNamedLength(string name, int length)
    {
        var text = MatrixTexts.Named(name).Build("Q0123456789");

        Assert.Equal(length, text.Length);
        Assert.StartsWith("Token Q0123456789. ", text);
        Assert.EndsWith("ACK Q0123456789", text);
        Assert.DoesNotContain("  ", text);
    }

    [Fact]
    public void AgentDefault_NeverHoldsATextThatIsForTheEchoAgentOnly()
    {
        Assert.All(MatrixTexts.AgentDefault, t => Assert.True(t.SafeForAgents, t.Name));
        Assert.Contains(MatrixTexts.All, t => !t.SafeForAgents);
    }

    [Theory]
    [InlineData("desktop-type", "one line", true)]
    [InlineData("desktop-type", "two\nlines", false)]
    [InlineData("desktop-newline", "two\nlines", true)]
    [InlineData("desktop-newline", "one line", false)]
    [InlineData("desktop-paste", "two\nlines", true)]
    [InlineData("prompt", "one line", true)]
    public void Routes_ApplyAsAPersonWouldUseThem(string route, string text, bool applies)
    {
        Assert.Equal(applies, Routes.AppliesTo(route, text));
    }

    [Fact]
    public void Routes_TypeNothingLongerThanAPersonWould()
    {
        Assert.False(Routes.AppliesTo("desktop-type", new string('a', Routes.TypedLimit + 1)));
    }

    [Theory]
    // Codex 0.163: the prompt is a response_item message with role user, its text in input_text parts.
    [InlineData("codex", """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Token Q1. hi"}]}}""", "Token Q1. hi")]
    // Older Codex: an event_msg user_message.
    [InlineData("codex", """{"type":"event_msg","payload":{"type":"user_message","message":"Token Q1. hi"}}""", "Token Q1. hi")]
    [InlineData("claude", """{"type":"user","message":{"role":"user","content":"Token Q1.\nhi"}}""", "Token Q1.\nhi")]
    [InlineData("claude", """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Token Q1. hi"}]}}""", "Token Q1. hi")]
    [InlineData("pi", """{"type":"message","message":{"role":"user","content":[{"type":"text","text":"Token Q1. hi"}]}}""", "Token Q1. hi")]
    public void UserTurn_EachAgentsRecordShape_IsReadAsItsPromptText(string agent, string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.True(AgentRecords.IsUserTurn(agent, doc.RootElement));
        Assert.Equal(expected, AgentRecords.TurnText(agent, doc.RootElement));
    }

    [Theory]
    [InlineData("codex", """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ACK Q1"}]}}""")]
    [InlineData("claude", """{"type":"user","isMeta":true,"message":{"role":"user","content":"Token Q1"}}""")]
    [InlineData("pi", """{"type":"message","message":{"role":"assistant","content":[{"type":"text","text":"ACK Q1"}]}}""")]
    public void UserTurn_AnswersAndMetaRecords_AreNotUserTurns(string agent, string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.False(AgentRecords.IsUserTurn(agent, doc.RootElement));
    }

    /// <summary>
    /// Claude Code's api-request-blob is its copy of what it sent the model. After an @-reference it holds the file's
    /// text inside the Read tool's result, which the witness read as a second copy of the prompt, so every file-route
    /// send to Claude Code in the baseline was judged DOUBLED though the turn was recorded once (issue #4379). The
    /// witness must read the user turn and never the blob.
    /// </summary>
    [Fact]
    public void StringsOf_ClaudeApiRequestBlob_IsNotReadAsARecord()
    {
        var file = Path.Combine(Path.GetTempPath(), "witness-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(file,
            [
                """{"type":"user","message":{"role":"user","content":"@.temp/input_x.txt"}}""",
                """{"type":"api-request-blob","message":{"role":"user","content":[{"type":"text","text":"Result of calling the Read tool:\n1\tToken Q1. the payload"}]}}""",
            ]);

            var strings = AgentRecords.StringsOf(file);

            Assert.Contains("@.temp/input_x.txt", strings);
            Assert.DoesNotContain(strings, s => s.Contains("Token Q1", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
