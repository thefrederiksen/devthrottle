using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests.Browsers;

/// <summary>
/// The two answers a browser create or start gives before any process is launched: which locations
/// were checked when the browser was not found, and why a Linux launch with no display cannot work.
/// Both exist because the alternative was a bare "not installed" on a machine that had the browser
/// (#3741), and a twenty-second timeout for a window that could never open.
/// </summary>
public sealed class AutomationBrowserServiceLaunchGuardTests
{
    [Fact]
    public void NotInstalledMessage_NamesEveryPathThatWasChecked()
    {
        var paths = new[] { "/opt/google/chrome/chrome", "/usr/bin/google-chrome-stable", "/usr/bin/google-chrome" };

        var message = AutomationBrowserService.NotInstalledMessage(BrowserKind.Chrome, paths);

        Assert.Contains("Chrome is not installed", message);
        Assert.All(paths, p => Assert.Contains(p, message));
    }

    [Fact]
    public void NotInstalledMessage_NoTableForThisPlatform_SaysSoInsteadOfAnEmptyList()
    {
        var message = AutomationBrowserService.NotInstalledMessage(BrowserKind.Edge, Array.Empty<string>());

        Assert.Contains("no install locations are known for this operating system", message);
        Assert.DoesNotContain("Checked:", message);
    }

    [Fact]
    public void CandidatePathsFor_ThisHost_ListsTheExeCandidatesInProbeOrder()
    {
        // On every platform the Director ships for, each browser has at least one location to check;
        // this is the list the "not installed" error repeats back to the person.
        var paths = BrowserLauncher.CandidatePathsFor(BrowserKind.Chrome);

        Assert.NotEmpty(paths);
        Assert.Equal(paths.Distinct().Count(), paths.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void LinuxDisplayProblem_NoDisplayAtAll_NamesBothVariables(string? display, string? wayland)
    {
        var problem = AutomationBrowserService.LinuxDisplayProblem(display, wayland);

        Assert.NotNull(problem);
        Assert.Contains("DISPLAY", problem);
        Assert.Contains("WAYLAND_DISPLAY", problem);
    }

    [Theory]
    [InlineData(":0", null)]
    [InlineData(null, "wayland-0")]
    [InlineData(":1.0", "wayland-0")]
    public void LinuxDisplayProblem_EitherDisplaySet_IsNoProblem(string? display, string? wayland)
    {
        Assert.Null(AutomationBrowserService.LinuxDisplayProblem(display, wayland));
    }
}
