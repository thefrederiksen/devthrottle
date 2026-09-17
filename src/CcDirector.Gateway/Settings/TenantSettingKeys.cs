namespace CcDirector.Gateway.Settings;

/// <summary>
/// The fixed set of per-tenant setting keys stored in the <c>tenant_settings</c> table (issue #2017). Each
/// key names one overridable setting; the string value matches the existing process-global <c>config.json</c>
/// key so the mapping between a tenant override and the operator global default it falls back to is obvious at
/// a glance. These are the ONLY keys the typed <see cref="TenantSettingsResolver"/> reads and writes - a value
/// under any other key is not a setting this resolver serves.
///
/// The set is deliberately the values a tenant can genuinely choose for itself. It does NOT include the
/// machine-scoped settings (network addressing, autostart, brain restart, diagnostics), which stay self-host
/// only and keep their hosted deny, nor the "included" hosted facts (provider, transcription endpoint) that
/// have exactly one hosted option.
/// </summary>
public static class TenantSettingKeys
{
    /// <summary>The wingman's main reasoning model (global default: <c>brain_model</c>).</summary>
    public const string WingmanModel = "brain_model";

    /// <summary>The wingman's quick-turn model (global default: <c>brain_model_fast</c>).</summary>
    public const string WingmanFastModel = "brain_model_fast";

    /// <summary>The text-to-speech engine (global default: <c>tts_model</c>).</summary>
    public const string TtsModel = "tts_model";

    /// <summary>The voice the text-to-speech engine uses (global default: <c>tts_voice</c>).</summary>
    public const string TtsVoice = "tts_voice";

    /// <summary>The snooze lengths every Snooze menu offers, as the serialized presets list (global default:
    /// <c>snooze_presets</c>).</summary>
    public const string SnoozePresets = "snooze_presets";

    /// <summary>The default snooze length in minutes (global default: <c>snooze_default_minutes</c>).</summary>
    public const string SnoozeDefaultMinutes = "snooze_default_minutes";

    /// <summary>The display time zone (IANA id) the dashboards read local hours in (global default:
    /// <c>time_zone</c>).</summary>
    public const string TimeZone = "time_zone";

    /// <summary>The injected agent-launch text choice - the "use yours" flag and the user's own text -
    /// stored as one JSON object so the null-vs-empty distinction survives (global default:
    /// <c>injected_text</c>). The resolver owns serializing <c>InjectedTextSettings</c> to and from this
    /// string.</summary>
    public const string InjectedText = "injected_text";

    /// <summary>Whether this tenant is IN VOICE MODE: every one of its sessions narrates its turns, including
    /// sessions created after the switch was thrown. Unlike every other key here there is no operator global
    /// default to fall back to - voice mode is a per-tenant choice and its default is OFF.
    ///
    /// This is the flag that makes voice mode a SWITCH rather than a one-shot fan-out. Before it, nothing
    /// anywhere held "this fleet is in voice mode": the phone inferred it by checking whether any session
    /// happened to be marked, and made it true by walking the roster - so a session created afterwards was
    /// never told, and quietly never joined the voice queue. The Gateway holds the intent here, and the
    /// sweep applies it to sessions as they appear.</summary>
    public const string VoiceModeAll = "voice_mode_all";

    /// <summary>
    /// Whether pending dictionary suggestions are mentioned in this tenant's daily report email. Stored as
    /// "true"/"false". Like <see cref="VoiceModeAll"/> there is no operator global default to fall back to -
    /// this is a per-tenant choice about a per-tenant email - and its default is ON, so the mention reaches the
    /// people who never open Settings, which is who the suggestions feature exists for.
    /// </summary>
    public const string DictationSuggestionsInDailyEmail = "dictation_suggestions_in_daily_email";

    /// <summary>
    /// The per-tenant state behind the daily email's "mention a batch at most twice" cadence, serialized as one
    /// JSON object so the batch identity and the send count stay consistent with each other. Not a setting the
    /// user edits - it is written by the email-block route and read by nothing else - but it lives here because
    /// it is exactly a small per-tenant value and needs no table of its own.
    /// </summary>
    public const string DictationEmailCadence = "dictation_email_cadence";

    /// <summary>
    /// How often this tenant wants the daily report email, stored as the cadence NAME (issue #1000). Like
    /// <see cref="VoiceModeAll"/> there is no operator global default to fall back to - it is one account's
    /// choice about one account's mail - and its default is every day, so nothing changes for anyone who
    /// never opens Settings.
    ///
    /// Stored as a NAME rather than a boolean on purpose. The question is "how often", and it already has a
    /// third answer waiting (weekly, once the report can summarize a range rather than one day). A boolean
    /// would have to be replaced by a name to admit that third answer, migrating every row already written;
    /// a name absorbs it as a new value.
    /// </summary>
    public const string DailyReportCadence = "daily_report_cadence";

