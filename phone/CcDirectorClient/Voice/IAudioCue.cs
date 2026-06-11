namespace CcDirectorClient.Voice;

/// <summary>
/// Platform audio cues for the voice recording flow. Fired fire-and-forget so the UI
/// thread is never blocked: start beeps when the mic opens, stop beeps on a successful
/// submit, and error sounds when a turn fails (transcription / network / send error).
/// Implementations must be non-blocking; heavy platforms (Android) run ToneGenerator
/// synchronously on a background thread via Task.Run.
/// </summary>
public interface IAudioCue
{
    /// <summary>Play the "recording started" cue (mic is now open).</summary>
    void PlayStart();

    /// <summary>Play the "submitted successfully" cue (audio captured and handed off).</summary>
    void PlayStop();

    /// <summary>Play the "error" cue (transcription or send failure).</summary>
    void PlayError();

    /// <summary>Announce that text was sent to the agent (spoken via local TTS).</summary>
    void PlaySent();

    /// <summary>Announce that the agent reply has arrived (spoken via local TTS).</summary>
    void PlayReply();

    /// <summary>Start a soft repeating "thinking" ambient sound while the agent is working.</summary>
    void StartThinking();

    /// <summary>Stop the thinking sound started by <see cref="StartThinking"/>.</summary>
    void StopThinking();
}
