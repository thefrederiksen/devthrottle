using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core;

/// <summary>
/// The fleet's central API-key store (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
/// A plain JSON file outside git on the Gateway box; the Gateway owns it and reads it at
/// request time. Directors pull keys from here on demand (never persisting them locally),
/// and the Gateway's own features (e.g. recording transcription) read it in-process.
///
/// The file is the single source of truth: every operation re-reads it, so an external edit
/// is honored, and writes are atomic (temp file + replace) so a concurrent reader never sees
/// a half-written file. Stores opaque name -> value pairs, e.g. "OPENAI_API_KEY".
/// </summary>
public sealed class KeyVault
{
    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="path">Override the store path (tests). Production omits it for the
    /// shared default at <c>%LOCALAPPDATA%\cc-director\keyvault.json</c>.</param>
    public KeyVault(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(CcStorage.Root(), "keyvault.json")
            : path;
    }

    /// <summary>The key names present, sorted. Never returns values.</summary>
    public IReadOnlyList<string> ListNames()
    {
        lock (_gate)
            return Read().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The value for <paramref name="name"/>, or null if absent.</summary>
    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
            return Read().TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Set or overwrite a key.</summary>
    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("key name is required", nameof(name));
        lock (_gate)
        {
            var map = Read();
            map[name] = value ?? "";
            Write(map);
            FileLog.Write($"[KeyVault] Set: {name} (length={value?.Length ?? 0})");
        }
    }

    /// <summary>
    /// Set <paramref name="name"/> only if it is not already present with a non-empty value.
    /// Returns true if it was written, false if an existing value was kept. Used to seed a key
    /// ONCE without ever clobbering a value an operator has rotated in - e.g. the one-time
    /// OPENAI_API_KEY environment -> vault bootstrap on a Gateway install (INSTALLATION.md section 4).
    /// </summary>
    public bool SetIfAbsent(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("key name is required", nameof(name));
        lock (_gate)
        {
            var map = Read();
            if (map.TryGetValue(name, out var existing) && !string.IsNullOrWhiteSpace(existing))
                return false;
            map[name] = value ?? "";
            Write(map);
            FileLog.Write($"[KeyVault] SetIfAbsent: {name} seeded (length={value?.Length ?? 0})");
            return true;
        }
    }

    /// <summary>Remove a key. Returns true if it existed.</summary>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate)
        {
            var map = Read();
            if (!map.Remove(name)) return false;
            Write(map);
            FileLog.Write($"[KeyVault] Delete: {name}");
            return true;
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
    }

    private void Write(Dictionary<string, string> map)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });

        // Atomic replace: write a sibling temp file then move it over the target, so a
        // concurrent reader (e.g. the recording transcriber on another KeyVault instance)
        // never observes a partially written file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
