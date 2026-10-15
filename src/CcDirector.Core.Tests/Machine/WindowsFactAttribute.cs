using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// A fact that reads the real Windows operating system. Anywhere else it is reported as SKIPPED with the
/// reason - never as passed, because a live test that did not run proved nothing and must not look as if
/// it did.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "reads the real Windows operating system; this machine is not Windows";
    }
}
