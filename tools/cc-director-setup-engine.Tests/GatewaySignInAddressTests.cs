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
    /// The address reaches the screen BEFORE the browser is asked to open it, and it is the same address. The
    /// order is the point: the shell call is the step that can leave nothing behind, so the address must already
    /// be on screen when it runs.
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_ShowsTheSignInAddress_BeforeAskingTheBrowserToOpenIt()
    {
        var events = new List<string>();
        using var stop = new CancellationTokenSource();
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new NoHttpHandler(),
            persist: (_, _) => throw new InvalidOperationException("nothing may be saved"),
            showSignInAddress: url => events.Add("shown " + url),
            // Windows' chooser was dismissed: the open call returns and no browser ever arrives.
            openBrowser: url => { events.Add("opened " + url); stop.Cancel(); });

        var result = await runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, stop.Token);

        Assert.False(result.Success);
        Assert.Equal(2, events.Count);
        Assert.StartsWith("shown ", events[0]);
        Assert.StartsWith("opened ", events[1]);
        var shown = events[0]["shown ".Length..];
        Assert.Equal(shown, events[1]["opened ".Length..]);
        Assert.StartsWith("http://127.0.0.1:", RedirectUriOf(shown));
    }

    /// <summary>
    /// The address on screen is a working way in, not a decoration: with NO browser opened by the shell, a
    /// person who takes the shown address to a browser of their own completes the sign-in, and the machine
    /// enrolls. The "browser" here follows the shown address's own callback, exactly as devthrottle.com does
    /// after a sign-in.
    /// </summary>
    [Fact]
    public async Task DefaultSignIn_WhenNoBrowserOpens_TheShownAddressStillCompletesTheSignIn()
    {
        var saved = new List<(string url, string key)>();
        var shownAddress = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatewayAccountEnrollRunner(
            handlerFactory: () => new EnrollSucceedsHandler(),
            persist: (url, key) => saved.Add((url, key)),
            showSignInAddress: url => shownAddress.TrySetResult(url),
            openBrowser: _ => { /* the chooser was dismissed: nothing opens */ });

        var enrolling = runner.VerifyAndSaveAsync(GatewayUrl, DeviceId, MachineName, CancellationToken.None);

        var address = await shownAddress.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var personsBrowser = new HttpClient())
        {
            var callback = RedirectUriOf(address) + "?access_token=access-xyz&refresh_token=refresh-xyz";
            using var landed = await personsBrowser.GetAsync(callback);
        }
        var result = await enrolling.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { (GatewayUrl, "local-key-abc") }, saved);
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

    private sealed class NoHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No HTTP call is expected: " + request.RequestUri);
    }

    /// <summary>The cloud registers the device and the gateway exchanges its key for a local one.</summary>
    private sealed class EnrollSucceedsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
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
