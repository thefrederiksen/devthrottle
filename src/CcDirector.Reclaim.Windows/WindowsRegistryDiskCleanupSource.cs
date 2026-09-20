using System.Runtime.Versioning;
using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// One category from the list Windows' own Disk Cleanup keeps in its registry.
/// </summary>
/// <param name="Name">The category's name, exactly as its registry key is named. It is the name
/// Windows itself owns and this tool never renames or translates.</param>
/// <param name="Folders">
/// The folders the category's own registry entry names, with environment variables expanded. A
/// question mark in place of a drive letter means the category names a place on every volume. An
/// empty list means Windows decides where the category looks when it runs, and this tool does not
/// guess for it.
/// </param>
public sealed record DiskCleanupCategory(string Name, IReadOnlyList<string> Folders);

/// <summary>
/// Where the Disk Cleanup categories are read from, so the rule can be tested.
///
/// The list lives in the machine's registry and there is no way to build a fixture registry the way
/// a fixture tree is built on disk, so the one thing that cannot be faked is put behind this. It
/// exists for the same reason the installer record source does, and for no other.
/// </summary>
public interface IDiskCleanupSource
{
    /// <summary>
    /// Read the categories Windows' own Disk Cleanup offers on this machine.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The machine is not running Windows.</exception>
    IReadOnlyList<DiskCleanupCategory> Read();
}

/// <summary>
/// Reads the Disk Cleanup categories out of the machine's registry, which is where Windows keeps
/// the list of what its own tool offers.
///
/// Windows registers every category its Disk Cleanup tool can clean as one key under
/// VolumeCaches, and each key can name the folders the category looks at. The list is Windows' own
/// and is read from the machine every time, never typed into this tool: a typed list goes stale
/// invisibly, because an update that adds a category would leave a typed list behind saying nothing
/// about it.
///
/// It reads. It changes nothing, and it opens every key read-only.
/// </summary>
public sealed class WindowsRegistryDiskCleanupSource : IDiskCleanupSource
{
    private const string VolumeCachesPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    /// <summary>Read the categories Windows' own Disk Cleanup offers on this machine.</summary>
    public IReadOnlyList<DiskCleanupCategory> Read()
    {
        FileLog.Write("[WindowsRegistryDiskCleanupSource] Read: entry");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Disk Cleanup categories live in the Windows registry and this machine is not running Windows.");
        }

        var categories = ReadOnWindows();

        FileLog.Write($"[WindowsRegistryDiskCleanupSource] Read done: categories={categories.Count}");
        return categories;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DiskCleanupCategory> ReadOnWindows()
    {
        using var volumeCaches = Registry.LocalMachine.OpenSubKey(VolumeCachesPath, writable: false);

        // A missing key is reported as an empty list, not as an error. The rule turns an empty list
        // into BROKEN, which is the honest answer: a machine whose Disk Cleanup list cannot be
        // found is a machine this rule can say nothing about, and it must not read as a machine
        // where Windows offers nothing.
        if (volumeCaches is null)
        {
            FileLog.Write($"[WindowsRegistryDiskCleanupSource] ReadOnWindows: no key at {VolumeCachesPath}");
            return [];
        }

        var categories = new List<DiskCleanupCategory>();
        foreach (var keyName in volumeCaches.GetSubKeyNames())
        {
            using var category = volumeCaches.OpenSubKey(keyName, writable: false);
            if (category is null) continue;

            // The Folder value is read unexpandeded and expanded here, so that a value written with
            // an environment variable in it arrives in this tool exactly as the machine spells the
            // folder, and not as whatever the reading process happened to have in its environment.
            var folders = new List<string>();
            if (category.GetValue("Folder", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                is string folderValue && folderValue.Length > 0)
            {
                foreach (var piece in folderValue.Split('|'))
                {
                    var expanded = Environment.ExpandEnvironmentVariables(piece);
                    if (expanded.Length > 0) folders.Add(expanded);
                }
            }

            categories.Add(new DiskCleanupCategory(keyName, folders));
        }

        return categories;
    }
}
