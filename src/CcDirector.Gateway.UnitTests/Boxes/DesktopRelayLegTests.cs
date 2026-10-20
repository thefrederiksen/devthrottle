using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Boxes;
using CcDirector.Gateway.Boxes;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// Factory Boxes (issue #4258), phase 1: the desktop relay leg, browser to box and back, through the real sign-in
/// gate, route, legs, registry, pump and Director handler (see <see cref="DesktopRelayRig"/>). Fakes only: in-memory
/// hosts, an in-memory tunnel, a fake clock.
/// </summary>
public sealed class DesktopRelayLegTests : IAsyncLifetime
{
    private DesktopRelayRig _rig = null!;

    public async Task InitializeAsync() => _rig = await DesktopRelayRig.StartAsync();

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    // ------------------------------------------------------------------- who may open, and where ----

    [Fact]
    public async Task Ticket_SignedOut_IsRefused401()
    {
        var (response, url) = await _rig.AskForTicketAsync(key: null).Within();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(url);
    }

    [Fact]
    public async Task Ticket_SignedIn_AnswersTheDesktopHostAddressWithATicket()
    {
        var (response, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("http://desktop.test/boxes/box-one/desktop/?ticket=", url);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task Ticket_AnotherAccountsBox_Is404AndNoTicketIsMinted()
    {
        // Account B is signed in and has Boxes on, but box-one is hosted only in account A.
        var (response, url) = await _rig.AskForTicketAsync(_rig.KeyB).Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(url);
        Assert.Contains(_rig.Hosts.Asked, a => a.Tenant == _rig.TenantB && a.Box == "box-one");
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task Ticket_BoxesFlagOff_Is404WithNoBody()
    {
        _rig.Feature.Enabled.Clear();

        var (response, _) = await _rig.AskForTicketAsync(_rig.KeyA).Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Empty(_rig.Hosts.Asked);
    }

    [Fact]
    public async Task Ticket_MalformedBoxId_Is400()
    {
        var (response, _) = await _rig.AskForTicketAsync(_rig.KeyA, "Box_One").Within();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TicketExchange_SetsAStrictBoxOnlyCookieAndMovesOnWithoutTheTicket()
    {
        var (_, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();
        using var desktop = _rig.Desktop(cookie: null);

        var opened = await desktop.GetAsync(url).Within();

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var cookie = opened.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        Assert.StartsWith("cc-box-desktop=", cookie);
        Assert.Contains("path=/boxes/box-one/desktop/", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        var page = await opened.Content.ReadAsStringAsync();
        Assert.Contains("url=/boxes/box-one/desktop/\"", page);
        Assert.DoesNotContain("ticket", page);
        Assert.Equal("no-referrer", opened.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(0, _rig.OpenCommands); // the exchange relays nothing
    }

    [Fact]
    public async Task TicketExchange_WipesTheSitesStorageButNotItsCookies()
    {
        // The production desktop host is the Gateway's Azure name: anyone who once opened the Cockpit there has device
        // keys in that origin's storage. The exchange wipes it before any box page loads, and leaves the cookie it sets.
        var (_, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();
        using var desktop = _rig.Desktop(cookie: null);

        var opened = await desktop.GetAsync(url).Within();

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("\"storage\"", opened.Headers.GetValues("Clear-Site-Data").Single());
        Assert.Single(opened.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task TicketExchange_ASecondUse_IsRefused()
    {
        var (_, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();
        using var desktop = _rig.Desktop(cookie: null);
        await desktop.GetAsync(url).Within();

        var again = await desktop.GetAsync(url).Within();

        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.False(again.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TicketExchange_AfterAMinute_IsRefused()
    {
        var (_, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();
        _rig.Time.Advance(BoxDesktopTickets.TicketLifetime + TimeSpan.FromSeconds(1));
        using var desktop = _rig.Desktop(cookie: null);

        var opened = await desktop.GetAsync(url).Within();

        Assert.Equal(HttpStatusCode.Forbidden, opened.StatusCode);
    }

    [Fact]
    public async Task TicketExchange_ATicketForAnotherBox_IsRefused()
    {
        var (_, url) = await _rig.AskForTicketAsync(_rig.KeyA).Within();
        using var desktop = _rig.Desktop(cookie: null);

        var opened = await desktop.GetAsync(url!.Replace("/boxes/box-one/", "/boxes/box-two/")).Within();

        Assert.Equal(HttpStatusCode.Forbidden, opened.StatusCode);
    }

    [Fact]
    public async Task DesktopHost_NoCookie_IsRefused401ForThePageAndTheSocket()
    {
        using var desktop = _rig.Desktop(cookie: null);

        var page = await desktop.GetAsync("/boxes/box-one/desktop/").Within();
        var socket = await Assert.ThrowsAnyAsync<Exception>(() => _rig.OpenDesktopSocketAsync(cookie: null).Within());

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Contains("Open this desktop from the Cockpit", await page.Content.ReadAsStringAsync());
        Assert.Contains("401", socket.Message);
        Assert.Equal(0, _rig.OpenCommands);
        Assert.Empty(_rig.Box.Requests);
    }

    [Fact]
    public async Task DesktopHost_TheGatewaysOwnSignIn_IsNotEnough()
    {
        // The Gateway's sign-in, sent to the desktop host, opens nothing there: only the box cookie does.
        using var desktop = _rig.Desktop($"cc-gateway-token={_rig.KeyA}");
        desktop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _rig.KeyA);

        var page = await desktop.GetAsync("/boxes/box-one/desktop/").Within();

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task DesktopHost_ACookieForOneBox_DoesNotOpenAnother()
    {
        var cookie = await _rig.SignInToDesktopAsync(_rig.KeyA).Within();
        _rig.Hosts.Hosted[(_rig.TenantA.Value, "box-two")] = DesktopRelayRig.DirectorId;
        using var desktop = _rig.Desktop(cookie);

        var other = await desktop.GetAsync("/boxes/box-two/desktop/").Within();

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task DesktopHost_ASessionPastEightHours_IsRefused()
    {
        var cookie = await _rig.SignInToDesktopAsync(_rig.KeyA).Within();
        _rig.Time.Advance(BoxDesktopTickets.SessionLifetime + TimeSpan.FromMinutes(1));
        using var desktop = _rig.Desktop(cookie);

        var page = await desktop.GetAsync("/boxes/box-one/desktop/").Within();

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
    }

    [Fact]
    public async Task DesktopHost_BoxesTurnedOffAfterSignIn_Is404()
    {
        var cookie = await _rig.SignInToDesktopAsync(_rig.KeyA).Within();
        _rig.Feature.Enabled.Clear();
        using var desktop = _rig.Desktop(cookie);

        var page = await desktop.GetAsync("/boxes/box-one/desktop/").Within();

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task DesktopHost_NoTrailingSlash_RedirectsToTheSlashFormOnTheSameHost()
    {
        using var desktop = _rig.Desktop(cookie: null);

        var response = await desktop.GetAsync("/boxes/box-one/desktop?theme=dark").Within();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.False(location.IsAbsoluteUri);
        Assert.Equal("/boxes/box-one/desktop/?theme=dark", location.OriginalString);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Theory]
    [InlineData("/boxes/box-one/desktop/")]
    [InlineData("/boxes/box-one/desktop/api/status")]
    [InlineData("/boxes/box-one/desktop")]
    public async Task MainHost_TheDesktopRoute_DoesNotExist(string path)
    {
        using var browser = _rig.Browser(_rig.KeyA);

        var response = await browser.GetAsync(path).Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Theory]
    [InlineData("GET", "/sessions")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/mobile/")]
    [InlineData("POST", "/director-stream/negotiate")]
    [InlineData("POST", "/boxes/box-one/desktop-ticket")]
    [InlineData("GET", "/boxes")]
    public async Task DesktopHost_EveryOtherRoute_IsRefused(string method, string path)
    {
        using var desktop = _rig.Desktop(cookie: null);
        desktop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _rig.KeyA);

        var response = await desktop.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)).Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "This address serves only box desktops. The DevThrottle Gateway is at https://gateway.public.test; use that address instead.",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public void RefusalText_NoGatewayAddress_StillSaysWhatThisAddressServes()
    {
        var text = BoxDesktopEndpoints.RefusalText(null);

        Assert.Equal("This address serves only box desktops. The DevThrottle Gateway is at its configured public address; use that address instead.", text);
    }

    [Fact]
    public async Task DesktopHost_Healthz_StillAnswers_ForTheDeployWorkflows()
    {
        using var desktop = _rig.Desktop(cookie: null);

        var response = await desktop.GetAsync("/healthz").Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MainHost_OrdinaryRoutes_AreUntouched()
    {
        using var browser = _rig.Browser(_rig.KeyA);

        var response = await browser.GetAsync("/sessions").Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the main host's sessions", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DesktopPage_OnAHostedGateway_SendsTheOpenInsideTheBoxSessionsAccount()
    {
        // The desktop host runs before the sign-in and the tenant-scope middleware. The rig's command send, like a hosted
        // Gateway's, drops a command sent with no tenant scope (review finding 1 on pull request #4280).
        var cookie = await _rig.SignInToDesktopAsync(_rig.KeyA).Within();
        using var desktop = _rig.Desktop(cookie);

        var page = await desktop.GetAsync("/boxes/box-one/desktop/").Within();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(0, _rig.UnscopedCommands);
        Assert.Equal(new[] { _rig.TenantA }, _rig.CommandTenants.ToArray());
    }

    // -------------------------------------------------------------------------- the allow-list ----

    [Fact]
    public async Task DesktopPage_BoxNotOnTheDirectorsList_IsRefusedAtTheDirector()
    {
        // The Gateway is fooled: it believes the Director hosts box-two. The Director's own list says it does not.
        _rig.Hosts.Hosted[(_rig.TenantA.Value, "box-two")] = DesktopRelayRig.DirectorId;
        var cookie = await _rig.SignInToDesktopAsync(_rig.KeyA, "box-two").Within();
        using var desktop = _rig.Desktop(cookie);

        var response = await desktop.GetAsync("/boxes/box-two/desktop/").Within();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("does not host a desktop for that box", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, _rig.OpenCommands);
        Assert.Empty(_rig.Connector.Addresses);
        Assert.Empty(_rig.Box.Requests);
        Assert.Equal(0, _rig.Registry.LiveStreamCount);
    }

    // --------------------------------------------------------------------------------- HTTP leg ----

    [Fact]
    public async Task DesktopRequest_ReachesOnlyTheListedPortOnLoopback_WithThePrefixStripped()
    {
        using var browser = await SignedInDesktopAsync();

        var response = await browser.GetAsync("/boxes/box-one/desktop/").Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html><body>fake desktop</body></html>", await response.Content.ReadAsStringAsync());
        var address = Assert.Single(_rig.Connector.Addresses);
        Assert.Equal($"http://127.0.0.1:{DesktopRelayRig.BoxPort}/", address.ToString());
        Assert.Equal("/", Assert.Single(_rig.Box.Requests).Path);
    }

    [Fact]
    public async Task DesktopRequest_CarriesPathQueryAndHeaders_ButNotTheGatewaysCredentials()
    {
        var boxCookie = await _rig.SignInToDesktopAsync(_rig.KeyA).Within();
        using var browser = _rig.Desktop(cookie: null);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _rig.KeyA);
        var request = new HttpRequestMessage(HttpMethod.Get, "/boxes/box-one/desktop/headers?a=1&b=two");
        request.Headers.Add("Cookie", $"cc-gateway-token={_rig.KeyA}; {boxCookie}; selkies-token=abc");
        request.Headers.Add("X-Upload-Id", "upload-7");
        request.Headers.Add("Accept-Language", "da");

        var response = await browser.SendAsync(request).Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = RelayJson.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/headers", echoed.GetProperty("path").GetString());
        Assert.Equal("?a=1&b=two", echoed.GetProperty("query").GetString());
        var headers = echoed.GetProperty("headers");
        Assert.False(headers.TryGetProperty("Authorization", out _));
        Assert.Equal("selkies-token=abc", headers.GetProperty("Cookie").GetString());
        Assert.Equal("upload-7", headers.GetProperty("X-Upload-Id").GetString());
        Assert.Equal("da", headers.GetProperty("Accept-Language").GetString());
        Assert.DoesNotContain(_rig.KeyA, echoed.GetRawText());
        Assert.DoesNotContain(boxCookie.Split('=')[1], echoed.GetRawText());
    }

    [Fact]
    public async Task DesktopResponse_DropsSetCookie_AndKeepsTheBoxsOtherHeaders()
    {
        using var browser = await SignedInDesktopAsync();

        var response = await browser.GetAsync("/boxes/box-one/desktop/cookie").Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("kept", response.Headers.GetValues("X-Box-Header").Single());
    }

    [Theory]
    [InlineData("/boxes/box-one/desktop/redirect-root", "/boxes/box-one/desktop/landing")]
    [InlineData("/boxes/box-one/desktop/redirect-box", "/boxes/box-one/desktop/landing?x=1")]
    public async Task DesktopRedirect_StaysInsideTheBoxsPrefix_NeverLoopback(string path, string expected)
    {
        using var browser = await SignedInDesktopAsync();

        var response = await browser.GetAsync(path).Within();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task DesktopUpload_SixtyFourMegabytes_StreamsThroughWithTheUploadHeaders()
    {
        const int size = (int)DesktopRelayLimits.MaxRequestBodyBytes;
        using var browser = await SignedInDesktopAsync();
        var content = new ByteArrayContent(FakeDesktop.PatternBytes(size));
        var request = new HttpRequestMessage(HttpMethod.Post, "/boxes/box-one/desktop/api/upload") { Content = content };
        request.Headers.Add("X-Upload-Id", "big-file");
        request.Headers.Add("X-Upload-Offset", "0");
        request.Headers.Add("X-Upload-Total", "134217728");
        request.Headers.Add("X-Upload-Final", "false");

        var response = await browser.SendAsync(request).Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = RelayJson.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(size, answer.GetProperty("received").GetInt64());
        Assert.Equal(-1, answer.GetProperty("firstMismatch").GetInt64());
        Assert.Equal("0", answer.GetProperty("offset").GetString());
        Assert.Equal("134217728", answer.GetProperty("total").GetString());
        Assert.Equal("false", answer.GetProperty("final").GetString());
        // Streamed, not carried whole: the body crossed the tunnel in frames no larger than the shared cap.
        Assert.Equal(size, _rig.Legs.Tally.For(DesktopRelayRig.BoxId).ToBox);
    }

    [Fact]
    public async Task DesktopUpload_OverSixtyFourMegabytes_Is413AndNeverOpened()
    {
        using var browser = await SignedInDesktopAsync();
        var content = new ByteArrayContent(new byte[16]);
        content.Headers.ContentLength = DesktopRelayLimits.MaxRequestBodyBytes + 1;
        var request = new HttpRequestMessage(HttpMethod.Post, "/boxes/box-one/desktop/api/upload") { Content = content };

        var response = await browser.SendAsync(request).Within();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _rig.OpenCommands);
    }

    [Fact]
    public async Task DesktopDownload_TwentyMegabytes_ArrivesWholeAndInOrder()
    {
        const int size = 20 * 1024 * 1024;
        using var browser = await SignedInDesktopAsync();

        var response = await browser.GetAsync($"/boxes/box-one/desktop/api/files/big?bytes={size}", HttpCompletionOption.ResponseHeadersRead).Within();
        var body = await response.Content.ReadAsByteArrayAsync().Within();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(size, response.Content.Headers.ContentLength);
        Assert.Equal(SHA256.HashData(FakeDesktop.PatternBytes(size)), SHA256.HashData(body));
        Assert.All(_rig.UpFrames.Where(f => f.Kind == DesktopRelayFrameKind.Data),
            f => Assert.True(f.Data!.Length <= DesktopRelayLimits.MaxFrameBytes));
    }

    [Fact]
    public async Task DesktopRequest_FinishedStream_FreesBothEnds()
    {
        using var browser = await SignedInDesktopAsync();

        var response = await browser.GetAsync("/boxes/box-one/desktop/api/status").Within();
        await response.Content.ReadAsStringAsync();
        await Watchdog.Eventually(() => _rig.Registry.LiveStreamCount == 0 && _rig.Director.ActiveRequestCount == 0);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------------------- WebSocket leg ----

    [Fact]
    public async Task DesktopSocket_TextAndBinary_RoundTripWithTheirTypes()
    {
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();

        await socket.SendAsync(Encoding.UTF8.GetBytes("SETTINGS,{\"x\":1}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var text = await FakeDesktop.ReceiveMessageAsync(socket).Within();
        await socket.SendAsync(new byte[] { 0, 1, 2, 255 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        var binary = await FakeDesktop.ReceiveMessageAsync(socket).Within();

        Assert.Equal(WebSocketMessageType.Text, text.Type);
        Assert.Equal("SETTINGS,{\"x\":1}", Encoding.UTF8.GetString(text.Data));
        Assert.Equal(WebSocketMessageType.Binary, binary.Type);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, binary.Data);
        Assert.Equal($"ws://127.0.0.1:{DesktopRelayRig.BoxPort}/api/websockets", Assert.Single(_rig.Connector.Addresses).ToString());
    }

    [Fact]
    public async Task DesktopSocket_A244KilobyteMessage_IsSplitOnTheTunnelAndArrivesWholeByteForByte()
    {
        const int size = 244_156; // the largest frame the spike measured
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();

        await socket.SendAsync(Encoding.UTF8.GetBytes($"big:{size}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var message = await FakeDesktop.ReceiveMessageAsync(socket).Within();

        Assert.Equal(WebSocketMessageType.Binary, message.Type);
        Assert.Equal(FakeDesktop.PatternBytes(size), message.Data);
        var pieces = _rig.UpFrames.Where(f => f.Kind == DesktopRelayFrameKind.Data).ToList();
        Assert.Equal((size + DesktopRelayLimits.MaxFrameBytes - 1) / DesktopRelayLimits.MaxFrameBytes, pieces.Count);
        Assert.All(pieces, p => Assert.True(p.Data!.Length <= DesktopRelayLimits.MaxFrameBytes));
        Assert.Single(pieces, p => p.EndOfMessage);
        Assert.True(pieces[^1].EndOfMessage);
    }

    [Fact]
    public async Task DesktopSocket_ALargeMessageFromTheBrowser_ReachesTheBoxWhole()
    {
        const int size = 300_000;
        var payload = FakeDesktop.PatternBytes(size);
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();

        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
        var echoed = await FakeDesktop.ReceiveMessageAsync(socket).Within();

        Assert.Equal(payload, echoed.Data);
    }

    [Fact]
    public async Task DesktopSocket_BrowserCloses_ClosesTheBoxAndFreesBothEnds()
    {
        var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();
        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "tab closed", CancellationToken.None);
        var boxSaw = await _rig.Box.SawClose.Task.Within();
        await Watchdog.Eventually(() => _rig.Registry.LiveStreamCount == 0 && _rig.Director.ActiveSocketCount == 0);

        Assert.Equal(1000, boxSaw.Status);
        Assert.Equal("tab closed", boxSaw.Reason);
        socket.Dispose();
    }

    [Fact]
    public async Task DesktopSocket_BoxCloses_ClosesTheBrowserWithTheBoxsStatus()
    {
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();

        await socket.SendAsync(Encoding.UTF8.GetBytes("close"), WebSocketMessageType.Text, true, CancellationToken.None);
        var closed = await FakeDesktop.ReceiveMessageAsync(socket).Within();
        await Watchdog.Eventually(() => _rig.Registry.LiveStreamCount == 0 && _rig.Director.ActiveSocketCount == 0);

        Assert.Equal(WebSocketMessageType.Close, closed.Type);
        Assert.Equal(4000, closed.CloseStatus);
        Assert.Equal("box says bye", closed.CloseReason);
    }

    [Fact]
    public async Task DesktopSocket_DirectorDisconnects_ClosesTheBrowserWithAClearReason()
    {
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();
        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();

        _rig.DirectorConnection.Cancel();
        _rig.Director.CancelAll("the tunnel closed");
        var closed = await FakeDesktop.ReceiveMessageAsync(socket).Within();
        await _rig.Box.SocketEnded.Task.Within();
        await Watchdog.Eventually(() => _rig.Registry.LiveStreamCount == 0 && _rig.Director.ActiveSocketCount == 0);

        Assert.Equal(WebSocketMessageType.Close, closed.Type);
        Assert.Equal(1001, closed.CloseStatus);
        Assert.Equal("The Director hosting this box disconnected.", closed.CloseReason);
    }

    [Fact]
    public async Task DesktopSocket_QuietForAnHour_IsClosed_ButNotBefore()
    {
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();
        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();

        _rig.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, _rig.Registry.LiveStreamCount);
        await socket.SendAsync(Encoding.UTF8.GetBytes("still here"), WebSocketMessageType.Text, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();
        _rig.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, _rig.Registry.LiveStreamCount);

        _rig.Time.Advance(TimeSpan.FromMinutes(2));
        var closed = await FakeDesktop.ReceiveMessageAsync(socket).Within();

        Assert.Equal(WebSocketMessageType.Close, closed.Type);
        Assert.Equal(1001, closed.CloseStatus);
        // The check runs once a minute, so the quiet it reports is the hour, or the hour and the minute it fell in.
        var minutes = int.Parse(System.Text.RegularExpressions.Regex.Match(closed.CloseReason!, @"nothing moved for (\d+) minutes").Groups[1].Value);
        Assert.InRange(minutes, 60, 61);
        Assert.True(DesktopRelayLimits.IdleTimeout >= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task DesktopSocket_BytesAreCountedPerBoxInEachDirection()
    {
        using var socket = await _rig.OpenDesktopSocketAsync(await _rig.SignInToDesktopAsync(_rig.KeyA)).Within();

        await socket.SendAsync(new byte[1000], WebSocketMessageType.Binary, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();
        await socket.SendAsync(Encoding.UTF8.GetBytes("big:50000"), WebSocketMessageType.Text, true, CancellationToken.None);
        await FakeDesktop.ReceiveMessageAsync(socket).Within();

        var gateway = _rig.Legs.Tally.For(DesktopRelayRig.BoxId);
        var director = _rig.Director.Tally.For(DesktopRelayRig.BoxId);
        Assert.Equal(1000 + "big:50000".Length, gateway.ToBox);
        Assert.Equal(1000 + 50_000, gateway.ToBrowser);
        Assert.Equal(gateway, director);
    }

    /// <summary>A desktop-host client already let into box-one, the way a browser is: ticket, then cookie.</summary>
    private async Task<HttpClient> SignedInDesktopAsync() => _rig.Desktop(await _rig.SignInToDesktopAsync(_rig.KeyA).Within());

}
