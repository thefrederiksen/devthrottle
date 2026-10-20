namespace CcDirector.Core.Dictation;

/// <summary>
/// Thrown when the microphone delivered a full recording's worth of bytes and EVERY
/// ONE OF THEM WAS ZERO.
///
/// WHY THIS EXISTS
/// ---------------
/// <see cref="NoAudioCapturedException"/> covers the case where no bytes arrived at
/// all. It cannot cover this one: a microphone the operating system has refused still
/// opens, still runs at the correct byte rate, and still delivers buffers - they are
/// simply all silence. The byte count alone therefore cannot tell a refused microphone
/// from a working one, and without this gate the recording travels to the Gateway, is
/// transcribed honestly as nothing, and the user is handed an empty transcript with no
/// explanation at all.
///
/// An every-bit-zero recording is what a muted device and a refused permission both look
/// like, and on macOS it is the ordinary signature of an application that has not been
/// granted microphone access, because that platform refuses by substituting silence rather
/// than by failing the open. A live microphone in a quiet room normally still carries a
/// faint noise floor, but this gate does not rest on that: whatever produced it, a clip
/// with no sound in it cannot yield a transcript, so refusing it with a named cause is
/// strictly better than sending it and handing back an empty one. The message therefore
/// names the likely causes rather than asserting which one it is.
///
/// The guidance sentence comes from the capture that reported the silence, because only
/// it knows which platform's settings the user has to go and change.
/// </summary>
public sealed class SilentCaptureException : Exception
{
    /// <summary>The capture device that delivered nothing but silence.</summary>
    public string DeviceDescription { get; }

    public SilentCaptureException(string deviceDescription, string howToFix)
        : base(BuildMessage(deviceDescription, howToFix))
    {
        DeviceDescription = deviceDescription;
    }

    private static string BuildMessage(string device, string howToFix)
        => $"'{device}' recorded no sound at all - every sample was silent, so there is nothing in the "
           + "recording to transcribe. The usual causes are a muted microphone, or Director not being "
           + $"allowed to hear it. {howToFix}";
}
