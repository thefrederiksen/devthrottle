using System.Diagnostics;
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
/// attributes and <see cref="CloudPlaceholder"/> throws and names the reason, rather than building an
/// ordinary file and letting a test pass on a tree that never held a placeholder.
/// </summary>
public sealed class FixtureTree : IDisposable
{
    private readonly List<string> _deniedFolders = [];
    private readonly List<string> _links = [];

    /// <summary>Build an empty tree in a folder of its own.</summary>
    /// <param name="name">A short name for the test, used in the folder name so a stray tree can be traced.</param>
    public FixtureTree(string name)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
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
    /// </summary>
    /// <param name="relativePath">Where the file goes, below the root.</param>
    /// <param name="sizeInBytes">The size the file claims.</param>
    /// <exception cref="PlatformNotSupportedException">This platform does not keep the offline mark.</exception>
    public string CloudPlaceholder(string relativePath, int sizeInBytes)
    {
        var full = File(relativePath, sizeInBytes);
        System.IO.File.SetAttributes(full, System.IO.File.GetAttributes(full) | FileAttributes.Offline);

        if ((new FileInfo(full).Attributes & FileAttributes.Offline) == 0)
        {
            throw new PlatformNotSupportedException(
                "This fixture needs a file system that keeps the offline attribute, which is where a " +
                "cloud placeholder is marked. This platform does not keep it, so the proof that a " +
                "placeholder is counted apart from the bytes on the disk cannot be built here. The " +
                "same decision is proven on every platform by EntryClassifierTests.");
        }

        return full;
    }

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

    private static void MakeJunction(string link, string target)
    {
        // There is no managed call that makes a junction, so this asks Windows for one the way a
        // person would. It is fixture code and never ships.
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The fixture could not start cmd.exe to make a junction.");

        var error = process.StandardError.ReadToEnd().Trim();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The fixture could not make a junction at {link}: exit code {process.ExitCode}. {error}");
        }
    }

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
