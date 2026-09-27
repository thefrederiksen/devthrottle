using System.Security.Cryptography;
using System.Text;
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
    private const string AgentReplyIdentityPrefix = "agent-reply@";
    private const string TerminalIdentityPrefix = "terminal-failure@";

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
    ///
    /// <paramref name="generation"/> and <paramref name="completedTurns"/> only name WHICH occurrence the source is
    /// (see <see cref="Identity"/>); they never change which source is chosen or what it says. A caller that stores
    /// the fingerprint passes both.
    /// </summary>
    /// <param name="generation">The stored generation the widgets belong to (<see cref="StoredConversation.Generation"/>).</param>
    /// <param name="completedTurns">The Director's count of the turns this session has finished
    /// (<see cref="SessionDto.TurnCount"/>); null when the Director does not report it.</param>
    public static WingmanNarrationSource? Select(
        IReadOnlyList<TurnWidgetDto>? widgets,
        IReadOnlyList<string>? liveRows,
        string generation = "",
        int? completedTurns = null)
    {
        var (agent, user) = LatestSpeakerIndexes(widgets);
        if (agent >= user && agent >= 0)
        {
            var reply = widgets![agent].Content?.Trim() ?? "";
            return reply.Length == 0
                ? null
                : new WingmanNarrationSource(WingmanNarrationSourceKind.AgentReply, reply,
                    AgentReplyIdentityPrefix + Occurrence(widgets, agent, generation) + reply);
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
                TerminalIdentityPrefix + TurnOccurrence(completedTurns) + Occurrence(widgets, user, generation) + terminal);
    }

    /// <summary>
    /// WHERE a source happened, as the first line of its <see cref="Identity"/>: the stored generation, the widget's
    /// position in it, and the time its message was recorded when the agent records one. For a reply that is the reply
    /// itself; for a terminal failure it is the person's unanswered message the failure followed.
    ///
    /// ONE STOP IS ONE OCCURRENCE, NOT ONE TEXT (review of pull request 3445). Two turns often end with the same words -
    /// "Done.", the same question, the same failure - and when the Gateway misses the Working edge between them, the
    /// words alone would name the second stop as the first, and it would never be read. The stored conversation is the
    /// contiguous prefix of ONE generation and is append-only, so inside a generation a stored reply never moves. A new
    /// generation starts again at position zero, and Grok's transcript records no time at all, so position and time
    /// alone repeat across a new Grok conversation (round 2 of that review): the generation is what makes the position
    /// an occurrence. "-1" when nothing was said at all - a screen-only agent.
    /// </summary>
    private static string Occurrence(IReadOnlyList<TurnWidgetDto>? widgets, int index, string generation)
    {
        var at = index >= 0 && widgets is not null ? widgets[index].Timestamp?.UtcDateTime.ToString("O") ?? "" : "";
        return $"{generation}|{index}|{at}\n";
    }

    /// <summary>
    /// WHICH FINISHED TURN a terminal failure ended, as the Director counted it (<see cref="SessionDto.TurnCount"/>), on
    /// the first line of a failure's <see cref="Identity"/>. A failure is read off the screen, and when the person's
    /// retry never reached the stored conversation - a screen-only agent above all, which stores nothing - the
    /// conversation has no new place for the second failure, and the same words at the same place would name it as
    /// the first. The Director counts a turn at the flip into waiting for input, on its own terminal, whether or not
    /// the Gateway's fifteen-second sample saw the Working edge between; a redraw is not a flip, so the same stop keeps
    /// its count.
    ///
    /// Only a failure carries it. A reply already has a place of its own in the stored conversation, and a count that
    /// restarts with the Director would only make that reply look new after a restart.
    ///
    /// The count restarts with the Director, so a Director restart during a failed stop reads that failure once more:
    /// one call, where the alternative is a missed stop. "turn ?" when the Director does not report the count (older
    /// than the field) - there the failure's own words and place are all there is, and two identical failures in a row
    /// with a missed Working edge are still read as one.
    /// </summary>
    private static string TurnOccurrence(int? completedTurns)
        => completedTurns is { } n ? $"turn {n}\n" : "turn ?\n";

    /// <summary>
    /// The fingerprint of a stop's source, stored on its reading as <see cref="TurnVerdictDto.SourceHash"/>: a hash of
    /// <see cref="Identity"/> - which occurrence it is and what it says - so a reply and a terminal failure with the same
    /// words are never the same stop, and neither are two replies with the same words at different places. ""
    /// when there is no source - the screen is then the only evidence there is, and nothing can name the stop.
    /// </summary>
    public static string Fingerprint(WingmanNarrationSource? source)
        => source is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Identity)));

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
