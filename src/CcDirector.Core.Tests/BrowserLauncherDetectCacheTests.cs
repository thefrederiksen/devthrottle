using System;
using System.Linq;
using CcDirector.Core.Browsers;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// DetectBrowsers reads the disk once and reuses the answer: the browsers rail asks for it every 30
/// seconds. A forced probe reads the disk now, which is what the first-run wizard, the Browsers settings
/// page and the picker do, so a browser installed or removed a moment ago is seen without a restart.
/// The exists check is swapped so an install and a removal can be simulated.
/// </summary>
[Collection("BrowserLauncherDetectCache")]
public sealed class BrowserLauncherDetectCacheTests : IDisposable
{
    private readonly Func<string, bool> _realExists = BrowserLauncher.ExeExists;

    public void Dispose()
    {
        BrowserLauncher.ExeExists = _realExists;
        BrowserLauncher.DetectBrowsers(forceProbe: true);
    }

    [Fact]
    public void DetectBrowsers_CalledTwice_ReturnsTheSameCachedList()
    {
        var first = BrowserLauncher.DetectBrowsers(forceProbe: true);
        var second = BrowserLauncher.DetectBrowsers();

        Assert.Same(first, second);
    }

    [Fact]
    public void DetectBrowsers_AfterAnInstall_TheCachedAnswerIsStaleUntilAForcedProbe()
    {
        BrowserLauncher.ExeExists = _ => false;
        var none = BrowserLauncher.DetectBrowsers(forceProbe: true);
        Assert.Empty(none);

        // A browser is installed: every candidate path now exists.
        BrowserLauncher.ExeExists = _ => true;
        Assert.Same(none, BrowserLauncher.DetectBrowsers());

        var installed = BrowserLauncher.DetectBrowsers(forceProbe: true);
        Assert.NotEmpty(installed);
        Assert.Contains(installed, b => b.Kind == BrowserKind.Chrome);
        Assert.Same(installed, BrowserLauncher.DetectBrowsers());
    }

    [Fact]
    public void DetectBrowsers_AfterARemoval_AForcedProbeSeesTheBrowserGone()
    {
        BrowserLauncher.ExeExists = _ => true;
        var installed = BrowserLauncher.DetectBrowsers(forceProbe: true);
        Assert.NotEmpty(installed);

        BrowserLauncher.ExeExists = _ => false;
        var gone = BrowserLauncher.DetectBrowsers(forceProbe: true);
        Assert.Empty(gone);
        Assert.NotSame(installed, gone);
    }
}
