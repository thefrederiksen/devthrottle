using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A test of behaviour only Windows has, which SKIPS - visibly, with a stated reason - everywhere else, rather
/// than returning early and reporting a pass for a test that asserted nothing (the same discipline as
/// CcDirector.Core.Tests.WindowsOnlyFactAttribute). Not for a test that is merely written Windows-shaped.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: " + because;
    }
}

/// <summary>A test of behaviour only Linux has; SKIPS visibly, with its reason, everywhere else.</summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux-only: " + because;
    }
}
