namespace CcDirectorClient.Voice;

/// <summary>
/// A tiny string key/value persistence seam. Extracted so the in-flight voice-turn
/// store (issue #406) can be unit tested off-device against an in-memory fake, while
/// the app wires it to MAUI <c>Preferences</c> via <see cref="PreferencesKeyValueStore"/>.
/// The runner/store layer depends only on this interface, never on MAUI types, so the
/// persistence round-trip is exercised without the Android/MAUI workload.
/// </summary>
public interface IKeyValueStore
{
    /// <summary>The stored value for <paramref name="key"/>, or null when nothing is stored.</summary>
    string? Get(string key);

    /// <summary>Store <paramref name="value"/> under <paramref name="key"/>, replacing any prior value.</summary>
    void Set(string key, string value);

    /// <summary>Remove any value stored under <paramref name="key"/> (no-op when absent).</summary>
    void Remove(string key);
}
