namespace CcDirector.Core.Configuration;

/// <summary>
/// HOW transcription (speech-to-text) connects for this machine (issue #497, #541). The user chooses
/// a capability, not a raw provider key:
///
///   - <see cref="Local"/>: local Whisper.net in-process transcription (issue #541). The
///     Whisper model runs on THIS machine with no external call and no API key - it works offline
///     and out of the box. This is the default so a fresh install can transcribe immediately.
///   - <see cref="Byo"/> ("bring your own key"): the user's own OpenAI key. Transcription goes
///     directly to <c>https://api.openai.com/v1</c> with their <c>sk-</c> key. The key stays on
///     this machine and is NEVER sent to DevThrottle.
///   - <see cref="DevThrottle"/>: a DevThrottle-issued <c>dt_</c> key. Transcription is proxied
///     through DevThrottle's managed Groq Whisper endpoint at <c>https://devthrottle.com/api/v1</c>,
///     metered on the user's subscription. The <c>dt_</c> key is DevThrottle's own credential,
///     not a provider key. DevThrottle-hosted transcription is "coming soon" (issue #541) and not
///     yet selectable from the Cockpit settings UI.
///
/// The two keys are stored separately so switching modes never loses the other key.
/// </summary>
public enum TranscriptionMode
{
    /// <summary>Bring your own OpenAI key; call api.openai.com directly. Opt-in.</summary>
    Byo = 0,

    /// <summary>Use a DevThrottle key; call devthrottle.com's managed proxy.</summary>
    DevThrottle = 1,

    /// <summary>Local Whisper.net in-process transcription; no key, works offline. The default (issue #541).</summary>
    Local = 2,
}

/// <summary>Parse/format helpers for <see cref="TranscriptionMode"/>. Pure - unit-tested.</summary>
public static class TranscriptionModeExtensions
{
    /// <summary>The config.json string form: "local", "byo", or "devthrottle".</summary>
    public static string ToConfigString(this TranscriptionMode mode) => mode switch
    {
        TranscriptionMode.Local => "local",
        TranscriptionMode.Byo => "byo",
        TranscriptionMode.DevThrottle => "devthrottle",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown transcription mode"),
    };

    /// <summary>
    /// Parse a config.json value. Null/empty/whitespace yields the default (<see cref="TranscriptionMode.Local"/>,
    /// issue #541 - local works offline with no key, so it is the zero-configuration default).
    /// Any other unrecognized value THROWS with the allowed set named (no-fallback rule: a typo
    /// must not silently pick a mode).
    /// </summary>
    public static TranscriptionMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TranscriptionMode.Local;
        return value.Trim().ToLowerInvariant() switch
        {
            "local" => TranscriptionMode.Local,
            "byo" => TranscriptionMode.Byo,
            "devthrottle" => TranscriptionMode.DevThrottle,
            _ => throw new ArgumentException(
                $"transcription_mode '{value}' is not valid - it must be \"local\", \"byo\", or \"devthrottle\".", nameof(value)),
        };
    }

    /// <summary>True when <paramref name="value"/> is a recognized mode (for input validation).</summary>
    public static bool IsValid(string? value)
    {
        try { Parse(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
