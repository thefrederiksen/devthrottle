using System.Globalization;

namespace CcDirector.Gateway.Speech;

/// <summary>
/// EVERY FIXED SENTENCE THIS PRODUCT SPEAKS, IN EVERY LANGUAGE IT SPEAKS (issue #1009).
///
/// Four spoken paths ask a model for words, and the spoken output contract makes those come back in the
/// account's language. These are the OTHER ones - the sentences the product says itself, with no model
/// involved: the confirmation after a delete, the line when it cannot read a session's screen, the Car
/// Mode help script, the notice when a narration was too long to read out. Before this file they were
/// string constants scattered across five files, every one of them English, and an account set to French
/// heard them in English in the middle of an otherwise French conversation. The owner's decision on
/// issue #1009 was explicit: everything speaks the language, no exceptions.
///
/// WHY THE TRANSLATIONS LIVE IN CODE AND NOT IN A RESOURCE FILE. A missing translation has to be
/// impossible, not merely unlikely. Here, adding a language to <see cref="SpokenLanguages"/> turns every
/// phrase below red in <c>SpokenPhraseTests</c> until it is translated, and a phrase added without all
/// three languages does not compile. A .resx pair would have moved that from build time to run time, and
/// a spoken string that is missing at run time is silence in somebody's car.
///
/// ACCENTS ARE REQUIRED HERE, AND THIS IS THE ONE PLACE IN THE REPOSITORY THAT IS TRUE. The Architect's
/// ruling (docs/MISSION-multilingual-RULINGS.md) settles it: Kokoro PHONEMIZES what it is given, so
/// "Desole" and "Desole" spelled with its accents are different vowels and the model mispronounces the
/// stripped form. Stripping accents does not produce slightly-off French - it produces a voice saying
/// the wrong sounds, and it fails silently because the audio still plays. Every measurement this mission
/// rests on used accented text. The repository's ASCII rule protects OUTPUT CHANNELS - terminals, logs,
/// console messages - and it still binds every identifier, key, comment and log line in this file. It
/// does not bind the payload.
///
/// Two consequences of that, both load-bearing:
///   1. NEVER LOG THE TEXT. If a log line needs to mention a phrase, log its <see cref="SpokenPhrase.Key"/>
///      or a length. <see cref="SpokenPhrase.In(SpokenLanguage)"/> throws with the KEY, never the words.
///   2. THIS FILE IS UTF-8 WITH A BYTE ORDER MARK, and a test asserts it. Read as the machine's default
///      code page instead, an accented letter becomes mojibake and nothing fails - the audio just comes
///      out wrong. That test compares against \u escapes rather than accented literals, so a test file
///      decoded the same wrong way cannot agree with the bug.
///
/// TRANSLATION PROVENANCE, recorded because it is an accepted risk and not an oversight. Issue #1009:
/// one model translates, a second separately-prompted model reviews, and there is NO native human
/// reviewer - nobody in-house reads French or Spanish. The owner accepted that knowingly. If French or
/// Spanish quality is ever reported as poor, this is the first place to look.
///
/// REGISTER: French addresses the owner as "vous" and Spanish as "tu", consistently within each
/// language. Both are the ordinary register for a professional tool talking to one person; mixing them
/// inside one language is what would read as machine output.
/// </summary>
public static class SpokenPhrases
{
    // ---- Voice turn: when the product will not answer, and says why ---------------------------------

    /// <summary>Voice turn refuses because a menu owns the session's screen and could not be read
    ///  clearly. It fails CLOSED and says so - it never types an answer into a menu blindly.</summary>
    public static readonly SpokenPhrase VoiceTurnBlockedMenu = new(
        "voice-turn.blocked-menu",
        en: "There's a menu on this session's screen that I couldn't read clearly, so I won't answer it blindly. "
            + "Open the session to pick an option.",
        // Two sentences in French rather than one long relative clause: with nine words between "menu"
        // and "que", a listener starts attaching the clause to "cette session" instead.
        fr: "Un menu s'affiche à l'écran de cette session, et je n'ai pas réussi à le lire clairement. "
            + "Je ne vais donc pas y répondre à l'aveugle. Ouvrez la session pour choisir une option.",
        es: "Hay un menú en la pantalla de esta sesión que no he podido leer con claridad, "
            + "así que no voy a contestarlo a ciegas. Abre la sesión para elegir una opción.");

