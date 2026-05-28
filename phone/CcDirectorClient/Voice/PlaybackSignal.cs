namespace CcDirectorClient.Voice;

/// <summary>
/// Tracks whether spoken audio is currently playing and raises <see cref="Changed"/> only
/// when the value actually flips. A "Stop talking" button shows/hides off this signal, so
/// change-only notification keeps it from flickering on no-op updates (issue #146). Pure and
/// MAUI-free so the speaker's state logic is unit-testable off-device; the Android speaker
/// owns one of these and drives it from real MediaPlayer start/stop.
/// </summary>
public sealed class PlaybackSignal
{
    private readonly object _gate = new();
    private bool _playing;

    /// <summary>Raised with the new playing state, only on an actual transition.</summary>
    public event Action<bool>? Changed;

    /// <summary>True while a clip is playing.</summary>
    public bool IsPlaying
    {
        get { lock (_gate) return _playing; }
    }

    /// <summary>Mark playback started; raises Changed(true) only on a false-&gt;true transition.</summary>
    public void Begin() => Set(true);

    /// <summary>Mark playback stopped; raises Changed(false) only on a true-&gt;false transition.</summary>
    public void End() => Set(false);

    private void Set(bool playing)
    {
        lock (_gate)
        {
            if (_playing == playing) return;
            _playing = playing;
        }
        // Raise outside the lock so a handler can read IsPlaying without deadlocking.
        Changed?.Invoke(playing);
    }
}
