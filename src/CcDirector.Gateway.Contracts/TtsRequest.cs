namespace CcDirector.Gateway.Contracts;

/// <summary>
/// POST /tts request.  Used by the voice mode (Phase 3) to convert the
/// Supervisor's spoken_text into natural-sounding audio via OpenAI TTS.
/// </summary>
public sealed class TtsRequest
{
    /// <summary>The text to speak.  Required.  Capped to ~1000 chars server-side.</summary>
    public string Text { get; set; } = "";

    /// <summary>OpenAI voice name. Optional. Defaults to the Director's
    /// configured <c>Voice.TtsVoice</c> (typically "alloy").</summary>
    public string? Voice { get; set; }

    /// <summary>OpenAI TTS model.  Optional.  Defaults to <c>Voice.TtsModel</c>
    /// ("tts-1" or "tts-1-hd").</summary>
    public string? Model { get; set; }
}

/// <summary>
/// JSON error shape returned by POST /tts when the request fails.  On success
/// the endpoint returns raw audio/mpeg bytes, not JSON.
/// </summary>
public sealed class TtsErrorResponse
{
    /// <summary>"no_key" | "empty_text" | "openai_failed" | "internal_error".</summary>
    public string Status { get; set; } = "";

    /// <summary>Free-text error detail.</summary>
    public string Error { get; set; } = "";
}
