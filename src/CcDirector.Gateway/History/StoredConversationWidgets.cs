using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// One session's stored conversation as the narration path sees it: whether this agent keeps a readable
/// conversation at all, and the words if it does.
///
/// The flag is here because losing it lost a capability. When the narration read the transcript down the
/// tunnel, an agent with no history provider answered "unsupported", and that recorded a TERMINAL verdict -
/// the voice screen said so, and the sweep stopped spending its small per-cycle budget on a session that
/// could never produce a narration. Reading the store instead removed the failing read and, with it, the
/// only producer of that verdict: such a session would have gone back to reading as an ordinary quiet wait,
/// forever, with nothing said on any screen. That is the exact shape of the silence issue #2561 was filed
/// about, arriving from the other direction. The Director already pushes the fact, so it is carried here.
/// </summary>
/// <param name="IsSupported">This agent keeps a conversation that can be read. False is terminal: no amount
/// of waiting or retrying will produce words to narrate.</param>
/// <param name="Widgets">The conversation, in the shape the wingman already reads. Empty is ordinary - a
/// session that has not spoken yet.</param>
public readonly record struct StoredConversation(bool IsSupported, IReadOnlyList<TurnWidgetDto> Widgets);

/// <summary>
/// Turns the Gateway's STORED conversation into the widget list the wingman already reads (the turn-push
/// mission, phase 3).
///
/// Why an adapter rather than a new shape. The narration path reads a conversation through
/// <c>WingmanTranslator.BuildRecentContext</c> and by taking the last agent text - both written against
/// <see cref="TurnWidgetDto"/>, both well exercised, and neither having anything to do with where the
/// conversation came from. Changing the SOURCE from a tunnel read of the user's transcript to a read of
/// stored rows is the whole point of this phase; changing the shape the translator sees at the same time
/// would mean the narration could come out different and nobody could say which change did it. So the
/// shape is preserved exactly and only the source moves.
///
/// The mapping is the one the Director's own widget builder made from the same messages: an agent's text
/// is <c>Text</c> (the narration reads the last of these), a person's text is <c>UserMessage</c>, and
/// everything else is labelled with its own kind, which is all <c>BuildRecentContext</c> asks of it.
/// </summary>
public static class StoredConversationWidgets
{
    /// <summary>The kind a widget carries for an assistant's spoken-aloud text - what the narration reads.</summary>
    public const string AgentTextKind = "Text";

    /// <summary>The kind a widget carries for something the person typed or said.</summary>
    public const string UserTextKind = "UserMessage";

    public static List<TurnWidgetDto> From(IReadOnlyList<HistoryMessageDto>? messages)
    {
        var widgets = new List<TurnWidgetDto>();
        if (messages is null) return widgets;
        foreach (var message in messages)
        {
            if (message?.Parts is null) continue;
            // Text becomes the NARRATABLE kind only for an assistant. The stored role comes from
            // ConversationRole, which today has exactly User and Assistant - but "anything that is not the
            // user is the agent" would quietly make a system or tool role narratable if that enum ever
            // grows, and the thing it would narrate is text the person never heard the agent say (found in
            // review). An unrecognised role keeps its own name as the widget kind: it still reaches the
            // context window, and it can never be picked as the reply to read aloud.
            var isAssistant = string.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase);
            var isUser = string.Equals(message.Role, "User", StringComparison.OrdinalIgnoreCase);
            foreach (var part in message.Parts)
            {
                if (part is null) continue;
                var text = part.Text ?? "";
                // A part with no text carries nothing the wingman can use, and an empty widget only dilutes
                // the recent-context window that the narration's quality depends on.
                if (text.Trim().Length == 0) continue;
                var kind = !string.Equals(part.Kind, "Text", StringComparison.Ordinal) ? part.Kind ?? ""
                         : isAssistant ? AgentTextKind
                         : isUser ? UserTextKind
                         : string.IsNullOrEmpty(message.Role) ? "UnknownRole" : message.Role;
                widgets.Add(new TurnWidgetDto
                {
                    Kind = kind,
                    Content = text,
                    Header = part.ToolName ?? "",
                    ToolUseId = part.ToolId ?? "",
                    Timestamp = message.Timestamp,
                });
            }
        }
        return widgets;
    }

    /// <summary>The agent's most recent spoken text - what a narration is made from - or null when the
    /// conversation holds none. A conversation whose last word is the person's, or which is all tool work,
    /// genuinely has nothing to read aloud, and that is a fact about the conversation rather than a
    /// failure to read it.</summary>
    public static string? LastAgentText(IReadOnlyList<TurnWidgetDto>? widgets)
    {
        if (widgets is null) return null;
        for (var i = widgets.Count - 1; i >= 0; i--)
            if (string.Equals(widgets[i].Kind, AgentTextKind, StringComparison.Ordinal))
                return widgets[i].Content;
        return null;
    }
}
