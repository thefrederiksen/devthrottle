namespace CcDirector.Core.Storage;

/// <summary>
/// Single source of truth for all cc-director storage paths.
/// Mirrors the Python cc_storage.CcStorage API.
///
/// Storage categories:
///   Vault  - Personal data: contacts, docs, tasks, goals, health, vectors
///   Config - Tool settings, OAuth tokens, credentials, app state
///   Output - Generated files: PDFs, reports, transcripts, exports
///   Logs   - All application and tool logs
///   Bin    - Installed executables (tool binaries)
///
/// Environment variable overrides:
///   CC_DIRECTOR_ROOT - Override the base directory (default: %LOCALAPPDATA%\cc-director)
///   CC_VAULT_PATH    - Override the vault directory specifically
///   CC_DIRECTOR_INSTANCES_DIR - Override the Director instance-discovery directory specifically
///
/// NOTE: CcStorage methods intentionally omit FileLog.Write calls because
/// FileLog's default directory is initialized from CcStorage.ToolLogs(), creating a
/// circular dependency at static initialization time.
/// </summary>
public static class CcStorage
{
    // -- Root categories --

    /// <summary>Root directory for all cc-director storage.</summary>
    public static string Root() => Base();

    /// <summary>
    /// The Gateway's EF Core database file (gateway.db) under the storage root, beside the existing
    /// gateway-stats.db. One SQLite file holds the structured stores that have moved off hand-rolled JSON
    /// onto the EF data layer (Hosted Gateway mission, Step 1b). Resolved through the root so
    /// CC_DIRECTOR_ROOT redirects it - callers must ask here rather than composing the path themselves.
    /// </summary>
    public static string GatewayDb() => Path.Combine(Root(), "gateway.db");

    private static string Base()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return overrideRoot;

