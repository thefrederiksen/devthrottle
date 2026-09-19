using System.Runtime.Versioning;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// What Windows says it still needs from its package cache.
/// </summary>
/// <param name="ReferencedPackagePaths">
/// Every cached package path Windows points at, without repeats, compared without regard to letter
/// case the way the file system does.
/// </param>
/// <param name="ProductRecordsRead">How many installed-product records were read.</param>
/// <param name="PatchRecordsRead">How many applied-patch records were read.</param>
public sealed record InstallerRecords(
    IReadOnlySet<string> ReferencedPackagePaths,
    int ProductRecordsRead,
    int PatchRecordsRead);

/// <summary>
/// Where the installer records are read from.
///
/// This exists so the rule can be tested. The records live in the machine's registry and there is no
/// way to build a fixture registry the way a fixture tree is built on disk, so the one thing that
/// cannot be faked is put behind this and everything else is tested for real. It is the only
/// substitution in this phase, and it is here because it is genuinely needed rather than by habit.
/// </summary>
public interface IInstallerRecordSource
{
    /// <summary>
    /// Read what Windows says it still needs.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The machine is not running Windows.</exception>
    InstallerRecords Read();
}

/// <summary>
/// Reads the installer records out of the machine's registry, which is where Windows keeps them.
///
/// Windows records, for every installed product and every applied patch, the cached package file it
/// will need to repair or uninstall that thing. The value is called LocalPackage and it names a file
/// in the package cache. A file in that cache that no record points at is an orphan: nothing can ask
/// for it, and Windows will not put it back.
///
/// It reads. It changes nothing, and it opens every key read-only.
/// </summary>
public sealed class WindowsRegistryInstallerRecordSource : IInstallerRecordSource
{
    private const string UserDataPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    /// <summary>Read what Windows says it still needs.</summary>
    public InstallerRecords Read()
    {
        FileLog.Write("[WindowsRegistryInstallerRecordSource] Read: entry");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows installer records live in the Windows registry and this machine is not running Windows.");
        }

        var records = ReadOnWindows();

        FileLog.Write(
            "[WindowsRegistryInstallerRecordSource] Read done: " +
            $"products={records.ProductRecordsRead}, patches={records.PatchRecordsRead}, " +
            $"referenced={records.ReferencedPackagePaths.Count}");
        return records;
    }

    [SupportedOSPlatform("windows")]
    private static InstallerRecords ReadOnWindows()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var products = 0;
        var patches = 0;

        using var userData = Registry.LocalMachine.OpenSubKey(UserDataPath, writable: false);

        // A missing key is reported as nothing read, not as an error. The rule turns nothing read
        // into BROKEN, which is the honest answer: a machine whose records cannot be found is a
        // machine we cannot say anything about, and it must not read as a machine with no orphans.
        if (userData is null)
        {
            FileLog.Write($"[WindowsRegistryInstallerRecordSource] ReadOnWindows: no key at {UserDataPath}");
            return new InstallerRecords(referenced, 0, 0);
        }

        foreach (var accountName in userData.GetSubKeyNames())
        {
            using var account = userData.OpenSubKey(accountName, writable: false);
            if (account is null) continue;

            products += ReadLocalPackages(account, "Products", "InstallProperties", referenced);
            patches += ReadLocalPackages(account, "Patches", null, referenced);
        }

        return new InstallerRecords(referenced, products, patches);
    }

    [SupportedOSPlatform("windows")]
    private static int ReadLocalPackages(
        RegistryKey account,
        string groupName,
        string? valueHolderName,
        HashSet<string> referenced)
    {
        using var group = account.OpenSubKey(groupName, writable: false);
        if (group is null) return 0;

        var read = 0;
        foreach (var entryName in group.GetSubKeyNames())
        {
            using var entry = group.OpenSubKey(entryName, writable: false);
            if (entry is null) continue;

            read++;

            using var holder = valueHolderName is null ? null : entry.OpenSubKey(valueHolderName, writable: false);
            var source = valueHolderName is null ? entry : holder;
            if (source?.GetValue("LocalPackage") is string path && path.Length > 0)
                referenced.Add(path);
        }

        return read;
    }
}
