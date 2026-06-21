using System.Text;
using System.Text.Json;
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
          carry it.
        - EXPLAIN TECHNICAL ANSWERS - do NOT flatten them. When the answer is technical (a
          diagnosis, what is broken and why, how something works, a code review, a
          recommendation, an error and its cause, a trade-off, a design decision), say the
          actual substance in plain spoken words: what it is, why, and what to do next. The
          listener is technical and wants the real point - NEVER reduce a technical answer to
          a vague line like "the agent gave a technical explanation" or "it made some
          changes". Name the specific thing, the cause, and the fix.
        - LEAD WITH THE ASK. If the agent is asking the person something (a question, a
          decision, a choice, a permission, "should I..."), open with that ask in plain words
          so they know exactly what they are being asked to answer, before any detail.
        - GIVE ENOUGH CONTEXT. A short or terse reply on its own (like "Done", "Yes", a
          single number, or one line) is not enough to understand out loud. Use the recent
          conversation provided below to add just enough - what the reply is answering, or
          what was just done - so the listener, who cannot see the screen, knows what it
          means. Reach back only as far as is needed to be understood; a reply that already
          stands on its own needs nothing added, and never re-narrate the whole session.
        - RESOLVE REFERENCES. When the reply uses a pronoun or shorthand that refers to
          something named in the recent conversation ("it", "that file", "the bug I
          mentioned", "the one we discussed"), say the actual thing in plain words. The
          listener cannot see the screen or scroll back; resolve every reference they
          cannot otherwise anchor from the reply alone.
        - Do not add, embellish, reframe, or change the topic. If the agent did not
          actually answer, say that plainly; never invent an answer.
        - Make it sound natural to say out loud, but completeness wins over shortness. Use
          as many sentences as the answer needs; do not pad and do not force a fixed length.
        - Speak for the ear: do not spell out raw code, commands, file paths, function names,
          or symbols character by character - instead say in plain words what they ARE and
          what they DO, keeping the technical meaning intact. Translating for the ear must
          never mean dropping the technical content.
        - The reply may be in ANY language or script. Non-Latin characters are valid
          content, never corruption. Translate faithfully in the same language; never
          refuse or say the text cannot be read.
        """;

    /// <summary>
    /// Version of the DEPLOYED default instructions above (issue #537). Bump this whenever the
    /// DevThrottle dev team changes <see cref="FidelityPrompt"/>, so a user who has customized their
    /// instructions is shown that the recommended default changed and can switch to it. The content
    /// hash is the real identity; this is the human-facing label.
    /// </summary>
    public const string DefaultInstructionsVersion = "2";

    private readonly Func<CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Action<string> _log;
    private readonly Func<string> _instructions;

    /// <summary>
    /// Create a translator over a warm-brain provider. The provider is the same
    /// <c>BrainSupervisor.GetAsync</c> the turn-brief agent uses; tests pass a provider
    /// that hands back a fake <see cref="IAgentBrain"/> so the translation logic is
    /// exercised with no live model. <paramref name="instructionsProvider"/> (issue #537) returns
    /// the ACTIVE wingman instructions at call time - the user's edited/versioned prompt when set,
    /// else the deployed <see cref="FidelityPrompt"/> default; omit it to always use the default.
    /// </summary>
    public WingmanTranslator(Func<CancellationToken, Task<IAgentBrain>> brainProvider, Action<string>? log = null, Func<string>? instructionsProvider = null)
    {
        _brainProvider = brainProvider ?? throw new ArgumentNullException(nameof(brainProvider));
        _log = log ?? FileLog.Write;
        _instructions = instructionsProvider ?? (() => FidelityPrompt);
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
    public Task<WingmanTranslation> TranslateAsync(string recentContext, string latestReply, CancellationToken ct = default)
        => TranslateWithAsync(_instructions(), recentContext, latestReply, ct);

    /// <summary>
    /// Same as <see cref="TranslateAsync"/> but with caller-supplied instructions instead of the
    /// active ones (issue #537 A/B testing): re-run a DRAFT prompt over a captured reply to compare
    /// its spoken output against what the wingman said before, without changing the live instructions.
    /// </summary>
    public async Task<WingmanTranslation> TranslateWithAsync(string instructions, string recentContext, string latestReply, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(latestReply))
            throw new ArgumentException("Latest reply is required - there is nothing to translate.", nameof(latestReply));

        _log($"[WingmanTranslator] TranslateWithAsync: instrLen={instructions?.Length ?? 0}, contextLen={recentContext?.Length ?? 0}, replyLen={latestReply.Length}");

        var prompt = BuildPrompt(instructions ?? FidelityPrompt, recentContext ?? "", latestReply);

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

        _log($"[WingmanTranslator] TranslateWithAsync OK: spokenLen={spoken.Length}, replySeconds={ask.ReplySeconds:F1}");
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

    /// <summary>
    /// The DevThrottle product/docs Q&amp;A path (issue #472): the person asks a question about the
    /// product itself - "what is DevThrottle?", "how do I start a session?" - on the Cockpit
    /// Learning page, and the wingman answers it directly, grounded in a DevThrottle system prompt.
    /// This is NOT a session translation and NOT a free-form chat: the brain is told it is
    /// DevThrottle's in-product help and answers only about the product, declining off-topic asks.
    /// Same warm brain as the other paths, cleared after the answer so context never accumulates.
    /// </summary>
    public async Task<WingmanTranslation> AskAboutDevThrottleAsync(string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A question is required to ask about DevThrottle.", nameof(question));

        _log($"[WingmanTranslator] AskAboutDevThrottleAsync: questionLen={question.Length}");
        var prompt = BuildDevThrottlePrompt(question);

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

        var answer = CleanupForSpeech(ExtractSpoken(ask.Text));
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("[WingmanTranslator] The wingman returned an empty answer about DevThrottle.");

        _log($"[WingmanTranslator] AskAboutDevThrottleAsync OK: answerLen={answer.Length}, replySeconds={ask.ReplySeconds:F1}");
        return new WingmanTranslation { Spoken = answer, ReplySeconds = ask.ReplySeconds };
    }

    /// <summary>The DevThrottle product/docs Q&amp;A prompt (issue #472). Public so a test can assert
    /// it grounds the brain as DevThrottle's in-product help and carries the user's question.</summary>
    public static string BuildDevThrottlePrompt(string question)
    {
        var sb = new StringBuilder();
        sb.Append("You are DevThrottle's in-product help assistant, answering a question typed on the ");
        sb.Append("Learning page of the DevThrottle Cockpit (the fleet web dashboard). DevThrottle ");
        sb.Append("(the app and command-line tools are named cc-director) is an open-source tool that ");
        sb.Append("runs and supervises many Claude Code coding sessions at once: a desktop Director app ");
        sb.Append("drives sessions on each machine, a Gateway aggregates every machine's Directors into ");
        sb.Append("one fleet, and the Cockpit is the web dashboard the Gateway serves to every machine ");
        sb.Append("and phone. The Wingman is the assistant that summarizes and answers questions about ");
        sb.Append("sessions. Answer the person's question about DevThrottle helpfully, accurately, and ");
        sb.Append("concisely, in plain words. Rules:\n");
        sb.Append("- Answer ONLY about DevThrottle: what it is, what it does, and how to use it. If the ");
        sb.Append("question is not about DevThrottle, say so plainly and point them back to product help.\n");
        sb.Append("- If you are not sure of a specific detail, say so rather than inventing it; never ");
        sb.Append("guess at a feature that may not exist.\n");
        sb.Append("- Keep it readable on screen: a short paragraph or a few short points, not an essay.\n\n");
        sb.Append("The person asked:\n");
        sb.Append(question.Trim());
        sb.Append("\n\n");
        sb.Append("Output ONLY your answer, and nothing else, between these two markers, each on its own line:\n");
        sb.Append(SessionAskRunner.AnswerBeginMarker);
        sb.Append('\n');
        sb.Append("<answer>\n");
        sb.Append(SessionAskRunner.AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>
    /// Menu handling (issue #531): decide whether the agent is RIGHT NOW showing an interactive
    /// menu/choice on screen and, if so, extract its options as structured, pressable data plus a
    /// speakable reading. The warm brain reads the bottom of the terminal. Returns
    /// <see cref="WingmanMenu.IsMenu"/>=false (never throws) when it is not a menu or parsing fails -
    /// the caller then treats the input as a normal typed prompt, which is the correct default.
    /// </summary>
    public async Task<WingmanMenu> DetectMenuAsync(string terminalText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(terminalText)) return new WingmanMenu { IsMenu = false };
        _log($"[WingmanTranslator] DetectMenuAsync: terminalLen={terminalText.Length}");

        var brain = await _brainProvider(ct);
        AskResult ask;
        try { ask = await brain.AskAsync(BuildMenuDetectPrompt(terminalText), ct); }
        finally { await brain.ClearAsync(CancellationToken.None); }

        var menu = ParseMenu(ExtractSpoken(ask.Text));
        if (menu.IsMenu && menu.Options.Count == 0) menu.IsMenu = false;   // a menu with no options is not actionable
        if (menu.IsMenu) menu.Spoken = BuildMenuSpoken(menu);
        _log($"[WingmanTranslator] DetectMenuAsync: isMenu={menu.IsMenu}, options={menu.Options.Count}");
        return menu;
    }

    /// <summary>
    /// Brain fallback for mapping a person's spoken/typed answer to a menu option when the cheap
    /// local match (<see cref="WingmanMenuLogic.MatchOption"/>) was not confident. Returns the
    /// 0-based option index, or -1 when the brain says it is unclear.
    /// </summary>
    public async Task<int> MapChoiceAsync(WingmanMenu menu, string userText, CancellationToken ct = default)
    {
        if (menu?.Options is null || menu.Options.Count == 0 || string.IsNullOrWhiteSpace(userText)) return -1;

        var brain = await _brainProvider(ct);
        AskResult ask;
        try { ask = await brain.AskAsync(BuildMenuMapPrompt(menu, userText), ct); }
        finally { await brain.ClearAsync(CancellationToken.None); }

        var raw = ExtractSpoken(ask.Text);
        var m = Regex.Match(raw, @"\d{1,2}");
        if (m.Success && int.TryParse(m.Value, out var n) && n >= 1 && n <= menu.Options.Count)
        {
            _log($"[WingmanTranslator] MapChoiceAsync: chose option {n}");
            return n - 1;
        }
        _log("[WingmanTranslator] MapChoiceAsync: unclear (0/none)");
        return -1;
    }

    /// <summary>The menu-detection prompt. Public so a test can assert its contract.</summary>
    public static string BuildMenuDetectPrompt(string terminalText)
    {
        // Menus render at the BOTTOM of the screen; the tail is enough and keeps the call fast.
        var tail = terminalText.Length > 4000 ? terminalText[^4000..] : terminalText;
        var sb = new StringBuilder();
        sb.AppendLine("You are the wingman. Look at the BOTTOM of this coding-agent terminal and decide if it is");
        sb.AppendLine("RIGHT NOW showing an interactive menu the person must answer by picking a listed option -");
        sb.AppendLine("a numbered/lettered list, a permission prompt (\"Do you want to proceed?\"), a picker, or a");
        sb.AppendLine("plan approval. A free-text \"type your message\" prompt or an idle screen is NOT a menu.");
        sb.AppendLine();
        sb.AppendLine("If it IS a menu, extract it (rules from the proven brief contract):");
        sb.AppendLine("- question: the choice being asked, in plain words to hear out loud. No code or paths.");
        sb.AppendLine("- options: each listed choice. key = its visible label (e.g. \"1. Yes\"). send = the EXACT");
        sb.AppendLine("  keystrokes that pick it - a picker confirms with Enter, so send \"1\\r\" (the number then a");
        sb.AppendLine("  carriage return). note = the consequence/scope/risk (a \"don't ask again\" choice is a");
        sb.AppendLine("  standing grant - say so). recommended = true for AT MOST ONE option, the safest/default");
        sb.AppendLine("  pick, and ONLY if you are sure - never guess a recommendation.");
        sb.AppendLine("- selectionMode: \"single\" to pick one; \"multiple\" for a pick-any checklist (then each send");
        sb.AppendLine("  is just the toggle number and submit=\"\\r\" completes). For single, submit=\"\".");
        sb.AppendLine("- Use ONLY what is on the screen. Never invent options. Never read code or symbols aloud.");
        sb.AppendLine();
        sb.AppendLine("Terminal (bottom of the screen):");
        sb.AppendLine("---");
        sb.AppendLine(tail.Trim());
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Output ONLY this JSON (no prose) between the two markers, each marker on its own line:");
        sb.AppendLine(SessionAskRunner.AnswerBeginMarker);
        sb.AppendLine("{\"isMenu\":true,\"question\":\"...\",\"selectionMode\":\"single\",\"submit\":\"\",\"options\":[{\"key\":\"1. Yes\",\"send\":\"1\\r\",\"note\":\"...\",\"recommended\":false}]}");
        sb.Append(SessionAskRunner.AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>The choice-mapping prompt. Public so a test can assert its contract.</summary>
    public static string BuildMenuMapPrompt(WingmanMenu menu, string userText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A coding agent is showing this menu. A person who cannot see the screen said which one");
        sb.AppendLine("they want, in plain words. Decide which numbered option they mean.");
        sb.AppendLine();
        for (var i = 0; i < menu.Options.Count; i++)
            sb.AppendLine($"{i + 1}. {StripForSpeech(menu.Options[i].Key)}");
        sb.AppendLine();
        sb.AppendLine("The person said:");
        sb.AppendLine(userText.Trim());
        sb.AppendLine();
        sb.AppendLine($"Reply with ONLY the single option number (1-{menu.Options.Count}) they chose, or 0 if it is");
        sb.AppendLine("unclear or they did not pick one. Output the number between the markers:");
        sb.AppendLine(SessionAskRunner.AnswerBeginMarker);
        sb.AppendLine("<number>");
        sb.Append(SessionAskRunner.AnswerEndMarker);
        return sb.ToString();
    }

    /// <summary>Parse the brain's menu JSON tolerantly into a <see cref="WingmanMenu"/>. Internal so a
    /// test can assert both the happy path and that garbage degrades to IsMenu=false.</summary>
    internal static WingmanMenu ParseMenu(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WingmanMenu { IsMenu = false };
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return new WingmanMenu { IsMenu = false };
        try
        {
            var menu = JsonSerializer.Deserialize<WingmanMenu>(json[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (menu is null) return new WingmanMenu { IsMenu = false };
            // Null-proof the strings the model may have emitted as null.
            menu.Question ??= "";
            menu.SelectionMode = string.IsNullOrWhiteSpace(menu.SelectionMode) ? "single" : menu.SelectionMode;
            menu.Submit ??= "";
            menu.Options ??= new();
            foreach (var o in menu.Options) { o.Key ??= ""; o.Send ??= ""; }
            // An option you cannot actually send is not pressable - drop it.
            menu.Options = menu.Options.Where(o => o.Send.Length > 0).ToList();
            return menu;
        }
        catch (JsonException) { return new WingmanMenu { IsMenu = false }; }
    }

    /// <summary>Build the speakable reading of a menu: the question, each option (recommended +
    /// note), and how to answer. Public so a test can assert the ear-friendly wording.</summary>
    public static string BuildMenuSpoken(WingmanMenu menu)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(menu.Question)) sb.Append(menu.Question.Trim()).Append(' ');
        for (var i = 0; i < menu.Options.Count; i++)
        {
            var o = menu.Options[i];
            sb.Append("Option ").Append(i + 1).Append(": ").Append(StripForSpeech(o.Key));
            if (o.Recommended) sb.Append(" (recommended)");
            sb.Append('.');
            if (!string.IsNullOrWhiteSpace(o.Note)) sb.Append(' ').Append(o.Note!.Trim().TrimEnd('.')).Append('.');
            sb.Append(' ');
        }
        sb.Append(menu.SelectionMode == "multiple"
            ? "Say which ones apply, then say done."
            : "Say the number, or the option.");
        return sb.ToString().Trim();
    }

    /// <summary>Drop a leading "1." / "2)" / "a." marker from a label so it reads cleanly out loud.</summary>
    private static string StripForSpeech(string key)
        => Regex.Replace(key ?? "", @"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s*", "").Trim();

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
        => BuildPrompt(FidelityPrompt, recentContext, latestReply);

    /// <summary>
    /// Same as <see cref="BuildPrompt(string,string)"/> but with caller-supplied instructions
    /// (issue #537: the user's active, possibly-edited wingman instructions) instead of the
    /// embedded default. Public so a test can assert the active instructions are what gets used.
    /// </summary>
    public static string BuildPrompt(string instructions, string recentContext, string latestReply)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(instructions) ? FidelityPrompt : instructions);
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
