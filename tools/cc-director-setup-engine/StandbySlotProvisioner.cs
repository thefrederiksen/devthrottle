using System.Text.Json;
using System.Diagnostics;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>Whether this build may be copied, judged from every Director's post-update health check.</summary>
public enum HealthGate
{
    /// <summary>No Director on this machine owes a health check for this build.</summary>
    Clear,
    /// <summary>Some Director on this machine still owes a health check for this exact build.</summary>
    PendingForThisBuild,
    /// <summary>A health state file exists but could not be read, so a pending check cannot be ruled out.</summary>
    Unreadable,
}

/// <summary>What one standby slot pass decided.</summary>
public enum StandbySlotDecision
{
    /// <summary>Not Windows. The macOS application bundle needs its own slot layout.</summary>
    NotWindows,
    /// <summary>The other slot's executable already exists. It is never touched.</summary>
    AlreadyPresent,
    /// <summary>The other slot was missing, and was created from this build.</summary>
    Created,
    /// <summary>Another update held the machine-wide binary swap lock past the wait.</summary>
    HeldBecauseAnotherSwapIsRunning,
    /// <summary>Some Director on this machine still owes a post-update health check for this exact build.</summary>
    HeldBecauseThisBuildIsUnproven,
    /// <summary>A health state file could not be read, so this build cannot be shown to be proven.</summary>
    HeldBecauseHealthStateUnreadable,
}

/// <summary>The decision a pass reached, and a sentence for the log.</summary>
public sealed record StandbySlotOutcome(StandbySlotDecision Decision, string Detail)
{
    /// <summary>True when there is nothing left to do: the slot exists, or this platform has no slot.</summary>
    public bool IsSettled => Decision is StandbySlotDecision.AlreadyPresent
        or StandbySlotDecision.Created
        or StandbySlotDecision.NotWindows;
}

/// <summary>
/// CREATES THE OTHER DIRECTOR SLOT WHEN IT IS MISSING (issue #2945).
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
/// IT ONLY EVER CREATES. An existing slot is never replaced, whatever its version. Each Director keeps
/// itself up to date through the ordinary update path, and a Director can only update a file it was
/// started from - a file that exists. So this pass, which writes only where no file exists, can never
/// collide with a Director updating itself. A replace would have raced that update; this cannot.
///
/// THE RULES, each for a reason:
/// - Only a PROVEN build is copied: the standby starts life as a copy of it. Health checks are per named
///   instance while the executable is shared, so EVERY instance's state is read, and the pass holds while
///   any of them still owes a check for THIS version. A marker for another version is about another build
///   and does not hold - a machine can carry a stale marker for months. A state file that exists but
///   cannot be read, or whose marker is not a version, holds too: a pending check cannot be ruled out.
/// - Wait, bounded, for the machine-wide binary swap lock, so two Directors starting together never
///   create the slot at once. On the first start after an update the launcher may still hold that lock
///   while it waits for this Director to answer, so the wait is long.
/// - appsettings.json is written into the slot BEFORE the executable, and the executable is moved into
///   place last. The executable's presence therefore means creation finished; an interrupted creation
///   leaves no executable, and the next pass completes it without overwriting the settings.
///
/// It never STARTS a Director. It only makes sure the slot exists.
/// </summary>
public sealed class StandbySlotProvisioner
{
    /// <summary>The standby slot's folder under the primary's folder. Permanent: identity follows the path.</summary>
    public const string StandbyFolderName = "standby";

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

