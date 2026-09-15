using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Assembles the one package a stop is judged from: the live screen, the agent's latest reply or the
/// failure that replaced it, the four turns before it, and the handful of facts that let a judge tell
/// a report apart from an ask.
///
/// It lives in the Gateway rather than beside the record it builds, because it walks the Gateway's
/// STORED conversation and the Gateway's screen read, and CcDirector.Core cannot reference the
/// Gateway. The record and the contract stay in Core so that the same package can be rebuilt from a
/// saved turn-log bundle and replayed against a different judge with no Gateway in the room.
///
/// The source selection is the one the narration path uses, and it is the ONLY one
/// (<see cref="WingmanNarrationSource"/>): a completed agent reply wins; when the person's later
/// message has no reply, or when there is no stored conversation at all, a recognised failure on the
/// live terminal takes its place. This builder has no rule of its own and never classifies the screen
/// itself. It did, for one round, and that second rule is how two parts of one product come to
/// disagree about what a session just did.
/// </summary>
public static class TurnVerdictPackageBuilder
{
    /// <summary>
    /// Build the package for one stop. Never null: even a session with no readable conversation and a
    /// blank screen produces a package, because "there was not enough to judge" is an answer the
    /// contract has a word for (cannot-tell) and a silent skip is not.
    /// </summary>
    /// <param name="signal">The observed turn boundary. It carries no cause and no confidence in this
    /// build - no producer stamps either - so <see cref="TurnVerdictPackage.TurnEndCause"/> and
    /// <see cref="TurnVerdictPackage.TurnEndConfidence"/> are null for every session today, and the
    /// judge is told so. They are read from here the day the switching design starts stamping them.</param>
    /// <param name="session">The session as the push store last saw it.</param>
    /// <param name="conversation">The stored conversation, and whether this agent keeps one at all.</param>
    /// <param name="screen">The live grid, or null when the screen could not be read. An unreadable
    /// screen is carried as no rows rather than as an empty screen that looks read.</param>
    /// <param name="previousVerdictLabel">The label this session's last verdict carried, or null.</param>
    public static TurnVerdictPackage Build(
        TurnEndSignal signal,
        SessionDto session,
        StoredConversation conversation,
        ScreenGridResponse? screen,
        string? previousVerdictLabel)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(session);

        var rows = screen?.HasGrid == true && screen.Rows is { Count: > 0 }
            ? (IReadOnlyList<string>)screen.Rows
            : Array.Empty<string>();

        var widgets = conversation.Widgets ?? (IReadOnlyList<TurnWidgetDto>)Array.Empty<TurnWidgetDto>();
        var conversationAvailable = conversation.IsSupported && widgets.Count > 0;

        var (kind, latestReply, failureText, replyIndex) = SelectSource(widgets, rows);

        var package = new TurnVerdictPackage
        {
            ScreenRows = rows,
            CursorRow = screen?.CursorRow ?? -1,
            IsAlternateScreen = screen?.IsAlternateScreen ?? false,
            ScreenHash = rows.Count == 0 ? "" : WingmanScreenVerdictCache.HashRows(rows),
            Kind = kind,
            LatestReply = latestReply,
            FailureText = failureText,
            RecentTurns = BuildRecentTurns(widgets, replyIndex),
            SessionTitle = string.IsNullOrWhiteSpace(session.Name) ? null : session.Name.Trim(),
            FirstUserPrompt = FirstUserPrompt(widgets),
            PreviousVerdictLabel = string.IsNullOrWhiteSpace(previousVerdictLabel) ? null : previousVerdictLabel,
            AgentKind = string.IsNullOrWhiteSpace(session.Agent) ? null : session.Agent,
            TurnEndCause = null,
            TurnEndConfidence = null,
            PendingWakeUps = null,
            NextScheduledWakeUtc = null,
            ConversationAvailable = conversationAvailable,
        };

        FileLog.Write(
            $"[TurnVerdictPackageBuilder] Build: session={signal.SessionId}, "
            + $"kind={TurnVerdictPackage.WireName(kind)}, conversation={conversationAvailable}, "
            + $"screenRows={rows.Count}, replyChars={latestReply?.Length ?? 0}, "
            + $"failureChars={failureText?.Length ?? 0}, recentTurnsChars={package.RecentTurns.Length}");

