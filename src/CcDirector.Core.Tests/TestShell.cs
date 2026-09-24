using System.Runtime.InteropServices;

namespace CcDirector.Core.Tests;

/// <summary>
/// The long-lived shell a test spawns when it needs a real process on the other end of a terminal and
/// does not care which one.
///
/// WHY THE ARGUMENTS BELONG HERE TOO. Three Gateway suites hard-coded <c>cmd</c> with <c>/k</c> - the
/// Windows way to keep a shell open. On macOS and Linux there is no <c>cmd</c>, so the spawn simply
/// failed and eight tests about spawn ORIGIN, mission attachment and workflow seats reported a
/// spawn Error instead of proving anything. They were not testing Windows behaviour; their fixture was
/// just written Windows-shaped, which is the case the WindowsOnlyFact attribute explicitly says to fix
/// rather than skip.
/// </summary>
internal static class TestShell
{
    public static string Path => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "cmd.exe"
        : "/bin/sh";

    /// <summary>Arguments that keep the shell running rather than exiting immediately. On Windows that
    /// is <c>/k</c>; a POSIX shell handed a terminal and no script stays interactive on its own.</summary>
    public static string Args => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "/k"
        : string.Empty;
}