    /// <summary>
    /// The executable in the OTHER slot: the standby when running from the primary, the primary when
    /// running from the standby.
    /// </summary>
    public static string OtherSlotFor(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var directory = Path.GetDirectoryName(executable)
            ?? throw new ArgumentException($"Executable has no directory: {executable}", nameof(executable));
        var fileName = Path.GetFileName(executable);

        if (string.Equals(Path.GetFileName(directory), StandbyFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var primaryDirectory = Path.GetDirectoryName(directory)
                ?? throw new ArgumentException($"Standby slot has no parent directory: {executable}", nameof(executable));
            return Path.Combine(primaryDirectory, fileName);
        }

        return Path.Combine(directory, StandbyFolderName, fileName);
    }

    /// <summary>What a pass should do, given what is true of the machine. Pure, so every branch is tested.</summary>
    public static StandbySlotDecision Decide(bool isWindows, bool otherExists, HealthGate health)
    {
        if (!isWindows) return StandbySlotDecision.NotWindows;
        if (otherExists) return StandbySlotDecision.AlreadyPresent;
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
        if (Directory.Exists(instancesRoot))
            files.AddRange(Directory.GetDirectories(instancesRoot).Select(StateFileUnder));

        foreach (var file in files)
        {
            if (!File.Exists(file))
                continue;

            string? pending;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                FileLog.Write($"[StandbySlotProvisioner] health state could not be read: {file}: {ex.Message}");
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
    /// One pass: if the other slot is missing, wait for the swap lock and create it when this build is
    /// proven. Throws when the file work fails; the caller is the boundary that logs it.
    /// </summary>
    public Task<StandbySlotOutcome> RunOnceAsync()
    {
        var other = OtherSlotFor(_selfExecutable);
        FileLog.Write($"[StandbySlotProvisioner] RunOnceAsync: self={_selfExecutable}, other={other}");

        if (!_isWindows)
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.NotWindows,
                "not Windows; the application bundle needs its own slot layout"));

        // Cheap answer for the common case, without taking the machine-wide lock.
        if (File.Exists(other))
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.AlreadyPresent, $"{other} exists; it is never touched"));

        return BinarySwapLock.RunExclusivelyAsync(
            () => Task.FromResult(DecideAndCreate(other)),
            message => new StandbySlotOutcome(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, message),
            who: "standby slot",
            name: SwapLockName,
            waitFor: LockPatience);
    }

    /// <summary>
    /// Run passes until one settles - the slot exists, or this platform has none - waiting
    /// <paramref name="retryDelay"/> between held passes. A pass that throws is logged and retried: this is
    /// a boundary. Returns the settling outcome, or null when cancelled first.
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

    private StandbySlotOutcome DecideAndCreate(string other)
    {
        var selfVersion = _readVersion(_selfExecutable)
            ?? throw new InvalidOperationException($"This Director's own executable has no readable version: {_selfExecutable}");

        // Read under the lock, after any wait: the slot may have been created, or a check armed or cleared.
        var otherExists = File.Exists(other);
        var health = otherExists ? HealthGate.Clear : ReadHealthGate(_sharedRoot, selfVersion);
        var decision = Decide(_isWindows, otherExists, health);
        FileLog.Write($"[StandbySlotProvisioner] self={selfVersion}, otherExists={otherExists}, health={health} -> {decision}");

        switch (decision)
        {
            case StandbySlotDecision.Created:
                CreateSlot(other);
                return new StandbySlotOutcome(decision, $"{other} created at {selfVersion}");
            case StandbySlotDecision.AlreadyPresent:
                return new StandbySlotOutcome(decision, $"{other} exists; it is never touched");
            case StandbySlotDecision.HeldBecauseThisBuildIsUnproven:
                return new StandbySlotOutcome(decision, $"a Director still owes a health check for {selfVersion}; nothing is copied");
            case StandbySlotDecision.HeldBecauseHealthStateUnreadable:
                return new StandbySlotOutcome(decision, "a health state file could not be read; nothing is copied");
            default:
                throw new InvalidOperationException($"Decision {decision} cannot be reached after the lock was taken.");
        }
    }

    /// <summary>
    /// Settings first, executable last: the executable's presence is what says creation finished. The
    /// executable is copied beside the target and moved into place, so it is never a half-written file.
    /// </summary>
    private void CreateSlot(string target)
    {
        var selfDirectory = Path.GetDirectoryName(_selfExecutable)
            ?? throw new InvalidOperationException($"Executable has no directory: {_selfExecutable}");
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"Target has no directory: {target}");
        Directory.CreateDirectory(targetDirectory);

        var settingsSource = Path.Combine(selfDirectory, AppSettingsFileName);
        var settingsDestination = Path.Combine(targetDirectory, AppSettingsFileName);
        if (File.Exists(settingsSource) && !File.Exists(settingsDestination))
        {
            File.Copy(settingsSource, settingsDestination);
            FileLog.Write($"[StandbySlotProvisioner] carried {settingsSource} to {settingsDestination}");
        }

        var staging = target + ".new";
        try
        {
            File.Copy(_selfExecutable, staging, overwrite: true);
            File.Move(staging, target, overwrite: false);
            FileLog.Write($"[StandbySlotProvisioner] created {target} from {_selfExecutable}");
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
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