    /// <summary>Voice turn refuses because the session's screen could not be read at all.</summary>
    public static readonly SpokenPhrase VoiceTurnBlockedUnreadable = new(
        "voice-turn.blocked-unreadable",
        en: "I can't read this session's screen right now, so I won't type your answer in blindly. "
            + "Open the session to see what it's asking.",
        fr: "Je n'arrive pas à lire l'écran de cette session pour le moment, donc je ne vais pas saisir "
            + "votre réponse à l'aveugle. Ouvrez la session pour voir ce qu'elle demande.",
        es: "Ahora mismo no puedo leer la pantalla de esta sesión, así que no voy a escribir tu respuesta "
            + "a ciegas. Abre la sesión para ver qué te está preguntando.");

    /// <summary>The prompt front door's menu guard: a menu owns the screen, so nothing was typed and no
    ///  Enter was pressed. "Cockpit" is the product's own name and stays as it is in every language.</summary>
    public static readonly SpokenPhrase WaitingScreenMenu = new(
        "waiting-screen.menu",
        en: "This session is waiting on a menu, and I can't pick an option for you yet. "
            + "Open the session in the Cockpit or on your machine and choose one, then I can carry on from here.",
        fr: "Cette session attend une réponse à un menu, et je ne peux pas encore choisir une option à votre place. "
            + "Ouvrez la session dans le Cockpit ou sur votre machine et choisissez-en une ; je pourrai ensuite reprendre.",
        // "esperando una respuesta a un menu", not "esperando en un menu" - the latter is a calque that
        // means waiting AT a place, like waiting at a bus stop.
        es: "Esta sesión está esperando una respuesta a un menú y todavía no puedo elegir una opción por ti. "
            + "Abre la sesión en el Cockpit o en tu máquina y elige una; luego podré continuar.");

    /// <summary>Appended to a turn's narration when the turn ENDED on a menu, so the person hears it as
    ///  the turn is read out instead of discovering it when their reply goes nowhere. The leading space
    ///  is deliberate - it joins the sentence before it.</summary>
    public static readonly SpokenPhrase WaitingScreenMenuNarrationSuffix = new(
        "waiting-screen.menu-narration-suffix",
        en: " Heads up - this session is now waiting on a menu, so you'll need to open it and pick an option; "
            + "I can't answer that by voice yet.",
        // "Attention", not "Petite precision": this tells the listener something CHANGED and they must
        // now act. "Petite precision" announces a footnote and tells them to relax, at the exact moment
        // the English tells them to pay attention - a meaning change, not a style preference.
        fr: " Attention : cette session attend maintenant une réponse à un menu, vous devrez donc l'ouvrir "
            + "et choisir une option. Je ne peux pas encore y répondre par la voix.",
        es: " Aviso: esta sesión ahora está esperando una respuesta a un menú, así que tendrás que abrirla "
            + "y elegir una opción. Todavía no puedo responder a eso por voz.");

