using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Traffic optimization, phase 2, through the REAL Gateway pipeline: the conversation tail and the roster without
/// its clock, read over HTTP exactly as the Cockpit and the phone read them.
///
/// Revert-proof: send the full conversation for a cursor request and the tail test goes red on its count; put a
/// cursor or tailFrom on the answer to a request WITHOUT a cursor and the old-client test goes red; drop the
/// clock-field strip and the roster 304 test goes red; strip for a request that did not ask and the old-roster
/// test goes red.
/// </summary>
public sealed class TrafficTailAndRosterHostTests : IAsyncLifetime
{
    private const string Token = "test-token";
    // One session per test: the test database is shared by every host test in the run, and a conversation seeded
    // by one test would otherwise be the start of another's.
    private readonly string Sid = Guid.NewGuid().ToString();
    // Exactly the options the host serializes with (the ASP.NET defaults, relaxed escaping included).
    private static readonly JsonSerializerOptions Web = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic2-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _http = null!; // Initialized before each test by InitializeAsync.
    private FakeTunnelDirector _dir = null!; // Initialized before each test by InitializeAsync.
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);

        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "dir-traffic2");
        await _dir.PushSnapshotAsync(new SessionDto
        {
            SessionId = Sid,
            Agent = "claude",
            RepoPath = "/repo",
            ActivityState = "WaitingForInput",
            Status = "Running",
            StatusColor = "red",
            CreatedAt = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
            LastActivityAt = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
        });
    }

    public async Task DisposeAsync()
    {
        await _dir.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best effort */ }
    }

    private async Task<HttpResponseMessage> Get(string path, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Accept.ParseAdd("application/json");
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await _http.SendAsync(request);
    }

    private async Task<JsonObject> Json(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static (string Role, string Text)[] Turns(int count) =>
        Enumerable.Range(0, count).Select(i => (i % 2 == 0 ? "User" : "Assistant", $"turn {i}")).ToArray();

    private string History(string? cursor) =>
        cursor is null ? $"sessions/{Sid}/history" : $"sessions/{Sid}/history?cursor={Uri.EscapeDataString(cursor)}";

    [Fact]
    public async Task History_WithoutACursor_IsExactlyTheOldAnswer()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic2", Sid, Turns(3));

        using var response = await Get(History(null));
        var bytes = await response.Content.ReadAsByteArrayAsync();

        // The body is precisely a SessionHistoryDto as the host serializes one - no cursor, no tailFrom, no field
        // an older Cockpit or phone has never seen, and nothing re-ordered.
        var dto = JsonSerializer.Deserialize<SessionHistoryDto>(bytes, Web)!;
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(dto, Web));
        Assert.Equal(3, dto.Messages.Count);
        var body = JsonNode.Parse(bytes)!.AsObject();
        Assert.False(body.ContainsKey("cursor"));
        Assert.False(body.ContainsKey("tailFrom"));
    }

    [Fact]
    public async Task History_TailAfterNewTurns_PlusWhatWasHeld_IsTheFullAnswer()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic2", Sid, Turns(3));
        using var firstResponse = await Get(History(""));
        var first = await Json(firstResponse);
        Assert.Equal(0, (int)first["tailFrom"]!);
        var held = first["messages"]!.AsArray().Select(m => m!.DeepClone()).ToList();
        Assert.Equal(3, held.Count);

        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic2", Sid, Turns(5));
        using var tailResponse = await Get(History((string)first["cursor"]!));
        var tail = await Json(tailResponse);
        using var fullResponse = await Get(History(null));
        var full = await Json(fullResponse);

        Assert.Equal(3, (int)tail["tailFrom"]!);
        Assert.Equal(2, tail["messages"]!.AsArray().Count);
        var assembled = new JsonArray(held.Concat(tail["messages"]!.AsArray().Select(m => m!.DeepClone())).ToArray());
        Assert.True(JsonNode.DeepEquals(full["messages"], assembled), "held + tail differs from a full read");
        foreach (var (name, value) in full)
        {
            if (name == "messages") continue;
            Assert.True(JsonNode.DeepEquals(value, tail[name]), $"{name} differs between the tail and a full read");
        }
    }

    [Fact]
    public async Task History_TailPollWithNothingNew_Is304()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic2", Sid, Turns(4));
        using var firstResponse = await Get(History(""));
        var cursor = (string)(await Json(firstResponse))["cursor"]!;

        using var again = await Get(History(cursor));
        var tag = again.Headers.ETag!.ToString();
        var body = await Json(again);
        Assert.Empty(body["messages"]!.AsArray());
        Assert.Equal(4, (int)body["tailFrom"]!);

        using var unchanged = await Get(History(cursor), ifNoneMatch: tag);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("v1.2.0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task History_ABadCursor_IsAnsweredInFull(string cursor)
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic2", Sid, Turns(4));

        using var response = await Get(History(cursor));
        var body = await Json(response);

        Assert.Equal(0, (int)body["tailFrom"]!);
        Assert.Equal(4, body["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task Roster_OptIn_UnchangedPoll_Is304_AndEveryAnswerCarriesTheGatewayTime()
    {
        const string path = "sessions?envelope=true&clockFields=absolute";
        using var first = await Get(path);
        var tag = first.Headers.ETag!.ToString();
        var firstTime = DateTime.Parse(first.Headers.GetValues(Api.RosterClockFields.GatewayTimeHeader).Single(),
            null, System.Globalization.DateTimeStyles.RoundtripKind);
        var body = await Json(first);
        var session = body["sessions"]!.AsArray().Single()!.AsObject();
        Assert.False(session.ContainsKey("idleSeconds"));
        Assert.True(session.ContainsKey("lastActivityAt"));
        var director = body["directors"]!.AsArray().Single()!.AsObject();
        Assert.False(director.ContainsKey("lastSeenAgeSeconds"));
        Assert.True(director.ContainsKey("lastSeenUtc"));

        await Task.Delay(30);
        using var again = await Get(path, ifNoneMatch: tag);

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
        var againTime = DateTime.Parse(again.Headers.GetValues(Api.RosterClockFields.GatewayTimeHeader).Single(),
            null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(againTime > firstTime, "the 304 must carry its own, later, Gateway time");
    }

    [Theory]
    [InlineData("sessions?envelope=true")]
    [InlineData("sessions")]
    public async Task Roster_WithoutTheParameter_IsTheOldAnswer_ClockFieldsAndAll(string path)
    {
        using var first = await Get(path);
        Assert.False(first.Headers.Contains(Api.RosterClockFields.GatewayTimeHeader));
        var tag = first.Headers.ETag!.ToString();
        var node = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        var session = (node is JsonArray a ? a : node["sessions"]!.AsArray()).Single()!.AsObject();
        Assert.True(session.ContainsKey("idleSeconds"));
        if (node is JsonObject envelope)
            Assert.True(envelope["directors"]!.AsArray().Single()!.AsObject().ContainsKey("lastSeenAgeSeconds"));

        // Still recomputed on every read: a later read of the same roster is a different answer.
        await Task.Delay(30);
        using var again = await Get(path, ifNoneMatch: tag);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }
}

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
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
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
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
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
