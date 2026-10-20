using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// A theory about behaviour only Windows has, which SKIPS - visibly, with a stated reason -
/// everywhere else, rather than failing for a platform it was never about or returning early and
/// reporting a pass for a theory that asserted nothing. The same discipline as
/// CcDirector.Core.Tests.WindowsOnlyFactAttribute and CcDirector.Gateway.Tests' pair; this project
/// had no equivalent, so Windows-shaped expectations here simply ran and failed on every Mac.
///
/// Not for a test that is merely written Windows-shaped and could be written platform-neutrally
/// instead - see InstallLayoutTests, which names the platform it asserts about and therefore runs
/// everywhere.
/// </summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: " + because;
    }
}