    /// <summary>What the listener hears instead of being cut off mid-word in silence, when a narration
    ///  ran past the runaway ceiling. The cut is the lie, not the limit - so the cut is AUDIBLE.</summary>
    public static readonly SpokenPhrase NarrationCutNotice = new(
        "narration.cut-notice",
        en: " That is as much as I can read out. This summary was too long, so the rest is not spoken - "
            + "open the session to read the full reply.",
        // Present tense and a named agent in both. "le reste n'est pas dit" / "el resto no se dice" are
        // agentless passives traced word for word from the English, and they are the two sentences in
        // this file most likely to make a native listener say "a machine wrote this". The summary also
        // IS too long, not WAS - it has not stopped being too long.
        fr: " C'est tout ce que je peux lire à voix haute. Ce résumé est trop long : je ne lirai pas la suite. "
            + "Ouvrez la session pour lire la réponse complète.",
        es: " Esto es todo lo que puedo leer en voz alta. Este resumen es demasiado largo, así que no leeré "
            + "el resto. Abre la sesión para leer la respuesta completa.");

    // ---- Reading a menu out loud -------------------------------------------------------------------
    //
    // These are WHOLE SENTENCES with slots, not fragments to glue. The old code built the reading by
    // concatenating "Option ", the number, ": ", the label, and " (recommended)" - which cannot be
    // translated, because word order, agreement and the position of the recommendation all differ per
    // language. Issue #1009 says so explicitly. Each language now owns the finished sentence and the
    // code only fills in the number and the label.

    /// <summary>One option in a spoken menu. {0} is its number, {1} its label.</summary>
    public static readonly SpokenPhrase MenuOption = new(
        "menu.option",
        en: "Option {0}: {1}.",
        fr: "Option {0} : {1}.",
        es: "Opción {0}: {1}.");

    /// <summary>The recommended option in a spoken menu. {0} is its number, {1} its label. A separate
    ///  whole sentence rather than a "(recommended)" tag welded onto the end of the one above, because
    ///  where the recommendation goes and how it agrees is a per-language decision.</summary>
    public static readonly SpokenPhrase MenuOptionRecommended = new(
        "menu.option-recommended",
        // The noun is NAMED rather than left to a pronoun. {1} is an arbitrary option label that may
        // itself end in a noun, so by the time the listener hears "celle" / "esa" the nearest candidate
        // is whatever happened to be at the end of the label, not the option as a whole.
        en: "Option {0}: {1}. That is the recommended option.",
        fr: "Option {0} : {1}. C'est l'option recommandée.",
        es: "Opción {0}: {1}. Esa es la opción recomendada.");

    /// <summary>How to answer a pick-one menu.</summary>
    public static readonly SpokenPhrase MenuAnswerSingle = new(
        "menu.answer-single",
        en: "Say the number, or the option.",
        fr: "Dites le numéro ou le nom de l'option.",
        es: "Di el número o el nombre de la opción.");

    /// <summary>How to answer a pick-any menu.</summary>
    public static readonly SpokenPhrase MenuAnswerMultiple = new(
        "menu.answer-multiple",
        en: "Say which ones apply, then say done.",
        fr: "Dites celles qui s'appliquent, puis dites terminé.",
        es: "Di cuáles se aplican y luego di listo.");

    // ---- The Language tab: the sentence you audition a voice with ------------------------------------

    /// <summary>
    /// The sample the Language tab speaks when you press Play sample (issue #1010) - the ONE spoken string
    /// on this list that is heard in a settings screen rather than in a session.
    ///
    /// IT IS PER LANGUAGE FOR A REASON, and the reason is not neatness. Auditioning a French voice on an
    /// English sentence tests the wrong thing: the engine phonemizes what it is given, so English words in
    /// a French voice tell you nothing about how that voice will read French. The screen also SHOWS this
    /// sentence beside the button, so the person can see what they are about to hear.
    ///
    /// It lives here, with every other fixed spoken sentence, rather than in the settings tab that plays
    /// it. A sentence held in the client would be spoken content in a file with no accent guard, no
    /// missing-translation test and no encoding test - three protections this file already has and a
    /// second home would have to grow again.
    /// </summary>
    public static readonly SpokenPhrase SettingsVoiceSample = new(
        "settings.voice-sample",
        en: "Hi, I'm your DevThrottle wingman. This is how I'll sound.",
        // "vous", like every other French phrase here. The second sentence is deliberately about the
        // VOICE rather than a literal "this is how I sound" - the natural French for auditioning a voice
        // talks about the voice, and a word-for-word carry-over reads as translated.
        fr: "Bonjour, je suis votre wingman DevThrottle. Voilà à quoi ressemble ma voix.",
        // "tu", like every other Spanish phrase here.
        es: "Hola, soy tu wingman de DevThrottle. Así es como voy a sonar.");

