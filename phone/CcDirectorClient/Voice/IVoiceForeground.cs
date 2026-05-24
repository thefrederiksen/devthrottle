namespace CcDirectorClient.Voice;

/// <summary>
/// Starts and stops the platform foreground service that keeps the voice
/// round-trip and TTS alive while the app is backgrounded or the screen is off.
/// Abstracted so the Talk screen can control it without referencing Android
/// types directly.
/// </summary>
public interface IVoiceForeground
{
    /// <summary>True while the foreground service is running.</summary>
    bool IsActive { get; }

    /// <summary>Start the foreground service. Idempotent.</summary>
    void Start();

    /// <summary>Stop the foreground service. Idempotent.</summary>
    void Stop();
}
