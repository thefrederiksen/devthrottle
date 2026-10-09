using System.Security.Cryptography;
using System.Text;
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
    /// cc-director.exe; on macOS the installed "Director.app" bundle directory.
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

    // ---- What the last check and the last install pass actually concluded (issue #1030) -------
    //
    // Auto-update has always worked and has always been silent, and silence is indistinguishable
    // from broken: up to date, never checked, downloading, downloaded-and-waiting, and a check that
    // failed all rendered as an unchanged version number, so the owner concluded the feature was
    // broken and had no way to conclude anything else.
    //
    // These fields are the record that makes the difference sayable. They are deliberately kept in
    // THIS file rather than in a new one, because two processes already read and write it and it is
    // already the shared record: the Director writes what its check found, the launcher writes what
    // its install pass decided (issue #1033), and whoever renders the status reads both from one
    // place. A second file would have needed a second discovery path and could disagree with this one.

    /// <summary>
    /// What the last completed check concluded, as an <see cref="UpdatePhase"/> name: UpToDate,
    /// Staged, ReleaseNotReady, or Failed. Stored as text, not as the enum, so an older build reading
    /// a newer state file gets an unrecognised word it can show rather than a deserialization failure.
    /// </summary>
    [JsonPropertyName("lastCheckOutcome")]
    public string? LastCheckOutcome { get; set; }

    /// <summary>Why the last check failed, when it did. Null on success.</summary>
    [JsonPropertyName("lastCheckError")]
    public string? LastCheckError { get; set; }

    /// <summary>The newest version the last check saw published, whether or not it could be staged.</summary>
    [JsonPropertyName("lastCheckLatestVersion")]
    public string? LastCheckLatestVersion { get; set; }

    /// <summary>
    /// What the launcher's last install pass decided, as a <c>DirectorUpdateDecision</c> name -
    /// HeldBecauseBusy, RolledBack, Applied, and the rest. Written by the launcher, read by whoever
    /// shows the status. Text for the same reason as <see cref="LastCheckOutcome"/>.
    ///
    /// Two of these are worth as much as the version number and neither could be learned any other
    /// way: HeldBecauseBusy is "waiting for your sessions to finish", which looked exactly like a
    /// stall, and RolledBack is "the new build did not come up, so the old one is back", which a
    /// person had no way to find out at all.
    /// </summary>
    [JsonPropertyName("lastApplyDecision")]
    public string? LastApplyDecision { get; set; }

    /// <summary>When the launcher recorded <see cref="LastApplyDecision"/>.</summary>
    [JsonPropertyName("lastApplyDecisionAt")]
    public DateTimeOffset? LastApplyDecisionAt { get; set; }

    /// <summary>The version that decision was about, so a stale decision is not read as being about a new download.</summary>
    [JsonPropertyName("lastApplyVersion")]
    public string? LastApplyVersion { get; set; }

    /// <summary>One plain sentence of detail from the launcher's pass - the session count it held for, or why it failed.</summary>
    [JsonPropertyName("lastApplyDetail")]
    public string? LastApplyDetail { get; set; }

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
    public static UpdaterState Load() => LoadFrom(FilePath);

    /// <summary>
    /// Load persisted state from an explicit file.
    ///
    /// This exists because the launcher now owns applying the Director's update (issue #1033), and the
    /// launcher is NOT the Director: <see cref="FilePath"/> resolves against the calling process's own
    /// storage home, and the installed Director keeps its whole home one level in, under its instance
    /// folder. A launcher that asked for "the" updater state would read an empty file at the storage
    /// root and conclude, every single time, that no update was staged - the feature would look wired
    /// and never once fire. The launcher finds the Director's file and names it here.
    /// </summary>
    public static UpdaterState LoadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileLog.Write($"[UpdaterState] Load: {path}");
        try
        {
            using var _ = FileLock.Acquire(path);
            if (!File.Exists(path))
                return new UpdaterState();

            // Zero bytes is a save cut off between truncating the file and writing it (issue #3666); the
            // serializer never writes an empty string. Nothing else rewrites the file on a machine whose
            // Director does not check, so replace it now or every hourly load fails on it forever.
            if (new FileInfo(path).Length == 0)
            {
                FileLog.Write($"[UpdaterState] Load: {path} is empty, an interrupted save; replacing it with an empty state");
                var replacement = new UpdaterState();
                replacement.SaveTo(path);
                return replacement;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdaterState>(json, JsonOptions) ?? new UpdaterState();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdaterState] Load FAILED (using empty state): {ex.Message}");
            return new UpdaterState();
        }
    }

    /// <summary>Persist this state to disk, creating the directory if needed.</summary>
    public void Save() => SaveTo(FilePath);

    /// <summary>
    /// Persist this state to an explicit file, creating the directory if needed. The launcher writes
    /// the Director's own state file this way once it has applied or rejected a staged build; see
    /// <see cref="LoadFrom"/> for why the path cannot be assumed.
    /// </summary>
    public void SaveTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileLog.Write($"[UpdaterState] Save: {path}, stagedVersion={StagedVersion}, dismissedVersion={DismissedVersion}");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        // Write beside the file and move it over, so an interrupted save leaves the old file whole
        // instead of a truncated, zero-byte one (issue #3666). The Director, the launcher and the update
        // helper all read and write this file, so every load and save takes the file's lock: without it
        // two saves at once collided on the temporary file or on the replace, and one threw.
        using var _ = FileLock.Acquire(path);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// One lock per updater state file, shared by every process on the machine (issue #3666). A named
    /// operating-system mutex whose name is derived from the file's full path, in the <c>Global\</c>
    /// namespace on Windows because the launcher and a Director started by the Task Scheduler can run in
    /// different logon sessions. The same pattern as <see cref="Skills.SharedSkillFolderLock"/>.
    ///
    /// A load or save takes milliseconds, so a wait past <see cref="Wait"/> means something is wrong and
    /// it throws rather than touching the file unlocked. A lock left by a process that died holding it is
    /// taken over and said so in the log: the file itself is always whole, because a save only ever
    /// replaces it with a complete one.
    /// </summary>
    private sealed class FileLock : IDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
        private readonly Mutex _mutex;

        private FileLock(Mutex mutex) => _mutex = mutex;

        public static FileLock Acquire(string path)
        {
            var name = NameFor(path);
            var mutex = new Mutex(initiallyOwned: false, name);
            bool got;
            try
            {
                got = mutex.WaitOne(Wait);
            }
            catch (AbandonedMutexException)
            {
                FileLog.Write($"[UpdaterState] {name} was left held by a process that ended mid-save; taken over");
                got = true;
            }
            if (!got)
            {
                mutex.Dispose();
                throw new TimeoutException($"Updater state file {path} stayed locked by another process for {Wait.TotalSeconds:0}s");
            }
            return new FileLock(mutex);
        }

        private static string NameFor(string path)
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
                full = full.ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
            return (OperatingSystem.IsWindows() ? @"Global\" : "") + "devthrottle-updater-state-" + hash;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