    /// <summary>Every phrase above. The completeness test walks this, so a phrase that is not here is
    ///  not covered - which is why <c>SpokenPhraseTests</c> also checks that every public phrase field
    ///  on this class appears in the list.</summary>
    public static readonly IReadOnlyList<SpokenPhrase> All = new[]
    {
        VoiceTurnBlockedMenu, VoiceTurnBlockedUnreadable,
        WaitingScreenMenu, WaitingScreenMenuNarrationSuffix, NarrationCutNotice,
        MenuOption, MenuOptionRecommended, MenuAnswerSingle, MenuAnswerMultiple,
        SettingsVoiceSample,
    };
}

/// <summary>
/// One fixed sentence the product speaks, in every language it speaks (issue #1009).
///
/// The three languages are CONSTRUCTOR PARAMETERS rather than a dictionary the caller fills, so a phrase
/// cannot be declared with one missing: the compiler asks for all three. Adding a fourth language to
/// <see cref="SpokenLanguages"/> turns <c>SpokenPhraseTests</c> red for every phrase until it is
/// translated, which is the acceptance row on issue #1009 - "a test that fails if a spoken string has no
/// translation for a supported language".
/// </summary>
public sealed class SpokenPhrase
{
    private readonly IReadOnlyDictionary<string, string> _byLanguageCode;

    /// <param name="key">A stable, ASCII, log-safe name for this phrase. It is what a log line or an
    ///  exception says instead of the words - see the ruling on accents in the class above.</param>
    public SpokenPhrase(string key, string en, string fr, string es)
    {
        Key = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("A phrase key is required.", nameof(key)) : key;
        _byLanguageCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SpokenLanguages.English.Code] = en ?? throw new ArgumentNullException(nameof(en)),
            [SpokenLanguages.French.Code] = fr ?? throw new ArgumentNullException(nameof(fr)),
            [SpokenLanguages.Spanish.Code] = es ?? throw new ArgumentNullException(nameof(es)),
        };
    }

    /// <summary>The stable, ASCII name of this phrase - safe to log, unlike the phrase itself.</summary>
    public string Key { get; }

    /// <summary>Every translation, keyed by language code. Read by the completeness test.</summary>
    public IReadOnlyDictionary<string, string> Translations => _byLanguageCode;

    /// <summary>
    /// This phrase in <paramref name="language"/>.
    ///
    /// A language with no translation THROWS, and names only the key. It does not fall back to English:
    /// a silent fallback is precisely how the last attempt shipped four times looking like it worked,
    /// and the completeness test makes this throw unreachable in practice. The message carries the key
    /// and not the text, so an accented sentence never reaches a log through an exception either.
    /// </summary>
    public string In(SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        if (_byLanguageCode.TryGetValue(language.Code, out var text)) return text;
        throw new InvalidOperationException(
            $"Spoken phrase '{Key}' has no translation for language '{language.Code}'. Every phrase in "
            + "SpokenPhrases must cover every language in SpokenLanguages - add the translation rather "
            + "than falling back to English.");
    }

    /// <summary>This phrase in <paramref name="language"/>, with its slots filled. Formatting is
    ///  invariant on purpose: the slots carry names and numbers the product already decided, never
    ///  locale-formatted values, so a culture change can never alter what is said.</summary>
    public string In(SpokenLanguage language, params object?[] arguments)
        => string.Format(CultureInfo.InvariantCulture, In(language), arguments);
}
