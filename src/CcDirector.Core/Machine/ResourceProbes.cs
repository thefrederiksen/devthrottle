using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// The resource probe for the machine the Director is running on (the Resource Monitor mission, issue #2811).
/// The one place the operating system decides which probe the Director reads.
/// </summary>
public static class ResourceProbes
{
    /// <summary>The probe for this operating system.</summary>
    public static IResourceProbe ForThisMachine()
    {
        if (OperatingSystem.IsLinux())
        {
            FileLog.Write("[ResourceProbes] ForThisMachine: Linux");
            return new LinuxResourceProbe();
        }

        if (OperatingSystem.IsMacOS())
        {
            FileLog.Write("[ResourceProbes] ForThisMachine: macOS");
            return new MacOsResourceProbe();
        }

        if (OperatingSystem.IsWindows())
        {
            FileLog.Write("[ResourceProbes] ForThisMachine: Windows");
            return new WindowsResourceProbe();
        }

        throw new PlatformNotSupportedException(
            $"The resource monitor reads Windows, Linux and macOS; this operating system is {Environment.OSVersion}.");
    }
}
