using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// The conversation WINDOW (issue #4372): a Chat screen is sent only the last few turns of a conversation, and
/// fetches the turns before them when the reader scrolls back.
///
/// Why. The Chat was the tab every session opens on, and it read the whole conversation every time: up to 850 KB
/// compressed and two thousand messages for a long session, then drew every one of them, when the reader looks at
/// the bottom. The window is what is on screen; the rest is fetched only if the reader goes looking for it.
///
/// WHAT A TURN IS. A turn starts at a user message that carries words: a prompt, whoever typed it. A user message
/// that is only tool results fed back to the agent is not a turn - the Chat draws it as a "Tool result" bubble, not
/// as a prompt (packages/client-core/src/history/bubbleMapper.ts) - and neither is a user message with no words.
/// A window of N turns is everything from the Nth-last turn's first message to the end, so a turn is never cut in
/// half. A conversation with fewer than N turns is sent whole.
///
/// The window changes nothing about what the cursor proves. The cursor still names the WHOLE conversation up to the
/// end of the answer (<see cref="ConversationTail"/>), because what it proves is what the Gateway would send, not
/// what the client kept. So a client holding only the window is sent exactly the same tails as one holding all of it.
/// </summary>
public static class ConversationWindow
{
    /// <summary>The query parameter that asks for a window: how many turns to send from the end.</summary>
    public const string TurnsParameter = "turns";

    /// <summary>The query parameter that asks for the turns BEFORE a message index, instead of the latest ones.</summary>
    public const string BeforeParameter = "before";

    /// <summary>The most turns one answer is cut to. A larger number is not a window, and is refused.</summary>
    public const int MaxTurns = 500;

    /// <summary>True when <paramref name="message"/> starts a turn - see the class remarks.</summary>
    public static bool StartsTurn(HistoryMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Role, "User", StringComparison.Ordinal))
            return false;
        var parts = message.Parts;
        if (parts.Count > 0 && parts.All(p => string.Equals(p.Kind, "ToolResult", StringComparison.Ordinal)))
            return false;
        return parts.Any(p => string.Equals(p.Kind, "Text", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Text));
    }

    /// <summary>
    /// The index of the first message of the <paramref name="turns"/>th-last turn that starts before
    /// <paramref name="end"/>, or 0 when there are fewer turns than that before it.
    /// </summary>
    public static int StartOfLastTurns(IReadOnlyList<HistoryMessageDto> messages, int end, int turns)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, messages.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(turns, 1);

        var seen = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            if (!StartsTurn(messages[i]))
                continue;
            seen++;
            if (seen == turns)
                return i;
        }
        return 0;
    }

    /// <summary>
    /// Read the window parameters off a request. Neither present is the whole-conversation answer every older client
    /// gets. Returns false with <paramref name="error"/> set when a parameter is present but is not a usable number,
    /// so a malformed request is refused rather than quietly answered in full.
    /// </summary>
    public static bool TryRead(string? turnsRaw, string? beforeRaw, out int? turns, out int? before, out string? error)
    {
        turns = null;
        before = null;
        error = null;
        if (turnsRaw is not null)
        {
            if (!int.TryParse(turnsRaw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var t)
                || t < 1 || t > MaxTurns)
            {
                error = $"'{TurnsParameter}' must be a whole number from 1 to {MaxTurns}";
                return false;
            }
            turns = t;
        }
        if (beforeRaw is not null)
        {
            if (turns is null)
            {
                error = $"'{BeforeParameter}' needs '{TurnsParameter}': it asks for the turns before a message";
                return false;
            }
            if (!int.TryParse(beforeRaw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var b)
                || b < 1)
            {
                error = $"'{BeforeParameter}' must be a message index of 1 or more";
                return false;
            }
            before = b;
        }
        return true;
    }
}
