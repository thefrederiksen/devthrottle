using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>Whether a process is running from the other slot's executable.</summary>
public enum OtherSlotRunning
{
    /// <summary>No process is running from it.</summary>
    No,
    /// <summary>A process is running from it.</summary>
    Yes,
    /// <summary>
    /// A process with the executable's name would not report which image it runs, so it may be the other
    /// slot. A slot that might be running is never touched.
    /// </summary>
    Unknown,
}

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

/// <summary>One process with this executable's name, and the image it reported (null when it would not say).</summary>
public readonly record struct ProcessImage(int ProcessId, string? ImagePath);

/// <summary>What one standby slot pass decided.</summary>
public enum StandbySlotDecision
{
    /// <summary>Not Windows. The macOS application bundle needs its own slot layout.</summary>
    NotWindows,
    /// <summary>Another Director or launcher update held the machine-wide binary swap lock past the wait.</summary>
    HeldBecauseAnotherSwapIsRunning,
    /// <summary>Some Director on this machine still owes a post-update health check for this exact build.</summary>
    HeldBecauseThisBuildIsUnproven,
    /// <summary>A health state file could not be read, so this build cannot be shown to be proven.</summary>
    HeldBecauseHealthStateUnreadable,
    /// <summary>The other slot was missing, and was created from this build.</summary>
    Created,
    /// <summary>The other slot was older and not running, and was replaced with this build.</summary>
    Replaced,
    /// <summary>The other slot is the same version as this build or newer. Never downgraded.</summary>
    UpToDate,
    /// <summary>A Director is running from the other slot.</summary>
    HeldBecauseOtherSlotIsRunning,
    /// <summary>Whether a Director is running from the other slot could not be established.</summary>
    HeldBecauseOtherSlotStateUnknown,
    /// <summary>The other slot's version could not be read, so older or newer is unknown.</summary>
    HeldBecauseOtherVersionUnreadable,
}

/// <summary>The decision a pass reached, and a sentence for the log.</summary>
public sealed record StandbySlotOutcome(StandbySlotDecision Decision, string Detail);

