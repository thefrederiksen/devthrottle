using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching on this Director: the key holder lock, the usage reporter, the switch and its stored results,
/// composed once. ONE PER PROCESS: the key holder lock is held for the life of the process (contract 2.4), and the
/// Director's tunnel host can be stopped and rebuilt when its Gateway settings change - so the host attaches its
/// tunnel to <see cref="Reporter"/> and never owns any of this.
/// </summary>
public sealed class LicenseSwitching : IDisposable
{
    /// <summary>Points the license tool at an isolated default Claude config folder. Every test and every phase 1 run
    /// sets it; until phase 5 the tool refuses the machine's real default login.</summary>
    public const string DefaultConfigDirVariable = "CC_LICENSE_DEFAULT_CONFIG_DIR";

    /// <summary>Points the license tool at an isolated key store folder. When set, the Director's own files - the key
    /// holder lock and the stored switch results - live beside it too, so an isolated run never touches the real ones.</summary>
    public const string StoreDirVariable = "CC_LICENSE_STORE_DIR";

    private static readonly Lazy<LicenseSwitching> SharedInstance = new(CreateForThisMachine);

    private int _started;

    public LicenseSwitching(ILicenseCommandLine tool, string directorFolder, Func<DateTime> utcNow, string machineName, string osUser)
    {
        HolderLock = new LicenseKeyHolderLock(directorFolder);
        Reporter = new LicenseUsageReporter(tool, HolderLock, utcNow, machineName, osUser);
        Results = new LicenseSwitchResultStore(Path.Combine(directorFolder, "switch-results"), utcNow);
        Coordinator = new LicenseSwitchCoordinator(tool, HolderLock, Results, Reporter, utcNow);
    }

    /// <summary>This process's instance.</summary>
    public static LicenseSwitching Shared => SharedInstance.Value;

    public LicenseKeyHolderLock HolderLock { get; }

    public LicenseUsageReporter Reporter { get; }

    public LicenseSwitchResultStore Results { get; }

    public LicenseSwitchCoordinator Coordinator { get; }

    /// <summary>The Director's own license files for this machine and user: shared by every Director on the machine,
    /// which is what makes the lock decide between them.</summary>
    public static string DirectorFolder(string? storeDir) =>
        string.IsNullOrWhiteSpace(storeDir)
            ? Path.Combine(CcStorage.MachineRoot(), "licenses", "director")
            : Path.Combine(storeDir, "director");

    private static LicenseSwitching CreateForThisMachine()
    {
        var configDir = Environment.GetEnvironmentVariable(DefaultConfigDirVariable);
        var storeDir = Environment.GetEnvironmentVariable(StoreDirVariable);
        var folder = DirectorFolder(storeDir);
        FileLog.Write($"[LicenseSwitching] CreateForThisMachine: folder={folder}, isolatedConfig={!string.IsNullOrWhiteSpace(configDir)}, isolatedStore={!string.IsNullOrWhiteSpace(storeDir)}");
        return new LicenseSwitching(new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(configDir, storeDir)),
            folder, () => DateTime.UtcNow, Environment.MachineName, Environment.UserName);
    }

    /// <summary>Puts the usage reporter on the scheduler once for the life of the process. Further calls do nothing.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        FileLog.Write("[LicenseSwitching] Start");
        Results.Prune();
        Reporter.Start(Background.BackgroundJobs.Default);
    }

    public void Dispose()
    {
        Reporter.Stop();
        HolderLock.Dispose();
    }
}