    /// <summary>
    /// Whether this account receives the Development Mentor report - the weekly written-and-spoken feedback on
    /// how the person prompts (devthrottle_internal#1661). Stored as "true"/"false". Like
    /// <see cref="VoiceModeAll"/> there is no operator global default to fall back to: it is one account's
    /// choice about one account's mail, and its default is ON.
    ///
    /// A BOOLEAN and not a cadence name, which is the opposite of the call
    /// <see cref="DailyReportCadence"/> made, on purpose. That setting answers "how often" and had a third
    /// answer already waiting. This one answers "do you want this at all": the mentor reads a person's own
    /// prompts to write it, and the question somebody asks about that is whether it happens, never how often.
    /// A rhythm the account cannot choose is not a rhythm it should be offered a name for.
    /// </summary>
    public const string MentorReportEnabled = "mentor_report_enabled";

    /// <summary>
    /// The language this account is SPOKEN TO in, stored as the short code (<c>en</c>/<c>fr</c>/<c>es</c>)
    /// - issue #1008. Like <see cref="VoiceModeAll"/> there is no operator global default to fall back
    /// to: which language a person wants to be spoken to in is that person's own choice and nobody
    /// else's, and its default is English, so nothing changes for anyone who never opens the Language
    /// tab.
    ///
    /// Stored as a CODE rather than an engine, a model, or a voice. That is the whole lesson of the
    /// reverted attempt (devthrottle_internal#547): a language is a choice about words, and the moment
    /// it becomes a choice about which speech engine runs, it can break speech itself. What this key
    /// holds can only ever change what the product SAYS, never what says it.
    /// </summary>
    public const string SpokenLanguage = "spoken_language";

    /// <summary>
    /// The voice this account has last chosen FOR EACH LANGUAGE, as one JSON object keyed by language
    /// code: <c>{"en":"bm_george","fr":"ff_siwis","es":"ef_dora"}</c> - issue #1010. Like
    /// <see cref="SpokenLanguage"/> there is no operator global default; a language nobody has chosen a
    /// voice for falls back to that language's own default voice.
    ///
    /// ONE OBJECT PER ACCOUNT RATHER THAN ONE KEY PER LANGUAGE, and it is stored per language rather than
    /// as a single current voice, because that is what removes the restore logic entirely. Nothing is ever
    /// overwritten when the language changes, so nothing has to be put back: switch English to French and
    /// back and the English voice is still whatever it was. The reverted attempt had an automatic switch
    /// AND a restore step, and the restore was one of the moving parts that made it impossible to reason
    /// about (devthrottle_internal#547).
    ///
    /// This holds a VOICE, and only ever a voice. It cannot hold a model: a voice id is meaningless to
    /// anything but the one engine that already serves English.
    /// </summary>
    public const string SpokenVoiceByLanguage = "spoken_voice_by_language";

    // ---- the session supervisor (issue #915) ------------------------------------------------------------
    // Like VoiceModeAll these have no operator global default to fall back to: they are one account's choice
    // about its own unattended runs. Every default is documented on SupervisorSettings, and a stored value
    // that is missing, unparsable, or outside the validated bounds degrades to that default - never to "no
    // limit", which would be the infinite blind loop the issue forbids.

    /// <summary>Whether the session supervisor may auto-recover this tenant's sessions. Default ON.</summary>
    public const string SessionSupervisorEnabled = "session_supervisor_enabled";

    /// <summary>The first (short) wait before the first "continue", in seconds. Default 45.</summary>
    public const string SessionSupervisorFirstRetrySeconds = "session_supervisor_first_retry_seconds";

    /// <summary>The long retry cadence, in minutes. Default 15.</summary>
    public const string SessionSupervisorRetryCadenceMinutes = "session_supervisor_retry_cadence_minutes";

    /// <summary>How many long-cadence retries before escalating instead of retrying forever. Default 8.</summary>
    public const string SessionSupervisorMaxLongRetries = "session_supervisor_max_long_retries";

    /// <summary>Whether the supervisor's model fallback may classify an unrecognized terminating error.
    /// Separately switchable because it is the only tier that sends terminal text off the machine. Default
    /// ON.</summary>
    public const string SessionSupervisorModelFallbackEnabled = "session_supervisor_model_fallback_enabled";

    // ---- the Wingman on every turn -----------------------------------------------------------------
    // TWO SWITCHES, NOT ONE, and the split is the whole safety of the rollout. Judging is what COSTS
    // (one model call per stop) and what fills the record; colouring is what the account SEES. Separating
    // them makes a shadow run possible: an account can be judged for days, with every verdict stored and
    // gradeable, while nothing on any screen has moved. One switch would have forced the choice between
    // learning nothing and showing every account a calm colour the judge had not yet earned - and a calm
    // colour is the one that goes wrong quietly, because it is the one that does NOT wake somebody.
    //
    // Like VoiceModeAll there is no operator global default to fall back to, and BOTH default to OFF.
    // Off is the behaviour every account already has, so nothing changes for anyone until somebody asks.

