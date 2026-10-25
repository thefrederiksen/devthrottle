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

/// <summary>
/// A fact about behaviour only macOS and Linux have, which SKIPS - visibly, with a stated reason - on
/// Windows, rather than failing for a platform it was never about. The same discipline as
/// <see cref="WindowsOnlyTheoryAttribute"/>.
///
/// For a test that needs real symlink semantics: the retired-launcher purge proves a Unix shim by where
/// its link points (#3147), and a Windows run takes the text-shim branch instead, so these tests would
/// be asserting about code that did not run.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute(string because)
    {
        if (OperatingSystem.IsWindows())
            Skip = "macOS/Linux-only: " + because;
    }
}

/// <summary>
/// A fact about behaviour only macOS has, which SKIPS - visibly, with a stated reason - everywhere
/// else, rather than failing for a platform it was never about. The same discipline as
/// <see cref="WindowsOnlyTheoryAttribute"/>.
///
/// For a test that genuinely needs the real macOS: one that shells out to <c>plutil</c>, say. A test
/// that merely ASSERTS ABOUT macOS should name the platform and run everywhere instead - see
/// MacInstallLocationTests, which does exactly that.
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS-only: " + because;
    }
}
