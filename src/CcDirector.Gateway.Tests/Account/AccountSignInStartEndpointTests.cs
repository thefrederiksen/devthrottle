using System.Net;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for the credential-free cloud sign-in START front door (epic #1069, issue #1076):
/// <c>GET /account/sign-in-start</c> and <c>POST /account/sign-in-start</c>. Boots only
/// <see cref="AccountSignInStartEndpoint"/> on an ephemeral port and proves:
/// <list type="bullet">
/// <item>the GET front door is served (200 text/html) and its response carries NO token;</item>
/// <item>the GET handler IGNORES any supplied credential - the response is byte-identical whether or not a
///   Bearer/cookie is present (acceptance criterion 4);</item>
/// <item>the POST start action reports an explicit, user-safe "not available" on a host with no sign-in flow,
///   and "already signed in" on a signed-in Gateway - neither opening a browser nor echoing a token.</item>
/// </list>
/// The Started=true POST path opens the system browser and waits for the loopback hand-back, so it is the
/// live-QA gate (the same boundary <see cref="AccountSignInEndpointTests"/> draws) and is not exercised here.
/// </summary>
public sealed class AccountSignInStartEndpointTests
{
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    private static DevThrottleAccountService MakeAccount(DevThrottleTokens? seed)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
            if (seed is not null)
                service.StoreTokens(seed);
            return service;
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    private static async Task<(WebApplication app, HttpClient http)> StartAsync(GatewaySignInService? signIn)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        AccountSignInStartEndpoint.Map(app, signIn);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    // The front door is served as an HTML page and carries no token material.
    [Fact]
    public async Task Get_FrontDoor_Returns200Html_WithNoToken()
    {
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            var resp = await http.GetAsync(AccountSignInStartEndpoint.Path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);

            var body = await resp.Content.ReadAsStringAsync();
            // Security (DT-05): the front door names no token and offers no field to paste one.
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gateway-token", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Sign in with DevThrottle", body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Acceptance criterion 4: the handler IGNORES any supplied credential - a bogus Bearer and cookie do
    // not change the response, and the supplied token never appears in it.
    [Fact]
    public async Task Get_FrontDoor_IgnoresSuppliedCredential()
    {
        const string bogusToken = "BOGUS-CREDENTIAL-MARKER-1076";
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            var plain = await (await http.GetAsync(AccountSignInStartEndpoint.Path)).Content.ReadAsStringAsync();

            using var withCred = new HttpRequestMessage(HttpMethod.Get, AccountSignInStartEndpoint.Path);
            withCred.Headers.Add("Authorization", $"Bearer {bogusToken}");
            withCred.Headers.Add("Cookie", $"cc-gateway-token={bogusToken}");
            var withCredResp = await http.SendAsync(withCred);
            var withCredBody = await withCredResp.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, withCredResp.StatusCode);
            // Identical body regardless of credential, and the supplied token is never echoed back.
            Assert.Equal(plain, withCredBody);
            Assert.DoesNotContain(bogusToken, withCredBody, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // No sign-in flow on this host: the POST start action reports an explicit "not available" and no token.
    [Fact]
    public async Task Post_Start_NoSignInFlow_ReportsNotAvailable_NoToken()
    {
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            var resp = await http.PostAsync(AccountSignInStartEndpoint.Path, content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("not available", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Already signed in: report it, start no browser hand-off, and carry no token in the response.
    [Fact]
    public async Task Post_Start_AlreadySignedIn_ReportsAlreadySignedIn_NoToken()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-1076";
        var signIn = new GatewaySignInService(MakeAccount(new DevThrottleTokens(jwt, refreshToken)));

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.PostAsync(AccountSignInStartEndpoint.Path, content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();

            Assert.Contains("already signed in", body, StringComparison.OrdinalIgnoreCase);
            // Security (DT-05): the response never carries the access JWT or refresh token.
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // ---- Issue #1080: remote-vs-same-machine callback selection ----

    // A counting browser opener: proves that the REMOTE path opens NO browser on the host, and gives the
    // same-machine path a browser to open without launching a real one.
    private sealed class RecordingBrowserOpener
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task Open(string url) { Interlocked.Increment(ref _count); return Task.CompletedTask; }
    }

    // Boots the start endpoint behind middleware that stamps the requester's remote address and host, so the
    // remote-vs-same-machine decision (which reads ctx.Connection.RemoteIpAddress / ctx.Request.Host) can be
    // exercised over a loopback test socket.
    private static async Task<(WebApplication app, HttpClient http)> StartWithRequestOverrideAsync(
        GatewaySignInService? signIn, IPAddress remoteIp, string host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = remoteIp;
            ctx.Request.Host = new HostString(host);
            await next();
        });

        AccountSignInStartEndpoint.Map(app, signIn);
        await app.StartAsync();

        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };
        return (app, http);
    }

    // Acceptance criteria 1-3: a REMOTE requester is redirected to the cloud sign-in page carrying the
    // Gateway's reachable front-door callback as redirect_uri (NOT 127.0.0.1), and NO browser opens on the host.
    [Fact]
    public async Task Post_Start_RemoteRequester_RedirectsToCloudWithFrontDoorCallback_NoHostBrowser()
    {
        const string frontDoorHost = "gw.example-tailnet.ts.net";
        var opener = new RecordingBrowserOpener();
        var signIn = new GatewaySignInService(MakeAccount(seed: null), openBrowser: opener.Open);

        var (app, http) = await StartWithRequestOverrideAsync(signIn, IPAddress.Parse("100.86.144.11"), frontDoorHost);
        try
        {
            var resp = await http.PostAsync(AccountSignInStartEndpoint.Path, content: null);

            // The remote browser is redirected (302), not handed a "look at the host" status page.
            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            var location = resp.Headers.Location?.ToString() ?? "";
            Assert.StartsWith(FirstRunLoginCoordinator.ResolveSignInBaseUrl(), location, StringComparison.Ordinal);

            // redirect_uri is the reachable front-door callback, never a loopback URL (criterion 3).
            var expectedCallback = Uri.EscapeDataString($"http://{frontDoorHost}{RemoteSignInRouting.CallbackPath}");
            Assert.Contains($"redirect_uri={expectedCallback}", location, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", location, StringComparison.Ordinal);

            // Criterion 2: no browser was opened on the Gateway host for the remote case.
            Assert.Equal(0, opener.Count);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // The same-machine (loopback) requester keeps the host-local flow: a status page, NOT a redirect.
    [Fact]
    public async Task Post_Start_SameMachineRequester_KeepsHostLocalFlow_NoRedirect()
    {
        var opener = new RecordingBrowserOpener();
        var signIn = new GatewaySignInService(MakeAccount(seed: null), openBrowser: opener.Open);

        var (app, http) = await StartWithRequestOverrideAsync(signIn, IPAddress.Loopback, "127.0.0.1:7879");
        try
        {
            var resp = await http.PostAsync(AccountSignInStartEndpoint.Path, content: null);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(resp.Headers.Location);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Sign-in started", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
