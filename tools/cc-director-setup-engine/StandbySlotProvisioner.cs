using System.Diagnostics;
using CcDirector.Core.Update;
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
    /// slot. Treated exactly like <see cref="Yes"/>: a slot that might be running is never touched.
    /// </summary>
    Unknown,
}

/// <summary>What one standby slot pass decided.</summary>
public enum StandbySlotDecision
{
    /// <summary>Not Windows. The macOS application bundle needs its own slot layout.</summary>
    NotWindows,
    /// <summary>A post-update health check is still pending, so this build has not proven it can run.</summary>
    HeldBecauseThisBuildIsUnproven,
    /// <summary>Another Director or launcher update holds the machine-wide binary swap lock.</summary>
    HeldBecauseAnotherSwapIsRunning,
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
/// - Only once this build is proven. It runs after the main window, and holds off while a post-update
///   health check is pending: the standby is the fallback copy, and an unproven build must never be
///   written into it.
/// - Missing: create it. Older and idle: replace it. Same or newer: leave it - never downgrade.
/// - Running, or its running state unknown: do not touch it.
/// - Symmetric. The same code runs from either slot, so whichever slot carries the newer build brings
///   the other up to date the next time that one is idle.
/// - The per-install appsettings.json beside the executable wins over shared config, so it is carried
///   into the other slot when that slot has none, and an existing one is never overwritten.
/// - Taken under <see cref="BinarySwapLock"/> without waiting, so it never overlaps a Director or
///   launcher update. A pass held off simply tries again on the next start.
///
/// It never STARTS a Director. It only keeps the binary ready.
/// </summary>
public sealed class StandbySlotProvisioner
{
    /// <summary>The standby slot's folder under the primary's folder. Permanent: identity follows the path.</summary>
    public const string StandbyFolderName = "standby";

    private const string AppSettingsFileName = "appsettings.json";

    private readonly string _selfExecutable;
    private readonly Func<string, Version?> _readVersion;
    private readonly Func<string, OtherSlotRunning> _probeRunning;
    private readonly Func<bool> _healthCheckPending;
    private readonly bool _isWindows;

    /// <param name="selfExecutable">The executable this Director is running from.</param>
    /// <param name="readVersion">Reads an executable's version, or null when it has none readable.</param>
    /// <param name="probeRunning">Whether a process is running from the given executable.</param>
    /// <param name="healthCheckPending">Whether this build still owes a post-update health check.</param>
    /// <param name="isWindows">Whether this is Windows, the only platform this lays out.</param>
    public StandbySlotProvisioner(
        string selfExecutable,
        Func<string, Version?> readVersion,
        Func<string, OtherSlotRunning> probeRunning,
        Func<bool> healthCheckPending,
        bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfExecutable);
        _selfExecutable = selfExecutable;
        _readVersion = readVersion ?? throw new ArgumentNullException(nameof(readVersion));
        _probeRunning = probeRunning ?? throw new ArgumentNullException(nameof(probeRunning));
        _healthCheckPending = healthCheckPending ?? throw new ArgumentNullException(nameof(healthCheckPending));
        _isWindows = isWindows;
    }

    /// <summary>
    /// The machine-wide lock this pass takes. <see cref="BinarySwapLock.Name"/> in production; a test
    /// overrides it so it can hold a lock of its own without contending with every other swap on the
    /// machine. Internal for the same reason the update owners keep theirs internal.
    /// </summary>
    internal string SwapLockName { get; init; } = BinarySwapLock.Name;