        return ResolveDefaultBase(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("HOME"));
    }

    /// <summary>
    /// Composes the default storage root from the platform's per-user data directory.
    ///
    /// This is a separate, parameterised method for one reason: on Linux
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an EMPTY STRING when the
    /// per-user data directory does not yet exist - the normal state on a minimal install, and the state
    /// every first-run Linux user is in. Path.Combine("", "cc-director") does not fail; it returns the
    /// RELATIVE path "cc-director", which then resolves against the process working directory.
    ///
    /// For the shipped Linux build that working directory is the one holding the executable, and the
    /// executable is itself named "cc-director" - so the storage root resolved onto the binary and the
    /// Director died at startup with DirectoryNotFoundException creating "cc-director/logs/director". On
    /// Windows the ".exe" suffix means those two names can never collide, so the defect is invisible there.
    ///
    /// The rule this encodes: a storage root is either an ABSOLUTE path or a loud failure. It is never a
    /// relative path, because a relative root does not throw - it silently writes the user's data
    /// somewhere nobody will look.
    /// </summary>
    /// <param name="localAppData">SpecialFolder.LocalApplicationData; blank on Linux when it does not exist.</param>
    /// <param name="xdgDataHome">$XDG_DATA_HOME, the spec's first choice when set.</param>
    /// <param name="home">$HOME, from which the spec's default ~/.local/share is composed.</param>
    internal static string ResolveDefaultBase(string? localAppData, string? xdgDataHome, string? home)
    {
        // The normal path: Windows always, and any Linux box whose data directory already exists.
        if (!string.IsNullOrWhiteSpace(localAppData))
            return RequireAbsolute(Path.Combine(localAppData, "cc-director"), "the platform data directory");

        // The XDG Base Directory spec, in its own order: $XDG_DATA_HOME, else $HOME/.local/share. The
        // directory is created rather than treated as an error - "it does not exist yet" is the normal
        // state of a fresh account, not a fault.
        var dataHome = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : !string.IsNullOrWhiteSpace(home)
                ? Path.Combine(home, ".local", "share")
                : null;

        if (string.IsNullOrWhiteSpace(dataHome))
        {
            throw new InvalidOperationException(
                "Could not determine where to store cc-director data: the platform reported no local " +
                "application-data directory, and neither XDG_DATA_HOME nor HOME is set. Set CC_DIRECTOR_ROOT " +
                "to an absolute path to choose the storage root explicitly.");
        }

        var root = RequireAbsolute(Path.Combine(dataHome, "cc-director"), "XDG_DATA_HOME or HOME");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Guards the one invariant that matters: the storage root is absolute. A relative root is the exact
    /// failure this method exists to prevent, and it must fail loudly here rather than quietly become a
    /// directory beside whatever the working directory happens to be.
    /// </summary>
    private static string RequireAbsolute(string candidate, string source)
    {
        if (!Path.IsPathRooted(candidate))
        {
            throw new InvalidOperationException(
                $"Refusing a relative cc-director storage root '{candidate}' derived from {source}. A relative " +
                "root would resolve against the current working directory and write the user's data somewhere " +
                "unpredictable. Set CC_DIRECTOR_ROOT to an absolute path.");
        }

        return candidate;
    }

    /// <summary>Personal data: vault.db, vectors, documents, health, media.</summary>
    public static string Vault()
    {
        var overridePath = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        return Path.Combine(Base(), "vault");
    }

    /// <summary>Tool settings, OAuth tokens, credentials, app state.</summary>
    public static string Config() => Path.Combine(Base(), "config");

    /// <summary>
    /// DevThrottle's OWN automation-browser root: browsers/. Holds the registry.json describing each
    /// agent-drivable browser and, beside it, one <c>&lt;id&gt;/</c> sub-directory per browser that is that
    /// browser's dedicated Chromium <c>--user-data-dir</c>. This is intentionally NOT the personal
    /// bh-profiles location and NOT cc-director\connections - DevThrottle owns this tree so its
    /// browsers are per-machine state the local Director alone manages. Resolved through the root so
    /// CC_DIRECTOR_ROOT redirects it for tests.
    /// </summary>
    public static string Browsers() => Path.Combine(Base(), "browsers");

    /// <summary>
    /// Director instance-discovery directory: config/director/instances/. Each running Director writes
    /// a <c>{directorId}.json</c> here and the Gateway watches it. Honors the
    /// <c>CC_DIRECTOR_INSTANCES_DIR</c> override (issue #322) so tests can pin JUST this directory to a
    /// throwaway location - keeping a stray test Director's instance file out of the real directory,
    /// where the live Gateway's file watcher would otherwise discover it, probe its dead ephemeral port,
    /// and paint a phantom "unreachable" Director - without redirecting the whole storage root.
    /// </summary>
    public static string DirectorInstances()
    {
        var overridePath = Environment.GetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        return Path.Combine(Config(), "director", "instances");
    }

    /// <summary>
    /// Generated files: PDFs, reports, transcripts, exports. Lives in the user's Documents folder by
    /// design - these are files the user opens - but honors CC_DIRECTOR_ROOT when set so a test that
    /// pins the root cannot write into the real Documents\cc-director. The product never sets the root,
    /// so its location is unchanged. Same fix already applied to <see cref="Bin"/> and
    /// <see cref="Screenshots"/>; this was the last path here that ignored the override.
    /// </summary>
    public static string Output()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return Path.Combine(overrideRoot, "output");

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "cc-director");
    }

    /// <summary>All application and tool logs.</summary>
    public static string Logs() => Path.Combine(Base(), "logs");

    /// <summary>
    /// The turn-log corpus: base/turn-log/&lt;day&gt;/&lt;account&gt;/&lt;machine&gt;/. One self-contained
    /// record per turn end, written only where an administrator has switched capture on, and pulled from
    /// here into the internal repository that keeps the corpus.
    ///
    /// Deliberately NOT under <see cref="Logs"/>. Logs are diagnostic exhaust that anything may rotate away;
    /// this is a test asset whose whole value is that it accumulates, and putting it where a log sweep can
    /// reach it would eventually delete the corpus for tidiness. Composes the path without touching disk -
    /// a Gateway with capture switched off leaves no directory behind.
    /// </summary>
    public static string TurnLog() => Path.Combine(Base(), "turn-log");

    /// <summary>
    /// Durable activity-state transition logs: base/state-changes/&lt;sessionId&gt;.jsonl. The caller creates
    /// the directory, so this composes the path without touching disk. Resolved here rather than in
    /// <see cref="Wingman.StateChangeLog"/> so it honors CC_DIRECTOR_ROOT like every other path: that class
    /// baked %LOCALAPPDATA% into a static readonly field, which no test could redirect - so its test wrote
    /// into the real running Director's data directory.
    /// </summary>
    public static string StateChanges() => Path.Combine(Base(), "state-changes");

    /// <summary>
    /// The durable activity-event outbox: base/activity-outbox/outbox.jsonl. Delivery state for the
    /// Gateway activity ledger (docs/PLAN-trustworthy-working-start-2026-07-24.md): events wait here,
    /// each minted ONCE with its id and sequence, until the Gateway acknowledges the batch - the Gateway
    /// is the only durable history, this file is only the not-yet-acknowledged tail. Composes the path
    /// without touching disk; the outbox creates the directory.
    /// </summary>
    public static string ActivityOutbox() => Path.Combine(Base(), "activity-outbox", "outbox.jsonl");

    /// <summary>
    /// Per-utterance resumable voice-chunk staging: base/voice-utterances/&lt;uploadId&gt;/. Transient - the
    /// service deletes each utterance directory after transcription, and creates them itself, so this
    /// composes the path without touching disk. Resolved here for the same reason as
    /// <see cref="StateChanges"/>: <see cref="Voice.VoiceUtteranceService"/> baked %LOCALAPPDATA% into a
    /// static readonly field that no test could redirect.
    /// </summary>
    public static string VoiceUtterances() => Path.Combine(Base(), "voice-utterances");

    /// <summary>
    /// Per-turn voice comparison logs (short retention, auto-purged): base/voice-turn-logs/.
    /// Holds the audio, user transcript, agent reply, and wingman spoken reply for
    /// each voice turn so a meaning divergence can be flagged and compared later.
    /// </summary>
    public static string VoiceTurnLogs() => Ensure(Path.Combine(Base(), "voice-turn-logs"));

    /// <summary>
    /// Per-turn review logs (7-day retention, auto-purged): base/turn-review/&lt;date&gt;/&lt;sessionId&gt;/.
    /// One record per turn-end (Working -&gt; needs-you), holding the terminal screen + transcript
    /// for that turn plus whatever the Wingman said/did, so any turn can be reviewed later.
    /// </summary>
    public static string TurnReviewLogs() => Ensure(Path.Combine(Base(), "turn-review"));

    /// <summary>
    /// Durable voice-turn archive (issue: guaranteed audio-turn): base/voice-turn-archive/&lt;turnId&gt;/.
    /// Each completed async voice turn writes its result here - meta.json (session id, upload id,
    /// stage, summary) plus reply.mp3 - so the reply "sits in the session" and is retrievable long
    /// after the in-memory job cache TTL and across a Gateway restart. Owned by the Gateway.
    /// </summary>
    public static string VoiceTurnArchive() => Ensure(Path.Combine(Base(), "voice-turn-archive"));

    /// <summary>
    /// Resumable upload staging for the Gateway voice-turn front door: base/voice-turn-uploads/&lt;uploadId&gt;/.
    /// Each chunk lands here as it arrives (SHA-checked, idempotent) and the dir is deleted once the
    /// chunks are assembled and the turn has been started. An upload that never reaches that point is
    /// removed by the Gateway's voice-turn upload sweep once nothing has written to it for hours, so
    /// abandoned recorded audio has a retention ceiling. Owned by the Gateway.
    /// </summary>
    public static string VoiceTurnUploads() => Ensure(Path.Combine(Base(), "voice-turn-uploads"));

    /// <summary>
    /// Resumable upload staging for durable dictation (issue #1006): base/dictation-uploads/&lt;uploadId&gt;/.
    /// The mobile app persists the raw audio locally the instant Send is pressed and streams it here in
    /// SHA-checked chunks; once assembled the Gateway transcribes it and injects the turn into the owning
    /// session itself, so a dead tab or a dropped connection cannot lose a recorded utterance. There is
    /// deliberately NO age sweep here (unlike the voice-turn staging above): each upload id carries a durable
    /// delivery record, its chunks are retained while it is undelivered, and the record is retired only by
    /// the client's acknowledgment. Owned by the Gateway.
    /// </summary>
    public static string DictationUploads() => Ensure(Path.Combine(Base(), "dictation-uploads"));

    /// <summary>
    /// "This brief is wrong" reports (TURN_BRIEFING.md D7): base/brief-feedback/. Each report
    /// stores the brief + the user's note as a labeled example that drives wingman prompt
    /// iteration. Written by the GATEWAY's feedback endpoint since issue #187. (The old
    /// Director-side turn-briefs ring at base/turn-briefs/ is dead data, left on disk.)
    /// </summary>
    public static string BriefFeedback() => Ensure(Path.Combine(Base(), "brief-feedback"));

    /// <summary>Installed executables (tool binaries). Honors the CC_DIRECTOR_ROOT override via
    /// <see cref="Base"/> like every other path here, so an isolated root redirects the tool bin too
    /// (previously this hardcoded %LOCALAPPDATA%\cc-director\bin and silently ignored the override).</summary>
    public static string Bin() => Path.Combine(Base(), "bin");

    // -- Tool-specific shortcuts --

    // -- Feature folders --
    //
    // Each of these used to be composed by hand at its call site from
    // GetFolderPath(LocalApplicationData) + "cc-director" + ..., which produced a path that
    // CC_DIRECTOR_ROOT could not redirect - so any test reaching that code wrote into the real
    // running Director's folders (#1577, #1580). They are declared here, once, so the root has a
    // single owner. StorageRootGuardTests fails the build if a new hand-rolled one appears.

    /// <summary>The shared credentials file every tool reads: config/credentials.env.</summary>
    public static string CredentialsEnv() => Path.Combine(Config(), "credentials.env");

    /// <summary>Installed agent plugin manifests: base/agent-plugins/.</summary>
    public static string AgentPlugins() => Path.Combine(Base(), "agent-plugins");

    /// <summary>Claude hook scripts the Director installs: base/claude-hooks/.</summary>
    public static string ClaudeHooks() => Path.Combine(Base(), "claude-hooks");

    /// <summary>Codex hook scripts the Director installs: base/codex-hooks/.</summary>
    public static string CodexHooks() => Path.Combine(Base(), "codex-hooks");

    /// <summary>The Director's cache of the GATEWAY-OWNED injected text:
    /// config/director/injected-text-cache.json. The authoritative value lives on the Gateway
    /// (GET /gateway/injected-text); this file is the last-known copy the Director reads synchronously at
    /// session launch so a launch never waits on - or fails without - the network.</summary>
    public static string InjectedTextCache() =>
        Path.Combine(ToolConfig("director"), "injected-text-cache.json");

    /// <summary>The Director's cache of the Gateway's workflow catalog INDEX (Workflows mission,
    /// phase 5): config/director/workflow-index-cache.json. The few-line discoverability block that
    /// rides the fleet preamble; the authoritative catalog lives on the Gateway
    /// (GET /gateway/workflows). Mirrors the injected-text cache beside it.</summary>
    public static string WorkflowIndexCache() =>
        Path.Combine(ToolConfig("director"), "workflow-index-cache.json");

    /// <summary>The Director's cached SKILL index (the central skill library):
    /// config/director/skill-index-cache.json. The few-line discoverability block that rides the
    /// fleet preamble - names and one line each, never bodies. The library itself lives on the
    /// Gateway (GET /gateway/skills) and a skill's body is fetched only when it is used.</summary>
    public static string SkillIndexCache() =>
        Path.Combine(ToolConfig("director"), "skill-index-cache.json");

    /// <summary>Dictation root: base/dictation/. Holds the user dictionary plus the
    /// recordings/ and sessions/ subfolders.</summary>
    public static string Dictation() => Path.Combine(Base(), "dictation");

    /// <summary>The user's dictation dictionary: base/dictation/dictionary.yaml. Read by both the
    /// Director and the Gateway transcription owner, which composed it separately before.</summary>
    public static string DictationDictionary() => Path.Combine(Dictation(), "dictionary.yaml");

    /// <summary>Durable dictation audio: base/dictation/recordings/.</summary>
    public static string DictationRecordings() => Path.Combine(Dictation(), "recordings");

    /// <summary>Per-session dictation logs: base/dictation/sessions/.</summary>
    public static string DictationSessions() => Path.Combine(Dictation(), "sessions");

    /// <summary>The preamble file handed to the Pi agent: base/pi-preamble/.</summary>
    public static string PiPreamble() => Path.Combine(Base(), "pi-preamble");

    /// <summary>
    /// Remove-the-network-port mission, phase 3: the ready-to-print SessionStart hook output the
    /// Director MAINTAINS for each live session, one file per session:
    /// base/session-preambles/. A session's SessionStart hook prints this file instead of calling
    /// the Director's Control API for it.
    ///
    /// MAINTAINED, not written once. The preamble renders from three live stores - the user's
    /// injected text, the workflow index and the skill index, all Gateway-owned and all refreshed
    /// on the Director's poll - plus the session's own name and workflow seat. A file written once
    /// at launch would serve the user their OLD text after they edited it and would hide newly
    /// published skills, and nothing would look broken. See SessionPreambleMaintainer.
    /// </summary>
    public static string SessionPreambles() => Path.Combine(Base(), "session-preambles");

    /// <summary>
    /// Remove-the-network-port mission, phase 3: the drop box a Claude SessionStart hook writes its
    /// CURRENT session id and transcript path into, one file per session:
    /// base/session-pointers/. The Director watches this directory, so the hook reports a rotated
    /// transcript (after /clear or auto-compaction) by writing a file rather than by POSTing to a
    /// local HTTP route. See SessionPointerWatcher.
    /// </summary>
    public static string SessionPointers() => Path.Combine(Base(), "session-pointers");

    /// <summary>Recorded terminal sessions: base/session-recordings/.</summary>
    public static string SessionRecordings() => Path.Combine(Base(), "session-recordings");

    /// <summary>Transient recording transcripts (audio + markdown) before the user promotes the
    /// keepers into the vault: base/transcripts/.</summary>
    public static string Transcripts() => Path.Combine(Base(), "transcripts");

    /// <summary>The Gateway's durable prompt + reply record: base/prompt-log/.</summary>
    public static string PromptLog() => Path.Combine(Base(), "prompt-log");

    /// <summary>Bounded local history behind Transcription Health: base/transcription-history/.</summary>
    public static string TranscriptionHistory() => Path.Combine(Base(), "transcription-history");

    /// <summary>Rolling archive of the audio behind each transcription, keyed by the history turn id.
    /// Bounded by age and count by its owner (TranscriptionAudioArchive); sits beside TranscriptionHistory()
    /// so a suspicious transcript line leads straight to the clip that produced it.</summary>
    public static string TranscriptionAudio() => Path.Combine(Base(), "transcription-audio");

    /// <summary>Clips from the Test microphone / Test transcription checks, kept per tenant for later
    /// analysis: base/voice-test-clips/. Deliberately NOT the same directory as TranscriptionAudio():
    /// that one is a 24-hour troubleshooting buffer for ordinary dictation, whereas these are
    /// deliberate, user-initiated recordings of a passage WE supplied, kept longer on purpose so
    /// transcription quality can be compared across languages, headsets and releases.</summary>
    public static string VoiceTestClips() => Path.Combine(Base(), "voice-test-clips");

    /// <summary>Per-dictation microphone measurements behind the Cockpit's microphone-quality
    /// section: base/microphone-quality/. Numbers only - no audio and no transcript text - so it
    /// answers "which of my microphones is bad" without keeping any record of what was said.</summary>
    public static string MicrophoneQuality() => Path.Combine(Base(), "microphone-quality");

    /// <summary>Terminal screen captures: base/terminal-captures/.</summary>
    public static string TerminalCaptures() => Path.Combine(Base(), "terminal-captures");

    /// <summary>Machine-local reservations a live session holds on its worktree so the worktree reaper
    /// (in any Director slot) will not delete the folder under it: base/worktree-reservations/.</summary>
    public static string WorktreeReservations() => Path.Combine(Base(), "worktree-reservations");

    /// <summary>Machine-local record of worktree folders git deregistered but could not physically
    /// delete (a locked build output), retried on a later reap: base/worktree-leftovers/.</summary>
    public static string WorktreeLeftovers() => Path.Combine(Base(), "worktree-leftovers");

    /// <summary>WebView2 user-data directory for the card renderer: base/webview2-card/.</summary>
    public static string WebView2Card() => Path.Combine(Base(), "webview2-card");

    /// <summary>Config directory for a specific tool: config/{tool}/</summary>
    public static string ToolConfig(string tool) => Path.Combine(Config(), tool);

    /// <summary>Output directory for a specific tool: output/{tool}/</summary>
    public static string ToolOutput(string tool) => Path.Combine(Output(), tool);

    /// <summary>Log directory for a specific tool: logs/{tool}/</summary>
    public static string ToolLogs(string tool) => Path.Combine(Logs(), tool);

    // -- Vault subdirectories --

    /// <summary>Main personal data database: vault/vault.db</summary>
    public static string VaultDb() => Path.Combine(Vault(), "vault.db");

    /// <summary>Job scheduler state database: vault/engine.db</summary>
    public static string EngineDb() => Path.Combine(Vault(), "engine.db");

    /// <summary>Quick Actions chat database: vault/quick_actions.db</summary>
    public static string QuickActionsDb() => Path.Combine(Vault(), "quick_actions.db");

    /// <summary>Imported files: vault/documents/</summary>
    public static string VaultDocuments() => Path.Combine(Vault(), "documents");

    /// <summary>Promoted recording transcripts (the permanent copy): vault/transcripts/. RecordingEndpoints
    /// hardcoded LocalApplicationData + "cc-director/vault/transcripts", which ignored CC_VAULT_PATH and so
    /// would have promoted into the wrong vault for anyone who relocated theirs.</summary>
    public static string VaultTranscripts() => Path.Combine(Vault(), "transcripts");

    /// <summary>Embeddings: vault/vectors/</summary>
    public static string VaultVectors() => Path.Combine(Vault(), "vectors");

    /// <summary>Media files: vault/media/</summary>
    public static string VaultMedia() => Path.Combine(Vault(), "media");

    /// <summary>Health data: vault/health/</summary>
    public static string VaultHealth() => Path.Combine(Vault(), "health");

    /// <summary>Backup files: vault/backups/</summary>
    public static string VaultBackups() => Path.Combine(Vault(), "backups");

    /// <summary>Session handover documents: vault/handovers/</summary>
    public static string VaultHandovers() => Ensure(Path.Combine(Vault(), "handovers"));

    // -- Config shortcuts --

    /// <summary>Shared settings file: config/config.json</summary>
    public static string ConfigJson() => Path.Combine(Config(), "config.json");

    /// <summary>
    /// User's screenshots directory, where phone-uploaded images are filed so the
    /// owning session can read them by absolute path. Resolution order:
    ///   1. config.json -> screenshots.source_directory (honored when explicitly set).
    ///   2. Platform default:
    ///      - macOS: the Desktop (where macOS drops screenshots by default).
    ///      - Windows/Linux: the "Pictures" known folder + \Screenshots. GetFolderPath
    ///        follows a OneDrive redirect, so on a machine with Pictures backed up to
    ///        OneDrive this yields e.g. D:\...\OneDrive\Pictures\Screenshots.
    /// The directory is created if it does not exist. On a Mac neither default may match
    /// where the user actually keeps screenshots, so the explicit config override (set via
    /// the Settings page) is the reliable path - see CcDirectorConfigService.
    /// </summary>
    public static string Screenshots()
    {
        var configured = TryReadConfigString("screenshots", "source_directory");
        if (!string.IsNullOrWhiteSpace(configured))
            return Ensure(configured);

        // No configured folder. When CC_DIRECTOR_ROOT is set the caller has pinned storage to a throwaway
        // location (only tests do this - the product never sets it), so keep the screenshots folder inside
        // that root rather than falling through to the user's REAL Pictures\Screenshots. Without this a test
        // that redirects the root still wrote into the user's own screenshots folder, where the gallery listed
        // the leftovers as undrawable date-only entries; the leak shipped because setting the root LOOKS like
        // it sandboxes everything. Mirrors CC_DIRECTOR_INSTANCES_DIR (issue #322) in keeping test artefacts out
        // of the user's real environment.
        var overrideRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (!string.IsNullOrEmpty(overrideRoot))
            return Ensure(Path.Combine(overrideRoot, "screenshots"));

        if (OperatingSystem.IsMacOS())
            return Ensure(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Ensure(Path.Combine(pictures, "Screenshots"));
    }

    /// <summary>
    /// Read a nested string value from config.json (config[section][key]). Returns null
    /// when the file, section, or key is absent. Used for optional path overrides.
    /// </summary>
    private static string? TryReadConfigString(string section, string key)
    {
        var path = ConfigJson();
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty(section, out var sectionEl)) return null;
        if (!sectionEl.TryGetProperty(key, out var valueEl)) return null;
        return valueEl.ValueKind == System.Text.Json.JsonValueKind.String ? valueEl.GetString() : null;
    }

    /// <summary>Communication queue database: config/comm-queue/communications.db</summary>
    public static string CommQueueDb() => Path.Combine(ToolConfig("comm-queue"), "communications.db");

    /// <summary>
    /// Encrypted DevThrottle account credential blob: config/director/devthrottle-credential.bin.
    /// The access-plus-refresh token pair is written here encrypted at rest by the operating system
    /// credential store (Windows Data Protection on Windows), never as plain text. Distinct from the
    /// Claude sign-in account store (accounts.json) - this is the DevThrottle account.
    /// </summary>
    public static string DevThrottleCredentialBlob() =>
        Path.Combine(ToolConfig("director"), "devthrottle-credential.bin");

    /// <summary>
    /// Encrypted DevThrottle account credential blob on the Gateway: config/gateway/devthrottle-credential.bin.
    /// The Gateway-Centralization Phase 2 foundation (issue #636): the access-plus-refresh token pair is
    /// written here encrypted at rest by the operating system credential store (Windows Data Protection on
    /// Windows), never as plain text. Distinct from the per-Director credential blob
    /// (<see cref="DevThrottleCredentialBlob"/>) under config/director - the account moves onto the Gateway
    /// so each Director no longer holds its own copy.
    /// </summary>
    public static string GatewayDevThrottleCredentialBlob() =>
        Path.Combine(ToolConfig("gateway"), "devthrottle-credential.bin");

    // -- Life Operating System coaching directories --

    /// <summary>Life OS coaching root: vault/life/</summary>
    public static string VaultLife() => Path.Combine(Vault(), "life");

    /// <summary>
    /// Life OS coaching category directory: vault/life/{category}/
    /// Valid categories: assistant, health, business, personal, growth.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    public static string CoachingCategory(string category)
    {
        return Ensure(Path.Combine(VaultLife(), category));
    }

    // -- Workspaces --

    /// <summary>Workspace definitions directory: config/director/workspaces/</summary>
    public static string Workspaces() => Path.Combine(ToolConfig("director"), "workspaces");

    /// <summary>Named-session definitions directory: config/director/named-sessions/</summary>
    public static string NamedSessions() => Path.Combine(ToolConfig("director"), "named-sessions");

    // -- Browser Connections --

    /// <summary>Browser connections directory: base/connections/</summary>
    public static string Connections() => Path.Combine(Base(), "connections");

    /// <summary>Connection registry file: connections/connections.json</summary>
    public static string ConnectionsRegistry() => Path.Combine(Connections(), "connections.json");

    /// <summary>Chrome profile directory for a specific connection: connections/{name}/</summary>
    public static string ConnectionProfile(string name) => Path.Combine(Connections(), name);

    /// <summary>Workflow storage for a connection: connections/{name}/workflows/</summary>
    public static string ConnectionWorkflows(string name) =>
        Ensure(Path.Combine(ConnectionProfile(name), "workflows"));

    /// <summary>Workflow data directory: connections/{name}/workflows/{workflow}/</summary>
    public static string ConnectionWorkflowDir(string connectionName, string workflowName) =>
        Ensure(Path.Combine(ConnectionWorkflows(connectionName), SafeFileName(workflowName)));

    /// <summary>Workflow runs directory: connections/{name}/workflows/{workflow}/runs/</summary>
    public static string ConnectionWorkflowRuns(string connectionName, string workflowName) =>
        Ensure(Path.Combine(ConnectionWorkflowDir(connectionName, workflowName), "runs"));

    /// <summary>Workflow run screenshot directory: .../{workflow}/runs/{runId}/</summary>
    public static string ConnectionWorkflowRunDir(string connectionName, string workflowName, string runId) =>
        Ensure(Path.Combine(ConnectionWorkflowRuns(connectionName, workflowName), runId));

    // -- Utilities --

    /// <summary>Create directory if it doesn't exist and return the path.</summary>
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Sanitize a name for use as a file/directory name.</summary>
    internal static string SafeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
}
