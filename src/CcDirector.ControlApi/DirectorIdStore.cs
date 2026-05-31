using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Loads or creates this Director's stable identity GUID.
///
/// Identity is keyed by the running executable's path. Each install location gets
/// its own persistent GUID file:
///     %LOCALAPPDATA%\cc-director\config\director\director-id-{slot}.txt
/// where {slot} is the first 8 bytes of SHA256(lowercased exe path) in hex.
///
/// Why per-exe-path: developers and operators routinely run multiple Director builds
/// concurrently (e.g. cc-director1.exe, cc-director4.exe). A single
/// global id file made every running instance overwrite the same instances/{id}.json,
/// so the Gateway only ever saw one Director - last writer wins. Per-exe-path slots
/// give each distinct install its own stable identity.
///
/// Why persistent: when the same Director (same exe) comes back after a restart the
/// Gateway must recognize the same row, not see it as a brand-new Director.
/// </summary>
public static class DirectorIdStore
{
    /// <summary>Folder that holds all id slot files. Same parent as the instances directory.</summary>
    public static string DirectoryPath { get; } =
        Path.Combine(CcStorage.Config(), "director");

    /// <summary>Full path of the id file for the current process's exe.</summary>
    public static string FilePath => FilePathFor(DefaultSlotKey());

    /// <summary>
    /// Path of the id file for a given slot key (typically an executable path).
    /// Exposed so tests and operators can predict the file location.
    /// </summary>
    public static string FilePathFor(string slotKey)
        => Path.Combine(DirectoryPath, $"director-id-{Slot(slotKey)}.txt");

    /// <summary>
    /// Read the persisted id for this process's exe. If the slot file is missing,
    /// malformed, or empty, mint a fresh GUID, write it once, and return that.
    /// Subsequent calls for the same slot return the same id.
    /// </summary>
    public static string LoadOrCreate() => LoadOrCreate(DefaultSlotKey());

    /// <summary>
    /// Read the persisted id for the given slot key. Public so tests can drive the
    /// slot deterministically rather than depending on the test host's exe path.
    /// </summary>
    public static string LoadOrCreate(string slotKey)
    {
        var path = FilePathFor(slotKey);
        Directory.CreateDirectory(DirectoryPath);

        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path).Trim();
            if (Guid.TryParse(raw, out var existing))
            {
                FileLog.Write($"[DirectorIdStore] LoadOrCreate: reusing id={existing} slot={slotKey} path={path}");
                return existing.ToString();
            }
            FileLog.Write($"[DirectorIdStore] LoadOrCreate: file at {path} malformed, regenerating. raw=\"{raw}\"");
        }

        var fresh = Guid.NewGuid().ToString();
        File.WriteAllText(path, fresh);
        FileLog.Write($"[DirectorIdStore] LoadOrCreate: minted id={fresh}, slot={slotKey}, path={path}");
        return fresh;
    }

    /// <summary>The slot key for the currently-running process (its exe path).</summary>
    public static string CurrentProcessSlotKey() => DefaultSlotKey();

    /// <summary>
    /// 8-hex-char slot derived from a slot key. Stable across path-case and slash style.
    /// Public so consumers (e.g. SingleInstanceGuard) can derive matching identifiers.
    /// </summary>
    public static string SlotFor(string slotKey) => Slot(slotKey);

    private static string DefaultSlotKey()
        => Environment.ProcessPath ?? AppContext.BaseDirectory;

    private static string Slot(string slotKey)
    {
        // Windows paths are case-insensitive and may mix '/' and '\'. Normalize so
        // "D:\Foo\bar.exe" and "d:/foo/bar.EXE" map to the same slot.
        var normalized = slotKey.Replace('/', '\\').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
