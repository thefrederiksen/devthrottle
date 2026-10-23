using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #4372, through a real Gateway: <c>GET /sessions/{sid}/history</c> with <c>turns</c> answers the last few
/// turns, with <c>before</c> answers the turns before them - but ONLY on top of a cursor that proves what the client
/// holds. A cursor that does not, or a point past what it holds, is answered with a fresh window that replaces
/// what the client holds, never with a page that could leave a gap.
///
/// Revert-proof: serve the older page without checking the cursor and the stranger's-cursor test gets an older page
/// (olderFrom) instead of a fresh window - red.
/// </summary>
public sealed class ConversationWindowHostTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string Sid = Guid.NewGuid().ToString();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-chat-window-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private HostedTestDevice _device;

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

        _device = HostedTestEnrollment.Enroll(_gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        // Twelve turns of two messages each: prompt N at index 2N.
        var turns = Enumerable.Range(0, 12).SelectMany(i => new[] { ("User", $"prompt {i}"), ("Assistant", $"reply {i}") }).ToArray();
        _gateway.SeedStoredConversationForTest(_device.Tenant, "dir-a", Sid, turns);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> Get(string query, string? sid = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"sessions/{sid ?? Sid}/history?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _device.DeviceKey);
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    private static List<string> Texts(JsonObject body) =>
        body["messages"]!.AsArray().Select(m => (string)m!["parts"]![0]!["text"]!).ToList();

    [Fact]
    public async Task TheLatestTurns_ThenTheTurnsBeforeThem_OnTheClientsOwnCursor()
    {
        var (status, window) = await Get("cursor=&turns=5");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(14, (int)window["windowFrom"]!);
        Assert.Equal("prompt 7", Texts(window)[0]);
        Assert.Equal(10, Texts(window).Count);
        var cursor = (string)window["cursor"]!;
        Assert.StartsWith("v1.24.", cursor);

        var (olderStatus, older) = await Get($"cursor={Uri.EscapeDataString(cursor)}&turns=5&before=14");

        Assert.Equal(HttpStatusCode.OK, olderStatus);
        Assert.Equal(4, (int)older["olderFrom"]!);
        Assert.Equal(new[] { "prompt 2", "reply 2", "prompt 3", "reply 3", "prompt 4", "reply 4", "prompt 5", "reply 5", "prompt 6", "reply 6" }, Texts(older));
        Assert.Null(older["cursor"]);
    }

    [Fact]
    public async Task AnOlderPageOnACursorThatProvesNothing_IsAnsweredWithAFreshWindow()
    {
        var stranger = "v1.24." + new string('a', 64);

        var (status, body) = await Get($"cursor={stranger}&turns=5&before=14");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["olderFrom"]);
        Assert.Equal(0, (int)body["tailFrom"]!);
        Assert.Equal(14, (int)body["windowFrom"]!);
    }

    [Fact]
    public async Task AnOlderPagePastWhatTheClientHolds_IsAnsweredWithAFreshWindow()
    {
        var (_, window) = await Get("cursor=&turns=5");
        var cursor = (string)window["cursor"]!;

        var (status, body) = await Get($"cursor={Uri.EscapeDataString(cursor)}&turns=5&before=99");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["olderFrom"]);
        Assert.Equal(0, (int)body["tailFrom"]!);
        Assert.Equal(14, (int)body["windowFrom"]!);
    }

    [Fact]
    public async Task AnOlderPageInsideTheConversationButPastWhatTheCursorHolds_IsAnsweredWithAFreshWindow()
    {
        // The boundary a real client can produce: its cursor names the first 20 messages, the conversation has since
        // grown to 24, and it asks for the turns before message 22 - inside the conversation, past what it holds.
        var sid = Guid.NewGuid().ToString();
        var turns = Enumerable.Range(0, 12).SelectMany(i => new[] { ("User", $"prompt {i}"), ("Assistant", $"reply {i}") }).ToArray();
        _gateway.SeedStoredConversationForTest(_device.Tenant, "dir-a", sid, turns.Take(20).ToArray());
        var (_, first) = await Get("cursor=&turns=5", sid);
        var cursorFor20 = (string)first["cursor"]!;
        Assert.StartsWith("v1.20.", cursorFor20);
        _gateway.SeedStoredConversationForTest(_device.Tenant, "dir-a", sid, turns);

        var (status, body) = await Get($"cursor={Uri.EscapeDataString(cursorFor20)}&turns=5&before=22", sid);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["olderFrom"]);
        Assert.Equal(0, (int)body["tailFrom"]!);
        Assert.Equal(14, (int)body["windowFrom"]!);
        Assert.Equal(10, body["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task AWindowAskedForWithoutACursor_IsRefused()
    {
        var (status, body) = await Get("turns=10");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("cursor", (string)body["error"]!);
    }

    [Fact]
    public async Task AMalformedTurnsNumber_IsRefused()
    {
        var (status, body) = await Get("cursor=&turns=0");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("turns", (string)body["error"]!);
    }
}
