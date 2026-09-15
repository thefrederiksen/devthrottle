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
    /// True when the conversation ends with the person's words rather than an agent reply. Only this
    /// shape needs a live-screen read to decide whether the turn failed before its reply could be stored.
    /// </summary>
    public static bool NeedsLiveScreen(IReadOnlyList<TurnWidgetDto>? widgets)
    {
        var (agent, user) = LatestSpeakerIndexes(widgets);
        return user > agent;
    }

    /// <summary>
    /// Select what is true now. A completed agent reply wins. When the person's later message has no reply,
    /// a recognized failure on the live terminal replaces the older reply; without that positive screen
    /// evidence there is nothing new to narrate.
    ///
    /// A conversation with NOTHING in it - a screen-only agent, or a Director too old to store one - is
    /// answered the same way: the live terminal is the only source there is, so a recognized failure on it
    /// is the source, and anything else is nothing to narrate. This is the ONE place that question is
    /// answered. Every caller reads it here rather than classifying the screen itself, because a second
    /// rule for the same question is how two parts of one product come to disagree about what a session
    /// just did.
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

        // user < 0 here means agent < 0 too - the first branch takes every case where an agent spoke -
        // so this is the conversation with nothing in it, and the screen is all there is.
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
