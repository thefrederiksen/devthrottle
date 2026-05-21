using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Loads or creates the Director's stable identity GUID.
///
/// The identity lives in a single file:
///     %LOCALAPPDATA%\cc-director\config\director\director-id.txt
///
/// Why persistent: when the same Director comes back after a restart the Gateway
/// must recognize the same row, not see it as a brand-new Director. A per-process
/// GUID would force the Gateway to forget and re-add the entry on every restart.
/// </summary>
public static class DirectorIdStore
{
    /// <summary>Folder that holds the id file. Same parent as the instances directory.</summary>
    public static string DirectoryPath { get; } =
        Path.Combine(CcStorage.Config(), "director");

    /// <summary>Full path of the id file.</summary>
    public static string FilePath { get; } =
        Path.Combine(DirectoryPath, "director-id.txt");

    /// <summary>
    /// Read the persisted id. If the file is missing, malformed, or empty, mint a
    /// fresh GUID, write it once, and return that. Subsequent calls return the same id.
    /// </summary>
    public static string LoadOrCreate()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            if (File.Exists(FilePath))
            {
                var raw = File.ReadAllText(FilePath).Trim();
                if (Guid.TryParse(raw, out var existing))
                {
                    FileLog.Write($"[DirectorIdStore] LoadOrCreate: reusing id={existing}");
                    return existing.ToString();
                }
                FileLog.Write($"[DirectorIdStore] LoadOrCreate: existing file malformed, regenerating. raw=\"{raw}\"");
            }

            var fresh = Guid.NewGuid().ToString();
            File.WriteAllText(FilePath, fresh);
            FileLog.Write($"[DirectorIdStore] LoadOrCreate: minted id={fresh}, path={FilePath}");
            return fresh;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorIdStore] LoadOrCreate FAILED, falling back to in-memory id: {ex.Message}");
            // Last-resort: in-memory id. Means the Director will look new to the Gateway
            // until the disk problem is resolved, but it can still run.
            return Guid.NewGuid().ToString();
        }
    }
}
