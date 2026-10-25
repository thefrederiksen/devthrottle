using CcDirector.Core.Utilities;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// A real tree of real folders on a real disk, built by the test that uses it and destroyed when that
/// test ends. No test in this suite ever points at anything on the machine it runs on.
///
/// Every method here PROVES the thing it asked for actually happened before it returns: a junction is
/// read back, a denied folder is shown to refuse a listing, a cloud placeholder is read back with the
/// offline mark on it. That is deliberate. Each of those three depends on the operating system
/// agreeing to do something it is allowed to refuse, and a fixture that quietly did not build what it
/// said it built would leave a test passing while proving nothing at all.
///
/// One of the three is a Windows fact and says so plainly. A cloud placeholder is a file whose content
/// lives in a cloud store, marked by attributes that only Windows keeps, so the end-to-end proof of
/// placeholder counting can only be built on Windows. Run on a platform that does not keep file
/// attributes and <see cref="TryCloudPlaceholder"/> builds nothing and says so, and the tree's own
/// count of what it holds is what a test expects - never a constant that assumes a placeholder.
/// </summary>
public sealed class FixtureTree : IDisposable
{
    private readonly List<string> _deniedFolders = [];
    private readonly List<string> _links = [];

    /// <summary>Build an empty tree in a folder of its own, under the temporary folder.</summary>
    /// <param name="name">A short name for the test, used in the folder name so a stray tree can be traced.</param>
    public FixtureTree(string name) : this(name, onSystemVolume: false) { }

