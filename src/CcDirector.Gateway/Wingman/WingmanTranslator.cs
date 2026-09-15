using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The wingman as the TRANSLATOR of a working session (issue #531). Given the coding
/// agent's written reply to a turn, it produces the SHORTEST speakable version that still
/// leaves the listener knowing everything that would change what they think or do next -
/// but never gutted to a headline when the agent actually produced content or an answer.
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
    /// The contract for turning a coding agent's written reply into spoken words.
    /// BREVITY is the goal, keeping the answer true is the constraint - v5, issue #1612.
    ///
    /// v4 said "fidelity over brevity" and then stacked six rules that push LONGER (preserve
    /// every concrete fact, carry real content, explain technical answers, give context,
    /// resolve references) against two that push shorter. The model obeyed: on 2026-07-15 the
    /// median narration was 1,292 characters (~1m13 spoken) and the worst was 4,969 (4m40).
    /// Nobody listens to a four-minute summary of one turn. That was the prompt WORKING, not
    /// failing - so the framing is what changed, not another rule bolted on.
    ///
    /// There is NO length CAP - that cap was OpenAI's 4096 and we do not call them (issue #1612).
    /// But a cap and a TARGET are different things, and v5.1 exists because conflating them cost most
    /// of the fix. v5 told the model "there is no length limit": true of the provider, and irrelevant
    /// to the listener, whose patience is the actual constraint. The reframing was right and did real
    /// work - what it lacked was a NUMBER. "Prefer three sentences to six" sitting next to "there is
    /// no length limit" produced twelve. Models discount a principle and obey a budget.
    ///
    /// Measured against a real model, 3 runs per prompt (only same-batch numbers are compared - run
    /// to run variance is real):
    ///   A LONG, dense reply (3,652 chars) - the case that actually hurts:
    ///     v4 ("fidelity over brevity")   -> 2,892 chars, ~2m42 spoken
    ///     v5 (reframed, no number)       -> 1,810 chars, ~1m42 spoken
    ///     v5.1 (v5 + the ~30s budget)    ->   854 chars, ~48s spoken   (2.1x better than v5)
    ///   A TYPICAL reply (479 chars):
    ///     v5 -> ~30s spoken;  v5.1 -> ~29s spoken   (no difference - both already fine)
    ///
    /// So the budget binds exactly where the defect lives: the long, complex turns, which are the
    /// ones whose ending matters most and the ones the old 4000-char cut used to eat. v5.1 is not on
    /// the ~30s target for the hardest replies (~48s) - it halves the worst case without making the
    /// typical case vaguer, which is the trade we want. Tune further only with measurements; a prompt
    /// change whose effect is asserted rather than measured is how this rule rotted in the first place.
    ///
    /// v5.2 opens every narration with the SESSION TITLE. Someone listening with the phone in a pocket
    /// hears a summary with no idea which of a dozen sessions produced it - the one fact the screen was
    /// carrying for free, and the one the audio dropped. The title is spoken, not glued on in code,
    /// because real session names carry punctuation ("Athene / Stephanie #2624") and a literal
    /// prepend would read it out as "slash" and "hashtag" - exactly what SPEAK FOR THE EAR forbids.
    /// The model naturalizes it; code cannot. It costs a few words against the ~30s budget, which is
    /// why the rule says the title and nothing else about the session.
    ///
    /// v5.3 hardens the "never voice an identifier" rule after a narration read a full 40-char
    /// commit sha out one hex character at a time ("d zero six three zero a five c d ...") - about
    /// fifteen seconds of useless noise. The old rule already forbade it, but its only example was
    /// a DASHED guid; a bare continuous hex sha did not match that shape, so the model copied it
    /// through. v5.3 shows both shapes, tells the model to DROP the reference entirely rather than
    /// gloss it, and extends the ban to issue/pull-request/bug NUMBERS (say "three bug fixes are
    /// done", never "eighty-one eleven, eighty-one twelve..."). Prompt-only, per the standing rule
    /// that LLM behaviour is fixed in the prompt, not with a regex pass.
    ///
    /// v6 (issue #1008) MOVED TWO RULES OUT of this text and into
    /// <see cref="SpeechContract.SpokenOutputContract"/>: the no-Markdown rule, and the rule about what
    /// language to answer in. They left because they were never this contract's business - they bind
    /// EVERY spoken path, and this text is only one of four. Keeping them here made four copies that
    /// drifted, and it made THIS text - which a user is explicitly invited to REPLACE with their own
    /// words (issue #537) - the thing carrying them. A replaced prompt dropped both. The contract is
    /// now appended after whatever instructions are active, by <see cref="BuildPrompt"/>, so it cannot
    /// be edited away.
    /// </summary>
    internal const string FidelityPrompt = """
        You are the wingman: you turn a coding agent's written reply into words a person
        will hear out loud or read on a small screen, in a back-and-forth conversation.
        Say the LEAST you can while leaving the listener knowing everything that would change
        what they think or do next. Brevity is the GOAL; keeping the answer true is the
        CONSTRAINT. They are LISTENING, not reading, so say it the way a person would explain
        it out loud, and lead with the point. AIM FOR ABOUT THIRTY SECONDS OUT LOUD: two to
        four sentences, roughly 500 characters. That is how long a person will actually listen
        to a summary of one turn - it is not a technical limit, and going past it is the most
        common way to fail this job. A short reply needs less; never stretch one to fill it.
        Every extra sentence is a cost you must justify, not a budget you may spend. Rules:
        - OPEN WITH THE SESSION TITLE, then go straight into the summary. The session's title
          is given below. Your first words are that title - the listener usually cannot see
          the screen, so they need to know WHICH session is talking before they hear anything
          else. The title is the ONE thing allowed before the point; it is not preamble. Say
          ONLY the title: do not describe the session, do not say what it is working on, and
          do not wrap it in words like "the session ... says". Speak it for the ear like any
          other text - drop the punctuation instead of voicing it, so "devthrottle - mobile"
          becomes "devthrottle mobile" and "Banya/Yibo - WebSocket voice mode (architect)"
          becomes "Banya Yibo, WebSocket voice mode, architect". If no session title is given
          below, skip this rule and lead with the point.
        - BE SHORT. Lead with the single most important thing first - the answer, the result,
          or the ask - in your opening sentence, then add only what is needed to understand
          it, and STOP. If removing a sentence would not change what the listener knows or
          does, remove it. Prefer three sentences to six. No preamble, no restating the
          question, no narrating your own process, no summing up at the end what you just
          said. The listener wants the point, not a tour.
        - Preserve every fact that CARRIES the answer: the decision, the result, the yes/no,
          the specific thing named. Drop facts that do not change the listener's understanding
          - a supporting detail they would not act on is noise, not fidelity.
        - If the agent wrote real content (a paragraph, a result, a list of findings), carry
          what matters of it - not all of it.
        - EXPLAIN TECHNICAL ANSWERS - do NOT flatten them. When the answer is technical (a
          diagnosis, what is broken and why, how something works, a code review, a
          recommendation, an error and its cause, a trade-off, a design decision), say the
          actual substance in plain spoken words: what it is, why, and what to do next, in the
          fewest words that keep it true. The listener is technical and wants the real point -
          NEVER reduce a technical answer to a vague line like "the agent gave a technical
          explanation" or "it made some changes". Name the specific thing, the cause, and the
          fix - and stop. Depth is not length: three precise sentences beat a paragraph.
        - NEVER VOICE AN IDENTIFIER OR A HASH. This is the rule listeners notice most when you
          break it. A commit hash, session id, request id, token, port, file path, or any long
          run of letters and digits is USELESS to someone listening: they cannot write it down
          and cannot act on it, and spelling it out character by character is the single most
          irritating thing you can do. Catch BOTH shapes: a dashed identifier like
          "fe2ec700-458e-420e", AND a plain run of hex with no dashes like
          "d0630a5cd517167516675e0299009" - a bare commit hash will not look like the first
          example, but it is the same useless thing. Do not voice either. DROP it and say only
          that the thing happened: "merged at commit d0630a5..." becomes just
          "the changes were merged" - do not even say "at a commit", the hash adds nothing. If
          you truly must refer to one, name what it is FOR and stop: "the session id" or
          "a commit hash".
        - REFERENCE NUMBERS ARE NOT SPOKEN NUMBERS. A listener cannot use an issue,
          pull-request, or bug number; reading "eighty-one eleven, eighty-one twelve, eighty-one
          thirteen" is the same noise as a hash. Say the WORK, counted, not the numbers: "three
          bug fixes are done", "a pull request is open", "the deploy-protection fix landed".
          Drop the number unless it genuinely IS the answer to what was asked.
        - ROUND LARGE NUMBERS to what a person would really say out loud: 5,254,730 becomes
          "about five million"; 12,092,444 bytes becomes "about twelve megabytes"; 84 stays 84.
          Keep a number only when it IS the answer - a count, a version, a price, a duration, a
          size that matters - and round or simply name the rest.
        - IF THE PERSON ASKED FOR SOMETHING TO BE READ IN FULL - a document, a file, a passage
          - read it in full. An explicit request for the whole thing overrides brevity. That is
          the ONLY reason a long narration should exist.
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
        - Make it sound natural to say out loud. Completeness is about the answer's
          SUBSTANCE, not its length: keep what carries the answer, but do not pad, do not
          repeat yourself, and never stretch a short answer into a long one. Use only as many
          sentences as the answer truly needs - and when in doubt, use fewer.
        - SPEAK FOR THE EAR, NOT THE SCREEN. This is heard out loud, so never voice raw syntax
          or punctuation. Do NOT read out file paths, URLs, headings, or code symbols
          character by character - describe them in plain spoken words instead. Concretely:
          never say "hashtag", "colon slash slash", "slash", "dot", "backtick", "underscore",
          or a bare drive letter and colon out loud. For example, "file:///D:/repo/marketing.html"
          becomes "the marketing HTML file in the repo"; "## Root cause" becomes just "Root
          cause"; a function like "SweepStale()" becomes "the sweep-stale method". Say what the
          path, URL, heading, or symbol IS and what it DOES - keep the technical meaning, drop
          the punctuation. Never spell out the literal characters.
        - The reply may be in ANY language or script. Non-Latin characters are valid content,
          never corruption; never refuse or say the text cannot be read. What language you
          ANSWER in is settled by the spoken output contract below, not by the reply.
        """;

    /// <summary>
    /// Version of the DEPLOYED default instructions above (issue #537). Bump this whenever the
    /// DevThrottle dev team changes <see cref="FidelityPrompt"/>, so a user who has customized their
    /// instructions is shown that the recommended default changed and can switch to it. The content
    /// hash is the real identity; this is the human-facing label.
    /// </summary>
    public const string DefaultInstructionsVersion = "9";

    private readonly Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly Func<TenantId, SpokenLanguage> _languageFor;
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
    /// <param name="languageFor">The language an account is spoken to in, resolved from the tenant at
    ///  CALL time so a change on the Language tab applies to the next spoken answer with no restart.
    ///  It is REQUIRED and has no default ON PURPOSE (issue #1008): all three generators on this class
    ///  already require a tenant, so the compiler - not anybody's memory - is what guarantees the
    ///  language is available wherever a spoken answer is produced. Production wires
    ///  <c>TenantSettingsResolver.SpokenLanguage</c>; a test that does not care passes
    ///  <c>_ =&gt; SpokenLanguages.English</c>, which says so out loud rather than defaulting silently.</param>
    public WingmanTranslator(
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider,
        Func<TenantId, SpokenLanguage> languageFor,
        Action<string>? log = null,
        Func<string>? instructionsProvider = null)
    {
        _brainProvider = brainProvider ?? throw new ArgumentNullException(nameof(brainProvider));
        _languageFor = languageFor ?? throw new ArgumentNullException(nameof(languageFor));
        _log = log ?? FileLog.Write;
        _instructions = instructionsProvider ?? (() => FidelityPrompt);
    }

    /// <summary>This tenant's spoken language, or a loud failure. A null from the provider is a wiring
    ///  bug, not a reason to guess English: guessing is how an account set to French gets answered in
    ///  English and nobody finds out for three reports.</summary>
    private SpokenLanguage LanguageFor(TenantId tenant)
        => _languageFor(tenant)
           ?? throw new InvalidOperationException(
               "[WingmanTranslator] The spoken-language provider returned null. Every spoken path must "
               + "resolve a language from its tenant - fix the wiring rather than defaulting to English.");

    /// <summary>
    /// Tolerant matchers for the answer delimiters. The model is TOLD to wrap its answer in the
    /// exact "===DEVTHROTTLE-ANSWER-BEGIN===" / "===DEVTHROTTLE-ANSWER-END===" markers, but an LLM
    /// does not reproduce a literal delimiter perfectly every time - it may emit two equals signs
    /// instead of three, drop one, or add a stray space. An exact-string match then misses the
    /// ragged marker and leaks it into what the listener hears. So these match on the stable marker
    /// TEXT and swallow whatever run of '=' and surrounding whitespace came with it. Equals signs
    /// are never part of a real spoken answer, so consuming a "==" or "====" run is always safe.
    /// </summary>
    private static readonly Regex BeginMarkerRegex =
        new(@"=*\s*" + Regex.Escape(SessionAskRunner.AnswerBeginMarker.Trim('=')) + @"\s*=*", RegexOptions.Compiled);
    private static readonly Regex EndMarkerRegex =
        new(@"=*\s*" + Regex.Escape(SessionAskRunner.AnswerEndMarker.Trim('=')) + @"\s*=*", RegexOptions.Compiled);

    /// <summary>
    /// Pull the spoken answer out of the brain's reply, tolerating a ragged delimiter (see
    /// <see cref="BeginMarkerRegex"/>). When no opening marker is present the brain answered without
    /// the wrapper, so the whole reply IS the spoken answer (it WAS told to output only that) -
    /// never throw a good answer away over a missing delimiter. Internal so a test can assert both
    /// paths.
    /// </summary>
    internal static string ExtractSpoken(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return "";
        var begin = BeginMarkerRegex.Match(reply);
        if (begin.Success)
        {
            var afterBegin = reply[(begin.Index + begin.Length)..];
            var end = EndMarkerRegex.Match(afterBegin);
            return (end.Success ? afterBegin[..end.Index] : afterBegin).Trim();
        }
        // No opening marker: the brain answered without the wrapper. Use the whole reply, minus any
        // stray closing marker the model may have emitted on its own.
        var stray = EndMarkerRegex.Match(reply);
        return (stray.Success ? reply[..stray.Index] : reply).Trim();
    }

    /// <summary>
    /// Translate the agent's LATEST reply into its spoken form. <paramref name="recentContext"/> is
    /// the recent conversation BEFORE that reply (oldest first) - the wingman uses it to add just
    /// enough context when the latest reply is too short to stand on its own (e.g. "Done", "Yes").
    /// <paramref name="latestReply"/> is the agent's latest written reply, the thing to translate.
    /// <paramref name="sessionTitle"/> is the name of the session the reply came from - the wingman
    /// opens the narration with it so a listener who cannot see the screen knows who is talking.
    /// Pass null ONLY when there is genuinely no session behind the reply (the draft-prompt A/B
    /// path); the rule then no-ops rather than inventing a title.
    /// </summary>
    /// <returns>The faithful, speakable translation and how long the brain took.</returns>
    /// <exception cref="ArgumentException">The latest reply is empty - there is nothing to translate.</exception>
    public Task<WingmanTranslation> TranslateAsync(TenantId tenant, string recentContext, string latestReply, string? sessionTitle, string? liveScreen = null, CancellationToken ct = default)
        => TranslateWithAsync(tenant, _instructions(), recentContext, latestReply, sessionTitle, liveScreen, ct);

    /// <summary>
    /// Same as <see cref="TranslateAsync"/> but with caller-supplied instructions instead of the
    /// active ones (issue #537 A/B testing): re-run a DRAFT prompt over a captured reply to compare
    /// its spoken output against what the wingman said before, without changing the live instructions.
    /// </summary>
    public async Task<WingmanTranslation> TranslateWithAsync(TenantId tenant, string instructions, string recentContext, string latestReply, string? sessionTitle, string? liveScreen = null, CancellationToken ct = default)
    {
        RequireTenant(tenant);
        if (string.IsNullOrWhiteSpace(latestReply))
            throw new ArgumentException("Latest reply is required - there is nothing to translate.", nameof(latestReply));

        _log($"[WingmanTranslator] TranslateWithAsync: instrLen={instructions?.Length ?? 0}, contextLen={recentContext?.Length ?? 0}, replyLen={latestReply.Length}, screenLen={liveScreen?.Length ?? 0}, title={(string.IsNullOrWhiteSpace(sessionTitle) ? "(none)" : sessionTitle)}, language={LanguageFor(tenant).Code}");

        var language = LanguageFor(tenant);
        var prompt = BuildPrompt(language, instructions ?? FidelityPrompt, recentContext ?? "", latestReply, sessionTitle, liveScreen);

        var brain = await _brainProvider(tenant, WingmanModelRole.Fast, ct);
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

        var spoken = SpeechContract.Finish(ExtractSpoken(ask.Text));
        if (string.IsNullOrWhiteSpace(spoken))
            throw new InvalidOperationException(
                "[WingmanTranslator] The wingman returned an empty spoken translation for a non-empty reply.");

        // The SCREEN verdict is asked for only when a live screen went in, and its absence or garbling is
        // deliberately harmless: Screen stays null and every consumer falls back to its fail-safe default.
        var screen = string.IsNullOrWhiteSpace(liveScreen) ? null : ParseScreenVerdict(ask.Text);
        _log($"[WingmanTranslator] TranslateWithAsync OK: spokenLen={spoken.Length}, needs={screen?.Needs ?? "(none)"}, replySeconds={ask.ReplySeconds:F1}");
        return new WingmanTranslation
        {
            Spoken = spoken,
            ReplySeconds = ask.ReplySeconds,
            Screen = screen,
        };
    }

    /// <summary>
    /// Pull the one-line <c>SCREEN: {...}</c> verdict out of the brain's reply (issue devthrottle_internal#1195).
    /// Tolerant on purpose: the LAST "SCREEN" label in the reply wins (the prompt's own example lines sit
    /// earlier), the JSON object is taken by brace balance rather than to end-of-line, and anything that does
    /// not parse to a known <c>needs</c> value returns null - unknown, never a guess. Internal so a test can
    /// assert the happy path and that garbage degrades to null.
    /// </summary>
    internal static WingmanScreenVerdict? ParseScreenVerdict(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var label = Regex.Matches(reply, @"SCREEN\s*:\s*\{");
        if (label.Count == 0) return null;
        var m = label[^1];
        var start = m.Index + m.Length - 1;   // the '{'
        var depth = 0;
        var end = -1;
        for (var i = start; i < reply.Length; i++)
        {
            if (reply[i] == '{') depth++;
            else if (reply[i] == '}' && --depth == 0) { end = i; break; }
        }
        if (end < 0) return null;
        try
        {
            var v = JsonSerializer.Deserialize<WingmanScreenVerdict>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (v is null) return null;
            v.Needs = (v.Needs ?? "").Trim().ToLowerInvariant();
            v.Question ??= "";
            v.Options ??= new();
            return v.Needs is "menu" or "answer" or "nothing" ? v : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The direct-to-wingman path (issue #531): the person talks to the wingman itself
    /// ("hey wingman, ...") instead of the working session. The wingman answers directly in
    /// speakable form. It does NOT act on files - if real code work is implied it says so and
    /// suggests handing it to the session. Same warm brain, cleared after the answer.
    /// </summary>
    public async Task<WingmanTranslation> AskDirectAsync(TenantId tenant, string userMessage, CancellationToken ct = default)
    {
        RequireTenant(tenant);
        if (string.IsNullOrWhiteSpace(userMessage))
            throw new ArgumentException("A message is required to ask the wingman.", nameof(userMessage));

        var language = LanguageFor(tenant);
        _log($"[WingmanTranslator] AskDirectAsync: userLen={userMessage.Length}, language={language.Code}");
        var prompt = BuildDirectPrompt(language, userMessage);

        var brain = await _brainProvider(tenant, WingmanModelRole.Thinking, ct);
        AskResult ask;
        try
        {
            ask = await brain.AskAsync(prompt, ct);
        }
        finally
        {
            await brain.ClearAsync(CancellationToken.None);
        }

        var spoken = SpeechContract.Finish(ExtractSpoken(ask.Text));
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
    public async Task<WingmanTranslation> AskAboutDevThrottleAsync(TenantId tenant, string question, CancellationToken ct = default)
    {
        RequireTenant(tenant);
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A question is required to ask about DevThrottle.", nameof(question));

        var language = LanguageFor(tenant);
        _log($"[WingmanTranslator] AskAboutDevThrottleAsync: questionLen={question.Length}, language={language.Code}");
        var prompt = BuildDevThrottlePrompt(language, question);

        var brain = await _brainProvider(tenant, WingmanModelRole.Thinking, ct);
        AskResult ask;
        try
        {
            ask = await brain.AskAsync(prompt, ct);
        }
        finally
        {
            await brain.ClearAsync(CancellationToken.None);
        }

        var answer = SpeechContract.Finish(ExtractSpoken(ask.Text));
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("[WingmanTranslator] The wingman returned an empty answer about DevThrottle.");

        _log($"[WingmanTranslator] AskAboutDevThrottleAsync OK: answerLen={answer.Length}, replySeconds={ask.ReplySeconds:F1}");
        return new WingmanTranslation { Spoken = answer, ReplySeconds = ask.ReplySeconds };
    }

    /// <summary>The DevThrottle product/docs Q&amp;A prompt (issue #472). Public so a test can assert
    /// it grounds the brain as DevThrottle's in-product help and carries the user's question. Takes
    /// the account's <paramref name="language"/> because this answer is spoken (and shown as raw
    /// text), so it is one of the paths the spoken output contract binds - issue #1008.</summary>
    public static string BuildDevThrottlePrompt(SpokenLanguage language, string question)
    {
        ArgumentNullException.ThrowIfNull(language);
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
        sb.Append("- Keep it short and readable: a short paragraph, not an essay.\n\n");
        sb.Append(SpeechContract.SpokenOutputContract(language));
        sb.Append("\n\n");
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
    public async Task<WingmanMenu> DetectMenuAsync(TenantId tenant, string terminalText, CancellationToken ct = default)
    {
        RequireTenant(tenant);
        if (string.IsNullOrWhiteSpace(terminalText)) return new WingmanMenu { IsMenu = false };
        var language = LanguageFor(tenant);
        _log($"[WingmanTranslator] DetectMenuAsync: terminalLen={terminalText.Length}, language={language.Code}");

        var brain = await _brainProvider(tenant, WingmanModelRole.Fast, ct);
        AskResult ask;
        try { ask = await brain.AskAsync(BuildMenuDetectPrompt(language, terminalText), ct); }
        finally { await brain.ClearAsync(CancellationToken.None); }

        var menu = ParseMenu(ExtractSpoken(ask.Text));
        if (menu.IsMenu && menu.Options.Count == 0) menu.IsMenu = false;   // a menu with no options is not actionable
        if (menu.IsMenu) menu.Spoken = BuildMenuSpoken(language, menu);
        _log($"[WingmanTranslator] DetectMenuAsync: isMenu={menu.IsMenu}, options={menu.Options.Count}");
        return menu;
    }

    /// <summary>
    /// Brain fallback for mapping a person's spoken/typed answer to a menu option when the cheap
    /// local match (<see cref="WingmanMenuLogic.MatchOption"/>) was not confident. Returns the
    /// 0-based option index, or -1 when the brain says it is unclear.
    /// </summary>
    public async Task<int> MapChoiceAsync(TenantId tenant, WingmanMenu menu, string userText, CancellationToken ct = default)
    {
        RequireTenant(tenant);
        if (menu?.Options is null || menu.Options.Count == 0 || string.IsNullOrWhiteSpace(userText)) return -1;

        var brain = await _brainProvider(tenant, WingmanModelRole.Fast, ct);
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

    private static void RequireTenant(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("Wingman runtime selection requires an explicit tenant.", nameof(tenant));
    }

    /// <summary>
    /// The menu-detection prompt. Public so a test can assert its contract.
    ///
    /// A SPOKEN-FIELD path (issue #1009): its OUTPUT is JSON that code parses, so the plain-spoken-prose
    /// rule must not be applied to it - "output no formatting characters" would break the JSON and menu
    /// handling decides whether a keypress lands in somebody's terminal. But two of its FIELDS, the
    /// question and each option's note, are read aloud verbatim by <see cref="BuildMenuSpoken"/>. So the
    /// language rule alone is applied, scoped to those fields. Without this, a French account heard a
    /// French frame wrapped around an English question, which is exactly the "English fragment in a
    /// French session" the owner ruled out.
    /// </summary>
    public static string BuildMenuDetectPrompt(SpokenLanguage language, string terminalText)
    {
        ArgumentNullException.ThrowIfNull(language);
        // Menus render at the BOTTOM of the screen; the tail is enough and keeps the call fast.
        var tail = terminalText.Length > 4000 ? terminalText[^4000..] : terminalText;
        var sb = new StringBuilder();
        sb.AppendLine("You are the wingman. Look at the BOTTOM of this coding-agent terminal and decide if it is");
        sb.AppendLine("RIGHT NOW showing an interactive menu the person must answer by picking a listed option -");
        sb.AppendLine("a numbered/lettered list, a permission prompt (\"Do you want to proceed?\"), a picker, or a");
        sb.AppendLine("plan approval. A free-text \"type your message\" prompt or an idle screen is NOT a menu.");
        sb.AppendLine();
        sb.AppendLine("A numbered list INSIDE the agent's own prose is NOT a menu. Agents routinely end a turn");
        sb.AppendLine("with a numbered summary of finished work (\"1. Committed the fix... 2. Updated the tests...");
        sb.AppendLine("3. Cleaned up...\") sitting above an empty input box - that is a report, not a choice, even");
        sb.AppendLine("when a stray marker glyph appears next to a number. A menu is an interactive control that");
        sb.AppendLine("owns the screen and is waiting for a selection, like:");
        sb.AppendLine("  Do you want to proceed?");
        sb.AppendLine("  > 1. Yes");
        sb.AppendLine("    2. Yes, and don't ask again this session");
        sb.AppendLine("    3. No, and tell the agent what to do differently");
        sb.AppendLine("If the screen only shows a finished reply above an empty composer, answer isMenu=false.");
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
        sb.AppendLine("The \"question\" and each option's \"note\" are READ ALOUD to the person word for word, so");
        sb.AppendLine("write those two fields under this rule. It applies to their CONTENT only - the surrounding");
        sb.AppendLine("JSON, the field names, and each option's \"key\" and \"send\" are machine-read and stay exactly");
        sb.AppendLine("as they appear on the screen:");
        sb.AppendLine(SpeechContract.SpeakInLanguageRule(language));
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

    /// <summary>
    /// Build the speakable reading of a menu: the question, each option (recommended + note), and how to
    /// answer. Public so a test can assert the ear-friendly wording.
    ///
    /// REBUILT FOR ISSUE #1009. It used to compose the reading by gluing English fragments - the word
    /// "Option", then the number, then ": ", then the label, then " (recommended)" - and the issue names
    /// it as the one thing that "cannot be translated as written". It is right: word order, agreement and
    /// where a recommendation goes are per-language decisions, and code that concatenates fragments has
    /// already made them for English. So each language now owns the FINISHED SENTENCE
    /// (<see cref="SpokenPhrases.MenuOption"/> and friends) and this method only chooses which sentence
    /// and fills in the number and the label.
    ///
    /// The question and the per-option note are the MODEL's words, extracted from the screen, and they
    /// arrive in the account's language because <see cref="BuildMenuDetectPrompt"/> asks for them that
    /// way. That is what stops a French reading from being a French frame around English content.
    /// </summary>
    public static string BuildMenuSpoken(SpokenLanguage language, WingmanMenu menu)
    {
        ArgumentNullException.ThrowIfNull(language);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(menu.Question)) sb.Append(menu.Question.Trim()).Append(' ');
        for (var i = 0; i < menu.Options.Count; i++)
        {
            var o = menu.Options[i];
            var sentence = o.Recommended ? SpokenPhrases.MenuOptionRecommended : SpokenPhrases.MenuOption;
            sb.Append(sentence.In(language, i + 1, StripForSpeech(o.Key)));
            if (!string.IsNullOrWhiteSpace(o.Note)) sb.Append(' ').Append(o.Note!.Trim().TrimEnd('.')).Append('.');
            sb.Append(' ');
        }
        sb.Append(menu.SelectionMode == "multiple"
            ? SpokenPhrases.MenuAnswerMultiple.In(language)
            : SpokenPhrases.MenuAnswerSingle.In(language));
        return sb.ToString().Trim();
    }

    /// <summary>Drop a leading "1." / "2)" / "a." marker from a label so it reads cleanly out loud.</summary>
    private static string StripForSpeech(string key)
        => Regex.Replace(key ?? "", @"^\W*(?:\d{1,2}|[A-Za-z])[.)]\s*", "").Trim();

    /// <summary>The direct-to-wingman prompt. Public so a test can assert its contract. Takes the
    /// account's <paramref name="language"/> because this answer is spoken - issue #1008.</summary>
    public static string BuildDirectPrompt(SpokenLanguage language, string userMessage)
    {
        ArgumentNullException.ThrowIfNull(language);
        var sb = new StringBuilder();
        sb.Append("You are the wingman, talking directly to a person in a spoken back-and-forth ");
        sb.Append("(not relaying a coding session). Answer their message helpfully and briefly, in ");
        sb.Append("words that are natural to hear out loud - no code, paths, or symbols read aloud. ");
        sb.Append("You do NOT edit files or run commands yourself; if the request needs real code ");
        sb.Append("work, say so plainly and suggest sending it to the working session.\n\n");
        sb.Append(SpeechContract.SpokenOutputContract(language));
        sb.Append("\n\n");
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
    /// Wrap the agent reply in the fidelity contract and the shared answer markers, with
    /// caller-supplied instructions (issue #537: the user's active, possibly-edited wingman
    /// instructions; pass <see cref="FidelityPrompt"/> for the deployed default). Public so a test
    /// can assert the exact contract text and that the reply is carried verbatim.
    ///
    /// <paramref name="sessionTitle"/> is the name of the session the reply came from, which the
    /// OPEN WITH THE SESSION TITLE rule speaks first. It is a REQUIRED parameter and not an
    /// optional one on purpose: every caller that has a session must be made to pass it, and the
    /// compiler is the only thing that reliably enforces that. Null/blank is the honest "there is
    /// no session here" case (the draft-prompt A/B path) and omits the block entirely, which the
    /// rule's own escape clause covers - the model is never handed an empty title to voice.
    /// </summary>
    public static string BuildPrompt(SpokenLanguage language, string instructions, string recentContext, string latestReply, string? sessionTitle, string? liveScreen = null)
    {
        ArgumentNullException.ThrowIfNull(language);
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(instructions) ? FidelityPrompt : instructions);
        sb.Append("\n\n");
        // The contract goes AFTER the instructions, always. The instructions may be the user's own
        // words (issue #537) and will not mention a language or Markdown; appending here rather than
        // embedding there is what makes those two rules un-editable - see SpeechContract.
        sb.Append(SpeechContract.SpokenOutputContract(language));
        sb.Append("\n\n");
        if (!string.IsNullOrWhiteSpace(sessionTitle))
        {
            sb.Append("The title of the session this reply came from. Open the narration by saying ");
            sb.Append("this, in words, before anything else:\n");
            sb.Append("---\n");
            sb.Append(sessionTitle.Trim());
            sb.Append("\n---\n\n");
        }
        if (!string.IsNullOrWhiteSpace(recentContext))
        {
            sb.Append("Recent conversation for context, oldest first. Use ONLY as much of this as the ");
            sb.Append("listener needs to understand the latest reply - do not re-narrate it:\n");
            sb.Append("---\n");
            sb.Append(recentContext.Trim());
            sb.Append("\n---\n\n");
        }
        if (!string.IsNullOrWhiteSpace(liveScreen))
        {
            sb.Append("The terminal screen this session is showing RIGHT NOW, top to bottom (its live ");
            sb.Append("grid; stale glyphs from earlier frames may linger on it). Use it ONLY for the ");
            sb.Append("SCREEN verdict asked for after the markers - do not narrate it:\n");
            sb.Append("---\n");
            sb.Append(liveScreen.Trim());
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
        if (!string.IsNullOrWhiteSpace(liveScreen))
        {
            sb.Append('\n');
            sb.Append(ScreenVerdictContract);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The SCREEN verdict addendum (issue devthrottle_internal#1195): asked for ONLY when a live screen was
    /// supplied, and asked for AFTER the spoken markers so the proven spoken contract stays byte-identical -
    /// <see cref="ExtractSpoken"/> reads between the markers and never sees this line, which is what makes a
    /// garbled verdict harmless to the narration. Public so a test can assert the contract.
    /// </summary>
    public const string ScreenVerdictContract =
        "Then, AFTER the end marker, output EXACTLY ONE more line - your verdict on what the live screen\n"
        + "needs from the person, as JSON on a single line:\n"
        + "SCREEN: {\"needs\":\"menu\",\"question\":\"...\",\"options\":[\"1. Yes\",\"2. No\"]}\n"
        + "or SCREEN: {\"needs\":\"answer\"}  or  SCREEN: {\"needs\":\"nothing\"}\n"
        + "- \"menu\": the screen RIGHT NOW shows an interactive picker that must be answered by SELECTING a\n"
        + "  listed option (a permission prompt, a plan approval, a chooser with a selection marker). Typed\n"
        + "  free text will NOT work there. Include the question and the visible option labels.\n"
        + "- \"answer\": the agent asked the person something they answer in typed or spoken WORDS, or is\n"
        + "  waiting on a decision from them.\n"
        + "- \"nothing\": informational - the agent reported progress or results and is not waiting on the\n"
        + "  person for anything.\n"
        + "A numbered list INSIDE the agent's prose (a summary of work done, steps, findings) is NOT a menu,\n"
        + "even if a stray marker glyph sits next to a number - a menu is an interactive control waiting for\n"
        + "a selection. A finished reply above an empty input box is \"answer\" or \"nothing\", never \"menu\".";

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

    // The sanitize-for-speech pass used to live here as CleanupForSpeech, and that is precisely why
    // Car Mode never had it: Car Mode does not go through this class. It is now
    // SpeechContract.Finish, where every spoken path can reach it (issue #1008).
}

/// <summary>The result of one wingman translation: the spoken text and the brain latency.</summary>
public sealed class WingmanTranslation
{
    /// <summary>The faithful, speakable version of the agent's reply.</summary>
    public string Spoken { get; init; } = "";

    /// <summary>Seconds the warm brain took to produce the translation.</summary>
    public double ReplySeconds { get; init; }

    /// <summary>The model's verdict on what the live screen needs from the person
    /// (issue devthrottle_internal#1195), or null when no live screen was supplied or the verdict line did
    /// not parse. Null means UNKNOWN: callers fall back to their fail-safe default, never to a block.</summary>
    public WingmanScreenVerdict? Screen { get; init; }
}
