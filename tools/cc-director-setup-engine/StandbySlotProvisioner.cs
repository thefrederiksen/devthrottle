using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Instances;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>Whether this build may be copied, judged from every Director's post-update health check.</summary>
public enum HealthGate
{
    /// <summary>No Director on this machine owes a health check for this build.</summary>
    Clear,
    /// <summary>Some Director on this machine still owes a health check for this exact build.</summary>
    PendingForThisBuild,
    /// <summary>Health state that may exist could not be read, so a pending check cannot be ruled out.</summary>
    Unreadable,
}

/// <summary>What one standby slot pass decided.</summary>
public enum StandbySlotDecision
{
    /// <summary>Not Windows. The macOS application bundle needs its own slot layout.</summary>
    NotWindows,
    /// <summary>This Director runs from the standby slot. Only the primary ever creates the standby.</summary>
    NotPrimary,
    /// <summary>The standby executable already exists. It is never touched.</summary>
    AlreadyPresent,
    /// <summary>The standby slot was missing, and was created from this build.</summary>
    Created,
    /// <summary>Another update held the machine-wide binary swap lock past the wait.</summary>
    HeldBecauseAnotherSwapIsRunning,
    /// <summary>
    /// The standby executable is missing but an update's staging or backup file sits beside it: an update
    /// is under way or was interrupted there, and its own recovery owns that slot.
    /// </summary>
    HeldBecauseAnUpdateOwnsTheSlot,
    /// <summary>Some Director on this machine still owes a post-update health check for this exact build.</summary>
    HeldBecauseThisBuildIsUnproven,
    /// <summary>Health state could not be read, so this build cannot be shown to be proven.</summary>
    HeldBecauseHealthStateUnreadable,
}

/// <summary>The decision a pass reached, and a sentence for the log.</summary>
public sealed record StandbySlotOutcome(StandbySlotDecision Decision, string Detail)
{
    /// <summary>True when there is nothing left to do.</summary>
    public bool IsSettled => Decision is StandbySlotDecision.AlreadyPresent
        or StandbySlotDecision.Created
        or StandbySlotDecision.NotWindows
        or StandbySlotDecision.NotPrimary;
}

/// <summary>
/// CREATES THE STANDBY DIRECTOR SLOT WHEN IT IS MISSING (issue #2945).
///
/// Every Windows install carries two copies of the Director executable:
///     app\cc-director.exe            the primary, where it has always been
///     app\standby\cc-director.exe    the standby
/// so a second Director can run from its own executable. Sessions can then move to it while the first
/// updates, and an update no longer has to take anybody's work down.
///
/// THE PRIMARY NEVER MOVES. A Director's identity is keyed on its executable path, so moving it would
/// give every existing Director a new identity; Start menu entries, autostart and the launcher also
/// point at app\.
///
/// THE DIRECTOR DOES THIS, NOT THE INSTALLER, and that is what reaches existing installs. An existing
/// machine updates through its OLD launcher, which knows nothing about slots - but the Director that
/// starts after the update is the NEW build, so it creates the slot with no reinstall. A new install
/// reaches the same code the first time its Director starts. One mechanism covers both.
///
/// IT ONLY EVER CREATES THE STANDBY, AND ONLY FROM THE PRIMARY. An existing standby is never touched,
/// whatever its version: each Director keeps itself up to date through the ordinary update path. The
/// primary is never written: the installer, the launcher and the updater own it. So the one file this
/// ever writes is a standby executable that does not exist - and a Director can only update a file it
/// was started from, which does exist.
///
/// THE ONE WAY "MISSING" CAN LIE: an update interrupted halfway, with the executable gone and its backup
/// waiting to be put back. Creating the slot then would defeat that recovery. So a missing standby with
/// an update's staging or backup file beside it is left to the update that owns it.
///
/// THE RULES, each for a reason:
/// - Only a PROVEN build is copied: the standby starts life as a copy of it. Health checks are per named
///   instance while the executable is shared, so EVERY instance's state is read, and the pass holds while
///   any of them still owes a check for THIS version. A marker for another version is about another build
///   and does not hold - a machine can carry a stale marker for months. Anything that cannot be read holds.
///   Existence is never ASKED: File.Exists and Directory.Exists answer false when access is denied, which
///   would silently skip the very state that must hold. Every read is attempted, and only "not found"
///   counts as absent.
/// - Wait, bounded, for the machine-wide binary swap lock, so two Directors starting together never
///   create the slot at once. On the first start after an update the launcher may still hold that lock
///   while it waits for this Director to answer, so the wait is long. A held pass retries.
/// - Every file is written under a staging name no other writer uses, then moved into place, so no file
///   is ever half-written in its final name. appsettings.json goes first and the executable last: the
///   executable's presence means creation finished.
///
/// It never STARTS a Director. It only makes sure the slot exists.
/// </summary>
public sealed class StandbySlotProvisioner
{
    /// <summary>The standby slot's folder under the primary's folder. Permanent: identity follows the path.</summary>
    public const string StandbyFolderName = "standby";

