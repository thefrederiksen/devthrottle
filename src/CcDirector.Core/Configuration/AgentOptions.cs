using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";
    public string DefaultClaudeArgs { get; set; } = "--dangerously-skip-permissions";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Path to the Pi agent CLI (<c>pi.cmd</c> from <c>@earendil-works/pi-coding-agent</c>).
    /// Defaults to the standard npm global install location on Windows; users can override
    /// in config.json if pi is installed elsewhere.
    /// </summary>
    public string PiPath { get; set; } = DefaultNpmCliPath("pi");

    /// <summary>
    /// Path to the OpenAI Codex CLI (<c>codex.cmd</c> from <c>@openai/codex</c>).
    /// Defaults to the standard npm global install location on Windows.
    /// </summary>
    public string CodexPath { get; set; } = DefaultNpmCliPath("codex");

    /// <summary>
    /// Path to the Google Gemini CLI (<c>gemini.cmd</c> from <c>@google/gemini-cli</c>).
    /// Defaults to the standard npm global install location on Windows.
    /// </summary>
    public string GeminiPath { get; set; } = DefaultNpmCliPath("gemini");

    /// <summary>
    /// Path to the opencode CLI (the <c>opencode</c> binary from opencode.ai).
    /// opencode is not an npm package; its installer (curl/brew/scoop/mise) puts
    /// <c>opencode</c> on PATH, so the default relies on PATH resolution. Users can
    /// override in config.json if opencode is installed somewhere off PATH.
    /// </summary>
    public string OpenCodePath { get; set; } = "opencode";

    /// <summary>
    /// Absolute path to the repository the Director chat will relay every chat
    /// message to. Set via appsettings.json "Chat.SessionRepoPath" - e.g.
    /// "D:/ReposFred/private" - so the Director's /chat endpoint knows which
    /// session represents "the agent" for one-session deployments.
    /// Null means the Director chat will require an explicit SessionId per request.
    /// </summary>
    public string? ChatSessionRepoPath { get; set; }

    /// <summary>
    /// OpenAI TTS voice for the voice mode.  Defaults to "onyx" - OpenAI's deep,
    /// natural male voice.  Both the web voice page and the Android client POST
    /// /tts with no voice override, so this single default is the voice every
    /// client speaks with.  Valid values: alloy, echo, fable, onyx, nova, shimmer.
    /// </summary>
    public string TtsVoice { get; set; } = "onyx";

    /// <summary>
    /// OpenAI TTS model for Phase 3 of the voice mode.  Defaults to "tts-1".
    /// Use "tts-1-hd" for higher quality at 2x cost.
    /// </summary>
    public string TtsModel { get; set; } = "tts-1";

    /// <summary>
    /// OpenAI API key used by the voice mode for Whisper transcription.
    /// Loaded from appsettings.json "Voice.OpenAiKey" first, then falls back to
    /// the OPENAI_API_KEY environment variable. Null/empty disables the voice
    /// mode in the Director UI (the button is hidden / disabled).
    /// Never sent to browsers.
    /// </summary>
    public string? OpenAiKey { get; set; }

    /// <summary>
    /// Path to the user-editable dictation dictionary YAML. If null, resolves
    /// to <c>%LOCALAPPDATA%/cc-director/dictation/dictionary.yaml</c>. Missing
    /// file means no vocabulary bias and no cleanup glossary; the rest of the
    /// dictation pipeline still works.
    /// </summary>
    public string? DictationDictionaryPath { get; set; }

    /// <summary>
    /// OpenAI chat model used by the dictation library's CleanupOrchestrator.
    /// Defaults to <c>gpt-4.1-nano</c> — the smallest/fastest gpt-4.1 tier,
    /// purpose-built for high-throughput follow-instructions tasks like
    /// transcript cleanup. Override to <c>gpt-4o-mini</c> for slightly higher
    /// quality at noticeably higher latency, or to <c>gpt-4o</c> for the
    /// best quality at substantially higher latency and cost.
    /// </summary>
    public string DictationCleanupModel { get; set; } = "gpt-4.1-nano";

    /// <summary>
    /// OpenAI transcription model used by the dictation live preview
    /// (<see cref="Dictation.LivePreviewTranscriber"/>), which re-transcribes
    /// the growing clip while the user is still talking so the dialog shows
    /// the words as they are spoken. Defaults to <c>gpt-4o-mini-transcribe</c>:
    /// the preview re-runs every few seconds, so the cheap/fast tier is the
    /// right default. The FINAL transcript does not use this model.
    /// </summary>
    public string DictationPreviewModel { get; set; } = "gpt-4o-mini-transcribe";

    /// <summary>
    /// Resolve the effective dictation dictionary path. Always returns a
    /// concrete path; callers should treat a missing file as "empty dictionary".
    /// </summary>
    public string ResolveDictationDictionaryPath()
    {
        if (!string.IsNullOrWhiteSpace(DictationDictionaryPath))
            return DictationDictionaryPath;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-director", "dictation", "dictionary.yaml");
    }

    /// <summary>
    /// Resolve the effective OpenAI key: explicit config wins, then environment.
    /// Returns null if neither is set.
    /// </summary>
    public string? ResolveOpenAiKey()
    {
        if (!string.IsNullOrWhiteSpace(OpenAiKey))
            return OpenAiKey.Trim();
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    private static string DefaultNpmCliPath(string binName)
    {
        // Windows npm global install: %APPDATA%\npm\<bin>.cmd
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var path = Path.Combine(appData, "npm", binName + ".cmd");
            FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): resolved from %APPDATA% to {path}");
            return path;
        }
        FileLog.Write($"[AgentOptions] DefaultNpmCliPath({binName}): %APPDATA% unavailable, falling back to bare '{binName}' (relying on PATH)");
        return binName;
    }
}
