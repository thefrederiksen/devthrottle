using System.Runtime.InteropServices;

namespace CcDirector.Core.Tests;

/// <summary>
/// The long-lived shell a test spawns when it needs a real process on the other end of a terminal and does not
/// care which one. The Gateway spawn suites (FleetSpawnOrigin, FleetSpawnMissionAttach, WorkflowSeat) hard-coded
/// <c>cmd</c> with <c>/k</c>, so off Windows their spawn failed and they proved nothing; they use this instead.
/// </summary>
internal static class TestShell
{
    public static string Path => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "cmd.exe"
        : "/bin/sh";

    /// <summary>Arguments that keep the shell running rather than exiting at once: <c>/k</c> on Windows; a POSIX
    /// shell handed a terminal and no script stays interactive on its own.</summary>
    public static string Args => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "/k"
        : string.Empty;
}