    /// <summary>
    /// The suffix of this pass's own staging files. Deliberately not ".new": the update swapper stages
    /// under that name, and two writers must never share a staging file.
    /// </summary>
    internal const string StagingSuffix = ".standby-create";

    private const string AppSettingsFileName = "appsettings.json";

    /// <summary>The JSON name UpdaterState gives its health marker; a test ties this to the real serializer.</summary>
    internal const string PendingHealthCheckProperty = "pendingHealthCheckVersion";

    private readonly string _selfExecutable;
    private readonly string _sharedRoot;
    private readonly Func<string, Version?> _readVersion;
    private readonly bool _isWindows;

    /// <param name="selfExecutable">The executable this Director is running from.</param>
    /// <param name="sharedRoot">The machine-wide cc-director root, above every named instance's home.</param>
    /// <param name="readVersion">Reads an executable's version, or null when it has none readable.</param>
    /// <param name="isWindows">Whether this is Windows, the only platform this lays out.</param>
    public StandbySlotProvisioner(string selfExecutable, string sharedRoot, Func<string, Version?> readVersion, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        _selfExecutable = selfExecutable;
        _sharedRoot = sharedRoot;
        _readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
        _isWindows = isWindows;
    }

    /// <summary>
    /// The machine-wide lock this pass takes. <see cref="BinarySwapLock.Name"/> in production; a test
    /// overrides it so it can hold a lock of its own without contending with every other swap on the
    /// machine. Internal for the same reason the update owners keep theirs internal.
    /// </summary>
    internal string SwapLockName { get; init; } = BinarySwapLock.Name;

    /// <summary>
    /// How long to wait for the swap lock. Generous, because the launcher holds it across its whole
    /// witness of a new Director. The pass runs in the background, so waiting never delays startup.
    /// Internal so a test need not wait minutes.
    /// </summary>
    internal TimeSpan LockPatience { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a held pass waits before trying again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    /// <summary>The provisioner for the Director running in this process, wired to the real machine.</summary>
    public static StandbySlotProvisioner ForThisProcess()
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate this Director's executable.");
        return new StandbySlotProvisioner(self, InstanceContext.SharedRoot, ReadFileVersion, OperatingSystem.IsWindows());
    }

    /// <summary>True when <paramref name="executable"/> runs from a standby slot folder.</summary>
    public static bool IsInStandbySlot(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var directory = Path.GetDirectoryName(executable)
            ?? throw new ArgumentException($"Executable has no directory: {executable}", nameof(executable));
        return string.Equals(Path.GetFileName(directory), StandbyFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The standby executable for a primary executable.</summary>
    public static string StandbySlotFor(string primaryExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryExecutable);
        var directory = Path.GetDirectoryName(primaryExecutable)
            ?? throw new ArgumentException($"Executable has no directory: {primaryExecutable}", nameof(primaryExecutable));
        return Path.Combine(directory, StandbyFolderName, Path.GetFileName(primaryExecutable));
    }

    /// <summary>The files an update leaves beside an executable while it is under way or interrupted.</summary>
    public static IReadOnlyList<string> UpdateLeftoversFor(string executable) =>
    [
        DirectorBuildSwapper.StagingPathFor(executable),
        DirectorBuildSwapper.BackupPathFor(executable),
        DirectorBuildSwapper.BackupPathFor(executable, DirectorBuildSwapper.LauncherBackupSuffix),
    ];

    /// <summary>What a pass should do, given what is true of the machine. Pure, so every branch is tested.</summary>
    public static StandbySlotDecision Decide(bool isWindows, bool isPrimary, bool standbyExists, bool updateLeftoverPresent, HealthGate health)
    {
        if (!isWindows) return StandbySlotDecision.NotWindows;
        if (!isPrimary) return StandbySlotDecision.NotPrimary;
        if (standbyExists) return StandbySlotDecision.AlreadyPresent;
        if (updateLeftoverPresent) return StandbySlotDecision.HeldBecauseAnUpdateOwnsTheSlot;
        if (health == HealthGate.PendingForThisBuild) return StandbySlotDecision.HeldBecauseThisBuildIsUnproven;
        if (health == HealthGate.Unreadable) return StandbySlotDecision.HeldBecauseHealthStateUnreadable;
        return StandbySlotDecision.Created;
    }

    /// <summary>
    /// Read every Director's post-update health state on this machine - the shared root's own file and one
    /// per named instance home - and say whether this build may be copied.
    /// </summary>
    public static HealthGate ReadHealthGate(string sharedRoot, Version selfVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(selfVersion);

        var files = new List<string> { StateFileUnder(sharedRoot) };
        var instancesRoot = Path.Combine(sharedRoot, "instances");
        try
        {
            files.AddRange(Directory.GetDirectories(instancesRoot).Select(StateFileUnder));
        }
        catch (DirectoryNotFoundException)
        {
            // No named instances at all: only the shared root's own file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[StandbySlotProvisioner] named instances could not be listed: {instancesRoot}: {ex.Message}");
            return HealthGate.Unreadable;
        }

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Write($"[StandbySlotProvisioner] health state could not be read: {file}: {ex.Message}");
                return HealthGate.Unreadable;
            }

            string? pending;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    FileLog.Write($"[StandbySlotProvisioner] health state is not a JSON object: {file}");
                    return HealthGate.Unreadable;
                }

