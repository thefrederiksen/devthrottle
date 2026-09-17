using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// The ONE typed per-tenant settings resolver (issue #2017) - the seam the MTR runtime-threading follow-up
/// builds on. Every method REQUIRES an explicit <see cref="TenantId"/>: this layer performs no ambient or
/// static tenant inference. For each setting it returns the tenant's own override when one is set and valid,
/// otherwise the OPERATOR GLOBAL DEFAULT (the existing process-global <c>config.json</c> value) - never another
/// tenant's value. A <see cref="TenantId"/> cannot be blank by construction, and on hosted a blank/unresolved
/// tenant is a route-level 403 before this resolver is reached, so a returned value can never be attributed to
/// the wrong tenant.
///
/// Two faces:
///   - READ methods (<see cref="WingmanModel"/> etc.) are what the runtime consumers call once MTR threads the
///     resolved tenant to their call sites (wingman brain, text-to-speech, car mode, snooze creation, stats).
///   - WRITE methods (<see cref="SetWingmanModel"/> etc.) are what the settings-page endpoints call; they
///     VALIDATE with the same rules the global setters use, then persist a per-tenant override.
///
/// Serialization is opaque to the <see cref="TenantSettingsStore"/> below it: this resolver owns turning a
/// typed value into the stored string and back, and owns the fallback when a stored string is missing or fails
/// validation (a corrupt override must never leak another tenant's value or crash a turn - it degrades to the
/// operator default, which is the safe, tenant-neutral answer).
/// </summary>
public sealed class TenantSettingsResolver
{
    private readonly TenantSettingsStore _store;

    /// <param name="store">The per-tenant override store. Required.</param>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public TenantSettingsResolver(TenantSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    // ---- reads: tenant override, else operator global default -------------------------------------------

    /// <summary>
    /// The tenant's wingman model for a role, or the operator global default when unset. Only a
    /// DevThrottle internal included id is honored from the override (issue #1360, Included AI): a
    /// tenant override saved as a catalog id in an older release would bill credits on an internal
    /// feature, so the mint falls forward to the included default exactly as the config.json path
    /// does. Returns the PROVEN <see cref="IncludedModelId"/>, so what this resolves can be handed
    /// straight to the deployment credential.
    /// </summary>
    public IncludedModelId WingmanModel(TenantId tenant, TranscriptionMode mode, WingmanModelRole role)
    {
        var key = role switch
        {
            WingmanModelRole.Thinking => TenantSettingKeys.WingmanModel,
            WingmanModelRole.Fast => TenantSettingKeys.WingmanFastModel,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown wingman model role"),
        };
        return IncludedModelId.MintOrFallForward(
            NonEmptyOverride(tenant, key), WingmanModelConfig.Resolve(mode, role));
    }

    /// <summary>
    /// The tenant's text-to-speech voice - ALWAYS A VOICE THAT SPEAKS THIS ACCOUNT'S LANGUAGE (issue
    /// #1010).
    ///
    /// This is the single read every synthesis path already went through, which is why the language is
    /// applied HERE rather than at each call site: the Language tab has to change what a session actually
    /// sounds like, and a voice chosen anywhere else would be a second place to remember. A French
    /// account read out by an English voice is the same class of failure as a French account answered in
    /// English - the setting appears to work and the product does not.
    ///
    /// The order, and why each step is where it is:
    ///   1. THE VOICE THIS ACCOUNT CHOSE FOR THIS LANGUAGE, when it is still a voice of that language.
    ///      Validated on every read, so a voice retired upstream, or one written by a newer Gateway,
    ///      degrades to step 2 or 3 instead of being sent to an engine that answers 422 - which reaches
    ///      the listener as silence rather than as an error.
    ///   2. ENGLISH ONLY: the account's existing <c>tts_voice</c> override, else the operator global
    ///      default. This is the whole of the old behaviour, unchanged, and it is deliberately reachable
    ///      only in English. An account that has never opened the Language tab is English by default and
    ///      therefore resolves EXACTLY as it did before this setting existed - including a voice id that
    ///      is not in our own list, which a self-hosted operator may legitimately have set.
    ///   3. THE LANGUAGE'S DEFAULT VOICE, for French and Spanish. Never the English override: an English
    ///      voice cannot read French, so falling through to it would be worse than any default.
    ///
    /// It picks a VOICE and never an engine. The engine is <see cref="TtsModel"/>, resolved separately and
    /// with no knowledge of the language - the two concepts never meet, and
    /// <c>SpokenLanguageContractTests</c> fails the build if they ever do in one method
    /// (devthrottle_internal#547).
    /// </summary>
    public string TtsVoice(TenantId tenant, TranscriptionMode mode)
    {
        var language = SpokenLanguage(tenant);
        var chosen = SpokenVoice(tenant, language);
        if (chosen is not null) return chosen;
        if (language == Speech.SpokenLanguages.English)
            return NonEmptyOverride(tenant, TenantSettingKeys.TtsVoice) ?? TtsVoiceConfig.Resolve(mode);
        return Speech.SpokenVoices.Default(language).Id;
    }

