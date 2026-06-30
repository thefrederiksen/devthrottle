using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// HTTP wire tests for the account device-list proxy (issue #854): <c>GET /account/devices</c> and
/// <c>DELETE /account/devices/{id}</c>. Boots only <see cref="AccountDevicesEndpoint"/> on an ephemeral
/// port over a Gateway credential service built on an in-memory token store, and points its
/// <see cref="DeviceRegistryClient"/> at an in-process STUB cloud handler (no real network). Proves the
/// issue's five acceptance criteria:
/// <list type="number">
/// <item>(a) GET forwards the Bearer token and maps the cloud response to the DTO (with the this-device
/// marker);</item>
/// <item>(b) the Cockpit-facing response contains NO access/refresh token;</item>
/// <item>(c) DELETE forwards the revoke and a subsequent GET no longer lists the device;</item>
/// <item>(d) signed-out returns the explicit <c>signedIn:false</c> envelope, never an empty 200 list;</item>
/// <item>(e) cloud-unreachable returns a clear error (502), not a fabricated list.</item>
/// </list>
/// The real signed-in cloud end-to-end is the QA gate; here the stub stands in for the cloud contract
/// devthrottle_internal#81/#82.
/// </summary>
public sealed class AccountDevicesEndpointTests
{
    private const string GatewayToken = "test-gateway-token-for-issue-854";
    private const string ThisMachine = "GATEWAY-HOST-854";

    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// An in-process stub of the cloud device registry. It records the Authorization header it received
    /// (so the test can assert the Bearer token was forwarded), serves a mutable device list for
    /// <c>GET /api/v1/devices</c>, and removes a device on <c>DELETE /api/v1/devices/{id}</c>. When
    /// <see cref="Unreachable"/> is set it throws like an unreachable host.
    /// </summary>
    private sealed class StubCloudHandler : HttpMessageHandler
    {
        private readonly List<(string Id, string Name)> _devices;

        public StubCloudHandler(IEnumerable<(string Id, string Name)> devices) => _devices = devices.ToList();

        public string? LastAuthorization { get; private set; }
        public bool Unreachable { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();

            if (Unreachable)
                throw new HttpRequestException("simulated cloud unreachable");

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Get && path == DeviceRegistryClient.DevicesPath)
            {
                var sb = new StringBuilder("[");
                for (var i = 0; i < _devices.Count; i++)
                {
                    var d = _devices[i];
                    if (i > 0) sb.Append(',');
                    // Masked record shape from the cloud contract: id, name, platform, device_type,
                    // app_version, key_prefix, key_last4, created_at, last_seen_at. No key_hash, no raw key.
                    sb.Append($"{{\"id\":\"{d.Id}\",\"name\":\"{d.Name}\",\"platform\":\"windows\",\"device_type\":\"gateway\",\"app_version\":\"1.2.3\",\"key_prefix\":\"dtk_\",\"key_last4\":\"ab12\",\"created_at\":\"2026-06-01T00:00:00Z\",\"last_seen_at\":\"2026-06-30T12:00:00Z\"}}");
                }
                sb.Append(']');
                return Task.FromResult(Json(HttpStatusCode.OK, sb.ToString()));
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith(DeviceRegistryClient.DevicesPath + "/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path[(DeviceRegistryClient.DevicesPath.Length + 1)..]);
                var removed = _devices.RemoveAll(d => d.Id == id);
                if (removed == 0)
                    return Task.FromResult(Json(HttpStatusCode.NotFound, "{\"error\":\"not found\"}"));
                return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"id\":\"{id}\",\"revoked\":true}}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static DevThrottleAccountService MakeAccount(DevThrottleTokens? seed)
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var authEventsLog = Path.Combine(Path.GetTempPath(), "cc-gw-acct-devices-" + Guid.NewGuid().ToString("N") + ".jsonl");
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

    private static async Task<(WebApplication app, HttpClient http)> StartAsync(
        DevThrottleAccountService? account, DeviceRegistryClient devices, bool authEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        if (authEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken
            {
                Token = GatewayToken,
                Devices = new DeviceRegistry(Path.Combine(Path.GetTempPath(), "cc-gw-acct-devices-dev-" + Guid.NewGuid().ToString("N") + ".json")),
            };
            app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        AccountDevicesEndpoint.Map(app, account, devices, ThisMachine);
        await app.StartAsync();

        var baseUrl = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, http);
    }

    private static DeviceRegistryClient ClientOver(StubCloudHandler stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://stub-cloud.invalid") }, baseUrl: "https://stub-cloud.invalid");

    // Criterion (a) + (b): GET forwards the Bearer token, maps the cloud records to the DTO with the
    // this-device marker, and the response body contains NO access or refresh token.
    [Fact]
    public async Task Get_SignedIn_ForwardsBearer_MapsDtos_AndNoToken()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        const string refreshToken = "REFRESH-TOKEN-PLAINTEXT-MARKER-854";
        var account = MakeAccount(new DevThrottleTokens(jwt, refreshToken));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine), (Id: "dev-2", Name: "OTHER-LAPTOP") });

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/devices");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();

