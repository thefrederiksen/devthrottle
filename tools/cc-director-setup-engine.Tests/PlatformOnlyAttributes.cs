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
/// A fact that needs a real zsh to run, which SKIPS - visibly, with a stated reason - where there is
/// none, rather than reporting a pass for a test that never exercised a shell. The same discipline as
/// <see cref="WindowsOnlyTheoryAttribute"/>.
///
/// It exists because the defect in #4292 was ENTIRELY about which start-up file zsh reads for which
/// kind of shell. No assertion about the list of file names can catch that; only running zsh can. So
/// where zsh is absent this must say it did not run, and never quietly agree.
/// </summary>
public sealed class ZshOnlyFactAttribute : FactAttribute
{
    public ZshOnlyFactAttribute(string because)
    {
        if (!File.Exists("/bin/zsh"))
            Skip = "needs /bin/zsh: " + because;
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
