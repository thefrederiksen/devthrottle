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
}
