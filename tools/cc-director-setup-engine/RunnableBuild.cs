namespace CcDirector.Setup.Engine;

/// <summary>
/// Makes a single-file build that was just downloaded or just placed actually startable on this
/// platform.
///
/// WHY THIS IS NOT PART OF THE COPY. <see cref="InstallSwapper"/> is plain file moves, deliberately, so
/// its ordering and rollback can be exercised and proved on any platform. Whether the resulting file
/// may be EXECUTED is a different question with a different answer per platform, and on Windows it has
/// no answer at all.
///
/// THE TWO THINGS UNIX NEEDS, AND WHY EACH IS ITS OWN FAILURE. A file arriving over HTTP has no execute
/// bit, so without <c>chmod</c> the swap succeeds and the launcher never starts again - the file is
/// there, it is the right bytes, and it is not a program. On macOS there is a second gate: Gatekeeper
/// refuses a quarantined copy of a binary that is ad-hoc signed and never notarized (there is no Apple
/// developer account), and the quarantine attribute can only be stripped BEFORE first launch. Miss
/// either and the failure reads as a bad build, which is exactly the misdiagnosis that gets a good
/// version rolled back and pinned.
///
/// This is the same preparation <see cref="CcDirector.Core.Update.DirectorBuildSwapper"/> already does
/// for the Director's macOS bundle. The launcher is a bare executable rather than a bundle, so the
/// steps are the two above rather than the bundle's.
/// </summary>
public static class RunnableBuild
{
    /// <summary>
    /// Give <paramref name="path"/> whatever this platform needs before it can be started. A no-op on
    /// Windows.
    ///
    /// BEST EFFORT, AND DELIBERATELY NOT FATAL. A failure here is reported and the caller's health
    /// check is what decides whether the build actually works - because the health check asks the
    /// question that matters (did the new version come up and answer) rather than this one (did a
    /// preparatory step return zero). Throwing here would fail a swap that might have been fine and
    /// skip the rollback path that handles the case where it was not.
    /// </summary>
    public static void Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows()) return;
        if (!File.Exists(path))
        {
            EngineLog.Write($"[RunnableBuild] nothing to prepare at {path}");
            return;
        }

        // macOS first: the quarantine attribute has to go before the file is ever launched, and a
        // failure to remove it looks identical to a build that crashes on startup.
        if (OperatingSystem.IsMacOS())
        {
            var (exit, output) = ProcessRunner.Run("/usr/bin/xattr", $"-dr com.apple.quarantine \"{path}\"");
            // A file that was never quarantined has no attribute to remove and says so non-zero. That
            // is the normal case for a build we produced locally, so it is noted rather than warned
            // about; what matters is that the attribute is not there afterwards either way.
            EngineLog.Write($"[RunnableBuild] xattr quarantine strip on {path} -> exit={exit} {output?.Trim()}");
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            EngineLog.Write($"[RunnableBuild] {path} is executable");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[RunnableBuild] could not set the execute bit on {path}: {ex.Message}");
        }
    }
}
