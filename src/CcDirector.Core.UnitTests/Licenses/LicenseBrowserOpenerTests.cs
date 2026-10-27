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

        /// <summary>The first probe does not answer in time, as on a busy machine; the browser may well be running.</summary>
        public bool SlowFirstProbe { get; set; }

        public List<string> Calls { get; } = new();

        public LicenseBrowserControl Control => new(
            idOrName => idOrName == WorkChrome.Id || idOrName == WorkChrome.Name ? WorkChrome : null,
            (browser, ct) => AutomationBrowserService.StartIfStoppedAsync(browser, (_, _) =>
            {
                var answer = SlowFirstProbe ? BrowserProbe.NoAnswer : Up ? BrowserProbe.Up : BrowserProbe.Refused;
                SlowFirstProbe = false;
                return Task.FromResult(answer);
            }, (b, _) => { Calls.Add($"launch {b.Id}"); Up = true; return Task.CompletedTask; }, ct),
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
    public async Task OpenAsync_ARunningProfileWhoseFirstProbeIsSlow_IsNeitherStartedNorStopped_AndTheRunSaysWhy()
    {
        // The review's case: a probe that did not answer in two seconds read as "not running", so the check started a
        // second window and then closed the session's browser. A probe with no answer is not "stopped".
        var browsers = new Browsers { Up = true, SlowFirstProbe = true };
        var opener = new LicenseBrowserOpener(browsers.Control);

        var refused = await Assert.ThrowsAsync<LicenseCommandLineException>(() => opener.OpenAsync("work-chrome", CancellationToken.None));

        Assert.Contains("did not answer on its debug port in 10 seconds, so it may be running; it was neither started nor stopped", refused.Message);
        Assert.Empty(browsers.Calls);
        Assert.True(browsers.Up);
    }

    [Fact]
    public void ProbeAnswerOf_OnlyARefusedConnectionMeansStopped()
    {
        var refused = new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        var reset = new HttpRequestException("reset", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));

        Assert.Equal(BrowserProbe.Refused, AutomationBrowserService.ProbeAnswerOf(refused));
        Assert.Equal(BrowserProbe.NoAnswer, AutomationBrowserService.ProbeAnswerOf(reset));
        Assert.Equal(BrowserProbe.NoAnswer, AutomationBrowserService.ProbeAnswerOf(new TaskCanceledException("timed out", new TimeoutException())));
        Assert.Equal(BrowserProbe.NoAnswer, AutomationBrowserService.ProbeAnswerOf(new HttpRequestException("no inner")));
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
