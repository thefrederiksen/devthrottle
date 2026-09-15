using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Supervision;

namespace CcDirector.Gateway.Wingman;

/// <summary>The current source Wingman should speak for a session.</summary>
public sealed record WingmanNarrationSource(
    WingmanNarrationSourceKind Kind,
    string Content,
    string Identity)
{
    private const string TerminalIdentityPrefix = "terminal-failure\n";

    /// <summary>
    /// Select what is true now. A completed agent reply wins. When the person's later message has no reply,
    /// a recognized failure on the live terminal replaces the older reply; without that positive screen
    /// evidence there is nothing new to narrate.
    ///
    /// A conversation with NOTHING in it - a screen-only agent, or a Director too old to store one - is
    /// answered the same way: the live terminal is the only source there is, so a recognized failure on it
    /// is the source, and anything else is nothing to narrate.
    ///
    /// THIS IS THE ONE PLACE THAT QUESTION IS ANSWERED, AND SINCE SLICE C EVERY CALLER ASKS IT THE SAME WAY.
    /// Its only caller is the turn-verdict seat: the package builder for a judged stop, and the seat itself for
    /// the source text a reused verdict is played with - both over the one screen read that stop was judged on.
    /// The voice narration, the explain route and the spoken reply no longer select a source at all; they speak
    /// the verdict's spoken section and carry its package kind. So a screen-only session whose turn ended on a
    /// visible failure is terminal-failure on the verdict path and on the voice path alike, because it is the
    /// same answer. Before slice C the voice callers returned "voice unavailable" or "nothing to narrate" for
    /// an empty or unsupported conversation without reading the screen, and the two paths disagreed.
    /// </summary>
    public static WingmanNarrationSource? Select(
        IReadOnlyList<TurnWidgetDto>? widgets,
        IReadOnlyList<string>? liveRows)
    {
        var (agent, user) = LatestSpeakerIndexes(widgets);
        if (agent >= user && agent >= 0)
        {
            var reply = widgets![agent].Content?.Trim() ?? "";
            return reply.Length == 0
                ? null
                : new WingmanNarrationSource(WingmanNarrationSourceKind.AgentReply, reply, reply);
        }

        // Either the person spoke last, or nobody has spoken at all (user < 0 means agent < 0 too, because the
        // first branch takes every case where an agent spoke). Either way the screen is the evidence.
        var fault = TerminatingFaultClassifier.Classify(liveRows);
        if (fault.Class == SessionFaultClass.None) return null;

        var terminal = string.Join('\n', TerminatingFaultClassifier.ContentWindow(liveRows)).Trim();
        return terminal.Length == 0
            ? null
            : new WingmanNarrationSource(
                WingmanNarrationSourceKind.TerminalFailure,
                terminal,
                TerminalIdentityPrefix + terminal);
    }

    /// <summary>
    /// True when the conversation ends with the person's words rather than an agent reply - so any narration
    /// made before that message answers an earlier request. False for a conversation with nothing in it.
    /// </summary>
    public static bool EndsWithALaterUserMessage(IReadOnlyList<TurnWidgetDto>? widgets)
    {
        var (agent, user) = LatestSpeakerIndexes(widgets);
        return user > agent;
    }

    private static (int Agent, int User) LatestSpeakerIndexes(IReadOnlyList<TurnWidgetDto>? widgets)
    {
        var agent = -1;
        var user = -1;
        if (widgets is null) return (agent, user);

        for (var i = 0; i < widgets.Count; i++)
        {
            if (string.Equals(widgets[i].Kind, StoredConversationWidgets.AgentTextKind, StringComparison.Ordinal))
                agent = i;
            else if (string.Equals(widgets[i].Kind, StoredConversationWidgets.UserTextKind, StringComparison.Ordinal))
                user = i;
        }
        return (agent, user);
    }
}

public enum WingmanNarrationSourceKind
{
    AgentReply,
    TerminalFailure,
}
