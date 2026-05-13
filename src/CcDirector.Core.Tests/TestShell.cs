using System.Runtime.InteropServices;

namespace CcDirector.Core.Tests;

internal static class TestShell
{
    public static string Path => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "cmd.exe"
        : "/bin/sh";
}
