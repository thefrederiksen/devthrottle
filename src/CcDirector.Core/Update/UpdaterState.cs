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

    /// <summary>Load persisted state from <see cref="FilePath"/>; see <see cref="LoadFrom(string)"/>.</summary>
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
    ///
    /// A load takes the file's lock for the read (issue #3666): Windows refuses to replace a file while
    /// anyone has it open, so an unlocked reader made a concurrent save fail, and a reader racing the
    /// replace found no file at all. The lock is held for milliseconds.
    ///
    /// WHAT A LOAD RETURNS (issue #3666, review of #3680). An empty state means the file holds nothing:
    /// it does not exist yet, it is zero bytes long, or it is not JSON - in all three there is nothing
    /// to recover, and the last two are logged as failures. A file that EXISTS but cannot be read - the
    /// lock not had in time, access denied - THROWS. It used to be returned as an empty state too, and
    /// an empty state is a statement: "nothing staged, nothing pinned, no health check pending". Startup
    /// cleanup acting on that deleted the rollback backup while the real file said a new build had not
    /// yet proved itself. Every caller catches, logs a failure and skips what it was about to do.
    ///
    /// To CHANGE the state, use <see cref="Update"/> or <see cref="UpdateAt(string, Action{UpdaterState})"/>,
    /// which re-reads the file under the lock and applies only the caller's own change.
    /// </summary>
    public static UpdaterState LoadFrom(string path) => LoadFrom(path, FileLock.DefaultWait);

    internal static UpdaterState LoadFrom(string path, TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileLog.Write($"[UpdaterState] Load: {path}");
        if (!File.Exists(path))
            return new UpdaterState();

        using var _ = AcquireLock(path, wait);

        // Zero bytes is a save cut off between truncating the file and writing it (issue #3666); the
        // serializer never writes an empty string. Nothing else rewrites the file on a machine whose
        // Director does not check, so replace it now or every hourly load fails on it forever. The file
        // holds nothing either way, so a repair that fails is logged and the empty state is still true.
        if (File.Exists(path) && new FileInfo(path).Length == 0)
        {
            FileLog.Write($"[UpdaterState] Load: {path} is empty, an interrupted save; replacing it with an empty state");
            var replacement = new UpdaterState();
            try
            {
                replacement.WriteFile(path);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[UpdaterState] Load: replacing the empty file {path} FAILED: {ex.Message}");
            }
            return replacement;
        }

        try
        {
            return ReadFile(path);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[UpdaterState] Load FAILED, {path} is not valid JSON (using empty state): {ex.Message}");
            return new UpdaterState();
        }
    }

    /// <summary>Change the state in <see cref="FilePath"/>; see <see cref="UpdateAt(string, Action{UpdaterState})"/>.</summary>
    public static UpdaterState Update(Action<UpdaterState> change) => UpdateAt(FilePath, change);

    /// <summary>
    /// Change the state in an explicit file as ONE step: take the file's lock, read the file as it is now,
    /// apply <paramref name="change"/>, write it, release (issue #3666). Returns the state as written.
    ///
    /// The Director, the launcher and the update helper all edit this file. Each used to load it, change
    /// a field or two and save the whole thing back, so a Director that loaded it, spent a minute
    /// downloading and then saved wrote its stale copy over whatever the launcher had recorded in
    /// between - bringing back a staged record the launcher had cleared and erasing its decision. Under
    /// the lock, every change lands on the latest state and touches only what the caller sets.
    ///
    /// The lock is held only for the read and the write, never across a download or any other wait.
    /// THROWS when the lock cannot be had (<see cref="TimeoutException"/>) or the file cannot be read or
    /// written; it never changes the file on a guess, and never starts from an empty state because a
    /// read failed. A file that reads but is not JSON is treated as
    /// empty, as a load has always treated it, and that is logged as a failure.
    /// </summary>
    public static UpdaterState UpdateAt(string path, Action<UpdaterState> change) => UpdateAt(path, change, DefaultLockWait);

    /// <summary>How long a read or a change waits for the state file's lock when the caller names no wait.</summary>
    public static TimeSpan DefaultLockWait => FileLock.DefaultWait;

    /// <summary>
    /// <see cref="UpdateAt(string, Action{UpdaterState})"/> with the lock wait chosen by the caller instead of
    /// <see cref="DefaultLockWait"/>. Throws <see cref="TimeoutException"/> when the lock is not had within it.
    /// </summary>
    public static UpdaterState UpdateAt(string path, Action<UpdaterState> change, TimeSpan wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(change);
        using var _ = AcquireLock(path, wait);

        UpdaterState state;
        try
        {
            state = ReadFile(path);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[UpdaterState] Update: reading {path} FAILED, it is not valid JSON ({ex.Message}); replacing it with an empty state");
            state = new UpdaterState();
        }

        change(state);
        state.WriteFile(path);
        return state;
    }

    /// <summary>Replace the whole default file with this state; see <see cref="SaveTo"/>.</summary>
    internal void Save() => SaveTo(FilePath);

    /// <summary>
    /// Replace the WHOLE file with this state, under the file's lock. For writing a state from scratch -
    /// tests seeding one. Product code changes the state with <see cref="UpdateAt(string, Action{UpdaterState})"/>,
    /// because saving a copy loaded earlier overwrites what other processes wrote since.
    /// </summary>
    internal void SaveTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var _ = AcquireLock(path, FileLock.DefaultWait);
        WriteFile(path);
    }

    /// <summary>Read the file. Callers hold the file's lock. A file that does not exist yet is an empty state.</summary>
    private static UpdaterState ReadFile(string path)
    {
        if (!File.Exists(path))
            return new UpdaterState();
        var json = File.ReadAllText(path);
        if (json.Length == 0)
            return new UpdaterState();
        return JsonSerializer.Deserialize<UpdaterState>(json, JsonOptions) ?? new UpdaterState();
    }

    /// <summary>
    /// Write beside the file and move it over, so an interrupted save leaves the old file whole instead of
    /// a truncated, zero-byte one (issue #3666). Callers hold the file's lock.
    /// </summary>
    private void WriteFile(string path)
    {
        FileLog.Write($"[UpdaterState] Save: {path}, stagedVersion={StagedVersion}, dismissedVersion={DismissedVersion}");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
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

    /// <summary>Take the lock for one updater state file, waiting at most <paramref name="wait"/>.</summary>
    internal static IDisposable AcquireLock(string path, TimeSpan wait) => FileLock.Acquire(path, wait);

    /// <summary>
    /// The lock's name for one file: the same for every spelling of the path, different for every other
    /// file. <c>Global\</c> on EVERY platform: on Windows a Director started by the Task Scheduler runs in a
    /// different logon session from the launcher, and on Linux and macOS an unprefixed named mutex is
    /// per session too - the launcher runs under launchd and starts the Director through
    /// <c>/usr/bin/open</c>, so without the prefix the two would hold two different locks.
    /// </summary>
    internal static string LockNameFor(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            full = full.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        return @"Global\devthrottle-updater-state-" + hash;
    }

    /// <summary>
    /// One lock per updater state file, shared by every process on the machine (issue #3666): a named
    /// operating-system mutex, the same pattern as <see cref="Skills.SharedSkillFolderLock"/>.
    ///
    /// A read and a write take milliseconds, so a wait past <see cref="DefaultWait"/> means something is
    /// wrong and the caller is told by an exception - nothing touches the file unlocked. A lock left by
    /// a process that died holding it is taken over and said so in the log: the file itself is always
    /// whole, because a save only ever replaces it with a complete one.
    /// </summary>
    private sealed class FileLock : IDisposable
    {
        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);
        private readonly Mutex _mutex;

        private FileLock(Mutex mutex) => _mutex = mutex;

        public static FileLock Acquire(string path, TimeSpan wait)
        {
            var name = LockNameFor(path);
            var mutex = new Mutex(initiallyOwned: false, name);
            bool got;
            try
            {
                got = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                // not-an-error: evidence of an earlier crash, which that crash's own report covers; taking the lock over is the recovery
                FileLog.Write($"[UpdaterState] {name} was left held by a process that ended mid-save; taken over");
                got = true;
            }
            if (!got)
            {
                mutex.Dispose();
                throw new TimeoutException($"Updater state file {path} stayed locked by another process for {wait.TotalSeconds:0.#}s");
            }
            return new FileLock(mutex);
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
