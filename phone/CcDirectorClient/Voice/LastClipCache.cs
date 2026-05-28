namespace CcDirectorClient.Voice;

/// <summary>
/// Holds at most ONE spoken clip - the most recent thing said for the current FIFO session -
/// so a Replay button can re-play it (issue #148). Setting a new clip replaces the previous
/// one (we keep only the last); <see cref="Clear"/> drops it when the session starts working
/// again so stale audio is never replayed. MAUI-free so it is unit-testable off-device.
/// </summary>
public sealed class LastClipCache
{
    private byte[]? _clip;

    /// <summary>True when there is a clip available to replay.</summary>
    public bool HasClip => _clip is { Length: > 0 };

    /// <summary>The cached clip bytes, or null when there is nothing to replay.</summary>
    public byte[]? Clip => HasClip ? _clip : null;

    /// <summary>
    /// Replace the cached clip with the latest one (we keep only the last). An empty or null
    /// clip clears the cache rather than caching nothing playable.
    /// </summary>
    public void Set(byte[]? audio) => _clip = audio is { Length: > 0 } ? audio : null;

    /// <summary>Drop the cached clip (the session started working again; stale audio must not replay).</summary>
    public void Clear() => _clip = null;
}
