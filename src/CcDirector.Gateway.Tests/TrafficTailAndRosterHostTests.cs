using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Traffic optimization, phase 2, on a HOSTED Gateway with two accounts that hold the SAME session id with the SAME
/// words: one account's cursor never unlocks a tail for the other. Account B, presenting account A's cursor, is
/// answered in full with B's own conversation.
///
/// Revert-proof: leave the tenant out of the cursor's hash and B presenting A's cursor gets a tail (tailFrom 2) -
/// the test goes red.
/// </summary>
public sealed class TrafficTailTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string SharedSid = Guid.NewGuid().ToString();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic2-tenant-" + Guid.NewGuid().ToString("N"));
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
        _gateway.SeedStoredConversationForTest(_a.Tenant, "dir-a", SharedSid, ("User", "same"), ("Assistant", "words"));
        _gateway.SeedStoredConversationForTest(_b.Tenant, "dir-b", SharedSid, ("User", "same"), ("Assistant", "words"), ("User", "bob's third"));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<JsonObject> GetHistory(string deviceKey, string cursor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"sessions/{SharedSid}/history?cursor={Uri.EscapeDataString(cursor)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    [Fact]
    public async Task AnotherAccountsCursor_ForTheSameSessionAndWords_IsAnsweredInFull_WithOwnConversation()
    {
        // Alice holds two messages - word for word the first two of Bob's, under the same session id and the same
        // generation source. Only the account in the cursor's hash tells the two apart.
        var alice = await GetHistory(_a.DeviceKey, "");
        Assert.Equal(2, alice["messages"]!.AsArray().Count);
        var aliceCursorForTwo = (string)alice["cursor"]!;
        Assert.StartsWith("v1.2.", aliceCursorForTwo);

        var bob = await GetHistory(_b.DeviceKey, aliceCursorForTwo);

        Assert.Equal(0, (int)bob["tailFrom"]!);
        Assert.Equal(3, bob["messages"]!.AsArray().Count);
        var texts = bob["messages"]!.AsArray().Select(m => (string)m!["parts"]![0]!["text"]!).ToList();
        Assert.Equal(new[] { "same", "words", "bob's third" }, texts);
    }
}
