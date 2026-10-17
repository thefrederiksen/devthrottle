using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching on this Director: the key holder lock, the usage reporter, the switch and its stored results,
/// the daily renewal of the sign-ins, and both sides of a sign-in approved on another machine, composed once. ONE PER PROCESS: the key holder lock is held for the life of the process (contract 2.4), and the
/// Director's tunnel host can be stopped and rebuilt when its Gateway settings change - so the host attaches its
/// tunnel to <see cref="Reporter"/> and never owns any of this.
/// </summary>
public sealed class LicenseSwitching : IDisposable
{
    /// <summary>Points the license tool at an isolated default Claude config folder. Every test and every phase 1 run
    /// sets it. Unset, the tool works on the machine's real default login - which it does only when license switching
    /// is on (<see cref="LicenseCommandLineOptions.AllowRealDefault"/>).</summary>
    public const string DefaultConfigDirVariable = "CC_LICENSE_DEFAULT_CONFIG_DIR";

    /// <summary>Points the license tool at an isolated key store folder. When set, the Director's own files - the key
    /// holder lock and the stored switch results - live beside it too, so an isolated run never touches the real ones.</summary>
    public const string StoreDirVariable = "CC_LICENSE_STORE_DIR";

    private static readonly Lazy<LicenseSwitching> SharedInstance = new(CreateForThisMachine);

    private int _started;

    /// <param name="enabled">The license switching setting (<see cref="Configuration.LicenseSwitchingSetting"/>). Off:
    /// no report loop, no renewal, the key holder lock is never taken, and every switch, sign-in and approval is
    /// answered failed or refused.</param>
    /// <param name="signInDelay">Test seam: how the wait for another machine's approval passes (<see cref="LicenseSignInCoordinator"/>),
    /// and the longest wait of a sign-in or approval for a running approver check (<see cref="LicenseBrowserLane"/>).</param>
    /// <param name="offersApprovers">The offer setting (<see cref="Configuration.LicenseApproverOfferSetting"/>): the
    /// daily approver check looks at this Director's own browser profiles too.</param>
    /// <param name="browserProfiles">This Director's own browser profiles; the machine's registry when not given.</param>
    public LicenseSwitching(ILicenseCommandLine tool, string directorFolder, Func<DateTime> utcNow, string machineName, string osUser, bool enabled,
        Func<TimeSpan, CancellationToken, Task>? signInDelay = null, bool offersApprovers = false, ILicenseBrowserProfiles? browserProfiles = null)
    {
        BrowserLane = new LicenseBrowserLane(signInDelay);
        Enabled = enabled;
        HolderLock = new LicenseKeyHolderLock(directorFolder);
        Reporter = new LicenseUsageReporter(tool, HolderLock, utcNow, machineName, osUser);
        Results = new LicenseSwitchResultStore(Path.Combine(directorFolder, "switch-results"), utcNow);
        Coordinator = new LicenseSwitchCoordinator(tool, HolderLock, Results, Reporter, utcNow, enabled);
        Approvals = new LicenseApprovalExchange();
        SignInResults = new LicenseSignInResultStore(Path.Combine(directorFolder, "sign-in-results"), utcNow);
        SignIns = new LicenseSignInCoordinator(tool, HolderLock, SignInResults, Reporter, Approvals, utcNow, enabled, signInDelay, BrowserLane);
        Approver = new LicenseApprover(tool, utcNow, enabled, BrowserLane);
        Renewer = new LicenseRenewer(tool, HolderLock, Reporter, SignIns, utcNow);
        ApproverCheck = new LicenseApproverCheck(tool, HolderLock, BrowserLane, browserProfiles ?? new AutomationBrowserProfiles(),
            utcNow, enabled, offersApprovers);
    }

    /// <summary>Keeps the daily approver check out of a running sign-in or approval.</summary>
    public LicenseBrowserLane BrowserLane { get; }

    /// <summary>The daily check of this machine's approver browser profiles (phase 6, Track C).</summary>
    public LicenseApproverCheck ApproverCheck { get; }

    /// <summary>Whether license switching is on for this Director (read once, at start).</summary>
    public bool Enabled { get; }

    /// <summary>This process's instance.</summary>
    public static LicenseSwitching Shared => SharedInstance.Value;

    public LicenseKeyHolderLock HolderLock { get; }

    public LicenseUsageReporter Reporter { get; }

    public LicenseSwitchResultStore Results { get; }

    public LicenseSwitchCoordinator Coordinator { get; }

    /// <summary>The approvals this Director is waiting for from another machine (contract 8.3).</summary>
    public LicenseApprovalExchange Approvals { get; }

    public LicenseSignInResultStore SignInResults { get; }

    /// <summary>Signing an account in on this machine when the Gateway asks, or the renewal needs another machine (8.3, 8.4).</summary>
    public LicenseSignInCoordinator SignIns { get; }

    /// <summary>Approving another machine's sign-in in a browser profile here (8.3).</summary>
    public LicenseApprover Approver { get; }

    /// <summary>The daily renewal of this machine's sign-ins (8.2).</summary>
    public LicenseRenewer Renewer { get; }

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
        var enabled = Configuration.LicenseSwitchingSetting.Get();
        var offersApprovers = Configuration.LicenseApproverOfferSetting.Get();
        FileLog.Write($"[LicenseSwitching] CreateForThisMachine: enabled={enabled}, offersApprovers={offersApprovers}, folder={folder}, isolatedConfig={!string.IsNullOrWhiteSpace(configDir)}, isolatedStore={!string.IsNullOrWhiteSpace(storeDir)}");
        return new LicenseSwitching(new CcDevthrottleLicenseCommandLine(new LicenseCommandLineOptions(configDir, storeDir, AllowRealDefault: enabled)),
            folder, () => DateTime.UtcNow, Environment.MachineName, Environment.UserName, enabled, offersApprovers: offersApprovers);
    }

    /// <summary>Puts the usage reporter on the scheduler once for the life of the process, when license switching is
    /// on. Further calls do nothing.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (!Enabled)
        {
            FileLog.Write("[LicenseSwitching] Start: license switching is turned off on this Director; no usage is read or reported");
            return;
        }
        FileLog.Write("[LicenseSwitching] Start");
        Results.Prune();
        SignInResults.Prune();
        Reporter.Start(Background.BackgroundJobs.Default);
        Renewer.Start(Background.BackgroundJobs.Default);
        ApproverCheck.Start(Background.BackgroundJobs.Default);
    }

    public void Dispose()
    {
        ApproverCheck.Stop();
        Renewer.Stop();
        Reporter.Stop();
        HolderLock.Dispose();
    }
}