    /// <summary>
    /// THE ONE PLACE AN UTTERANCE IS BORN (issue #1031). Given this account and some words, decide the language
    /// it is spoken in and the voice that speaks it, and hand back a package no sink can misread.
    ///
    /// This is what "one place we speak from" means. Not one speaker - some speech must be local and
    /// network-free, so there will always be more than one engine - but one DECIDER. Every sink in the product
    /// takes a <see cref="Speech.SpokenUtterance"/> and plays it; none of them reads a setting, resolves a
    /// voice, or picks a language, because none of them can: a bare string does not compile against that
    /// parameter.
    ///
    /// ADDING A FOURTH LANGUAGE IS A ONE-PLACE CHANGE, and this method is why. Every spoken path in the Gateway
    /// gets its language and voice from here, so a new entry in <see cref="Speech.SpokenLanguages"/> plus its
    /// voices reaches all of them at once - there is no second list to remember and no call site to revisit.
    /// The compiler and the phrase tests then hold the other end: a language with no translated phrases, or no
    /// registered voice, does not build.
    ///
    /// It resolves a VOICE and never a model. The engine is <see cref="TtsModel"/>, resolved separately from a
    /// tenant and a transcription mode with no knowledge of any language - so a language cannot select an
    /// engine, which is the failure that got this feature reverted (devthrottle_internal#547).
    /// </summary>
    /// <param name="voiceOverride">An explicit voice, for AUDITIONING one - the Language tab's Play sample
    ///  offers a voice before it is chosen. Blank means "the account's own voice", which is the normal path.
    ///  Note what is NOT overridable: the language. A caller may ask to hear a different voice; it may not ask
    ///  to be spoken to in a language the account did not choose.</param>
    public Speech.SpokenUtterance Utterance(TenantId tenant, TranscriptionMode mode, string text,
        string? voiceOverride = null)
    {
        var language = SpokenLanguage(tenant);
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? TtsVoice(tenant, mode) : voiceOverride.Trim();
        return Speech.SpokenUtterance.For(language, voice, text);
    }

    /// <summary>
    /// The voice this tenant has chosen for <paramref name="language"/>, or null when it has chosen none
    /// - and also null when the stored id is not a voice of that language, so a caller can never be
    /// handed a voice that cannot speak the words it is about to be given.
    /// </summary>
    public string? SpokenVoice(TenantId tenant, Speech.SpokenLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var chosen = SpokenVoicesByLanguage(tenant);
        if (!chosen.TryGetValue(language.Code, out var voice)) return null;
        return Speech.SpokenVoices.Speaks(language, voice) ? voice.Trim() : null;
    }

