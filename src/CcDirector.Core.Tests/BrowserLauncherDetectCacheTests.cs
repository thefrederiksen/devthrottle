using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// DetectBrowsers probes the disk once per process: the browsers rail asks for it every 30 seconds,
/// and installations do not change while the Director runs.
/// </summary>
public sealed class BrowserLauncherDetectCacheTests
{
    [Fact]
    public void DetectBrowsers_CalledTwice_ReturnsTheSameCachedList()
    {
        var first = BrowserLauncher.DetectBrowsers();
        var second = BrowserLauncher.DetectBrowsers();

        Assert.Same(first, second);
    }
}
