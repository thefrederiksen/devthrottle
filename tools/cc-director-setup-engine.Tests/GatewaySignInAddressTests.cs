using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3504: the browser sign-in must hand its address to the screen, so the person has a way in when
/// Windows opens no browser.
///
/// Opening a link goes through the Windows shell. On a machine where a second browser was just installed, the
/// shell answers with its own "Select an app to open this link" chooser, which disappears when focus moves.
/// Then no browser opens, and the wizard waited with "Finish signing in in your browser" and nothing to sign in
/// to - the address existed only in the log. These drive the REAL default sign-in (the real loopback listener,
/// the real sign-in address) with only the browser replaced, so what is asserted is what the screen receives.
/// </summary>
[Collection(HostedGatewayUrlCollection.Name)]
public class GatewaySignInAddressTests
{
    private const string GatewayUrl = "http://gateway.test:7878";
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string MachineName = "WORKSTATION-1";

    /// <summary>
    /// The address reaches the screen BEFORE the browser is asked to open it, it is the same address, and it is
    /// withdrawn when the wait ends. The order is the point: the shell call is the step that can leave nothing
    /// behind, so the address must already be on screen when it runs.
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_ShowsTheAddressBeforeOpeningTheBrowser_AndWithdrawsItWhenTheWaitEnds()
    {
        var events = new List<string>();
        using var stop = new CancellationTokenSource();
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new RecordingHandler(events, succeed: false),
            persist: (_, _) => throw new InvalidOperationException("nothing may be saved"),
            signInAddressDisplay: new RecordingDisplay(events),
            // Windows' chooser was dismissed: the open call returns and no browser ever arrives.
            openBrowser: url => { events.Add("opened " + url); stop.Cancel(); });

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, stop.Token);

        Assert.False(result.Success);
        Assert.Equal(3, events.Count);
        Assert.StartsWith("shown ", events[0]);
        var shown = events[0]["shown ".Length..];
        Assert.Equal("opened " + shown, events[1]);
        Assert.Equal("withdrawn", events[2]);
        Assert.StartsWith("http://127.0.0.1:", RedirectUriOf(shown));
    }

    /// <summary>
    /// With NO browser opened by the shell, the loopback listener behind the shown address is live: a hand-back
    /// to the shown address's own callback - what devthrottle.com sends after a person signs in there -
    /// completes the sign-in and the machine enrolls. The address is withdrawn as soon as the hand-back lands,
    /// BEFORE enrollment goes on, because the listener closes then and the address would be a dead way in.
    /// (This proves the listener the address names; the devthrottle.com page itself is outside this repository.)
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_WhenNoBrowserOpens_AHandBackToTheShownAddressCompletesTheSignIn()
    {
        var events = new List<string>();
        var saved = new List<(string url, string key)>();
        var display = new RecordingDisplay(events);
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new RecordingHandler(events, succeed: true),
            persist: (url, key) => saved.Add((url, key)),
            signInAddressDisplay: display,
            openBrowser: _ => { /* the chooser was dismissed: nothing opens */ });

        var enrolling = runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);
        await HandBackToTheShownAddressAsync(display);
        var result = await enrolling.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { (GatewayUrl, "local-key-abc") }, saved);
        var withdrawn = events.IndexOf("withdrawn");
        var firstCall = events.FindIndex(e => e.StartsWith("http "));
        Assert.True(withdrawn >= 0 && firstCall > withdrawn,
            "the address must be withdrawn before enrollment goes on: " + string.Join(" | ", events));
    }

    /// <summary>
    /// Windows cannot open a browser at all (no handler for the link). With the address on screen that is NOT the
    /// end of the sign-in: the screen is told the browser did not open, the wait goes on, and the address still
    /// completes the sign-in. This is the case the address exists for.
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_WhenWindowsCannotOpenABrowser_TheScreenIsToldAndTheAddressStillWorks()
    {
        var events = new List<string>();
        var display = new RecordingDisplay(events);
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new RecordingHandler(events, succeed: true),
            persist: (_, _) => { },
            signInAddressDisplay: display,
            openBrowser: _ => throw new System.ComponentModel.Win32Exception("No application is associated with the specified file"));

        var enrolling = runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);
        await HandBackToTheShownAddressAsync(display);
        var result = await enrolling.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("browser did not open: No application is associated with the specified file", events);
    }

    /// <summary>
    /// With no screen to show the address (the command line), a browser that cannot be opened ends the sign-in
    /// at once - waiting five minutes for an address nobody can see would be the original defect.
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_WithNoScreen_ABrowserThatCannotOpenEndsTheSignIn()
    {
        var events = new List<string>();
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new RecordingHandler(events, succeed: true),
            persist: (_, _) => throw new InvalidOperationException("nothing may be saved"),
            openBrowser: _ => throw new System.ComponentModel.Win32Exception("No application is associated with the specified file"));

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Contains("Sign-in did not complete", result.ErrorMessage);
        Assert.Empty(events);
    }

    private static async Task HandBackToTheShownAddressAsync(RecordingDisplay display)
    {
        var address = await display.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var browser = new HttpClient();
        var callback = RedirectUriOf(address) + "?access_token=access-xyz&refresh_token=refresh-xyz";
        using var landed = await browser.GetAsync(callback);
    }

    private static string RedirectUriOf(string signInAddress)
    {
        var query = new Uri(signInAddress).Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "redirect_uri")
                return Uri.UnescapeDataString(parts[1]);
        }
        throw new InvalidOperationException("The sign-in address carries no redirect_uri: " + signInAddress);
    }

    private sealed class RecordingDisplay(List<string> events) : ISignInAddressDisplay
    {
        public TaskCompletionSource<string> Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Show(string address) { lock (events) events.Add("shown " + address); Shown.TrySetResult(address); }
        public void BrowserDidNotOpen(string reason) { lock (events) events.Add("browser did not open: " + reason); }
        public void Withdraw() { lock (events) events.Add("withdrawn"); }
    }

    /// <summary>Records each HTTP call; when <c>succeed</c>, the cloud registers the device and the gateway
    /// exchanges its key for a local one.</summary>
    private sealed class RecordingHandler(List<string> events, bool succeed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (events) events.Add("http " + path);
            if (!succeed)
                throw new InvalidOperationException("No HTTP call is expected: " + request.RequestUri);
            if (path.EndsWith("/devices/register"))
                return Task.FromResult(Json(HttpStatusCode.Created,
                    "{\"data\":{\"device_key\":\"cloud-key-abc\",\"record\":{\"id\":\"cloud-dev-1\",\"name\":\"" + MachineName + "\"}}}"));
            if (path == "/mobile/enroll")
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { deviceKey = "local-key-abc" })));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
