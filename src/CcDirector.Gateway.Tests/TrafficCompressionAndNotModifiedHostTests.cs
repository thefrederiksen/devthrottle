using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Traffic optimization, phase 1, on a HOSTED Gateway with two accounts: a 304 never lets one account's request
/// match another account's answer. Both accounts own a conversation under the SAME session id. Account B,
/// presenting account A's tag, is answered in full with B's own conversation; account A, presenting its own tag,
/// gets its 304. Every answer is <c>private</c>, so no shared cache may hold either.
///
/// Revert-proof: compute the tag from anything but the served body (the session id, the request path, the
/// message count) and B presenting A's tag gets a 304 - which in a browser means B is shown A's conversation -
/// and <see cref="History_OtherAccountsTag_Is200WithOwnConversation"/> goes red.
/// </summary>
public sealed class TrafficNotModifiedTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SharedSid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic-tenant-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private HostedTestDevice _a;
    private HostedTestDevice _b;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _a = HostedTestEnrollment.Enroll(_gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        _b = HostedTestEnrollment.Enroll(_gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        _gateway.SeedStoredConversationForTest(_a.Tenant, "dir-a", SharedSid, ("User", "alice asks"), ("Assistant", "alice's private answer"));
        _gateway.SeedStoredConversationForTest(_b.Tenant, "dir-b", SharedSid, ("User", "bob asks"), ("Assistant", "bob's own answer"));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> GetHistory(string deviceKey, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"sessions/{SharedSid}/history");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await _http.SendAsync(request);
    }

    [Fact]
    public async Task History_OtherAccountsTag_Is200WithOwnConversation()
    {
        using var alice = await GetHistory(_a.DeviceKey);
        Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
        var aliceTag = alice.Headers.ETag!.ToString();
        Assert.Contains("alice's private answer", await alice.Content.ReadAsStringAsync());
        Assert.Equal("no-cache, private", alice.Headers.CacheControl?.ToString());

        using var bob = await GetHistory(_b.DeviceKey, ifNoneMatch: aliceTag);

        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.NotEqual(aliceTag, bob.Headers.ETag!.ToString());
        var bobBody = await bob.Content.ReadAsStringAsync();
        Assert.Contains("bob's own answer", bobBody);
        Assert.DoesNotContain("alice", bobBody);
        Assert.Equal("no-cache, private", bob.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task History_OwnTag_Is304()
    {
        using var alice = await GetHistory(_a.DeviceKey);
        var aliceTag = alice.Headers.ETag!.ToString();

        using var again = await GetHistory(_a.DeviceKey, ifNoneMatch: aliceTag);

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
    }
}
