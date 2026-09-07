using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";

    /// <summary>
    /// Default extra command-line arguments for Claude Code. Empty here: the permission posture is
    /// decided by the agent entry's preset (whose catalog default is "Automatic",
    /// <c>--permission-mode auto</c>), not by this field. It stays for callers that pin an explicit
    /// argument string of their own.
    /// </summary>
    public string DefaultClaudeArgs { get; set; } = "";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Faster-stop control for the FLEET/remote stop path only (a Gateway DELETE / stream "kill" verb,
    /// i.e. the mobile + Cockpit stop button). The graceful window in MILLISECONDS the Director waits for a
    /// clean exit (after sending Ctrl+C) before force-killing, when the stop arrives remotely. A remote stop
    /// usually means "I want this gone now", and most agent TUIs ignore Ctrl+C entirely, so waiting the full
    /// <see cref="GracefulShutdownTimeoutSeconds"/> (5s) is dead time; this escalates to force faster while
    /// still trying graceful first.
    ///
    /// Tunable + safe: raise it toward 5000 to be more patient, or set it to null / a non-positive value to
    /// DISABLE the fast path entirely - the fleet stop then uses the standard <see
    /// cref="GracefulShutdownTimeoutSeconds"/>, byte-identical to before. The LOCAL desktop kill is never
    /// affected: it always uses <see cref="GracefulShutdownTimeoutSeconds"/>.
    /// </summary>
    public int? FleetKillGraceMs { get; set; } = 1500;

    /// <summary>
    /// Fleet-message steward policy (flag: <c>messaging.steward</c>): dedupe + per-source rate limit +
    /// broadcast throttle on a session's OUTGOING fleet messages, applied at its own Director's
    /// <c>/fleet/*</c> handlers. Default-ON with generous limits; see <see cref="MessageStewardOptions"/>.
    /// </summary>
    public MessageStewardOptions MessageSteward { get; set; } = new();

    /// <summary>
    /// Path to the Pi agent CLI. pi ships two ways: an official per-platform archive that puts a
    /// native <c>pi</c> on PATH, and the npm package <c>@earendil-works/pi-coding-agent</c> that
    /// drops <c>pi.cmd</c> in the npm global directory (also on PATH). Both put <c>pi</c> on PATH,
    /// so the default is the bare command name and ExecutableResolver finds whichever is installed
    /// - exactly like codex, opencode, grok and cursor. The previous default hard-coded the npm
    /// <c>pi.cmd</c> path, which does not exist for archive-install users and cannot exist at all
    /// off Windows, so pi sessions failed to launch there. Users can override in config.json if pi
    /// is installed off PATH.
    /// </summary>
    public string PiPath { get; set; } = DefaultPiPath;

    /// <summary>
    /// Path to the OpenAI Codex CLI. Codex ships two ways: the standalone installer drops a
    /// native <c>codex.exe</c> under <c>%LOCALAPPDATA%\Programs\OpenAI\Codex\bin</c> and adds it
    /// to PATH, while the npm package <c>@openai/codex</c> drops <c>codex.cmd</c> in the npm global
    /// directory (also on PATH). Both put <c>codex</c> on PATH, so the default is the bare command
    /// name and ExecutableResolver finds whichever is installed - exactly like opencode, grok, and
    /// cursor. The previous default hard-coded the npm <c>codex.cmd</c> path, which does not exist
    /// for standalone-installer users, so Codex sessions failed to launch. Users can override in
    /// config.json if codex is installed off PATH.
    /// </summary>
    public string CodexPath { get; set; } = "codex";

    /// <summary>
    /// Path to the Google Gemini CLI (<c>@google/gemini-cli</c>). The npm global install puts
    /// <c>gemini</c> on PATH, so the default is the bare command name and ExecutableResolver
    /// resolves it - on Windows through PATHEXT to <c>gemini.cmd</c>. The previous default
    /// hard-coded the npm path, which cannot exist off Windows.
    /// </summary>
    public string GeminiPath { get; set; } = DefaultGeminiPath;

    /// <summary>
    /// Path to the opencode CLI (the <c>opencode</c> binary from opencode.ai).
    /// opencode is not an npm package; its installer (curl/brew/scoop/mise) puts
    /// <c>opencode</c> on PATH, so the default relies on PATH resolution. Users can
    /// override in config.json if opencode is installed somewhere off PATH.
    /// </summary>
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>
    /// Path to the Cursor CLI agent (the <c>cursor-agent</c> binary from cursor.com).
    /// cursor-agent is not an npm package; its installer (the cursor.com install
    /// script) puts <c>cursor-agent</c> on PATH, so the default relies on PATH
    /// resolution. Users can override in config.json if cursor-agent is installed
    /// somewhere off PATH or named differently (issue #517, assumption A1).
    /// </summary>
    public string CursorPath { get; set; } = "cursor-agent";

    /// <summary>
    /// Path to the xAI Grok CLI (<c>grok</c> binary).
    /// The Grok installer places <c>grok.exe</c> under <c>~/.grok/bin/</c> and adds that
    /// directory to the user PATH, so the default relies on PATH resolution. Users can
    /// override in config.json if grok is installed to a non-standard location.
    /// </summary>
    public string GrokPath { get; set; } = "grok";

    /// <summary>
    /// Path to the GitHub Copilot CLI (<c>copilot.cmd</c> from <c>@github/copilot</c>).
    /// The npm global install drops <c>copilot</c>, <c>copilot.cmd</c>, and <c>copilot.ps1</c> in
    /// <c>%APPDATA%\npm</c>; the launchable shim for a process spawner is <c>copilot.cmd</c> (the
    /// <c>.ps1</c> cannot be spawned directly). The default is nonetheless the BARE name: on Windows
    /// ExecutableResolver appends PATHEXT and lands on <c>copilot.cmd</c>, and it deliberately
    /// refuses the extensionless npm bash shim sitting beside it, so the launchable shim is still
    /// what gets chosen. That keeps Windows behaviour identical while letting a non-Windows install
    /// resolve at all. Users can override in config.json if copilot is installed elsewhere
    /// (Homebrew, WinGet, the gh.io/copilot-install script) (issue #625).
    /// </summary>
    public string CopilotPath { get; set; } = DefaultCopilotPath;

    /// <summary>
    /// GitHub Copilot authentication token, injected into a Copilot session's environment when set
    /// (issue #625). Loaded from config.json "agent.copilot_github_token" first. The effective
    /// token resolves with the precedence Copilot itself honors:
    /// <c>COPILOT_GITHUB_TOKEN</c> &gt; <c>GH_TOKEN</c> &gt; <c>GITHUB_TOKEN</c> (see
    /// <see cref="ResolveCopilotToken"/>). Null/empty means Director injects nothing and the user
    /// completes interactive auth (<c>copilot login</c> / the <c>/login</c> slash command) inside
    /// the session tab. Never logged.
    /// </summary>
    public string? CopilotGitHubToken { get; set; }

    /// <summary>
    /// Cursor authentication key, injected into a Cursor session's environment as
    /// <c>CURSOR_API_KEY</c> when set (issue #517, assumption A5). Loaded from
    /// config.json "agent.cursor_api_key" first, then falls back to the
    /// <c>CURSOR_API_KEY</c> environment variable. Null/empty means Director injects
    /// nothing and cursor-agent uses whatever key is already in the environment.
    /// Never logged.
    /// </summary>
    public string? CursorApiKey { get; set; }

    /// <summary>
    /// Absolute path to the repository the Director chat will relay every chat
    /// message to. Set via appsettings.json "Chat.SessionRepoPath" - e.g.
    /// "C:/repos/private" - so the Director's /chat endpoint knows which
    /// session represents "the agent" for one-session deployments.
    /// Null means the Director chat will require an explicit SessionId per request.
    /// </summary>
    public string? ChatSessionRepoPath { get; set; }

    /// <summary>
    /// Legacy standalone TTS voice for older no-resolver callers. Production hosted voice selection
    /// comes from <see cref="TtsVoiceConfig"/>.
    /// </summary>
    public string TtsVoice { get; set; } = "onyx";

    /// <summary>
    /// Legacy standalone TTS model for older no-resolver callers. Production hosted model selection
    /// comes from <see cref="TtsModelConfig"/>.
    /// </summary>
    public string TtsModel { get; set; } = "tts-1";

    /// <summary>
    /// Legacy standalone provider key for older no-resolver callers
    /// (<see cref="ResolveOpenAiKey"/>). Issue #839 removed the config.json
    /// Voice.OpenAiKey loading, so this is no longer populated from config and is
    /// NOT the transcription key store - transcription reads the key vault only
    /// (<see cref="HostedAiKeyResolver"/> / the Gateway transcription service). The
    /// Never sent to browsers.
    /// </summary>
    public string? OpenAiKey { get; set; }

    /// <summary>
    /// Path to the user-editable dictation dictionary YAML. If null, resolves
    /// to <c>%LOCALAPPDATA%/cc-director/dictation/dictionary.yaml</c>. Missing
    /// file means no cleanup glossary at all - no vocabulary and no known
    /// mistranscriptions - so transcripts come back uncorrected; the rest of the
    /// dictation pipeline still works. Nothing in this file is ever sent to the
    /// speech-to-text provider (issue 2481).
    /// </summary>
    public string? DictationDictionaryPath { get; set; }

    /// <summary>
    /// Hosted cleanup model used by the Gateway transcription CleanupOrchestrator.
    /// Defaults to the DevThrottle dictation cleanup model.
    /// </summary>
    public string DictationCleanupModel { get; set; } = TranscriptionEndpointResolver.DevThrottleDictationCleanupModel;

    /// <summary>
    /// Legacy preview model setting retained for config compatibility. Production transcription is
    /// Gateway-owned and does not use a Director-side preview transcriber.
    /// </summary>
    public string DictationPreviewModel { get; set; } = TranscriptionEndpointResolver.DevThrottleModel;

    /// <summary>
    /// Resolve the effective dictation dictionary path. Always returns a
    /// concrete path; callers should treat a missing file as "empty dictionary".
    /// </summary>
    public string ResolveDictationDictionaryPath()
    {
        if (!string.IsNullOrWhiteSpace(DictationDictionaryPath))
            return DictationDictionaryPath;
        return CcStorage.DictationDictionary();
    }

    /// <summary>
    /// Resolve the legacy standalone provider key: the in-process <see cref="OpenAiKey"/> when set,
    /// then the legacy environment variable. Returns null if neither is set.
    ///
    /// Issue #839: this is NOT the transcription key path. Transcription reads the key vault only
    /// (<see cref="HostedAiKeyResolver"/> and the Gateway transcription service); the config.json
    /// Voice.OpenAiKey loading was removed, so in production <see cref="OpenAiKey"/> is unset.
    /// </summary>
    public string? ResolveOpenAiKey()
    {
        if (!string.IsNullOrWhiteSpace(OpenAiKey))
            return OpenAiKey.Trim();
        var env = Environment.GetEnvironmentVariable(TranscriptionEndpointResolver.DevThrottleKeyName);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>
    /// Resolve the effective Cursor API key: explicit config wins, then the
    /// <c>CURSOR_API_KEY</c> environment variable. Returns null if neither is set,
    /// in which case Director injects nothing (issue #517, assumption A5).
    /// </summary>
    public string? ResolveCursorApiKey()
    {
        if (!string.IsNullOrWhiteSpace(CursorApiKey))
            return CursorApiKey.Trim();
        var env = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>
    /// Resolve the effective GitHub Copilot token (issue #625). Explicit config wins, then the
    /// environment variables in the precedence Copilot itself honors:
    /// <c>COPILOT_GITHUB_TOKEN</c> &gt; <c>GH_TOKEN</c> &gt; <c>GITHUB_TOKEN</c>. Returns null when
    /// none is set, in which case Director injects nothing and the user completes interactive auth.
    /// The token value is never logged.
    /// </summary>
    public string? ResolveCopilotToken()
    {
        if (!string.IsNullOrWhiteSpace(CopilotGitHubToken))
            return CopilotGitHubToken.Trim();

        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// The default command names for the three agents that used to hard-code an npm global
    /// <c>.cmd</c> path. They are bare names so <see cref="Utilities.ExecutableResolver"/> can find
    /// whichever install shape is present, on any platform.
    /// </summary>
    /// <remarks>
    /// The removed helper built an npm global <c>.cmd</c> path and looked like it degraded
    /// gracefully to the bare name when <c>%APPDATA%</c> was unavailable. That fallback was
    /// unreachable off Windows: .NET maps <see cref="Environment.SpecialFolder.ApplicationData"/>
    /// to <c>~/.config</c>, so the value was never empty and the function confidently returned a
    /// Windows batch path under a Linux home directory. It failed closed while reading as though
    /// it failed open. Measured on a real Ubuntu desktop, where the Director logged
    /// <c>configured=/home/qa/.config/npm/pi.cmd</c> on every scan and pi could not launch.
    /// These constants are also what <c>ToolDetectionService</c> compares against to decide a path
    /// is still the untouched default, so the two must not drift apart.
    /// </remarks>
    internal const string DefaultPiPath = "pi";

    /// <inheritdoc cref="DefaultPiPath"/>
    internal const string DefaultGeminiPath = "gemini";

    /// <inheritdoc cref="DefaultPiPath"/>
    internal const string DefaultCopilotPath = "copilot";
}
