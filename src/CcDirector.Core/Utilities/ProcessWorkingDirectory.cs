namespace CcDirector.Core.Utilities;

/// <summary>
/// The process's own working directory, for the callers that only want it as a starting point or a
/// last resort - and which must not die when the operating system refuses to name it.
///
/// WHY THIS EXISTS
/// ---------------
/// On macOS a directory can be deleted while a process is sitting in it, and <c>getcwd</c> then fails
/// with ENOENT, which .NET surfaces as <see cref="FileNotFoundException"/> from
/// <see cref="Environment.CurrentDirectory"/>. The Director walks straight into this every time it
/// updates itself: the updater replaces the application bundle and restarts the Director, and the new
/// process inherits a working directory inside the bundle directory that has just been replaced.
/// Windows cannot reach this state at all - it refuses to delete a directory that is a running
/// process's working directory - so the read looks completely safe when it is written and read.
///
/// Measured on 2026-10-19: a Director that had auto-updated minutes earlier threw
/// <c>FileNotFoundException at Interop.Sys.GetCwd()</c> from <c>SkillToolLinker.DefaultSkillRoots</c>
/// the moment the user clicked Settings, as an UNHANDLED user-interface exception - so the Settings
/// dialog simply never opened, with nothing shown to say why, until the Director was next restarted
/// from a path that still existed.
///
/// This is not a fallback that hides a problem: there is no problem to hide. A process is allowed to
/// have no usable working directory, the callers here all have a better answer available, and the one
/// that does not simply drops an optional search location. The failure is logged either way, and only
/// the two exceptions that MEAN "the system will not name it" are absorbed - anything else propagates.
/// </summary>
public static class ProcessWorkingDirectory
{
    /// <summary>
    /// The working directory, or null when the operating system will not say what it is.
    /// </summary>
    public static string? CurrentOrNull()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately NOT a blanket catch. These two are the operating system saying it cannot
            // name the directory - FileNotFoundException and DirectoryNotFoundException are both
            // IOException, and a directory that exists but cannot be reached is the access one.
            // Anything else coming out of this property would be a fault we do not understand, and
            // swallowing it would be the fallback programming the coding rules forbid, so it is left
            // to propagate.
            // not-an-error: a process is allowed to have no readable working directory - the case this helper exists to answer
            FileLog.Write($"[ProcessWorkingDirectory] the working directory cannot be read, so it is not used: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The working directory, or the directory the application was loaded from when it cannot be read.
    /// For callers that must hand SOME existing directory on - a child process to start in, for
    /// instance. <see cref="AppContext.BaseDirectory"/> is a reasonable stand-in because it is where
    /// this executable lives, and a process that is running has one.
    /// </summary>
    public static string CurrentOrAppDirectory() => CurrentOrNull() ?? AppContext.BaseDirectory;

    /// <summary>
    /// Is <paramref name="workingDirectory"/> the file-system root - what launchd hands a process it
    /// starts for the desktop? Pure, so the rule is testable without moving this process.
    /// </summary>
    internal static bool IsFilesystemRoot(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return false;

        var full = Path.GetFullPath(workingDirectory);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal);
    }

    /// <summary>
    /// Move out of the file-system root when a desktop launch left us there, to the directory this
    /// executable was loaded from. Returns true when the directory was changed.
    ///
    /// WHY. launchd gives a process it starts for the desktop <c>/</c> as its working directory. The
    /// macOS bundle used to name a <c>#!/bin/bash</c> wrapper as its <c>CFBundleExecutable</c>, and that
    /// wrapper ran <c>cd "$DIR"</c> before exec'ing the real binary; the wrapper is gone because it made
    /// the bundle's code identity Apple's copy of bash (issue #4518), and this is the other half of what
    /// it did. Without it, every caller of <see cref="CurrentOrAppDirectory"/> reads <c>/</c>
    /// SUCCESSFULLY and hands it on: a new session's working directory defaults to the file-system root
    /// instead of somewhere inside the install, and the skill search starts at <c>/</c>.
    ///
    /// Deliberately narrow: ONLY the root, and only when it really is the working directory. A Director
    /// started from a terminal is already somewhere deliberate and is left alone.
    /// </summary>
    public static bool LeaveTheFilesystemRoot()
    {
        if (!OperatingSystem.IsMacOS()) return false;

        var current = CurrentOrNull();
        if (!IsFilesystemRoot(current)) return false;

        try
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            FileLog.Write(
                "[ProcessWorkingDirectory] LeaveTheFilesystemRoot: a desktop launch left the working "
                + $"directory at '{current}'; moved to {AppContext.BaseDirectory}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not-an-error: the directory this executable came from can be gone after an update, and the callers all have a fallback
            FileLog.Write(
                $"[ProcessWorkingDirectory] LeaveTheFilesystemRoot: could not move out of '{current}': {ex.Message}");
            return false;
        }
    }
}
