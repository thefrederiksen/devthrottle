using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// Per-install state for the auto-updater. Persisted as director-local machine
/// state (NOT in config.json, which is meant to be portable/syncable) at
/// <c>config/director/updater-state.json</c>.
///
/// Tracks the last check time, any update already downloaded and waiting to be
/// applied (staged), and a version the user explicitly dismissed via "Later" so
/// the banner doesn't nag on every launch.
/// </summary>
public sealed class UpdaterState
{
    /// <summary>UTC timestamp of the last successful "check for updates" call.</summary>
    [JsonPropertyName("lastCheckedAt")]
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Version (e.g. "0.3.3") currently downloaded and waiting to be applied, if any.</summary>
    [JsonPropertyName("stagedVersion")]
    public string? StagedVersion { get; set; }

    /// <summary>
    /// Absolute path to the staged executable that performs the swap. On Windows
    /// this is the downloaded single-file exe; on macOS it is the binary inside
    /// the extracted .app bundle.
    /// </summary>
    [JsonPropertyName("stagedExecutable")]
    public string? StagedExecutable { get; set; }

    /// <summary>
    /// Absolute path the staged build should overwrite. On Windows the installed
    /// cc-director.exe; on macOS the installed "CC Director.app" bundle directory.
    /// </summary>
    [JsonPropertyName("installTarget")]
    public string? InstallTarget { get; set; }

    /// <summary>Version the user chose "Later" on; suppresses the banner for that exact version.</summary>
    [JsonPropertyName("dismissedVersion")]
    public string? DismissedVersion { get; set; }

    /// <summary>
    /// How many times startup has tried (and failed) to apply <see cref="StagedVersion"/>.
    /// Bounds the apply so a staged update that never completes the swap cannot make the
    /// app relaunch-and-exit forever (issue #242). Reset whenever the staged state is
    /// cleared (success or give-up) or a different version stages.
    /// </summary>
    [JsonPropertyName("applyAttempts")]
    public int ApplyAttempts { get; set; }

    /// <summary>The version <see cref="ApplyAttempts"/> is counting for, so the counter resets when a new version stages.</summary>
    [JsonPropertyName("applyAttemptVersion")]
    public string? ApplyAttemptVersion { get; set; }

    /// <summary>
    /// Version of a freshly-swapped build that must prove it can come up healthy before the
    /// update is trusted (issue #242). Set by the relauncher after it installs a new build;
    /// cleared by that new build once it reaches the main window. If a later startup still
    /// sees this set, the prior new-build launch never became healthy, so we roll back to the
    /// <c>.old</c> backup and pin the bad version.
    /// </summary>
    [JsonPropertyName("pendingHealthCheckVersion")]
    public string? PendingHealthCheckVersion { get; set; }

    /// <summary>
    /// A version that failed its post-update health self-check and was rolled back (issue #242).
    /// Pinned so the same bad version is not staged or applied again. Cleared only when a
    /// strictly newer version is offered.
    /// </summary>
    [JsonPropertyName("pinnedBadVersion")]
    public string? PinnedBadVersion { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Absolute path to the state file: config/director/updater-state.json.</summary>
    public static string FilePath =>
        Path.Combine(CcStorage.ToolConfig("director"), "updater-state.json");

    /// <summary>
    /// Load persisted state. Returns an empty state when the file is missing or
    /// unreadable -- a corrupt state file must never block startup or updates.
    /// </summary>
    public static UpdaterState Load()
    {
        FileLog.Write($"[UpdaterState] Load: {FilePath}");
        try
        {
            if (!File.Exists(FilePath))
                return new UpdaterState();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UpdaterState>(json, JsonOptions) ?? new UpdaterState();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdaterState] Load FAILED (using empty state): {ex.Message}");
            return new UpdaterState();
        }
    }

    /// <summary>Persist this state to disk, creating the directory if needed.</summary>
    public void Save()
    {
        FileLog.Write($"[UpdaterState] Save: stagedVersion={StagedVersion}, dismissedVersion={DismissedVersion}");
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}
