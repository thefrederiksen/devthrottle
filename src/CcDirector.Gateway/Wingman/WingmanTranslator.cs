using System.Text;
using System.Text.RegularExpressions;
using CcDirector.AgentBrain;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The wingman as the TRANSLATOR of a working session (issue #531). Given the coding
/// agent's written reply to a turn, it produces a faithful, speakable version a person
/// can hear or read in a back-and-forth - short enough to say out loud, but never gutted
/// to a headline when the agent actually produced content or an answer.
///
/// It runs on the gateway's one persistent, configured wingman session (the warm brain -
/// the configured agent and model from the Wingman settings tab, issues #509/#510), the
/// same brain the turn-brief agent uses. Each translation is one round trip:
///   1. <see cref="IAgentBrain.AskAsync"/> with the fidelity prompt wrapped around the
///      reply (the answer is wrapped in the shared <see cref="SessionAskRunner"/> markers
///      so it comes back cleanly);
///   2. <see cref="IAgentBrain.ClearAsync"/> so the wingman's context stays short between
///      translations - it almost never wants a long history.
///
/// No <c>--print</c> / <c>-p</c> anywhere: the brain is a real session billed against the
/// subscription (issue #511's direction). This is the single place both the Wingman Text
/// tab and the Wingman Voice tab get their summary from, so the text tab proves the
/// translation quality and the voice tab inherits it unchanged.
/// </summary>
public sealed class WingmanTranslator
{
    /// <summary>
    /// The fidelity contract for turning a coding agent's written reply into spoken words.
    /// Fidelity over brevity: the listener must hear the agent's actual answer, not a
    /// looser version of it. Carried over verbatim from the proven voice-summary prompt so
    /// the wording the team already validated is not lost in the move off <c>--print</c>.
    /// </summary>
    internal const string FidelityPrompt = """
        You are the wingman: you turn a coding agent's written reply into words a person
        will hear out loud or read on a small screen, in a back-and-forth conversation.
        Your job is FIDELITY, not brevity: the listener must hear the agent's actual
        answer, not a looser version of it. Rules:
        - Preserve the actual answer and every concrete fact: names, numbers, yes/no, the
          decision or result. Never drop the facts that ARE the answer.
        - If the agent wrote real content (a paragraph, a result, a list of findings),
          carry it. If the agent asked the person a question, surface that question
          clearly so they know what to answer.
        - GIVE ENOUGH CONTEXT. A short or terse reply on its own (like "Done", "Yes", a
          single number, or one line) is not enough to understand out loud. Use the recent
          conversation provided below to add just enough - what the reply is answering, or
          what was just done - so the listener, who cannot see the screen, knows what it
          means. Reach back only as far as is needed to be understood; a reply that already
          stands on its own needs nothing added, and never re-narrate the whole session.
        - Do not add, embellish, reframe, or change the topic. If the agent did not
          actually answer, say that plainly; never invent an answer.
        - Make it sound natural to say out loud, but completeness wins over shortness. Use
          as many sentences as the answer needs; do not pad and do not force a fixed length.
        - Speak for the ear: do not read code, commands, file paths, function names, or
          symbols out loud. When code matters, say in plain words what it does.
        - The reply may be in ANY language or script. Non-Latin characters are valid
          content, never corruption. Translate faithfully in the same language; never
          refuse or say the text cannot be read.
        """;

    private readonly Func<CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Action<string> _log;

    /// <summary>
    /// Create a translator over a warm-brain provider. The provider is the same
    /// <c>BrainSupervisor.GetAsync</c> the turn-brief agent uses; tests pass a provider
    /// that hands back a fake <see cref="IAgentBrain"/> so the translation logic is
    /// exercised with no live model.
    /// </summary>
    public WingmanTranslator(Func<CancellationToken, Task<IAgentBrain>> brainProvider, Action<string>? log = null)
    {
        _brainProvider = brainProvider ?? throw new ArgumentNullException(nameof(brainProvider));
        _log = log ?? FileLog.Write;
    }

    /// <summary>
    /// Pull the spoken answer out of the brain's reply. The brain is asked to wrap its answer in
    /// the shared markers, but an LLM does not always honor a formatting instruction perfectly - so
    /// when the markers are absent we use the whole reply (it WAS told to output only the spoken
    /// version). This tolerance is the difference between a reliable summary and a 502: never throw
    /// away a good answer over a missing delimiter. Internal so a test can assert both paths.
    /// </summary>
    internal static string ExtractSpoken(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return "";
        var begin = reply.IndexOf(SessionAskRunner.AnswerBeginMarker, StringComparison.Ordinal);
        if (begin >= 0)
        {
            var contentStart = begin + SessionAskRunner.AnswerBeginMarker.Length;
            var end = reply.IndexOf(SessionAskRunner.AnswerEndMarker, contentStart, StringComparison.Ordinal);
            return (end > contentStart ? reply[contentStart..end] : reply[contentStart..]).Trim();
        }
        // No markers: the brain answered without the wrapper. Use the whole reply, minus any stray
        // closing marker the model may have emitted on its own.
        var stray = reply.IndexOf(SessionAskRunner.AnswerEndMarker, StringComparison.Ordinal);
        return (stray >= 0 ? reply[..stray] : reply).Trim();
    }

    /// <summary>
    /// Translate the agent's LATEST reply into its spoken form. <paramref name="recentContext"/> is
    /// the recent conversation BEFORE that reply (oldest first) - the wingman uses it to add just
    /// enough context when the latest reply is too short to stand on its own (e.g. "Done", "Yes").
    /// <paramref name="latestReply"/> is the agent's latest written reply, the thing to translate.
    /// </summary>
    /// <returns>The faithful, speakable translation and how long the brain took.</returns>
    /// <exception cref="ArgumentException">The latest reply is empty - there is nothing to translate.</exception>
    public async Task<WingmanTranslation> TranslateAsync(string recentContext, string latestReply, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(latestReply))
            throw new ArgumentException("Latest reply is required - there is nothing to translate.", nameof(latestReply));

        _log($"[WingmanTranslator] TranslateAsync: contextLen={recentContext?.Length ?? 0}, replyLen={latestReply.Length}");

        var prompt = BuildPrompt(recentContext ?? "", latestReply);

        var brain = await _brainProvider(ct);
        AskResult ask;
        try
        {
            ask = await brain.AskAsync(prompt, ct);
        }
        finally
        {
            // Clear the context whether or not the ask succeeded so the next translation
            // starts fresh - the wingman almost never wants to carry the previous turn.
            await brain.ClearAsync(CancellationToken.None);
        }

        var spoken = CleanupForSpeech(ExtractSpoken(ask.Text));
        if (string.IsNullOrWhiteSpace(spoken))
            throw new InvalidOperationException(
                "[WingmanTranslator] The wingman returned an empty spoken translation for a non-empty reply.");

        _log($"[WingmanTranslator] TranslateAsync OK: spokenLen={spoken.Length}, replySeconds={ask.ReplySeconds:F1}");
        return new WingmanTranslation
        {
            Spoken = spoken,
            ReplySeconds = ask.ReplySeconds,
        };
    }

    /// <summary>
    /// The direct-to-wingman path (issue #531): the person talks to the wingman itself
    /// ("hey wingman, ...") instead of the working session. The wingman answers directly in
    /// speakable form. It does NOT act on files - if real code work is implied it says so and
    /// suggests handing it to the session. Same warm brain, cleared after the answer.
    /// </summary>
    public async Task<WingmanTranslation> AskDirectAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("A message is required to ask the wingman.", nameof(userMessage));

        _log($"[WingmanTranslator] AskDirectAsync: userLen={userMessage.Length}");
        var prompt = BuildDirectPrompt(userMessage);

        var brain = await _brainProvider(ct);
        AskResult ask;
        try
        {
            ask = await brain.AskAsync(prompt, ct);
        }
        finally
        {
            await brain.ClearAsync(CancellationToken.None);
        }

        var spoken = CleanupForSpeech(ExtractSpoken(ask.Text));
        if (string.IsNullOrWhiteSpace(spoken))
            throw new InvalidOperationException("[WingmanTranslator] The wingman returned an empty answer.");

        _log($"[WingmanTranslator] AskDirectAsync OK: spokenLen={spoken.Length}, replySeconds={ask.ReplySeconds:F1}");
        return new WingmanTranslation { Spoken = spoken, ReplySeconds = ask.ReplySeconds };
    }

    /// <summary>The direct-to-wingman prompt. Public so a test can assert its contract.</summary>
    public static string BuildDirectPrompt(string userMessage)
    {
        var sb = new StringBuilder();
        sb.Append("You are the wingman, talking directly to a person in a spoken back-and-forth ");
        sb.Append("(not relaying a coding session). Answer their message helpfully and briefly, in ");
        sb.Append("words that are natural to hear out loud - no code, paths, or symbols read aloud. ");
        sb.Append("You do NOT edit files or run commands yourself; if the request needs real code ");
        sb.Append("work, say so plainly and suggest sending it to the working session.\n\n");
        sb.Append("The person said:\n");
        sb.Append(userMessage.Trim());
        sb.Append("\n\n");
        sb.Append("Output ONLY your spoken answer, and nothing else, between these two markers, ");
        sb.Append("each on its own line:\n");
        sb.Append(SessionAskRunner.AnswerBeginMarker);
        sb.Append('\n');
        sb.Append("<spoken answer>\n");
        sb.Append(SessionAskRunner.AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>
    /// Wrap the agent reply in the fidelity contract and the shared answer markers. Public
    /// so a test can assert the exact contract text and that the reply is carried verbatim.
    /// </summary>
    public static string BuildPrompt(string recentContext, string latestReply)
    {
        var sb = new StringBuilder();
        sb.Append(FidelityPrompt);
        sb.Append("\n\n");
        if (!string.IsNullOrWhiteSpace(recentContext))
        {
            sb.Append("Recent conversation for context, oldest first. Use ONLY as much of this as the ");
            sb.Append("listener needs to understand the latest reply - do not re-narrate it:\n");
            sb.Append("---\n");
            sb.Append(recentContext.Trim());
            sb.Append("\n---\n\n");
        }
        sb.Append("The agent's LATEST reply - translate THIS for the ear (adding the minimum context ");
        sb.Append("from above only if the reply is too short to stand on its own):\n");
        sb.Append("---\n");
        sb.Append(latestReply.Trim());
        sb.Append("\n---\n\n");
        sb.Append("Output ONLY the spoken version, and nothing else, between these two markers, ");
        sb.Append("each on its own line:\n");
        sb.Append(SessionAskRunner.AnswerBeginMarker);
        sb.Append('\n');
        sb.Append("<spoken version>\n");
        sb.Append(SessionAskRunner.AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>
    /// Build the "recent conversation" context string from a session's transcript widgets: the last
    /// few exchanges BEFORE the latest agent reply (which is translated separately), oldest first,
    /// labeled "You:" / "Agent:". Capped so the brain gets enough to anchor a terse reply without
    /// the whole session. Returns "" when there is nothing before the latest reply.
    /// </summary>
    public static string BuildRecentContext(IReadOnlyList<TurnWidgetDto>? widgets, int maxWidgets = 8, int maxChars = 3000)
    {
        if (widgets is null || widgets.Count == 0) return "";
        var lastText = -1;
        for (var i = widgets.Count - 1; i >= 0; i--) { if (widgets[i].Kind == "Text") { lastText = i; break; } }
        if (lastText <= 0) return "";   // the latest reply is the first/only thing - no prior context
        var start = Math.Max(0, lastText - maxWidgets);
        var sb = new StringBuilder();
        for (var i = start; i < lastText; i++)
        {
            var w = widgets[i];
            var c = (w.Content ?? "").Trim();
            if (c.Length == 0) continue;
            var who = w.Kind == "UserMessage" ? "You" : w.Kind == "Text" ? "Agent" : w.Kind;
            sb.Append(who).Append(": ").Append(c).Append("\n\n");
        }
        var s = sb.ToString().Trim();
        if (s.Length > maxChars) s = s[^maxChars..];   // keep the most recent tail
        return s;
    }

    /// <summary>
    /// Light safety net for speech: the model is already told to speak for the ear, so this
    /// only strips code fences the model may have echoed and collapses blank runs. Inline
    /// identifiers in backticks keep their inner text (issue #368) - they are often the
    /// answer's content. Public so a test can prove non-Latin text passes through untouched.
    /// </summary>
    public static string CleanupForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ ]{2,}", " ");
        return text.Trim();
    }
}

/// <summary>The result of one wingman translation: the spoken text and the brain latency.</summary>
public sealed class WingmanTranslation
{
    /// <summary>The faithful, speakable version of the agent's reply.</summary>
    public string Spoken { get; init; } = "";

    /// <summary>Seconds the warm brain took to produce the translation.</summary>
    public double ReplySeconds { get; init; }
}
