namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// MAKE A RENAME OF ONE FILE IMPOSSIBLE, by whatever means this operating system actually has, and undo it
/// on dispose.
///
/// WHY THIS EXISTS. Two legacy-import tests need the post-commit rename-aside to FAIL so they can prove the
/// next construction recovers it. Both used to arrange that by holding the file open with
/// <c>FileShare.Read</c>, which permits the import's read and denies the move - and that is a WINDOWS
/// mechanism. macOS and Linux do not enforce .NET's sharing modes at all, so the rename quietly succeeded,
/// the state the tests are about was never built, and both reported a PRODUCT failure on a recovery path
/// that is the same platform-neutral code everywhere. The fault was in how the failure was induced, not in
/// what was being tested.
///
/// So each system induces it its own way, and both are real failures the product genuinely meets:
/// on Windows a file someone else is holding open, and on macOS and Linux a directory the account may not
/// write - a POSIX rename needs write permission on the containing directory, not on the file.
///
/// THE BLOCK IS PROVED, NOT ASSUMED. The constructor attempts a real rename and requires it to fail. Without
/// that, a system where the block did not take would run the test against a rename that succeeded and report
/// the result as a verdict on the product. That is the exact failure this type was written to remove, so it
/// is not left to the caller to remember.
///
/// ON UNIX THE FILE MUST HAVE ITS DIRECTORY TO ITSELF, because the whole directory is closed to writing.
/// The callers put the legacy file in a subdirectory of their own for that reason; a database or any other
/// file sharing it would be shut out too.
/// </summary>
internal sealed class BlockedRename : IDisposable
{
    private readonly string _directory;
    private readonly FileStream? _windowsHold;
    private readonly UnixFileMode _originalMode;

    internal BlockedRename(string path)
    {
        _directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"'{path}' has no directory to close off.", nameof(path));

        if (OperatingSystem.IsWindows())
        {
            // A read is still allowed, so the import can parse the file; File.Move needs delete-sharing,
            // which this does not grant.
            _windowsHold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else
        {
            _originalMode = File.GetUnixFileMode(_directory);
            // Read and traverse, but not write: the file can still be opened and read, and nothing in the
            // directory can be created, removed or renamed.
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        ProveTheBlockTook(path);
    }

    /// <summary>
    /// Rename the file for real and require the operating system to refuse. An exception here is the
    /// evidence; a success is a fixture that did not build what it claims, and it says so and puts the file
    /// back rather than letting the test proceed against the wrong state.
    /// </summary>
    private void ProveTheBlockTook(string path)
    {
        var probe = path + ".block-probe";
        try
        {
            File.Move(path, probe);
        }
        catch (IOException)
        {
            return;     // refused, which is the whole point
        }
        catch (UnauthorizedAccessException)
        {
            return;     // refused, which is the whole point
        }

        // It was NOT refused. Undo the probe, let the block go, and fail with the reason.
        Release();
        File.Move(probe, path);
        throw new InvalidOperationException(
            $"This host renamed '{path}' even though the test had closed it off "
            + (OperatingSystem.IsWindows()
                ? "by holding it open without delete-sharing. "
                : $"by removing write permission from '{_directory}'. Running the suite as the superuser "
                  + "would do this, because the superuser bypasses directory permissions. ")
            + "The failed-rename state this fixture exists to build was not built, so the test after it "
            + "would have been a verdict on the wrong thing. It must not be skipped into a green run.");
    }

    private void Release()
    {
        _windowsHold?.Dispose();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, _originalMode);
    }

    public void Dispose() => Release();
}
