using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for the reachable front-door sign-in CALLBACK (epic #1069, issue #1080), hardened so no
/// token rides in the callback URL (issue #1082, absorbs #877): <c>GET/POST /account/sign-in-callback</c>. This
/// is the routable address the cloud sign-in page redirects the user's OWN browser back to, so a person on
/// ANOTHER machine completes sign-in in their own browser. It boots only
/// <see cref="AccountSignInCallbackEndpoint"/> on an ephemeral port and proves:
/// <list type="bullet">
/// <item>NEW SHAPE: a POST carrying the token pair in the request BODY (what the fragment hand-back page posts)
///   leaves the Gateway signed in;</item>
/// <item>NEW SHAPE entry: a GET with no token in the URL serves the fragment hand-back page and stays signed out
///   until the page posts the credential;</item>
/// <item>TRANSITION: a GET still carrying both tokens in the query string signs in (backward compatible);</item>
/// <item>a hand-back missing the credential is failed loud (not signed in), never storing a half-credential;</item>
/// <item>neither the access JWT nor the refresh token is ever echoed back in the response (security rule DT-05);</item>
/// <item>a host with no sign-in flow reports an explicit "not available" rather than pretending to capture.</item>
/// </list>
/// </summary>
public sealed class AccountSignInCallbackEndpointTests
{
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    private static DevThrottleAccountService MakeAccount()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            return GatewayAccountFactory.Build(new InMemoryTokenStore(), hosted: false);
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

        AccountSignInCallbackEndpoint.Map(app, signIn);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    // NEW SHAPE (issue #1082): a POST carrying the token pair in the request BODY (what the fragment hand-back
    // page posts) leaves the Gateway signed in, with no token echoed in the response (DT-05).
    [Fact]
    public async Task Post_Callback_WithTokenPairBody_SignsIn_NoTokenEchoed()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-1082";
        var signIn = new GatewaySignInService(MakeAccount());
        Assert.False(signIn.IsSignedIn());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.PostAsJsonAsync(
                AccountSignInCallbackEndpoint.Path,
                new Dictionary<string, string> { ["access_token"] = jwt, ["refresh_token"] = refreshToken });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Acceptance criterion 2: signed-in after a sign-in run using the new shape.
            Assert.True(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            // Security (DT-05): the response never carries the access JWT or the refresh token.
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // NEW SHAPE entry: a GET with no token in the URL serves the fragment hand-back page (the token pair is in
    // the fragment, which the server never sees) and stays signed out until the page posts the credential.
    [Fact]
    public async Task Get_Callback_NoToken_ServesHandbackPage_StaysSignedOut()
    {
        var signIn = new GatewaySignInService(MakeAccount());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.GetAsync(AccountSignInCallbackEndpoint.Path);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // No credential arrived yet (it is in the fragment the browser has not posted back).
            Assert.False(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            // The served page is the fragment hand-back page (carries the reader script).
            Assert.Contains("Completing sign-in", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("location.hash", body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // NEW SHAPE failure (acceptance criterion 4): a POST whose body is missing a token is failed loud - not
    // signed in, no half-credential stored - and returns a non-OK status so the page shows a retry message.
    [Fact]
    public async Task Post_Callback_IncompleteBody_FailsLoud_StaysSignedOut()
    {
        var signIn = new GatewaySignInService(MakeAccount());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.PostAsJsonAsync(
                AccountSignInCallbackEndpoint.Path,
                new Dictionary<string, string> { ["access_token"] = "access-only-no-refresh" });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            Assert.False(signIn.IsSignedIn());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // TRANSITION: a GET still carrying both tokens in the query string signs in (backward compatible) and shows
    // a signed-in page, with no token echoed (DT-05). Removed once the cloud completion emits the fragment shape.
    [Fact]
    public async Task Get_Callback_WithQueryTokenPair_SignsIn_Transition_NoTokenEchoed()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-1080";
        var signIn = new GatewaySignInService(MakeAccount());
        Assert.False(signIn.IsSignedIn());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var url = $"{AccountSignInCallbackEndpoint.Path}?access_token={Uri.EscapeDataString(jwt)}&refresh_token={Uri.EscapeDataString(refreshToken)}";
            var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.True(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("signed in", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // TRANSITION failure: a GET carrying only one token in the query string is failed loud (not signed in).
    [Fact]
    public async Task Get_Callback_QueryMissingRefreshToken_FailsLoud_StaysSignedOut()
    {
        var signIn = new GatewaySignInService(MakeAccount());

        var (app, http) = await StartAsync(signIn);
        try
        {
            var url = $"{AccountSignInCallbackEndpoint.Path}?access_token=access-only-no-refresh";
            var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.False(signIn.IsSignedIn());

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("did not complete", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // No sign-in flow on this host: report it explicitly, capture nothing, echo no token.
    [Fact]
    public async Task Get_Callback_NoSignInFlow_ReportsNotAvailable()
    {
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            const string bogus = "TOKEN-THAT-MUST-NOT-BE-STORED-OR-ECHOED-1080";
            var url = $"{AccountSignInCallbackEndpoint.Path}?access_token={bogus}&refresh_token={bogus}";
            var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("not available", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(bogus, body, StringComparison.Ordinal);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
