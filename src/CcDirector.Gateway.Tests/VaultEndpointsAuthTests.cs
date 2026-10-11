using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the trust boundary in front of the key vault actually holds, at the ROUTE level.
///
/// Why this exists: <see cref="VaultEndpoints"/> hands out secret VALUES
/// (<c>GET /vault/keys/{name}</c>) and lets a caller overwrite them (<c>PUT /vault/keys/{name}</c>),
/// and its own doc comment says the protection is not in the route at all - "Auth is the Gateway's
/// host-wide token middleware, so these routes inherit it". That is a correct design and an untested
/// assumption: the routes contain no check of their own, so IF the host-wide gate were ever bypassed,
/// reordered, or handed a path that lands in the public allow-list, these endpoints would read and
/// write secrets for anyone who asked, and nothing in the suite would notice.
///
/// So these tests boot the REAL <see cref="AuthMiddleware"/> in front of the REAL
/// <see cref="VaultEndpoints"/> on an ephemeral port and drive it over real HTTP - the same shape the
/// account endpoint tests use. What is being proven is the composition, not either half:
///
///   1. an unauthenticated GET cannot read a key value - and the secret is not in the body;
///   2. an unauthenticated PUT cannot write - and the stored value is UNCHANGED afterwards
///      (a 401 that still mutated would be the worst of both worlds);
///   3. an unauthenticated DELETE cannot remove a key;
///   4. an unauthenticated list cannot even enumerate key names;
///   5. a valid credential still works - so a future "fix" cannot pass these by breaking the vault.
///
/// The tailnet half of the boundary is a network-level control (the Gateway binds where it binds) and
/// is not reproducible in-process; this covers the credential half, which is the half that lives in
/// code and can regress in a pull request.
/// </summary>
public sealed class VaultEndpointsAuthTests
{
    private const string GatewayToken = "test-gateway-token-for-vault-auth";
    private const string SecretName = "DEVTHROTTLE_API_KEY";
    private const string SecretValue = "dt_live_THIS_MUST_NEVER_LEAK";

    private static KeyVault TempVault()
    {
        var vault = new KeyVault(Path.Combine(Path.GetTempPath(), "cc-vault-auth-" + Guid.NewGuid().ToString("N") + ".json"));
        vault.Set(SecretName, SecretValue);
        return vault;
    }

    /// <summary>Boot the real middleware + the real vault routes on an ephemeral port.</summary>
    private static async Task<(WebApplication App, HttpClient Http, KeyVault Vault)> StartAsync()
    {
        var vault = TempVault();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var requireToken = new AuthMiddleware.RequireToken
        {
            Hosted = false,
            Token = GatewayToken,
            Devices = new DeviceRegistry(Path.Combine(Path.GetTempPath(), "cc-vault-auth-devices-" + Guid.NewGuid().ToString("N") + ".json")),
        };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));

        VaultEndpoints.Map(app, false, vault);
        await app.StartAsync();

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http, vault);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string? json = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {GatewayToken}");
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    // (1) The read that hands out a secret value must not answer an anonymous caller - and the secret
    //     must not appear in whatever it DOES answer (a 401 page that echoes the value is still a leak).
    [Fact]
    public async Task GetVaultKey_WithoutCredential_Returns401AndDoesNotLeakTheValue()
    {
        var (app, http, _) = await StartAsync();
        await using var _app = app;

        var res = await http.GetAsync($"/vault/keys/{SecretName}");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain(SecretValue, body, StringComparison.Ordinal);
    }

    // (2) The write must not land. The status is necessary but not sufficient: assert the STORED value
    //     is untouched, because "rejected the caller but wrote anyway" is the failure that matters.
    [Fact]
    public async Task PutVaultKey_WithoutCredential_Returns401AndDoesNotMutateTheVault()
    {
        var (app, http, vault) = await StartAsync();
        await using var _app = app;

        var res = await http.PutAsync($"/vault/keys/{SecretName}",
            new StringContent("{\"value\":\"attacker-supplied\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(SecretValue, vault.Get(SecretName));   // unchanged
    }

    // (3) Nor may an anonymous caller delete a key: denial of service on the vault is still an attack.
    [Fact]
    public async Task DeleteVaultKey_WithoutCredential_Returns401AndTheKeySurvives()
    {
        var (app, http, vault) = await StartAsync();
        await using var _app = app;

        var res = await http.DeleteAsync($"/vault/keys/{SecretName}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(SecretValue, vault.Get(SecretName));
    }

    // (4) Even the names are gated. They are not values, but they are a map of what this machine holds.
    [Fact]
    public async Task ListVaultKeys_WithoutCredential_Returns401()
    {
        var (app, http, _) = await StartAsync();
        await using var _app = app;

        var res = await http.GetAsync("/vault/keys");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain(SecretName, body, StringComparison.Ordinal);
    }

    // (5) The control. Without this, every test above would still pass if the vault routes were simply
    //     broken or unmapped - which would prove nothing about the boundary.
    [Fact]
    public async Task VaultRoutes_WithValidCredential_StillReadAndWrite()
    {
        var (app, http, vault) = await StartAsync();
        await using var _app = app;

        var read = await http.SendAsync(Authed(HttpMethod.Get, $"/vault/keys/{SecretName}"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync()))
        {
            Assert.Equal(SecretValue, doc.RootElement.GetProperty("value").GetString());
        }

        var write = await http.SendAsync(Authed(HttpMethod.Put, "/vault/keys/NEW_KEY", "{\"value\":\"set-by-an-authorized-caller\"}"));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal("set-by-an-authorized-caller", vault.Get("NEW_KEY"));
    }

    // The vault path must never drift into the middleware's public allow-list. That list is edited as
    // new public surfaces are added (sign-in, enroll, assets), and this asserts the invariant directly
    // at the place the decision is made, not just through one route's behaviour.
    [Theory]
    [InlineData("/vault/keys")]
    [InlineData("/vault/keys/DEVTHROTTLE_API_KEY")]
    public async Task VaultPath_WithoutCredential_IsNeverPublic(string path)
    {
        var (app, http, _) = await StartAsync();
        await using var _app = app;

        var res = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