                if (!doc.RootElement.TryGetProperty(PendingHealthCheckProperty, out var value)
                    || value.ValueKind == JsonValueKind.Null)
                {
                    pending = null;
                }
                else if (value.ValueKind == JsonValueKind.String)
                {
                    pending = value.GetString();
                }
                else
                {
                    FileLog.Write($"[StandbySlotProvisioner] health marker is a {value.ValueKind}, not a version: {file}");
                    return HealthGate.Unreadable;
                }
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[StandbySlotProvisioner] health state is not valid JSON: {file}: {ex.Message}");
                return HealthGate.Unreadable;
            }

            if (string.IsNullOrEmpty(pending))
                continue;
            if (!Version.TryParse(pending, out var pendingVersion))
            {
                FileLog.Write($"[StandbySlotProvisioner] health marker '{pending}' is not a version: {file}");
                return HealthGate.Unreadable;
            }
            if (Normalize(pendingVersion) == Normalize(selfVersion))
            {
                FileLog.Write($"[StandbySlotProvisioner] {file} still owes a health check for {pending}");
                return HealthGate.PendingForThisBuild;
            }
        }
        return HealthGate.Clear;
    }

    /// <summary>
    /// One pass: if this is the primary and the standby is missing, wait for the swap lock and create it when
    /// this build is proven. Throws when the file work fails; the caller is the boundary that logs it.
    /// </summary>
    public Task<StandbySlotOutcome> RunOnceAsync()
    {
        FileLog.Write($"[StandbySlotProvisioner] RunOnceAsync: self={_selfExecutable}");

        if (!_isWindows)
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.NotWindows,
                "not Windows; the application bundle needs its own slot layout"));

        if (IsInStandbySlot(_selfExecutable))
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.NotPrimary,
                "running from the standby slot; only the primary creates the standby"));

        var standby = StandbySlotFor(_selfExecutable);

        // Cheap answer for the common case, without taking the machine-wide lock. Only a TRUE from File.Exists
        // is trusted here; a false falls through to the authoritative check under the lock (MayBePresent).
        if (File.Exists(standby))
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.AlreadyPresent, $"{standby} exists; it is never touched"));

        return BinarySwapLock.RunExclusivelyAsync(
            () => Task.FromResult(DecideAndCreate(standby)),
            message => new StandbySlotOutcome(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, message),
            who: "standby slot",
            name: SwapLockName,
            waitFor: LockPatience);
    }

    /// <summary>
    /// Run passes until one settles, waiting <paramref name="retryDelay"/> between passes that did not. A pass
    /// that throws is logged and retried: this is a boundary. Returns the settling outcome, or null when
    /// cancelled first.
    /// </summary>
    public async Task<StandbySlotOutcome?> RunUntilSettledAsync(Func<CancellationToken, Task> retryDelay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retryDelay);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var outcome = await RunOnceAsync();
                FileLog.Write($"[StandbySlotProvisioner] pass: {outcome.Decision} - {outcome.Detail}");
                if (outcome.IsSettled)
                    return outcome;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[StandbySlotProvisioner] pass FAILED, will retry: {ex}");
            }

            try
            {
                await retryDelay(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    private StandbySlotOutcome DecideAndCreate(string standby)
    {
        var selfVersion = _readVersion(_selfExecutable)
            ?? throw new InvalidOperationException($"This Director's own executable has no readable version: {_selfExecutable}");

        // Read under the lock, after any wait: the slot may have been created, or a check armed or cleared.
        var standbyExists = MayBePresent(standby);
        var leftovers = standbyExists ? [] : UpdateLeftoversFor(standby).Where(MayBePresent).ToList();
        var health = standbyExists || leftovers.Count > 0 ? HealthGate.Clear : ReadHealthGate(_sharedRoot, selfVersion);
        var decision = Decide(_isWindows, isPrimary: true, standbyExists, leftovers.Count > 0, health);
        FileLog.Write($"[StandbySlotProvisioner] self={selfVersion}, standbyExists={standbyExists}, "
                      + $"updateLeftovers=[{string.Join(", ", leftovers)}], health={health} -> {decision}");

        switch (decision)
        {
            case StandbySlotDecision.Created:
                CreateSlot(standby);
                return new StandbySlotOutcome(decision, $"{standby} created at {selfVersion}");
            case StandbySlotDecision.AlreadyPresent:
                return new StandbySlotOutcome(decision, $"{standby} exists; it is never touched");
            case StandbySlotDecision.HeldBecauseAnUpdateOwnsTheSlot:
                return new StandbySlotOutcome(decision, $"an update's files sit beside the missing {standby}; its recovery owns the slot");
            case StandbySlotDecision.HeldBecauseThisBuildIsUnproven:
                return new StandbySlotOutcome(decision, $"a Director still owes a health check for {selfVersion}; nothing is copied");
            case StandbySlotDecision.HeldBecauseHealthStateUnreadable:
                return new StandbySlotOutcome(decision, "health state could not be read; nothing is copied");
            default:
                throw new InvalidOperationException($"Decision {decision} cannot be reached after the lock was taken.");
        }
    }

    /// <summary>Settings first, executable last; each staged under this pass's own name and moved into place.</summary>
    private void CreateSlot(string standby)
    {
        var selfDirectory = Path.GetDirectoryName(_selfExecutable)
            ?? throw new InvalidOperationException($"Executable has no directory: {_selfExecutable}");
        var standbyDirectory = Path.GetDirectoryName(standby)
            ?? throw new InvalidOperationException($"Target has no directory: {standby}");
        Directory.CreateDirectory(standbyDirectory);

        var settingsSource = Path.Combine(selfDirectory, AppSettingsFileName);
        var settingsDestination = Path.Combine(standbyDirectory, AppSettingsFileName);
        if (File.Exists(settingsSource) && !File.Exists(settingsDestination))
        {
            PlaceWhole(settingsSource, settingsDestination);
            FileLog.Write($"[StandbySlotProvisioner] carried {settingsSource} to {settingsDestination}");
        }

        PlaceWhole(_selfExecutable, standby);
        FileLog.Write($"[StandbySlotProvisioner] created {standby} from {_selfExecutable}");
    }

    /// <summary>
    /// Copy <paramref name="source"/> to a staging name only this pass uses, then move it to
    /// <paramref name="destination"/> without overwriting, so the destination is absent or whole and never
    /// half-written. The staging file is removed whatever happens.
    /// </summary>
    private static void PlaceWhole(string source, string destination)
    {
        var staging = destination + StagingSuffix;
        try
        {
            File.Copy(source, staging, overwrite: true);
            File.Move(staging, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    /// <summary>
    /// Whether something may be at <paramref name="path"/>. Only "not found" answers false. File.Exists is
    /// not used because it answers false for a directory and when access is denied - either of which is
    /// something present that this pass must not create over.
    /// </summary>
    internal static bool MayBePresent(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[StandbySlotProvisioner] could not inspect {path}, so it counts as present: {ex.Message}");
            return true;
        }
    }

    private static string StateFileUnder(string home) => Path.Combine(home, "config", "director", "updater-state.json");

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static Version? ReadFileVersion(string executable)
    {
        var text = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        return Version.TryParse(text, out var version) ? version : null;
    }
}