        return package;
    }

    /// <summary>
    /// What this stop is made of, and where the current reply sits in the conversation so the recent
    /// turns can stop short of it.
    ///
    /// There is ONE selection rule and it is <see cref="WingmanNarrationSource.Select"/>. This method
    /// asks it and does as it says; it never classifies the screen itself. A local second rule was what
    /// let the verdict path and the voice path disagree about the same stop.
    ///
    /// When Select yields no source at all, the stop is an agent-reply stop with NO reply. That is the
    /// honest shape: there is nothing the agent said to judge, the prompt already says in as many words
    /// what an empty reply means, and the receipt check then binds any calm verdict to the screen alone.
    /// Whether a conversation existed at all is carried separately, in
    /// <see cref="TurnVerdictPackage.ConversationAvailable"/>.
    /// </summary>
    private static (TurnVerdictPackageKind Kind, string? Reply, string? Failure, int ReplyIndex) SelectSource(
        IReadOnlyList<TurnWidgetDto> widgets,
        IReadOnlyList<string> rows)
    {
        var source = WingmanNarrationSource.Select(widgets, rows);
        if (source is { Kind: WingmanNarrationSourceKind.AgentReply })
            return (TurnVerdictPackageKind.AgentReply, StartCut(source.Content), null, LastAgentIndex(widgets));

        if (source is { Kind: WingmanNarrationSourceKind.TerminalFailure })
            return (TurnVerdictPackageKind.TerminalFailure, null, StartCut(source.Content), widgets.Count);

        return (TurnVerdictPackageKind.AgentReply, null, null, widgets.Count);
    }

    private static int LastAgentIndex(IReadOnlyList<TurnWidgetDto> widgets)
    {
        for (var i = widgets.Count - 1; i >= 0; i--)
            if (string.Equals(widgets[i].Kind, StoredConversationWidgets.AgentTextKind, StringComparison.Ordinal))
                return i;
        return widgets.Count;
    }

    /// <summary>Keep the END of a long text. The decisive sentence of a reply is at its end - the ask,
    /// the result, the recommendation - so a reply that outgrows the bound loses its opening, not its
    /// conclusion.</summary>
    private static string StartCut(string text)
        => text.Length <= TurnVerdictPackage.MaxLatestReplyChars
            ? text
            : text[^TurnVerdictPackage.MaxLatestReplyChars..];

    /// <summary>
    /// The last four whole turns BEFORE the one being judged, oldest first.
    ///
    /// A turn begins at something the person said and runs to the next thing they said. Tool calls and
    /// their results are dropped: they are most of the bulk of a conversation and almost none of its
    /// meaning, and the judge is being asked what the stop MEANS, not what the agent read. The block is
    /// cut from its OLDEST end, because the turn nearest the stop is the one that explains it.
    ///
    /// The turn being judged is excluded, ALWAYS. It is the last thing the person said within the window
    /// being read, whatever mixture of agent text, tool calls and more agent text follows it - the stop
    /// is the whole of that turn, not just its final reply. The person's message that opened it is not
    /// "before" the stop, it is part of it, and letting it in would spend one of the four slots on an ask
    /// the judge is already looking at the answer to.
    ///
    /// This used to be decided by asking whether the last turn contained any agent text, which is true of
    /// every turn that did any work before replying. A turn made of agent text, a tool call and then the
    /// reply was therefore kept, and the judge saw three turns of history and the ask it was already
    /// answering rather than four turns of history.
    ///
    /// NOT CARRIED, and it is a real gap rather than an oversight: on a stop with NO reply, the person's
    /// last message is the trailing turn, so it is excluded here and reaches the judge only through the
    /// live screen. The frozen package has no field for the current ask, and inventing one here would
    /// have made "the last four full turns" mean something other than what it says.
    /// </summary>
    internal static string BuildRecentTurns(IReadOnlyList<TurnWidgetDto> widgets, int upperBound)
    {
        if (widgets.Count == 0) return "";
        var end = Math.Clamp(upperBound, 0, widgets.Count);

        // Where each turn starts: every point the person spoke, within what we are allowed to read.
        var turnStarts = new List<int>();
        for (var i = 0; i < end; i++)
            if (string.Equals(widgets[i].Kind, StoredConversationWidgets.UserTextKind, StringComparison.Ordinal))
                turnStarts.Add(i);

        if (turnStarts.Count == 0) return "";

        // The last thing the person said inside this window opened the turn being judged. Drop it, and
        // everything after it, unconditionally.
        end = turnStarts[^1];
        turnStarts.RemoveAt(turnStarts.Count - 1);
        if (turnStarts.Count == 0) return "";

        var firstKept = Math.Max(0, turnStarts.Count - TurnVerdictPackage.RecentTurnCount);
        var sb = new StringBuilder();
        for (var t = firstKept; t < turnStarts.Count; t++)
        {
            var from = turnStarts[t];
            var to = t + 1 < turnStarts.Count ? turnStarts[t + 1] : end;
            for (var i = from; i < to; i++)
            {
                var widget = widgets[i];
                var isUser = string.Equals(widget.Kind, StoredConversationWidgets.UserTextKind, StringComparison.Ordinal);
                var isAgent = string.Equals(widget.Kind, StoredConversationWidgets.AgentTextKind, StringComparison.Ordinal);
                if (!isUser && !isAgent) continue;   // a tool call or its result: dropped

                var content = (widget.Content ?? "").Trim();
                if (content.Length == 0) continue;
                sb.Append(isUser ? "You: " : "Agent: ").Append(content).Append("\n\n");
            }
        }

        var text = sb.ToString().Trim();
        return text.Length <= TurnVerdictPackage.MaxRecentTurnsChars
            ? text
            : text[^TurnVerdictPackage.MaxRecentTurnsChars..];
    }

    /// <summary>The first thing the person asked this session - the seed of what the whole session is
    /// for. Null when the conversation holds nothing the person said, which is ordinary for an agent
    /// that keeps no readable conversation.</summary>
    private static string? FirstUserPrompt(IReadOnlyList<TurnWidgetDto> widgets)
    {
        foreach (var widget in widgets)
        {
            if (!string.Equals(widget.Kind, StoredConversationWidgets.UserTextKind, StringComparison.Ordinal))
                continue;
            var content = (widget.Content ?? "").Trim();
            if (content.Length == 0) continue;
            return content.Length <= TurnVerdictPackage.MaxFirstUserPromptChars
                ? content
                : content[..TurnVerdictPackage.MaxFirstUserPromptChars];
        }
        return null;
    }
}