            // Criterion (a): the Bearer access token was forwarded to the cloud.
            Assert.Equal($"Bearer {jwt}", stub.LastAuthorization);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("signedIn").GetBoolean());
            var arr = root.GetProperty("devices");
            Assert.Equal(2, arr.GetArrayLength());

            var first = arr[0];
            Assert.Equal("dev-1", first.GetProperty("id").GetString());
            Assert.Equal(ThisMachine, first.GetProperty("name").GetString());
            Assert.Equal("2026-06-30T12:00:00Z", first.GetProperty("lastSeenAt").GetString());
            Assert.True(first.GetProperty("thisDevice").GetBoolean(), "matching machine name must mark thisDevice");
            Assert.False(arr[1].GetProperty("thisDevice").GetBoolean());

            // Criterion (b): the Cockpit-facing body never carries the access JWT or refresh token,
            // nor any "token"/key_hash field.
            Assert.DoesNotContain(jwt, body, StringComparison.Ordinal);
            Assert.DoesNotContain(refreshToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key_hash", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Criterion (c): DELETE forwards the revoke and a subsequent GET no longer lists the removed device.
    [Fact]
    public async Task Delete_Revokes_AndSubsequentGet_NoLongerListsDevice()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine), (Id: "dev-2", Name: "OTHER-LAPTOP") });
        var client = ClientOver(stub);

        var (app, http) = await StartAsync(account, client, authEnabled: false);
        try
        {
            var del = await http.DeleteAsync("/account/devices/dev-2");
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
            using (var delDoc = JsonDocument.Parse(await del.Content.ReadAsStringAsync()))
            {
                Assert.True(delDoc.RootElement.GetProperty("signedIn").GetBoolean());
                Assert.Equal("dev-2", delDoc.RootElement.GetProperty("id").GetString());
                Assert.True(delDoc.RootElement.GetProperty("revoked").GetBoolean());
            }

            var after = await http.GetAsync("/account/devices");
            using var doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("devices");
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal("dev-1", arr[0].GetProperty("id").GetString());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Criterion (c, 404 path): revoking an id that is not the account's returns 404 with revoked:false.
    [Fact]
    public async Task Delete_UnknownId_Returns404_NotRevoked()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) });

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var del = await http.DeleteAsync("/account/devices/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
            using var doc = JsonDocument.Parse(await del.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("revoked").GetBoolean());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Criterion (d): a signed-out Gateway returns an explicit signedIn:false envelope with NO devices
    // array - never a fabricated empty 200 list - for both GET and DELETE.
    [Fact]
    public async Task SignedOut_Get_ReturnsExplicitSignedInFalse_NotEmptyList()
    {
        var account = MakeAccount(seed: null);
        var stub = new StubCloudHandler(Array.Empty<(string, string)>());

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/devices");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("devices", out _), "devices must be omitted when signed out");
            // The cloud was never called because there was no credential to forward.
            Assert.Null(stub.LastAuthorization);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task SignedOut_Delete_ReturnsExplicitSignedInFalse_NoRevoke()
    {
        var account = MakeAccount(seed: null);
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) });

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var del = await http.DeleteAsync("/account/devices/dev-1");
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
            using var doc = JsonDocument.Parse(await del.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("signedIn").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("revoked").GetBoolean());
            Assert.Null(stub.LastAuthorization);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Criterion (e): when the cloud is unreachable, GET returns a clear 502 error (not a fabricated list).
    [Fact]
    public async Task Get_CloudUnreachable_Returns502_NotFabricatedList()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) }) { Unreachable = true };

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/account/devices");
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"devices\"", body, StringComparison.Ordinal);
            Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Criterion (e, revoke path): when the cloud is unreachable, DELETE returns a clear 502 error.
    [Fact]
    public async Task Delete_CloudUnreachable_Returns502()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) }) { Unreachable = true };

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: false);
        try
        {
            var del = await http.DeleteAsync("/account/devices/dev-1");
            Assert.Equal(HttpStatusCode.BadGateway, del.StatusCode);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Gateway auth gate: with auth enabled a call carrying no Gateway token is 401 by the host-wide
    // middleware (the same gate the other /account routes use), before the delegate runs.
    [Fact]
    public async Task AuthEnabled_NoGatewayToken_Returns401()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) });

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: true);
        try
        {
            var resp = await http.GetAsync("/account/devices");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }

    // Gateway auth gate (authorized path): with the Gateway token present the endpoint passes (200).
    [Fact]
    public async Task AuthEnabled_WithGatewayToken_Returns200()
    {
        var jwt = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        var account = MakeAccount(new DevThrottleTokens(jwt, "refresh-1"));
        var stub = new StubCloudHandler(new[] { (Id: "dev-1", Name: ThisMachine) });

        var (app, http) = await StartAsync(account, ClientOver(stub), authEnabled: true);
        try
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
            var resp = await http.GetAsync("/account/devices");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("signedIn").GetBoolean());
        }
        finally
        {
            http.Dispose();
            await app.DisposeAsync();
        }
    }
}