    /// <summary>
    /// Every voice choice this tenant has made, keyed by language code. An empty map when it has made
    /// none, and ALSO when the stored object cannot be read: a corrupt map degrades to "no choices", which
    /// is each language's own default voice - a voice that certainly works - never another tenant's value
    /// and never a crashed turn.
    /// </summary>
    public IReadOnlyDictionary<string, string> SpokenVoicesByLanguage(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.SpokenVoiceByLanguage);
        if (string.IsNullOrEmpty(raw)) return EmptyVoiceChoices;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                   ?? EmptyVoiceChoices;
        }
        catch (System.Text.Json.JsonException)
        {
            return EmptyVoiceChoices;
        }
    }

    /// <summary>The tenant's text-to-speech model, or the operator global default when unset.</summary>
    public string TtsModel(TenantId tenant, TranscriptionMode mode)
        => NonEmptyOverride(tenant, TenantSettingKeys.TtsModel) ?? TtsModelConfig.Resolve(mode);

    /// <summary>The tenant's default snooze length in minutes, or the operator global default when unset or
    /// when the stored override fails validation.</summary>
    public int SnoozeDefaultMinutes(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.SnoozeDefaultMinutes);
        if (raw is not null && int.TryParse(raw, out var minutes) && SnoozeDefaultConfig.IsValid(minutes))
            return minutes;
        return SnoozeDefaultConfig.Get();
    }

    /// <summary>The tenant's snooze presets list (ascending), or the operator global default when unset or when
    /// the stored override fails validation as a set with its default.</summary>
    public IReadOnlyList<int> SnoozePresets(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.SnoozePresets);
        var parsed = ParsePresets(raw);
        if (parsed is not null)
        {
            var def = SnoozeDefaultMinutes(tenant);
            if (SnoozePresetsConfig.IsValidSet(parsed, def, out _))
                return parsed;
        }
        return SnoozePresetsConfig.Get();
    }

    /// <summary>The tenant's display time zone (IANA id), or the operator global default when unset or invalid.</summary>
    public string TimeZone(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.TimeZone);
        if (raw is not null && TimeZoneConfig.IsValid(raw))
            return raw;
        return TimeZoneConfig.Get();
    }

    /// <summary>The tenant's injected agent-launch text choice (use-yours flag + the user's own text), or the
    /// operator global default when unset or when the stored override cannot be parsed. The null-vs-empty
    /// distinction on the text is preserved: a corrupt override degrades to the global default (the
    /// tenant-neutral safe answer), never another tenant's value.</summary>
    public InjectedTextSettings InjectedText(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.InjectedText);
        if (raw is null) return InjectedTextConfig.Get();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<InjectedTextSettings>(raw) ?? InjectedTextConfig.Get();
        }
        catch (System.Text.Json.JsonException)
        {
            return InjectedTextConfig.Get();
        }
    }

    /// <summary>Whether this tenant is in VOICE MODE - every one of its sessions narrates its turns, including
    /// sessions created after the switch was thrown. Defaults to OFF: unlike every other setting here there is
    /// no operator global default to fall back to, because voice mode is the tenant's own choice and spends
    /// its own narration credits. Anything stored that is not exactly "true" reads as off - the quiet,
    /// nothing-happens answer, which is the safe way for a corrupt value to fail.</summary>
    public bool VoiceModeAll(TenantId tenant)
        => string.Equals(_store.Get(tenant, TenantSettingKeys.VoiceModeAll), "true", StringComparison.Ordinal);

    /// <summary>
    /// Whether pending dictionary suggestions are mentioned in this tenant's daily report email. Defaults to
    /// <see cref="SuggestionsInDailyEmailDefault"/> when the tenant has set no choice, and ALSO when the stored
    /// value is not a boolean: a corrupt override degrades to the documented default rather than to whichever
    /// boolean a lenient parse happened to leave behind.
    /// </summary>
    public bool SuggestionsInDailyEmail(TenantId tenant)
        => ParseBool(_store.Get(tenant, TenantSettingKeys.DictationSuggestionsInDailyEmail))
           ?? SuggestionsInDailyEmailDefault;

    /// <summary>
    /// This tenant's daily-email cadence state, or <see cref="DictationEmailCadenceState.None"/> when nothing
    /// has been sent yet or the stored state cannot be read. Degrading an unreadable state to "nothing sent" is
    /// the safe direction for a cadence whose whole job is to stay QUIET: it can cost at most one extra mention
    /// of a batch, where the opposite default would silence a batch that was never mentioned at all.
    /// </summary>
    public DictationEmailCadenceState DictationEmailCadence(TenantId tenant)
    {
        var raw = _store.Get(tenant, TenantSettingKeys.DictationEmailCadence);
        if (string.IsNullOrEmpty(raw)) return DictationEmailCadenceState.None;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<DictationEmailCadenceState>(raw)
                   ?? DictationEmailCadenceState.None;
        }
        catch (System.Text.Json.JsonException)
        {
            return DictationEmailCadenceState.None;
        }
    }

    /// <summary>
    /// How often this tenant wants the daily report email (issue #1000). Defaults to
    /// <see cref="ReportCadences.Default"/> - every day - when the tenant has expressed no choice, which is
    /// exactly what every account received before the setting existed.
    ///
    /// AN UNREADABLE VALUE ALSO READS AS DAILY, and that direction is deliberate. The two ways to be wrong
    /// are not symmetric: sending a report somebody silenced is visible to them and the mail carries its own
    /// way out, while silencing a report somebody wants is invisible - they simply stop hearing from the
    /// product and have nothing to act on. It also makes a Gateway rollback safe: a value written by a newer
    /// Gateway that this one does not know yet (weekly, when it lands) degrades to mail, not to silence.
    /// </summary>
    public ReportCadence DailyReportCadence(TenantId tenant)
        => ReportCadences.TryParse(_store.Get(tenant, TenantSettingKeys.DailyReportCadence), out var cadence)
            ? cadence
            : ReportCadences.Default;

    /// <summary>
    /// Whether this account receives the Development Mentor report (devthrottle_internal#1661). Defaults to
    /// <see cref="MentorReportEnabledDefault"/> - ON - when the account has expressed no choice, which is what
    /// every account received before the setting existed.
    ///
    /// AN UNREADABLE VALUE ALSO READS AS ON, matching <see cref="DailyReportCadence"/> and for its reason: a
    /// report somebody silenced arrives visibly and carries its own way out, while a report somebody wants
    /// going silent is invisible to them. It also keeps a Gateway rollback safe.
    ///
    /// THIS READ IS NOT THE GATE. Nothing in this process sends the mentor report - it is produced and sent by
    /// the harness in devthrottle_internal, which reads this same row out of the database directly. The harness
    /// refuses on an unreadable value rather than defaulting to ON, and the difference is deliberate rather
    /// than an inconsistency: this read answers "what does the card show", where being wrong costs a wrong
    /// checkbox, and that one answers "does an email quoting this person's prompts go out", where being wrong
    /// costs the send an opt-out was supposed to stop.
    /// </summary>
    public bool MentorReportEnabled(TenantId tenant)
        => ParseBool(_store.Get(tenant, TenantSettingKeys.MentorReportEnabled))
           ?? MentorReportEnabledDefault;

    /// <summary>
    /// The language this tenant is SPOKEN TO in (issue #1008) - the single source every spoken path
    /// reads, so a language reaches all of them or none of them. Defaults to English when the tenant has
    /// expressed no choice, which is what every account got before the setting existed.
    ///
    /// AN UNRECOGNIZED STORED CODE THROWS. It used to read as English, and that quiet default was the most
    /// dangerous line in the mission (re-audit): it turned every unknown code into a confident English answer
    /// no caller could distinguish from a real one. It is also unreachable in normal operation - the WRITE path
    /// refuses a code we cannot speak - so reaching it means data corruption or a rollback past a language we
    /// used to offer. Both are real failures, and a real failure that says so beats an account being spoken to
    /// in a language it did not choose.
    ///
    /// This is deliberately the ONLY read of the spoken language in the product. Every spoken path is
    /// handed a resolver call keyed on the tenant it already had to have, so there is no second place
    /// a language could be decided and no parameter anyone has to remember to thread.
    /// </summary>
    public Speech.SpokenLanguage SpokenLanguage(TenantId tenant)
    {
        var stored = _store.Get(tenant, TenantSettingKeys.SpokenLanguage);
        // ABSENT AND BLANK ARE DIFFERENT (audit 4, finding F1). No choice made is NO ROW, and the store returns
        // null for that - it is the documented default and what every account had before this setting existed.
        // A row that is PRESENT and blank is something else: the write path stores a canonical code and the store
        // rejects null, so three spaces in that row can only be malformed or rolled-back data. This used to test
        // IsNullOrWhiteSpace and laundered it into English, which is the same silent default the mission just
        // removed everywhere else - a probe pushed real English speech through it.
        if (stored is null) return Speech.SpokenLanguages.Default;
        return Speech.SpokenLanguages.Require(stored);
    }

    /// <summary>
    /// This tenant's session-supervisor settings (issue #915) - the auto-recovery master switch, the two
    /// waits, the retry ceiling and the model-fallback switch.
    ///
    /// Every value falls back to the documented default on <see cref="Supervision.SupervisorSettings"/> when
    /// the tenant has expressed no choice, and ALSO when the stored value is unparsable or outside the
    /// validated bounds. That direction is deliberate: a corrupt override must never widen the engine's
    /// licence. A zero-second first wait would hammer a session and an unbounded ceiling would be the
    /// infinite blind loop the issue explicitly forbids, so an unusable number reads as the shipped default,
    /// never as "no limit".
    /// </summary>
    public Supervision.SupervisorSettings SessionSupervisor(TenantId tenant)
    {
        var defaults = Supervision.SupervisorSettings.Defaults;

        var firstRetrySeconds = (int)defaults.FirstRetry.TotalSeconds;
        var storedFirst = _store.Get(tenant, TenantSettingKeys.SessionSupervisorFirstRetrySeconds);
        if (storedFirst is not null && int.TryParse(storedFirst, out var first)
            && Supervision.SupervisorSettings.IsValidFirstRetrySeconds(first))
            firstRetrySeconds = first;

        var cadenceMinutes = (int)defaults.RetryCadence.TotalMinutes;
        var storedCadence = _store.Get(tenant, TenantSettingKeys.SessionSupervisorRetryCadenceMinutes);
        if (storedCadence is not null && int.TryParse(storedCadence, out var cadence)
            && Supervision.SupervisorSettings.IsValidRetryCadenceMinutes(cadence))
            cadenceMinutes = cadence;

        var maxLongRetries = defaults.MaxLongRetries;
        var storedCeiling = _store.Get(tenant, TenantSettingKeys.SessionSupervisorMaxLongRetries);
        if (storedCeiling is not null && int.TryParse(storedCeiling, out var ceiling)
            && Supervision.SupervisorSettings.IsValidMaxLongRetries(ceiling))
            maxLongRetries = ceiling;

        return new Supervision.SupervisorSettings
        {
            Enabled = ParseBool(_store.Get(tenant, TenantSettingKeys.SessionSupervisorEnabled)) ?? defaults.Enabled,
            FirstRetry = TimeSpan.FromSeconds(firstRetrySeconds),
            RetryCadence = TimeSpan.FromMinutes(cadenceMinutes),
            MaxLongRetries = maxLongRetries,
            ModelFallbackEnabled = ParseBool(_store.Get(tenant, TenantSettingKeys.SessionSupervisorModelFallbackEnabled))
                                   ?? defaults.ModelFallbackEnabled,
        };
    }

    /// <summary>
    /// This tenant's turn-judging settings (the Wingman-on-every-turn mission) - the two switches plus the
    /// three numbers the seat runs on.
    ///
    /// The two switches fall back to OFF when the tenant has expressed no choice, and ALSO when the stored
    /// value is not a boolean. That direction is the same one the supervisor block takes and it matters
    /// more here: a corrupt override must never read as ON, because ON for the colour switch means a calm
    /// colour on somebody's screen that the judge has not earned, and a calm colour is the failure that is
    /// silent. The three numbers are not settable by a tenant today - they are the design's values, held
    /// here so the seat has ONE place to read them rather than three constants scattered through it.
    /// </summary>
    public Wingman.TurnVerdictSettings TurnVerdict(TenantId tenant)
        => new()
        {
            JudgeEnabled = ParseBool(_store.Get(tenant, TenantSettingKeys.TurnVerdictJudgeEnabled)) ?? false,
            ColourEnabled = ParseBool(_store.Get(tenant, TenantSettingKeys.TurnVerdictColourEnabled)) ?? false,
        };

    /// <summary>The id of the session this account has marked as its Fleet Manager, or null when it has marked
    /// none. Read by the turn-verdict held check at every stop, so a change applies to the next stop.</summary>
    public string? FleetManagerSessionId(TenantId tenant)
        => _store.Get(tenant, TenantSettingKeys.FleetManagerSessionId);

    /// <summary>The agent this account saved for its Fleet Manager, or null when it saved none (the default
    /// applies, and is worked out by the placement fold, not here).</summary>
    public string? FleetManagerAgent(TenantId tenant)
        => _store.Get(tenant, TenantSettingKeys.FleetManagerAgent);

    /// <summary>The computer this account saved for its Fleet Manager, or null when it saved none.</summary>
    public string? FleetManagerMachine(TenantId tenant)
        => _store.Get(tenant, TenantSettingKeys.FleetManagerMachine);

    // ---- writes: validate like the global setters, then persist a per-tenant override -------------------

    /// <summary>Set the tenant's wingman model for a role.</summary>
    /// <exception cref="ArgumentException">The model is null/empty.</exception>
    public void SetWingmanModel(TenantId tenant, WingmanModelRole role, string model, DateTime nowUtc)
    {
        var trimmed = RequireNonEmpty(model, nameof(model));
        var key = role switch
        {
            WingmanModelRole.Thinking => TenantSettingKeys.WingmanModel,
            WingmanModelRole.Fast => TenantSettingKeys.WingmanFastModel,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown wingman model role"),
        };
        _store.Set(tenant, key, trimmed, nowUtc);
    }

    /// <summary>Turn this tenant's VOICE MODE on or off. On is stored explicitly rather than by removing the
    /// override, so "off" is a decision this tenant made and not merely the absence of one - the sweep that
    /// applies voice mode to new sessions reads the same flag either way.</summary>
    public void SetVoiceModeAll(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.VoiceModeAll, enabled ? "true" : "false", nowUtc);

    /// <summary>Set the tenant's text-to-speech voice.</summary>
    /// <exception cref="ArgumentException">The voice is null/empty.</exception>
    public void SetTtsVoice(TenantId tenant, string voice, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TtsVoice, RequireNonEmpty(voice, nameof(voice)), nowUtc);

    /// <summary>Set the tenant's text-to-speech model.</summary>
    /// <exception cref="ArgumentException">The model is null/empty.</exception>
    public void SetTtsModel(TenantId tenant, string model, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TtsModel, RequireNonEmpty(model, nameof(model)), nowUtc);

    /// <summary>Set the tenant's display time zone (an IANA id).</summary>
    /// <exception cref="ArgumentException">The id is not a valid IANA time-zone id.</exception>
    public void SetTimeZone(TenantId tenant, string timeZone, DateTime nowUtc)
    {
        if (!TimeZoneConfig.IsValid(timeZone))
            throw new ArgumentException($"'{timeZone}' is not a valid time zone id.", nameof(timeZone));
        _store.Set(tenant, TenantSettingKeys.TimeZone, timeZone, nowUtc);
    }

    /// <summary>Set the tenant's injected agent-launch text (the use-yours flag and the text). Serialized as
    /// one JSON object so a null (cleared) text stays distinct from an empty ("inject nothing") one. Validate
    /// with <see cref="InjectedTextConfig.Validate"/> before calling, exactly as the global setter's caller
    /// does - this method persists, it does not re-validate.</summary>
    public void SetInjectedText(TenantId tenant, InjectedTextSettings settings, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.InjectedText,
            System.Text.Json.JsonSerializer.Serialize(settings), nowUtc);

    /// <summary>Reset this tenant's AI model/voice choices to the operator defaults by CLEARING those
    /// overrides (the legacy "reset to provider defaults" action). Snooze and time zone settings are left
    /// untouched.</summary>
    public void ClearAiProviderOverrides(TenantId tenant)
    {
        _store.Remove(tenant, TenantSettingKeys.WingmanModel);
        _store.Remove(tenant, TenantSettingKeys.WingmanFastModel);
        _store.Remove(tenant, TenantSettingKeys.TtsModel);
        _store.Remove(tenant, TenantSettingKeys.TtsVoice);
    }

    /// <summary>Set the tenant's snooze presets and default together, holding the invariant that the default is
    /// one of the presets (the same invariant the global setter enforces).</summary>
    /// <exception cref="ArgumentException">The presets/default are not a valid set.</exception>
    public void SetSnoozePresets(TenantId tenant, IReadOnlyList<int> presets, int defaultMinutes, DateTime nowUtc)
    {
        if (!SnoozePresetsConfig.IsValidSet(presets, defaultMinutes, out var error))
            throw new ArgumentException(error, nameof(presets));
        var sorted = presets.OrderBy(m => m).ToList();
        // Persist both, so a read of either is self-consistent for this tenant.
        _store.Set(tenant, TenantSettingKeys.SnoozePresets, SerializePresets(sorted), nowUtc);
        _store.Set(tenant, TenantSettingKeys.SnoozeDefaultMinutes, defaultMinutes.ToString(), nowUtc);
    }

    /// <summary>Set whether pending suggestions are mentioned in this tenant's daily report email.</summary>
    public void SetSuggestionsInDailyEmail(TenantId tenant, bool include, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.DictationSuggestionsInDailyEmail, include ? "true" : "false", nowUtc);

    /// <summary>Set how often this tenant wants the daily report email. Daily is stored EXPLICITLY rather
    /// than by clearing the override, so "back to daily" is a choice this account made and reads the same as
    /// one it never touched - and so a later look at the store can tell the two apart.</summary>
    public void SetDailyReportCadence(TenantId tenant, ReportCadence cadence, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.DailyReportCadence, ReportCadences.Name(cadence), nowUtc);

    /// <summary>Set whether this account receives the Development Mentor report. ON is stored EXPLICITLY
    /// rather than by clearing the override, so "turn it back on" is a choice this account made and a later
    /// look at the store can tell it apart from an account that never touched the setting.</summary>
    public void SetMentorReportEnabled(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.MentorReportEnabled, enabled ? "true" : "false", nowUtc);

    /// <summary>
    /// Set the language this tenant is spoken to in. English is stored EXPLICITLY rather than by
    /// clearing the override, so "back to English" is a choice this account made and a later look at
    /// the store can tell it apart from an account that never chose at all.
    ///
    /// An unsupported code is REFUSED here, where the person can see it - unlike the read, which
    /// degrades to English. A write is somebody making a choice; silently storing a language we cannot
    /// speak would leave them looking at a setting that says French while the product says English,
    /// which is the "the setting does nothing" report that came in three times on the last attempt.
    /// </summary>
    /// <exception cref="ArgumentException">The code is not a language this product speaks.</exception>
    public void SetSpokenLanguage(TenantId tenant, string code, DateTime nowUtc)
    {
        if (!Speech.SpokenLanguages.IsSupported(code))
            throw new ArgumentException(
                $"'{code}' is not a language DevThrottle speaks. Supported: "
                + string.Join(", ", Speech.SpokenLanguages.All.Select(l => l.Code)) + ".", nameof(code));
        _store.Set(tenant, TenantSettingKeys.SpokenLanguage,
            Speech.SpokenLanguages.Require(code).Code, nowUtc);
    }

    /// <summary>
    /// Remember the voice this tenant wants for ONE language, leaving its choices for the other languages
    /// exactly as they were (issue #1010). That is the whole mechanism: because a language's voice is never
    /// overwritten by a change to another language, switching away and back needs no restore step, and there
    /// is no restore step to get wrong.
    ///
    /// A VOICE THAT DOES NOT SPEAK THAT LANGUAGE IS REFUSED, here, where the person can see it - the same
    /// direction as <see cref="SetSpokenLanguage"/> and for the same reason. Storing an English voice under
    /// French would leave somebody looking at a screen that says French while an American voice reads French
    /// words aloud, which is indistinguishable from the setting doing nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The voice does not speak that language.</exception>
    public void SetSpokenVoice(TenantId tenant, Speech.SpokenLanguage language, string voice, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(language);
        var trimmed = (voice ?? "").Trim();
        if (!Speech.SpokenVoices.Speaks(language, trimmed))
        {
            var owner = Speech.SpokenVoices.LanguageOf(trimmed);
            throw new ArgumentException(
                $"'{trimmed}' is not a {language.EnglishName} voice."
                + (owner is null ? "" : $" It is a {owner.EnglishName} voice.")
                + " Choose one of: "
                + string.Join(", ", Speech.SpokenVoices.For(language).Select(v => v.Id)) + ".",
                nameof(voice));
        }

        // Read-modify-write of the one object, so the other languages' choices survive. Two settings pages
        // open at once is the only way this could lose a choice, and the loser is one voice preference on a
        // screen the person is looking at - not a value anything else depends on.
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, id) in SpokenVoicesByLanguage(tenant)) chosen[code] = id;
        chosen[language.Code] = trimmed;
        _store.Set(tenant, TenantSettingKeys.SpokenVoiceByLanguage,
            System.Text.Json.JsonSerializer.Serialize(chosen), nowUtc);
    }

    /// <summary>Turn this tenant's session supervisor on or off (issue #915). Stored explicitly, like voice
    /// mode, so "off" is a decision this account made rather than the absence of one.</summary>
    public void SetSessionSupervisorEnabled(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.SessionSupervisorEnabled, enabled ? "true" : "false", nowUtc);

    /// <summary>Turn this tenant's supervisor model fallback on or off.</summary>
    public void SetSessionSupervisorModelFallbackEnabled(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.SessionSupervisorModelFallbackEnabled, enabled ? "true" : "false", nowUtc);

    /// <summary>Set the first (short) wait before the supervisor's first "continue", in seconds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside the validated bounds.</exception>
    public void SetSessionSupervisorFirstRetrySeconds(TenantId tenant, int seconds, DateTime nowUtc)
    {
        if (!Supervision.SupervisorSettings.IsValidFirstRetrySeconds(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds,
                $"The first wait must be between {Supervision.SupervisorSettings.MinFirstRetrySeconds} and " +
                $"{Supervision.SupervisorSettings.MaxFirstRetrySeconds} seconds.");
        _store.Set(tenant, TenantSettingKeys.SessionSupervisorFirstRetrySeconds, seconds.ToString(), nowUtc);
    }

    /// <summary>Set this tenant's long retry cadence, in minutes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside the validated bounds.</exception>
    public void SetSessionSupervisorRetryCadenceMinutes(TenantId tenant, int minutes, DateTime nowUtc)
    {
        if (!Supervision.SupervisorSettings.IsValidRetryCadenceMinutes(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes,
                $"The retry cadence must be between {Supervision.SupervisorSettings.MinRetryCadenceMinutes} and " +
                $"{Supervision.SupervisorSettings.MaxRetryCadenceMinutes} minutes.");
        _store.Set(tenant, TenantSettingKeys.SessionSupervisorRetryCadenceMinutes, minutes.ToString(), nowUtc);
    }

    /// <summary>Set how many long-cadence retries this tenant allows before the supervisor escalates.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside the validated bounds.</exception>
    public void SetSessionSupervisorMaxLongRetries(TenantId tenant, int retries, DateTime nowUtc)
    {
        if (!Supervision.SupervisorSettings.IsValidMaxLongRetries(retries))
            throw new ArgumentOutOfRangeException(nameof(retries), retries,
                $"The retry ceiling must be between {Supervision.SupervisorSettings.MinLongRetries} and " +
                $"{Supervision.SupervisorSettings.MaxLongRetriesAllowed}.");
        _store.Set(tenant, TenantSettingKeys.SessionSupervisorMaxLongRetries, retries.ToString(), nowUtc);
    }

    /// <summary>Turn this tenant's turn JUDGING on or off. Stored explicitly, like voice mode, so "off" is a
    /// decision this account made rather than the absence of one.</summary>
    public void SetTurnVerdictJudgeEnabled(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TurnVerdictJudgeEnabled, enabled ? "true" : "false", nowUtc);

    /// <summary>Turn this tenant's turn-verdict COLOURS on or off - whether judged verdicts reach the
    /// screen. Independent of the judge switch on purpose: off here with the judge on is the shadow state,
    /// where verdicts are stored and gradeable and no row's colour moves.</summary>
    public void SetTurnVerdictColourEnabled(TenantId tenant, bool enabled, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.TurnVerdictColourEnabled, enabled ? "true" : "false", nowUtc);

    /// <summary>Mark <paramref name="sessionId"/> as this account's one Fleet Manager, replacing any earlier mark.
    /// Stored in the canonical lower-case form every roster row carries, so the held check compares like with
    /// like.</summary>
    /// <exception cref="ArgumentException">The session id is not a session id.</exception>
    public void SetFleetManagerSessionId(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        if (!Guid.TryParse(sessionId, out var parsed))
            throw new ArgumentException($"'{sessionId}' is not a session id.", nameof(sessionId));
        _store.Set(tenant, TenantSettingKeys.FleetManagerSessionId, parsed.ToString("D"), nowUtc);
    }

    /// <summary>
    /// Save where this account's Fleet Manager runs: both values together, so a half-saved placement cannot
    /// exist. Only the shape is checked here - a known agent kind and a non-blank machine name. Whether the
    /// machine is on the account and reachable is the caller's question, because only the caller holds that.
    /// </summary>
    /// <exception cref="ArgumentException">The agent is not an agent kind, or the machine is blank.</exception>
    public void SetFleetManagerPlacement(TenantId tenant, string agent, string machine, DateTime nowUtc)
    {
        var kind = Fleet.FleetManagerAgents.Canonical(agent)
            ?? throw new ArgumentException($"'{agent}' is not an agent the Fleet Manager can run on.", nameof(agent));
        if (string.IsNullOrWhiteSpace(machine))
            throw new ArgumentException("A computer is required.", nameof(machine));
        _store.Set(tenant, TenantSettingKeys.FleetManagerAgent, kind, nowUtc);
        _store.Set(tenant, TenantSettingKeys.FleetManagerMachine, machine.Trim(), nowUtc);
    }

    /// <summary>Remove this account's Fleet Manager mark. Returns true when there was one to remove.</summary>
    public bool ClearFleetManagerSessionId(TenantId tenant)
        => _store.Remove(tenant, TenantSettingKeys.FleetManagerSessionId);

    /// <summary>Record this tenant's daily-email cadence state after a mention is emitted.</summary>
    public void SetDictationEmailCadence(TenantId tenant, DictationEmailCadenceState state, DateTime nowUtc)
        => _store.Set(tenant, TenantSettingKeys.DictationEmailCadence,
            System.Text.Json.JsonSerializer.Serialize(state), nowUtc);

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>The default for <see cref="SuggestionsInDailyEmail"/> when a tenant has expressed no choice:
    /// ON. The suggestions feature exists to help people who never open Settings, so the one place it reaches
    /// them when they are not in the app has to be on by default.</summary>
    public const bool SuggestionsInDailyEmailDefault = true;

    /// <summary>The default for <see cref="MentorReportEnabled"/> when an account has expressed no choice: ON.
    /// The same reasoning as the daily report's default - an account that never opens Settings goes on
    /// receiving what it received before the setting existed, and the report itself carries the way out in its
    /// footer, so nobody has to find this page to be told the setting is here.</summary>
    public const bool MentorReportEnabledDefault = true;

    /// <summary>The "this account has chosen no voice for any language" answer. One shared instance, so the
    /// common read allocates nothing.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyVoiceChoices =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Parse a stored boolean override; null when absent or not a boolean, so the caller falls back to
    /// the documented default rather than guessing.</summary>
    private static bool? ParseBool(string? raw)
        => bool.TryParse(raw, out var value) ? value : null;


    /// <summary>An override value only when it is present AND non-empty; null otherwise (so the caller falls
    /// back to the operator global default rather than to an empty string).</summary>
    private string? NonEmptyOverride(TenantId tenant, string key)
    {
        var v = _store.Get(tenant, key);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Presets are stored as ascending comma-joined integers, e.g. "15,60,240,480".</summary>
    internal static string SerializePresets(IReadOnlyList<int> presets)
        => string.Join(",", presets);

    /// <summary>Parse the stored presets string back to a list, or null when absent or malformed (so the caller
    /// falls back to the operator default rather than a partial list).</summary>
    internal static IReadOnlyList<int>? ParsePresets(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n)) return null;
            list.Add(n);
        }
        return list.Count == 0 ? null : list;
    }

    private static string RequireNonEmpty(string value, string paramName)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException($"{paramName} is required.", paramName);
        return trimmed;
    }
}
