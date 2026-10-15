using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// A test of behaviour only Linux has - reading the real /proc and /sys - which SKIPS, visibly and with
/// its reason, everywhere else. The same reasoning as <see cref="WindowsOnlyFactAttribute"/>: an early
/// return would report a pass for a test that proved nothing.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux-only: " + because;
    }
}