    /// <summary>
    /// Build an empty tree in a folder of its own. With <paramref name="onSystemVolume"/> the tree is built under
    /// the user's local application data folder, which is on the system volume, instead of under the temporary
    /// folder, which follows the TEMP variable and may be on any volume.
    ///
    /// WHY A TEST WOULD ASK FOR THE SYSTEM VOLUME. By default Windows keeps 8.3 short names on the system volume
    /// only; on every other volume the short form of a path is the path itself. A test whose proof is built on a
    /// short name therefore proves nothing when TEMP points at another volume - and the release gate does exactly
    /// that when the system volume is short of space. On 17 October 2026 the v2.23.0 release gate ran with TEMP on
    /// D: and <see cref="WindowsShortPath"/> refused, correctly, to build a proof on a short name that was not short.
    /// </summary>
    /// <param name="name">A short name for the test, used in the folder name so a stray tree can be traced.</param>
    /// <param name="onSystemVolume">Build under the local application data folder rather than under TEMP.</param>
    public FixtureTree(string name, bool onSystemVolume)
    {
        var home = onSystemVolume
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Temp")
            : Path.GetTempPath();
        Root = Path.Combine(
            home,
            "cc-reclaim-tests",
            $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>The root of the tree.</summary>
    public string Root { get; }

    /// <summary>Make a folder, and every folder above it.</summary>
    /// <param name="relativePath">Where the folder goes, below the root.</param>
    public string Folder(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Make a file of an exact size.</summary>
    /// <param name="relativePath">Where the file goes, below the root.</param>
    /// <param name="sizeInBytes">Exactly how many bytes the file holds.</param>
    public string File(string relativePath, int sizeInBytes)
    {
        var full = Path.Combine(Root, relativePath);
        var folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(full, new byte[sizeInBytes]);

        var written = new FileInfo(full).Length;
        if (written != sizeInBytes)
        {
            throw new InvalidOperationException(
                $"The fixture asked for a file of {sizeInBytes} bytes at {full} and the disk holds {written}.");
        }

        return full;
    }

    /// <summary>
    /// Make a file that stands for one whose content lives in a cloud store: an ordinary file with the
    /// offline mark set on it, which is exactly what a scan meets on a machine with cloud storage
    /// switched on and exactly what it must count apart from the bytes on the disk.
    ///
    /// Only a file system that keeps the offline mark can hold one - Windows. Elsewhere the mark is
    /// dropped as it is set, nothing is left behind, and this answers null; the tree then holds no
    /// placeholder, and <see cref="PlaceholderFiles"/> and <see cref="PlaceholderBytes"/> say so, so a
    /// test that takes its expected numbers from them stays exact on every platform rather than passing
    /// on a tree that never held what it claims.
    /// </summary>
    /// <param name="relativePath">Where the file goes, below the root.</param>
    /// <param name="sizeInBytes">The size the file claims.</param>
    /// <returns>The placeholder's path, or null when this platform does not keep the offline mark.</returns>
    public string? TryCloudPlaceholder(string relativePath, int sizeInBytes)
    {
        var full = File(relativePath, sizeInBytes);
        System.IO.File.SetAttributes(full, System.IO.File.GetAttributes(full) | FileAttributes.Offline);

        if ((new FileInfo(full).Attributes & FileAttributes.Offline) == 0)
        {
            // Left in place it would be an ordinary file the scan counts as bytes seen.
            System.IO.File.Delete(full);
            return null;
        }

        PlaceholderFiles++;
        PlaceholderBytes += sizeInBytes;
        return full;
    }

    /// <summary>How many cloud placeholders this tree really holds.</summary>
    public long PlaceholderFiles { get; private set; }

    /// <summary>The bytes the placeholders this tree really holds claim, none of them on this disk.</summary>
    public long PlaceholderBytes { get; private set; }

    /// <summary>
    /// Make a link that stands where a folder would: a junction on Windows, a symbolic link
    /// elsewhere. The scan must count it and must not walk through it.
    /// </summary>
    /// <param name="relativePath">Where the link goes, below the root.</param>
    /// <param name="targetRelativePath">The folder the link names, below the root.</param>
    public string DirectoryLink(string relativePath, string targetRelativePath)
    {
        var full = Path.Combine(Root, relativePath);
        var target = Path.Combine(Root, targetRelativePath);

        if (!Directory.Exists(target))
            throw new InvalidOperationException($"The fixture cannot link to {target}; there is no folder there.");

        if (OperatingSystem.IsWindows())
        {
            // A junction, not a symbolic link. Windows lets any account make a junction, and needs a
            // privilege or developer mode for a symbolic link - so a symbolic link here would turn
            // this proof into something that passes on one machine and fails on the next. A junction
            // is also what Windows itself uses all over a real disk, which is what the scan meets.
            MakeJunction(full, target);
        }
        else
        {
            Directory.CreateSymbolicLink(full, target);
        }

        var attributes = new DirectoryInfo(full).Attributes;
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            throw new InvalidOperationException(
                $"The fixture made {full} and the file system does not report it as a link, so a test " +
                "that says the scan does not follow links would be proving nothing.");
        }

        _links.Add(full);
        return full;
    }


    /// <summary>
    /// A folder of this tree spelled a way that resolves to another final form, which the final path
    /// check must refuse. On Windows that is the short (8.3) name - see <see cref="WindowsShortPath"/>,
    /// the spelling Windows itself hands out. Elsewhere there are no short names, and the spelling is
    /// the folder reached through a "." segment: a path the operating system acts on as the folder,
    /// spelled differently from the folder's final form.
    /// </summary>
    /// <param name="relativePath">The folder, below the root.</param>
    public string NonCanonicalSpelling(string relativePath)
    {
        if (OperatingSystem.IsWindows()) return WindowsShortPath(relativePath);

        var full = Path.Combine(Root, relativePath);
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"The fixture cannot respell {full}; there is no folder there.");

        var spelled = Path.Combine(Path.GetDirectoryName(full)!, ".", Path.GetFileName(full));
        if (!Directory.Exists(spelled) || Path.GetFullPath(spelled) != full)
        {
            throw new InvalidOperationException(
                $"The fixture spelled {full} as {spelled}, which is not another spelling of the same folder. " +
                "A test built on it would be proving nothing.");
        }

        return spelled;
    }

    /// <summary>
    /// The Windows short (8.3) form of a folder's name in this tree: DOCUME~1 and its kin. The final
    /// path check must refuse a path spelled this way, because Path.GetFullPath leaves short names
    /// alone while the operating system acts on the long name.
    /// </summary>
    /// <param name="relativePath">The folder, below the root.</param>
    /// <exception cref="PlatformNotSupportedException">This platform does not keep short names.</exception>
    public string WindowsShortPath(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"The fixture cannot shorten {full}; there is no folder there.");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This fixture needs a file system that keeps 8.3 short names, which is where a path spelled " +
                "one way resolves to another. This platform does not keep them, so the proof that a short name " +
                "is refused cannot be built here.");
        }

        var shortPath = ShortPathName(full);
        if (shortPath.Length == 0)
            throw new InvalidOperationException($"The fixture could not ask Windows for the short form of {full}.");
        if (!shortPath.Contains('~') || !Path.GetFullPath(shortPath).Equals(Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Windows returned {shortPath} as the short form of {full}, which is not a short name of the " +
                "same folder. A test built on it would be proving nothing.");
        }

        return shortPath;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern unsafe uint GetShortPathNameW(string longPath, char* buffer, uint bufferLength);

    private static unsafe string ShortPathName(string longPath)
    {
        var buffer = new char[1024];
        fixed (char* pinned = buffer)
        {
            return GetShortPathNameW(longPath, pinned, (uint)buffer.Length) == 0
                ? string.Empty
                : new string(pinned);
        }
    }

    /// <summary>
    /// Take away this account's permission to list a folder, and prove the folder now refuses. The
    /// scan must count it and name it rather than walk past it.
    /// </summary>
    /// <param name="relativePath">The folder, below the root.</param>
    public string DenyListing(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        if (!Directory.Exists(full))
            throw new InvalidOperationException($"The fixture cannot deny {full}; there is no folder there.");

        if (OperatingSystem.IsWindows()) DenyListingOnWindows(full);
        else System.IO.File.SetUnixFileMode(full, UnixFileMode.None);

        if (CanList(full))
        {
            throw new InvalidOperationException(
                $"The fixture took away permission to list {full} and it can still be listed. A test " +
                "that says the scan names a folder it could not read would be proving nothing. This " +
                "happens when the account running the tests can read anything on the machine.");
        }

        _deniedFolders.Add(full);
        return full;
    }

    /// <summary>
    /// Make the file system refuse to move or remove <paramref name="filePath"/>, the folder holding it,
    /// or that folder's parent, until the returned handle is disposed - the shape a live folder on a real
    /// disk has.
    ///
    /// On Windows a file held open with no sharing does it. Elsewhere an open file stops nothing: a
    /// folder can be moved or removed with its files open, so write permission is taken from the file's
    /// folder and from the folder above it, and the refusal is proven in force before a test relies on
    /// it - a test that says the store keeps an item it could not move would otherwise prove nothing.
    /// </summary>
    /// <param name="filePath">A file inside the tree.</param>
    public IDisposable KeepInPlace(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new InvalidOperationException($"The fixture cannot keep {filePath} in place; there is no file there.");

        if (OperatingSystem.IsWindows())
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var folder = Path.GetDirectoryName(filePath)!;
        var parent = Path.GetDirectoryName(folder)!;
        var readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        System.IO.File.SetUnixFileMode(folder, readOnly);
        System.IO.File.SetUnixFileMode(parent, readOnly);
        var kept = new WritableAgain(folder, parent);

        var probe = Path.Combine(folder, ".keep-in-place-probe");
        try
        {
            System.IO.File.WriteAllBytes(probe, []);
        }
        catch (UnauthorizedAccessException)
        {
            return kept;
        }

        System.IO.File.Delete(probe);
        kept.Dispose();
        throw new InvalidOperationException(
            $"The fixture took write permission from {folder} and it can still be written. A test that says " +
            "the file system refused a move would be proving nothing. This happens when the account running " +
            "the tests can write anything on the machine.");
    }

    /// <summary>Gives write permission back to the folders <see cref="KeepInPlace"/> took it from.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private sealed class WritableAgain(params string[] folders) : IDisposable
    {
        public void Dispose()
        {
            foreach (var folder in folders)
            {
                System.IO.File.SetUnixFileMode(
                    folder,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Give back every permission taken away, remove every link, and delete the tree.</summary>
    public void Dispose()
    {
        foreach (var folder in _deniedFolders) AllowListing(folder);
        foreach (var link in _links)
        {
            // Removed as a link. Deleting the tree around it would work too, but taking the links out
            // first means a fixture can never reach through one into a folder it did not create.
            if (Directory.Exists(link)) Directory.Delete(link);
        }

        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static bool CanList(string folder)
    {
        try
        {
            Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void MakeJunction(string link, string target) =>
        // The junction a person would make with mklink /J, made in this process: a test starts no process.
        DirectoryJunction.Create(link, target);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DenyListingOnWindows(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        var account = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The fixture cannot read the account it is running as.");

        security.AddAccessRule(new FileSystemAccessRule(
            account,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void AllowListing(string folder)
    {
        if (!Directory.Exists(folder)) return;

        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            var account = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The fixture cannot read the account it is running as.");

            security.RemoveAccessRule(new FileSystemAccessRule(
                account,
                FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                AccessControlType.Deny));
            info.SetAccessControl(security);
        }
        else
        {
            System.IO.File.SetUnixFileMode(
                folder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
