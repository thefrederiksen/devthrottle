namespace CcDirectorClient.Voice;

/// <summary>
/// No-op <see cref="IAudioCue"/> for non-Android targets and unit tests. All methods
/// are intentionally empty: no audio is played, no exceptions are thrown.
/// </summary>
public sealed class NullAudioCue : IAudioCue
{
    public void PlayStart()      { }
    public void PlayStop()       { }
    public void PlayError()      { }
    public void PlaySent()       { }
    public void PlayReply()      { }
    public void StartThinking()  { }
    public void StopThinking()   { }
}
