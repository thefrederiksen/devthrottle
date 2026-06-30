using System.Net;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for the web sign-in trigger (issue #853): <c>POST /account/sign-in</c>. Boots only
/// <see cref="AccountSignInEndpoint"/> on an ephemeral port and proves the two DETERMINISTIC decision
/// paths that do not open a real browser:
/// <list type="bullet">
/// <item>a host with no sign-in flow (signIn null) returns an explicit, user-safe "not available" result
/// (Started=false, Error set) - never a fabricated started state;</item>
/// <item>an already-signed-in Gateway returns AlreadySignedIn=true with no browser hand-off started.</item>
/// </list>
/// The Started=true path opens the system browser and waits for the loopback hand-back, so the live
/// browser sign-in is the QA gate (the same boundary #637/#854 draw for their live cloud paths); it is
/// not exercised here. The response never carries a token (security rule DT-05), asserted below.
/// </summary>
public sealed class AccountSignInEndpointTests
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
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-acct-signin-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var service = GatewayAccountFactory.Build(new InMemoryTokenStore(), authEventsLog);
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

        AccountSignInEndpoint.Map(app, signIn);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    // No sign-in flow on this host: explicit "not available" result, never a fabricated started state.
    [Fact]
    public async Task NoSignInFlow_ReturnsExplicitNotAvailable()
    {
        var (app, http) = await StartAsync(signIn: null);
        try
        {
            var resp = await http.PostAsync("/account/sign-in", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("alreadySignedIn").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Already signed in: report it, start no browser hand-off, and carry no token in the response.
    [Fact]
    public async Task AlreadySignedIn_ReturnsAlreadySignedIn_NoToken()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-853";
        var signIn = new GatewaySignInService(MakeAccount(new DevThrottleTokens(jwt, refreshToken)));

        var (app, http) = await StartAsync(signIn);
        try
        {
            var resp = await http.PostAsync("/account/sign-in", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("alreadySignedIn").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("started").GetBoolean());

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
}