    /// <summary>The provisioner for the Director running in this process, wired to the real machine.</summary>
    public static StandbySlotProvisioner ForThisProcess()
    {
        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate this Director's executable.");
        return new StandbySlotProvisioner(
            self,
            ReadFileVersion,
            ProbeRunning,
            () => !string.IsNullOrEmpty(UpdaterState.Load().PendingHealthCheckVersion),
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
        bool healthCheckPending,
        bool otherExists,
        OtherSlotRunning otherRunning,
        Version selfVersion,
        Version? otherVersion)
    {
        ArgumentNullException.ThrowIfNull(selfVersion);
        if (!isWindows) return StandbySlotDecision.NotWindows;
        if (healthCheckPending) return StandbySlotDecision.HeldBecauseThisBuildIsUnproven;
        if (!otherExists) return StandbySlotDecision.Created;
        if (otherRunning == OtherSlotRunning.Yes) return StandbySlotDecision.HeldBecauseOtherSlotIsRunning;
        if (otherRunning == OtherSlotRunning.Unknown) return StandbySlotDecision.HeldBecauseOtherSlotStateUnknown;
        if (otherVersion is null) return StandbySlotDecision.HeldBecauseOtherVersionUnreadable;
        return Normalize(otherVersion) < Normalize(selfVersion)
            ? StandbySlotDecision.Replaced
            : StandbySlotDecision.UpToDate;
    }

    /// <summary>
    /// One pass: decide, and create or replace the other slot when the decision says so. Throws when the
    /// file work fails; the caller is the boundary that logs it.
    /// </summary>
    public Task<StandbySlotOutcome> RunOnceAsync()
    {
        var other = OtherSlotFor(_selfExecutable);
        FileLog.Write($"[StandbySlotProvisioner] RunOnceAsync: self={_selfExecutable}, other={other}");

        if (!_isWindows)
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.NotWindows,
                "not Windows; the application bundle needs its own slot layout"));

        if (_healthCheckPending())
            return Task.FromResult(new StandbySlotOutcome(StandbySlotDecision.HeldBecauseThisBuildIsUnproven,
                "a post-update health check is still pending, so this build is not copied anywhere yet"));

        return BinarySwapLock.RunExclusivelyAsync(
            () => Task.FromResult(DecideAndApply(other)),
            message => new StandbySlotOutcome(StandbySlotDecision.HeldBecauseAnotherSwapIsRunning, message),
            who: "standby slot",
            name: SwapLockName);
    }

    private StandbySlotOutcome DecideAndApply(string other)
    {
        var selfVersion = _readVersion(_selfExecutable)
            ?? throw new InvalidOperationException($"This Director's own executable has no readable version: {_selfExecutable}");

        var otherExists = File.Exists(other);
        var otherRunning = otherExists ? _probeRunning(other) : OtherSlotRunning.No;
        var otherVersion = otherExists ? _readVersion(other) : null;

        var decision = Decide(_isWindows, healthCheckPending: false, otherExists, otherRunning, selfVersion, otherVersion);
        FileLog.Write($"[StandbySlotProvisioner] self={selfVersion}, otherExists={otherExists}, otherRunning={otherRunning}, "
                      + $"otherVersion={otherVersion?.ToString() ?? "(none)"} -> {decision}");

        switch (decision)
        {
            case StandbySlotDecision.Created:
            case StandbySlotDecision.Replaced:
                PlaceSelfAt(other);
                CarryAppSettings(other);
                return new StandbySlotOutcome(decision, $"{other} is now {selfVersion}");
            case StandbySlotDecision.UpToDate:
                return new StandbySlotOutcome(decision, $"{other} is {otherVersion}, not older than {selfVersion}");
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

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static Version? ReadFileVersion(string executable)
    {
        var text = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// Whether any other process runs from <paramref name="executable"/>. A same-named process that will
    /// not report its image counts as Unknown, never as No.
    /// </summary>
    private static OtherSlotRunning ProbeRunning(string executable)
    {
        var answer = OtherSlotRunning.No;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                string? image;
                try
                {
                    image = process.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[StandbySlotProvisioner] pid={process.Id} would not report its image: {ex.Message}");
                    answer = OtherSlotRunning.Unknown;
                    continue;
                }

                if (image is null)
                {
                    answer = OtherSlotRunning.Unknown;
                    continue;
                }

                if (string.Equals(Path.GetFullPath(image), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                    return OtherSlotRunning.Yes;
            }
        }
        return answer;
    }
}
