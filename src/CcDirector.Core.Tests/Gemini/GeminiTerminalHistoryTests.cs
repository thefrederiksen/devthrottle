using System.Text;
using CcDirector.Core.Gemini;
using CcDirector.Core.History;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Gemini;

/// <summary>
/// Validates that GeminiTerminalHistory turns a session's raw terminal scrollback into the
/// canonical ConversationHistory as a SINGLE, unstructured message: ANSI escape sequences are
/// stripped, OSC 8 hyperlinks are preserved as markdown links, the conversation text survives,
/// and we never fabricate roles or tool structure (Gemini provides none).
/// </summary>
public class GeminiTerminalHistoryTests
{
    [Fact]
    public void FromText_StripsAnsiAndReturnsSingleMessage()
    {
        // A crafted scrollback: a colored prompt line and a colored answer line, with CSI color
        // codes around the visible text (ESC[ ... m).
        const string raw =
            "\x1B[1;32muser>\x1B[0m what is 2 plus 2?\r\n" +
            "\x1B[36mGemini:\x1B[0m The answer is 4.\r\n";

        var history = GeminiTerminalHistory.FromText(raw);

        // Exactly one message - we do NOT split the scrollback into structured turns.
        var message = Assert.Single(history.Messages);
        var part = Assert.Single(message.Parts);
        Assert.Equal(ConversationPartKind.Text, part.Kind);

        // The visible conversation text survives; the ANSI codes are gone.
        Assert.Contains("what is 2 plus 2?", part.Text);
        Assert.Contains("The answer is 4.", part.Text);
        Assert.DoesNotContain("\x1B[", part.Text);
        Assert.DoesNotContain("[1;32m", part.Text);
        Assert.DoesNotContain("[0m", part.Text);
    }

    [Fact]
    public void FromText_PreservesOsc8HyperlinkAsMarkdown()
    {
        // OSC 8 hyperlink: ESC]8;;URL BEL display ESC]8;; BEL
        const string raw = "See \x1B]8;;https://ai.google.dev/\x07the docs\x1B]8;;\x07 for details.";

        var history = GeminiTerminalHistory.FromText(raw);

        var part = Assert.Single(Assert.Single(history.Messages).Parts);
        Assert.Contains("[the docs](https://ai.google.dev/)", part.Text);
    }

    [Fact]
    public void FromBytes_DecodesUtf8AndCleans()
    {
        var raw = "\x1B[33mhello from gemini\x1B[0m";
        var bytes = Encoding.UTF8.GetBytes(raw);

        var history = GeminiTerminalHistory.FromBytes(bytes);

        var part = Assert.Single(Assert.Single(history.Messages).Parts);
        Assert.Equal("hello from gemini", part.Text);
    }

    [Fact]
    public void FromBuffer_ReadsTheSessionScrollback()
    {
        var buffer = new CircularTerminalBuffer();
        buffer.Write(Encoding.UTF8.GetBytes("\x1B[32mprompt one\x1B[0m\r\nresponse one\r\n"));

        var history = GeminiTerminalHistory.FromBuffer(buffer);

        var part = Assert.Single(Assert.Single(history.Messages).Parts);
        Assert.Contains("prompt one", part.Text);
        Assert.Contains("response one", part.Text);
    }

    [Fact]
    public void FromBuffer_NullBuffer_ReturnsEmpty()
    {
        Assert.Same(ConversationHistory.Empty, GeminiTerminalHistory.FromBuffer(null));
    }

    [Fact]
    public void FromBytes_Empty_ReturnsEmpty()
    {
        Assert.Same(ConversationHistory.Empty, GeminiTerminalHistory.FromBytes(System.Array.Empty<byte>()));
    }

    [Fact]
    public void FromText_OnlyAnsiNoText_ReturnsEmpty()
    {
        // A scrollback that is nothing but escape codes cleans down to nothing - no message.
        var history = GeminiTerminalHistory.FromText("\x1B[2J\x1B[H\x1B[0m");
        Assert.Empty(history.Messages);
    }
}
