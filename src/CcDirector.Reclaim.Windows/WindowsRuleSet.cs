using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// The Windows rules, and where each one looks on a real machine.
///
/// Every location is worked out here and handed to a rule, rather than written inside it. That is
/// what lets every rule be tested against a tree the tests build, which is the only way removal is
/// ever proven in this mission - nothing is tested by pointing it at the machine it runs on.
///
/// Gradle is deliberately absent, although the mission's table of first rules names it. Gradle ships
/// no command that clears its own cache: it cleans its caches itself on a schedule, and its own
/// guidance for clearing them by hand is to delete the folder. Deleting a folder because it looks
/// like a cache is exactly the reasoning this tool refuses, so Gradle has no proof of any of the
/// three kinds and no rule is written for it. Its cache is still reported by the scan like anything
/// else of that size; it is simply never offered for removal.
///
/// The recycle bin and the Disk Cleanup list are built once per fixed volume rather than once for
/// the machine, because a rule looks in one place and a bin is a place on each volume: the rules
/// for a volume are the ones that run when that volume is asked about, and the ones for the other
/// volumes are named as not run rather than silently absent.
/// </summary>
public static class WindowsRuleSet
{
    /// <summary>
    /// Every Windows rule, pointed at this machine's own folders, in order of measured value.
    /// </summary>
    public static IReadOnlyList<IReclaimRule> ForThisMachine()
    {
        FileLog.Write("[WindowsRuleSet] ForThisMachine: entry");

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var rules = new List<IReclaimRule>
        {
            new OrphanedInstallerPackagesRule(
                new WindowsRegistryInstallerRecordSource(),
                Path.Combine(windows, "Installer")),

            new PackageCacheRule(
                "npm-cache",
                "The npm package cache",
                Path.Combine(localApplicationData, "npm-cache"),
                "npm cache clean --force",
                "nothing but the time to fetch a package again the next time a build asks for one; " +
                "how long that takes depends on the size of the package and the connection, and is not known here"),

            new PackageCacheRule(
                "pip-cache",
                "The pip package cache",
                Path.Combine(localApplicationData, "pip", "Cache"),
                "pip cache purge",
                "nothing but the time to download a wheel again the next time one is installed; " +
                "how long that takes is not known here"),

            new PackageCacheRule(
                "uv-cache",
                "The uv package cache",
                Path.Combine(localApplicationData, "uv", "cache"),
                "uv cache clean",
                "nothing but the time to fetch a package again; uv refills its cache as it installs, " +
                "and how long that takes is not known here"),

            new PackageCacheRule(
                "nuget-cache",
                "The NuGet package caches",
                Path.Combine(userProfile, ".nuget", "packages"),
                "dotnet nuget locals all --clear",
                "nothing but the time for the next build to restore its packages again, which for a large " +
                "solution is minutes rather than seconds; the exact time is not known here"),

            new TestScratchFoldersRule(Path.GetTempPath()),

            new ComponentStoreRule(
                new DismComponentStoreAnalysis(),
                Path.Combine(windows, "WinSxS")),

            new CrashDumpsAndErrorReportsRule(
                Path.Combine(localApplicationData, "CrashDumps"),
                Path.Combine(windows, "Minidump"),
                Path.Combine(windows, "MEMORY.DMP"),
                Path.Combine(commonApplicationData, "Microsoft", "Windows", "WER"),
                Path.Combine(localApplicationData, "Microsoft", "Windows", "WER"))
        };

        // One recycle bin rule and one Disk Cleanup rule per fixed volume. The bin is a place on
        // each volume, and Windows' own Disk Cleanup is opened against one volume at a time.
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            rules.Add(new DiskCleanupRule(new WindowsRegistryDiskCleanupSource(), drive.Name));
            rules.Add(new RecycleBinRule(Path.Combine(drive.Name, "$RECYCLE.BIN")));
        }

        FileLog.Write($"[WindowsRuleSet] ForThisMachine done: rules={rules.Count}");
        return rules;
    }
}