    /// <summary>Whether this tenant's stops are judged at all. Default OFF. Off means no model call is
    /// made and no verdict row is written.</summary>
    public const string TurnVerdictJudgeEnabled = "turn_verdict_judge_enabled";

    /// <summary>Whether this tenant's judged verdicts reach the screen - the calm colours and the one-line
    /// label. Default OFF, which is the SHADOW state: verdicts are stored and can be graded, and every row
    /// stays exactly the colour the detector made it.</summary>
    public const string TurnVerdictColourEnabled = "turn_verdict_colour_enabled";

    /// <summary>
    /// WHICH SESSION IS THIS ACCOUNT'S FLEET MANAGER, as that session's id - Fleet Manager mission. There is
    /// exactly one per account, and this is the mark that says which: the Wingman judges the sessions that
    /// session directly owns, and the session list pins it first. No operator global default and no default
    /// value - an account with no row has no Fleet Manager. The workflow a session is seated on never stands
    /// in for this mark, because that seat is inherited.
    /// </summary>
    public const string FleetManagerSessionId = "fleet_manager_session_id";

    /// <summary>
    /// WHICH AGENT THE ACCOUNT'S FLEET MANAGER RUNS ON, as <c>NewSessionRequest.Agent</c> takes it (for example
    /// "ClaudeCode") - Fleet Manager mission, step 5. Saved together with <see cref="FleetManagerMachine"/>; no
    /// row means the default, which the Gateway works out and shows as the default without storing it.
    /// </summary>
    public const string FleetManagerAgent = "fleet_manager_agent";

    /// <summary>
    /// WHICH COMPUTER THE ACCOUNT'S FLEET MANAGER RUNS ON, by machine name - Fleet Manager mission, step 5. Saved
    /// together with <see cref="FleetManagerAgent"/>; no row means the default.
    /// </summary>
    public const string FleetManagerMachine = "fleet_manager_machine";

    /// <summary>
    /// THE NEW FLEET MANAGER THAT IS WAITING TO TAKE OVER, as that session's id - the steps 5 and 6 fixes. Set when a
    /// restart or a move has started a new Fleet Manager while the old one is still running; the mark stays on the
    /// old one until its turn has ended and it has been closed, and then moves here and this row is removed. No row
    /// means no replacement is under way. Kept in storage so a Gateway restart carries the replacement on.
    /// </summary>
    public const string FleetManagerSuccessorSessionId = "fleet_manager_successor_session_id";

    /// <summary>
    /// THE MARKED FLEET MANAGER A WAITING REPLACEMENT IS TO CLOSE, as that session's id - the steps 5 and 6 fixes,
    /// round 2. Written in the same save as <see cref="FleetManagerSuccessorSessionId"/>, so a Gateway restart still
    /// knows exactly which session the replacement may close. If the mark no longer names this session, the mark was
    /// changed by hand and the replacement is abandoned without closing anything.
    /// </summary>
    public const string FleetManagerSuccessorReplaces = "fleet_manager_successor_replaces";

    /// <summary>
    /// THE OLD FLEET MANAGER A WAITING REPLACEMENT HAS ALREADY CLOSED, as that session's id. Written the moment the
    /// close goes through, so a promotion that fails afterwards is finished by the next look without needing the
    /// closed session to still be in the roster. Removed with the successor.
    /// </summary>
    public const string FleetManagerSuccessorClosedOld = "fleet_manager_successor_closed_old";

    /// <summary>Every key this resolver serves, for validation and enumeration.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        WingmanModel, WingmanFastModel, TtsModel, TtsVoice,
        SnoozePresets, SnoozeDefaultMinutes, TimeZone, InjectedText,
        VoiceModeAll, DictationSuggestionsInDailyEmail, DictationEmailCadence, DailyReportCadence,
        MentorReportEnabled, SpokenLanguage, SpokenVoiceByLanguage,
        SessionSupervisorEnabled, SessionSupervisorFirstRetrySeconds, SessionSupervisorRetryCadenceMinutes,
        SessionSupervisorMaxLongRetries, SessionSupervisorModelFallbackEnabled,
        TurnVerdictJudgeEnabled, TurnVerdictColourEnabled,
        FleetManagerSessionId,
        FleetManagerAgent, FleetManagerMachine,
        FleetManagerSuccessorSessionId, FleetManagerSuccessorReplaces, FleetManagerSuccessorClosedOld,
    };
}
