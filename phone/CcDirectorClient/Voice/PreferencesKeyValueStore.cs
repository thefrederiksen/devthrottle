namespace CcDirectorClient.Voice;

/// <summary>
/// The production <see cref="IKeyValueStore"/>: a thin wrapper over MAUI <c>Preferences</c>
/// (issue #406). Lives only in the app project (it touches the MAUI <c>Preferences</c> type),
/// so it is NOT link-included into the off-device test project - the store logic itself is
/// tested against an in-memory fake through the <see cref="IKeyValueStore"/> seam instead.
/// </summary>
public sealed class PreferencesKeyValueStore : IKeyValueStore
{
    public string? Get(string key)
    {
        var value = Preferences.Get(key, "");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void Set(string key, string value) => Preferences.Set(key, value);

    public void Remove(string key) => Preferences.Remove(key);
}
