using System.Text;
using CcDirector.Core.History;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Gemini;

/// <summary>
/// Builds the canonical <see cref="ConversationHistory"/> for a Gemini session from the
/// session's own terminal buffer.
///
/// Gemini is the exception among the agents: it persists no usable transcript (its
/// logs.json records the user's prompts only, never the model's responses), so there is no
/// file to parse into structured turns. The saving grace is that Gemini runs in the normal
/// terminal buffer, so the full conversation is already present as plain text in the
/// session's scrollback. We therefore read that scrollback, strip the ANSI escape
/// sequences, and present it as a SINGLE, unstructured message - clearly labeled as raw
/// terminal text. We deliberately do NOT fake roles, turns, or tool structure: Gemini gives
/// us none, so we claim none.
/// </summary>
public static class GeminiTerminalHistory
{
    /// <summary>
    /// The label shown alongside Gemini's history so the user understands the fidelity: this
    /// is the raw terminal scrollback, not a structured transcript.
    /// </summary>
    public const string Label = "Raw terminal text - Gemini provides no structured transcript.";

    /// <summary>
    /// Build a single-message history from a session's terminal buffer. Returns
    /// <see cref="ConversationHistory.Empty"/> when the buffer is absent or has no text yet.
    /// </summary>
    public static ConversationHistory FromBuffer(CircularTerminalBuffer? buffer)
    {
        if (buffer is null)
            return ConversationHistory.Empty;

        return FromBytes(buffer.DumpAll());
    }

    /// <summary>
    /// Build a single-message history from raw terminal bytes (UTF-8). Exposed for testing
    /// without a live session.
    /// </summary>
    public static ConversationHistory FromBytes(byte[] rawTerminalBytes)
    {
        if (rawTerminalBytes is null || rawTerminalBytes.Length == 0)
            return ConversationHistory.Empty;

        return FromText(Encoding.UTF8.GetString(rawTerminalBytes));
    }

    /// <summary>
    /// Build a single-message history from raw terminal text. Strips ANSI and returns one
    /// Assistant message carrying the cleaned text; empty when nothing remains after cleaning.
    /// </summary>
    public static ConversationHistory FromText(string rawTerminalText)
    {
        // Reuse the Core-resident terminal cleaner (CcDirector.Core.Utilities). It strips CSI/
        // OSC/two-char escapes, preserves OSC 8 hyperlink URLs as markdown links, and collapses
        // runs of blank lines - exactly what a readable scrollback dump needs. The dedicated
        // AnsiCleaner lives in CcDirector.ControlApi / CcDirector.Gateway, which Core must not
        // reference, so TerminalOutputParser is the right Core-accessible choice and avoids
        // adding a second ANSI implementation.
        var clean = TerminalOutputParser.StripAnsi(rawTerminalText ?? string.Empty);
        if (clean.Length == 0)
            return ConversationHistory.Empty;

        var part = new ConversationPart(ConversationPartKind.Text, clean);
        var message = new ConversationMessage(ConversationRole.Assistant, new[] { part });
        return new ConversationHistory(new[] { message });
    }
}
