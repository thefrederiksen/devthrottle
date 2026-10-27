using CcDirector.Core.Browsers;
using CcDirector.Core.Licenses;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// The Director's own start of a browser profile for a run of the license tool. The daily approver check reads every
/// profile on the machine while sessions and night-time factories may be driving them (the Delivery Lead's ruling on
/// step 4): a profile already running is read where it is and left running; a stopped one is started, read, and
/// stopped again.
/// </summary>
public sealed class LicenseBrowserOpenerTests
{
    private static readonly AutomationBrowser WorkChrome =
        new("work-chrome", "Work Chrome", BrowserKind.Chrome, "unused-user-data-dir", 0, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null);

    private sealed class Browsers
    {
        public bool Up { get; set; }
        public List<string> Calls { get; } = new();

        public LicenseBrowserControl Control => new(
            idOrName => idOrName == WorkChrome.Id || idOrName == WorkChrome.Name ? WorkChrome : null,
            (_, _) => Task.FromResult(Up),
            (id, _) => { Calls.Add($"launch {id}"); Up = true; return Task.CompletedTask; },
            _ => new AttachInfo("work-chrome", "http://work-chrome.attach.example"),
            (id, _) => { Calls.Add($"stop {id}"); Up = false; return Task.CompletedTask; });
    }

    [Fact]
    public async Task OpenAsync_AProfileAlreadyRunning_IsNeitherLaunchedNorStopped()
    {
        var browsers = new Browsers { Up = true };
        var opener = new LicenseBrowserOpener(browsers.Control);

        await using (var opened = await opener.OpenAsync("Work Chrome", CancellationToken.None))
            Assert.Equal(("work-chrome", "http://work-chrome.attach.example"), (opened.BuName, opened.BuCdpUrl));

        Assert.Empty(browsers.Calls);
        Assert.True(browsers.Up);
    }

    [Fact]
    public async Task OpenAsync_AProfileThatWasStopped_IsStartedForTheRun_AndStoppedAgainOnce()
    {
        var browsers = new Browsers { Up = false };
        var opener = new LicenseBrowserOpener(browsers.Control);

        var opened = await opener.OpenAsync("work-chrome", CancellationToken.None);
        Assert.Equal(new[] { "launch work-chrome" }, browsers.Calls);
        await opened.DisposeAsync();
        await opened.DisposeAsync();

        Assert.Equal(new[] { "launch work-chrome", "stop work-chrome" }, browsers.Calls);
        Assert.False(browsers.Up);
    }

    [Fact]
    public async Task OpenAsync_NoSuchProfile_SaysSo_AndStartsNothing()
    {
        var browsers = new Browsers();
        var opener = new LicenseBrowserOpener(browsers.Control);

        var refused = await Assert.ThrowsAsync<LicenseCommandLineException>(() => opener.OpenAsync("home", CancellationToken.None));

        Assert.Equal("No browser profile \"home\" is on this machine.", refused.Message);
        Assert.Empty(browsers.Calls);
    }
}
