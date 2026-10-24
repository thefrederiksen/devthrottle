using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The one platform encoder (issue #4379): what the desktop terminal and the Director's own send write for a typed text
/// and for a paste. Each test names the failure from the parity baseline (23 October 2026) it guards.
/// </summary>
public sealed class TerminalInputEncoderTests
{
    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static string Utf8(IEnumerable<byte[]> writes) => Encoding.UTF8.GetString(writes.SelectMany(w => w).ToArray());

    // ---------------------------------------------------------------------------------------------
    // Line endings
    // ---------------------------------------------------------------------------------------------

    /// <summary>Codex read both halves of a CR LF inside a paste as line breaks, so every break of a Windows clipboard
    /// text arrived doubled. A CR LF, a lone CR and a lone LF must each become exactly one LF.</summary>
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\n\r\nb\rc\nd", "a\n\nb\nc\nd")]
    [InlineData("no breaks", "no breaks")]
    public void NormalizeLineBreaks_EveryKindOfBreak_BecomesOneLf(string text, string expected)
    {
        Assert.Equal(expected, TerminalInputEncoder.NormalizeLineBreaks(text));
    }

    // ---------------------------------------------------------------------------------------------
    // Which texts cannot be typed
    // ---------------------------------------------------------------------------------------------

    /// <summary>Typed, a line break is Enter (the text submits in pieces) and a tab is the Tab key (the tabs vanished on
    /// every agent in the baseline). Such a text must be recognised so it goes as a paste. Ordinary characters,
    /// accented letters and emoji are typed.</summary>
    [Theory]
    [InlineData("a\tb", true)]
    [InlineData("a\nb", true)]
    [InlineData("a\rb", true)]
    [InlineData("a\u001bb", true)]
    [InlineData("plain text, punctuation!? café \U0001F600", false)]
    public void HoldsControlCharacters_TabsAndBreaks_AreNotTypeable(string text, bool expected)
    {
        Assert.Equal(expected, TerminalInputEncoder.HoldsControlCharacters(text));
    }

    // ---------------------------------------------------------------------------------------------
    // Paste framing
    // ---------------------------------------------------------------------------------------------

    /// <summary>The desktop paste went as plain keystrokes, so each line break was an Enter. With bracketed paste on,
    /// the paste must be ONE paste: the start marker first, the end marker last, and between them the text with its
    /// breaks as single LFs, its tabs as tabs, and no CR anywhere (a CR is what Enter sends).</summary>
    [Fact]
    public void Paste_Bracketed_IsOneFramedPasteWithBreaksAsLfAndTabsKept()
    {
        var writes = TerminalInputEncoder.Paste("line one\r\nline\ttwo\rthree", bracketed: true, nonAsciiAsKeyEvents: false, chunkSize: 4).ToList();

        Assert.Equal(TerminalInputEncoder.PasteStart, writes[0]);
        Assert.Equal(TerminalInputEncoder.PasteEnd, writes[^1]);
        var inside = Utf8(writes.Skip(1).SkipLast(1));
        Assert.Equal("line one\nline\ttwo\nthree", inside);
        Assert.DoesNotContain('\r', inside);
        Assert.True(writes.Count > 3, "a small chunk size must give several writes between the markers");
    }

    /// <summary>
    /// REVIEW OF #4398, FINDING 1: text holding the paste's own end marker closed the paste early, and the rest was typed
    /// live - a following "!" put the agent in shell mode and the submit's Enter ran it. Whether the marker sits whole in the
    /// text, is cut across writes by a one-byte chunk size, or would be rebuilt by taking out a marker nested inside
    /// another, the only end marker in what is written must be the encoder's own, last.
    /// </summary>
    [Theory]
    [InlineData("first line\u001b[201~\n!run this", 4096)]
    [InlineData("first line\u001b[201~\n!run this", 1)]
    [InlineData("a\u001b[20\u001b[201~1~\n!run this", 1)]
    public void Paste_TextHoldingTheEndMarker_CannotEndThePasteEarly(string escaped, int chunkSize)
    {
        var text = System.Text.RegularExpressions.Regex.Unescape(escaped);

        var stream = Ascii(TerminalInputEncoder.Paste(text, bracketed: true, nonAsciiAsKeyEvents: false, chunkSize).SelectMany(w => w).ToArray());

        Assert.Equal(stream.Length - 6, stream.IndexOf("\u001b[201~", StringComparison.Ordinal));
        Assert.Equal(1, stream.Split("\u001b[201~").Length - 1);
        Assert.DoesNotContain('\u001b', stream[6..^6]);
        Assert.EndsWith("\n!run this\u001b[201~", stream);
    }

    /// <summary>A program that never asked for bracketed paste cannot be told a paste is a paste, so the text goes as a
    /// terminal sends it: no markers.</summary>
    [Fact]
    public void Paste_NotBracketed_HasNoMarkers()
    {
        var writes = TerminalInputEncoder.Paste("a\r\nb", bracketed: false, nonAsciiAsKeyEvents: false, chunkSize: 64).ToList();

        Assert.Equal("a\nb", Utf8(writes));
        Assert.DoesNotContain(writes, w => w.SequenceEqual(TerminalInputEncoder.PasteStart) || w.SequenceEqual(TerminalInputEncoder.PasteEnd));
    }

    /// <summary>The paste's text is split into writes; a split inside a surrogate pair turned an emoji into two U+FFFD.
    /// Every write must be whole UTF-8, whatever the chunk size.</summary>
    [Fact]
    public void Paste_SmallChunks_NeverSplitAnEmojiOrAJoinedEmoji()
    {
        const string text = "x\U0001F600y\U0001F469‍\U0001F4BBz";

        var writes = TerminalInputEncoder.Paste(text, bracketed: true, nonAsciiAsKeyEvents: false, chunkSize: 1).ToList();

        foreach (var w in writes.Skip(1).SkipLast(1))
            Assert.Equal(w, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(w)));
        Assert.Equal(text, Utf8(writes.Skip(1).SkipLast(1)));
    }

    /// <summary>THE WINDOWS PATH, which only a Windows run can exercise end to end: inside a paste on a Windows pseudo
    /// console, a character outside ASCII still goes as a win32-input-mode key press (issue #3290), the line break as
    /// LF, and the markers are untouched.</summary>
    [Fact]
    public void Paste_WindowsKeyEvents_NonAsciiInsideThePasteIsAKeyPress()
    {
        var writes = TerminalInputEncoder.Paste("é\r\nb", bracketed: true, nonAsciiAsKeyEvents: true, chunkSize: 64).ToList();

        Assert.Equal(TerminalInputEncoder.PasteStart, writes[0]);
        Assert.Equal(TerminalInputEncoder.PasteEnd, writes[^1]);
        Assert.Equal("\u001b[0;0;233;1;0;1_\u001b[0;0;233;0;0;1_\nb", Ascii(writes.Skip(1).SkipLast(1).SelectMany(w => w).ToArray()));
    }

    // ---------------------------------------------------------------------------------------------
    // Typed text: the same encoder for the desktop and the send
    // ---------------------------------------------------------------------------------------------

    /// <summary>The desktop typed raw UTF-8 while the send path typed key presses on Windows. The encoder is the one both
    /// now call: ASCII as it is, non-ASCII as key presses on a pseudo console, plain UTF-8 elsewhere.</summary>
    [Fact]
    public void Encode_TheSameTextOnBothPlatforms_DiffersOnlyInTheNonAsciiCharacter()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("café"), TerminalInputEncoder.Encode("café", nonAsciiAsKeyEvents: false));
        Assert.Equal("caf\u001b[0;0;233;1;0;1_\u001b[0;0;233;0;0;1_", Ascii(TerminalInputEncoder.Encode("café", nonAsciiAsKeyEvents: true)));
    }

    /// <summary>Only a Windows pseudo console needs key presses; a terminal that is not one (here a test double, as on
    /// Linux and macOS) gets UTF-8.</summary>
    [Fact]
    public void SendsNonAsciiAsKeyEvents_ABackendThatIsNotAPseudoConsole_IsFalse()
    {
        Assert.False(TerminalInputEncoder.SendsNonAsciiAsKeyEvents(new StubSessionBackend()));
    }

    // ---------------------------------------------------------------------------------------------
    // The newline key
    // ---------------------------------------------------------------------------------------------

    /// <summary>Shift+Enter sent a plain Enter, which submitted the first line alone. The rig measured that Ctrl+J (LF)
    /// makes a new line in Claude Code, Codex and Pi; an agent not measured gets nothing, so the terminal keeps its old
    /// key for it.</summary>
    [Theory]
    [InlineData(AgentKind.ClaudeCode, true)]
    [InlineData(AgentKind.Codex, true)]
    [InlineData(AgentKind.Pi, true)]
    [InlineData(AgentKind.Gemini, false)]
    [InlineData(AgentKind.OpenCode, false)]
    public void ComposerNewlineKey_MeasuredAgents_GetCtrlJ(AgentKind agent, bool measured)
    {
        var key = ComposerNewlineKey.For(agent);

        if (measured) Assert.Equal(new byte[] { 0x0A }, key);
        else Assert.Null(key);
    }
}
