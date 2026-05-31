namespace CcDirector.Core.Dictation;

/// <summary>
/// Thrown by <see cref="DictationPipeline.StopAsync"/> when the microphone
/// produced ZERO bytes of audio for the entire recording.
///
/// WHY THIS EXISTS
/// ---------------
/// When capture yields no audio, the old behaviour was to commit the empty
/// buffer to the transcription provider, which rejected it with an opaque
/// message ("buffer too small. Expected at least 100ms of audio, but buffer
/// only has 0.00ms of audio"). That surfaced the provider's internal complaint
/// to the user and said nothing about the real problem: the selected microphone
/// captured silence. This exception is thrown BEFORE the commit so the failure
/// names the actual device the user must check.
/// </summary>
public sealed class NoAudioCapturedException : Exception
{
    /// <summary>The capture device that produced no audio.</summary>
    public string DeviceDescription { get; }

    public NoAudioCapturedException(string deviceDescription)
        : base(BuildMessage(deviceDescription))
    {
        DeviceDescription = deviceDescription;
    }

    private static string BuildMessage(string device)
        => $"No audio was captured from '{device}'. The microphone produced no sound for the "
           + "entire recording. Check that this is the right microphone, that it is connected and "
           + "not muted, and that CC Director is allowed to use the microphone "
           + "(Windows Settings > Privacy & security > Microphone).";
}
