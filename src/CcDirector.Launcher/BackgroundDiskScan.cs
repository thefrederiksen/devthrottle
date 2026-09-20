using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Background;
using CcDirector.Reclaim.Rules;
using CcDirector.Reclaim.Windows;

namespace CcDirector.Launcher;

/// <summary>
/// The Launcher hosts the background scan of this machine's disks (Reclaim the Disk, phase 5).
///
/// THE LAUNCHER, NOT THE DIRECTOR. There is one Launcher for a machine and several Directors; a scan
/// in each Director would be the same disk read several times over. The Directors and the Cockpit
/// render what this saves and scan nothing.
///
/// IT NEVER REMOVES ANYTHING, and nothing it schedules does. It scans and it recommends; the engine
/// job it runs cannot delete, move or change a file outside its own store folder. Removal is a
/// person's act, from the command line.
///
/// This class decides only WHEN: which folders, and whether one is due. Everything about what a scan
/// found or what its state means is the engine's (critical rule 7 in CLAUDE.md).
///
/// The times below are built around the measured cost of the thing: a whole-disk scan took 1,406
/// seconds. So a scan is a daily event and not an hourly one, a scan that failed is not thrown
/// straight back at the disk, and the first one waits until the machine has finished starting.
/// </summary>
public sealed class BackgroundDiskScan
{
    /// <summary>How long after the Launcher starts before the first look. Never compete with startup.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(10);

    /// <summary>How often the Launcher looks at whether any folder is due.</summary>
    public static readonly TimeSpan LookInterval = TimeSpan.FromHours(1);

    /// <summary>How old a finished scan is before the folder is scanned again.</summary>
    public static readonly TimeSpan RescanAfter = TimeSpan.FromHours(24);

    /// <summary>How long after a scan that failed before the folder is tried again.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(6);

    private readonly string _storeDirectory;
    private readonly IReadOnlyList<IReclaimRule> _rules;

    /// <summary>Make a host that saves into one store folder and runs one set of rules.</summary>
    /// <param name="storeDirectory">The folder everything is saved into.</param>
    /// <param name="rules">Every rule this machine has.</param>
    public BackgroundDiskScan(string storeDirectory, IReadOnlyList<IReclaimRule> rules)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("A store directory cannot be blank.", nameof(storeDirectory));
        _storeDirectory = storeDirectory;
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>The host for this machine: the machine-wide store folder and this platform's rules.</summary>
    public static BackgroundDiskScan ForThisMachine()
    {
        // The Windows rules are the only set that exists. A platform with none gets an empty list,
        // and the engine turns an empty list into a broken recommendation, never into "nothing to
        // remove".
        IReadOnlyList<IReclaimRule> rules = OperatingSystem.IsWindows() ? WindowsRuleSet.ForThisMachine() : [];
        FileLog.Write($"[BackgroundDiskScan] ForThisMachine: windows={OperatingSystem.IsWindows()}, rules={rules.Count}");
        return new BackgroundDiskScan(BackgroundScanStatusStore.DefaultStoreDirectory(), rules);
    }

    /// <summary>
    /// The folders the background scan covers: the root of every fixed disk that is ready. The
    /// owner's brief says D: matters as much as C:, so it is every disk and not the system one.
    /// Removable, network and optical drives are left alone.
    /// </summary>
    public static IReadOnlyList<string> RootsToScan()
    {
        var roots = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .OrderBy(root => root, StringComparer.Ordinal)
            .ToList();
        FileLog.Write($"[BackgroundDiskScan] RootsToScan: {string.Join(", ", roots)}");
        return roots;
    }

    /// <summary>
    /// Whether a folder should be scanned now, given where its background scan stands.
    /// </summary>
    /// <param name="status">Where the scan stands, as the engine read it.</param>
    /// <param name="nowUtc">The moment it is asked.</param>
    public static bool IsDue(BackgroundScanStatus status, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.State switch
        {
            BackgroundScanState.NeverRun => true,
            BackgroundScanState.Running => false,
            BackgroundScanState.Completed =>
                status.FinishedUtc is not { } finished || nowUtc - finished >= RescanAfter,
            BackgroundScanState.Failed =>
                (status.FinishedUtc ?? status.StartedUtc) is not { } lastTried || nowUtc - lastTried >= RetryFailedAfter,
            _ => throw new InvalidOperationException($"There is no background scan state {status.State}.")
        };
    }

    /// <summary>
    /// Look, scan what is due, wait, and look again, until asked to stop. Failures only log here,
    /// because each one is already recorded where a reader will find it: in the scan's own record.
    /// </summary>
    /// <param name="stop">Asks the loop, and any scan it is running, to stop.</param>
    public async Task RunLoopAsync(CancellationToken stop)
    {
        FileLog.Write($"[BackgroundDiskScan] RunLoopAsync: store={_storeDirectory}, settle={SettleDelay}");

        try { await Task.Delay(SettleDelay, stop); } catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunDueScansAsync(RootsToScan(), DateTimeOffset.UtcNow, stop);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDiskScan] look FAILED: {ex}");
            }

            try { await Task.Delay(LookInterval, stop); } catch (OperationCanceledException) { break; }
        }

        FileLog.Write("[BackgroundDiskScan] RunLoopAsync done: asked to stop");
    }

    /// <summary>
    /// Scan every folder that is due, one after another, and say how each ended. One at a time on
    /// purpose: the engine's guard would refuse the second anyway, and two walks of two disks at once
    /// is a machine nobody can use.
    /// </summary>
    /// <param name="roots">The folders to consider.</param>
    /// <param name="nowUtc">The moment "due" is judged against.</param>
    /// <param name="stop">Asks a running scan to stop.</param>
    public async Task<IReadOnlyList<(string Root, BackgroundScanOutcome Outcome)>> RunDueScansAsync(
        IReadOnlyList<string> roots, DateTimeOffset nowUtc, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(roots);
        FileLog.Write($"[BackgroundDiskScan] RunDueScansAsync: roots={roots.Count}");

        var outcomes = new List<(string, BackgroundScanOutcome)>();
        var job = new BackgroundScanJob(_storeDirectory, _rules);

        foreach (var root in roots)
        {
            if (stop.IsCancellationRequested) break;

            var status = BackgroundScanStatusStore.Read(_storeDirectory, root);
            if (!IsDue(status, nowUtc))
            {
                FileLog.Write($"[BackgroundDiskScan] not due: root={root}, state={status.State}");
                continue;
            }

            // Its own thread, for the length of the walk. The walk is blocking file system calls for
            // something like 1,406 seconds on a whole disk, and the thread pool is not the place to
            // park that.
            var outcome = await Task.Factory.StartNew(
                () => job.Run(root, stop),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            FileLog.Write($"[BackgroundDiskScan] scanned: root={root}, outcome={outcome}");
            outcomes.Add((root, outcome));
        }

        FileLog.Write($"[BackgroundDiskScan] RunDueScansAsync done: scanned={outcomes.Count}");
        return outcomes;
    }
}