/// <summary>
/// KEEPS THE OTHER DIRECTOR SLOT PRESENT AND CURRENT (issue #2945).
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
/// starts after the update is the NEW build, so its startup provisions the slot with no reinstall. A new
/// install reaches the same code the first time its Director starts. One mechanism covers both.
///
/// THE RULES, each for a reason:
/// - Wait, bounded, for the machine-wide binary swap lock. On the very update that brings this code to an
///   existing machine, the launcher still holds that lock while it waits for this new Director to answer.
///   A pass that gave up immediately would be held off on exactly the first start that matters, and
///   there would be no second chance until the next restart.
/// - Only a PROVEN build is copied: the standby is the fallback copy. Health checks are per named
///   instance while the executable is shared, so EVERY instance's state is read, and the pass holds while
///   any of them still owes a check for THIS version. A marker for another version is about another build
///   and does not hold - a machine can carry a stale marker for years. A state file that exists but cannot
///   be read holds too: a pending check cannot be ruled out.
/// - Missing: create it. Older and idle: replace it. Same or newer: leave it - never downgrade.
/// - Running, or its running state unknown: do not touch it.
/// - Symmetric. The same code runs from either slot, so whichever slot carries the newer build brings
///   the other up to date the next time that one is idle.
/// - The per-install appsettings.json beside the executable wins over shared config, so it is carried
///   into an idle other slot whenever that slot has none. An existing one is never overwritten.
///
/// It never STARTS a Director. It only keeps the binary ready.
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
    private readonly Func<string, OtherSlotRunning> _probeRunning;
    private readonly bool _isWindows;

    /// <param name="selfExecutable">The executable this Director is running from.</param>
    /// <param name="sharedRoot">The machine-wide cc-director root, above every named instance's home.</param>
    /// <param name="readVersion">Reads an executable's version, or null when it has none readable.</param>
    /// <param name="probeRunning">Whether a process is running from the given executable.</param>
    /// <param name="isWindows">Whether this is Windows, the only platform this lays out.</param>
    public StandbySlotProvisioner(
        string selfExecutable,
        string sharedRoot,
        Func<string, Version?> readVersion,
        Func<string, OtherSlotRunning> probeRunning,
        bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        _selfExecutable = selfExecutable;
        _sharedRoot = sharedRoot;
        _readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
        _probeRunning = probeRunning ?? throw new ArgumentNullException(nameof(probeRunning));
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
    /// witness of a new Director - several minutes on a slow machine. The pass runs in the background, so
    /// waiting never delays startup. Internal so a test need not wait minutes.
    /// </summary>
    internal TimeSpan LockPatience { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The provisioner for the Director running in this process, wired to the real machine.</summary>
    public static StandbySlotProvisioner ForThisProcess()
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate this Director's executable.");
        return new StandbySlotProvisioner(self, InstanceContext.SharedRoot, ReadFileVersion, ProbeRunning,
            OperatingSystem.IsWindows());
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
    public static StandbySlotDecision Decide(
        bool isWindows,
        HealthGate health,
        bool otherExists,
        OtherSlotRunning otherRunning,
        Version selfVersion,
        Version? otherVersion)
    {
        ArgumentNullException.ThrowIfNull(selfVersion);
        if (!isWindows) return StandbySlotDecision.NotWindows;
        if (health == HealthGate.PendingForThisBuild) return StandbySlotDecision.HeldBecauseThisBuildIsUnproven;
        if (health == HealthGate.Unreadable) return StandbySlotDecision.HeldBecauseHealthStateUnreadable;
        if (!otherExists) return StandbySlotDecision.Created;
        if (otherRunning == OtherSlotRunning.Yes) return StandbySlotDecision.HeldBecauseOtherSlotIsRunning;
        if (otherRunning == OtherSlotRunning.Unknown) return StandbySlotDecision.HeldBecauseOtherSlotStateUnknown;
        if (otherVersion is null) return StandbySlotDecision.HeldBecauseOtherVersionUnreadable;
        return Normalize(otherVersion) < Normalize(selfVersion)
            ? StandbySlotDecision.Replaced
            : StandbySlotDecision.UpToDate;
    }

    /// <summary>
    /// Whether any process other than this one runs from <paramref name="executable"/>. A process that would
    /// not report its image makes the answer Unknown unless another is proven to be running from it. Pure.
    /// </summary>
    public static OtherSlotRunning ClassifyRunning(IEnumerable<ProcessImage> processes, int selfProcessId, string executable)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var target = Path.GetFullPath(executable);
        var answer = OtherSlotRunning.No;
        foreach (var process in processes)
        {
            if (process.ProcessId == selfProcessId)
                continue;
            if (process.ImagePath is null)
            {
                answer = OtherSlotRunning.Unknown;
                continue;
            }
            if (string.Equals(Path.GetFullPath(process.ImagePath), target, StringComparison.OrdinalIgnoreCase))
                return OtherSlotRunning.Yes;
        }
        return answer;
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
                pending = doc.RootElement.TryGetProperty(PendingHealthCheckProperty, out var value)
                          && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
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
    /// One pass: wait for the swap lock, decide, and create or replace the other slot when the decision says
    /// so. Throws when the file work fails; the caller is the boundary that logs it.
    /// </summary>
    public Task<StandbySlotOutcome> RunOnceAsync()
    {
        var other = OtherSlotFor(_selfExecutable);
        FileLog.Write($"[StandbySlotProvisioner] RunOnceAsync: self={_selfExecutable}, other={other}");

        if (!_isWindows)
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.NotWindows,
                "not Windows; the application bundle needs its own slot layout"));

        return BinarySwapLock.RunExclusivelyAsync(
            () => Task.FromResult(DecideAndApply(other)),
            message => new StandbySlotOutcome(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, message),
            who: "standby slot",
            name: SwapLockName,
            waitFor: LockPatience);
    }

    private StandbySlotOutcome DecideAndApply(string other)
    {
        var selfVersion = _readVersion(_selfExecutable)
            ?? throw new InvalidOperationException($"This Director's own executable has no readable version: {_selfExecutable}");

        // Read under the lock, after any wait: an update that held the lock may have armed or cleared a check.
        var health = ReadHealthGate(_sharedRoot, selfVersion);
        var otherExists = File.Exists(other);
        var otherRunning = otherExists ? _probeRunning(other) : OtherSlotRunning.No;
        var otherVersion = otherExists ? _readVersion(other) : null;

        var decision = Decide(_isWindows, health, otherExists, otherRunning, selfVersion, otherVersion);
        FileLog.Write($"[StandbySlotProvisioner] self={selfVersion}, health={health}, otherExists={otherExists}, "
                      + $"otherRunning={otherRunning}, otherVersion={otherVersion?.ToString() ?? "(none)"} -> {decision}");

        switch (decision)
        {
            case StandbySlotDecision.Created:
            case StandbySlotDecision.Replaced:
                PlaceSelfAt(other);
                CarryAppSettings(other);
                return new StandbySlotOutcome(decision, $"{other} is now {selfVersion}");
            case StandbySlotDecision.UpToDate:
                CarryAppSettings(other);
                return new StandbySlotOutcome(decision, $"{other} is {otherVersion}, not older than {selfVersion}");
            case StandbySlotDecision.HeldBecauseThisBuildIsUnproven:
                return new StandbySlotOutcome(decision, $"a Director still owes a health check for {selfVersion}; nothing is copied");
            case StandbySlotDecision.HeldBecauseHealthStateUnreadable:
                return new StandbySlotOutcome(decision, "a health state file could not be read; nothing is copied");
            case StandbySlotDecision.HeldBecauseOtherSlotIsRunning:
                return new StandbySlotOutcome(decision, $"a Director is running from {other}; it is left alone");
            case StandbySlotDecision.HeldBecauseOtherSlotStateUnknown:
                return new StandbySlotOutcome(decision, $"could not tell whether a Director is running from {other}; it is left alone");
            case StandbySlotDecision.HeldBecauseOtherVersionUnreadable:
                return new StandbySlotOutcome(decision, $"{other} has no readable version; it is left alone");
            default:
                throw new InvalidOperationException($"Decision {decision} cannot be reached after the lock was taken.");
        }
    }

    /// <summary>
    /// Copy this executable beside the target first, then move it into place, so the target is never a
    /// half-written file. The staging copy is removed whatever happens.
    /// </summary>
    private void PlaceSelfAt(string target)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"Target has no directory: {target}");
        Directory.CreateDirectory(directory);

        var staging = target + ".new";
        try
        {
            File.Copy(_selfExecutable, staging, overwrite: true);
            File.Move(staging, target, overwrite: true);
            FileLog.Write($"[StandbySlotProvisioner] placed {_selfExecutable} at {target}");
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    /// <summary>Carry this slot's appsettings.json into the other slot when the other has none. Never overwrites.</summary>
    private void CarryAppSettings(string otherExecutable)
    {
        var selfDirectory = Path.GetDirectoryName(_selfExecutable)
            ?? throw new InvalidOperationException($"Executable has no directory: {_selfExecutable}");
        var otherDirectory = Path.GetDirectoryName(otherExecutable)
            ?? throw new InvalidOperationException($"Executable has no directory: {otherExecutable}");

        var source = Path.Combine(selfDirectory, AppSettingsFileName);
        var destination = Path.Combine(otherDirectory, AppSettingsFileName);
        if (!File.Exists(source) || File.Exists(destination))
            return;

        File.Copy(source, destination);
        FileLog.Write($"[StandbySlotProvisioner] carried {source} to {destination}");
    }

    private static string StateFileUnder(string home) => Path.Combine(home, "config", "director", "updater-state.json");

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static Version? ReadFileVersion(string executable)
    {
        var text = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>The real process list, handed to <see cref="ClassifyRunning"/>.</summary>
    private static OtherSlotRunning ProbeRunning(string executable)
    {
        var images = new List<ProcessImage>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                string? image;
                try
                {
                    image = process.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[StandbySlotProvisioner] pid={process.Id} would not report its image: {ex.Message}");
                    image = null;
                }
                images.Add(new ProcessImage(process.Id, image));
            }
        }
        return ClassifyRunning(images, Environment.ProcessId, executable);
    }
}
