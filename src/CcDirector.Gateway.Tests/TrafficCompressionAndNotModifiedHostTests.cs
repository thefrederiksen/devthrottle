using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Traffic optimization, phase 1, through the REAL Gateway pipeline (its middleware order, its auth, its
/// endpoints): the conversation and the roster answer an unchanged poll with 304 and no body and a changed one
/// in full; the JSON is compressed and decodes to exactly the uncompressed bytes; and the events feed is never
/// compressed and still delivers each event as it happens.
///
/// Revert-proof: take <c>GatewayResponseCompression.Use</c> out of GatewayHost and the two compression tests go
/// red; send <c>/events</c> through the compressor (drop BOTH the path bypass and the event-stream type
/// exclusion - either one alone still protects it) and the stream test goes red on its Content-Encoding; put Results.Json back on either route and its 304 test goes red (no ETag, no 304).
/// </summary>
public sealed class TrafficCompressionAndNotModifiedHostTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Sid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-traffic-" + Guid.NewGuid().ToString("N"));
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
        // No automatic decompression: these tests read the bytes that crossed the wire.
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
        _dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "dir-traffic");
        await _dir.PushSnapshotAsync(Row("red"));
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

    private static SessionDto Row(string color) => new()
    {
        SessionId = Sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = color,
        CreatedAt = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
        LastActivityAt = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
    };

    private async Task<HttpResponseMessage> Get(string path, string? ifNoneMatch = null, string? acceptEncoding = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Accept.ParseAdd("application/json");
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _http.SendAsync(request);
    }

    private static string TagOf(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("no ETag on the response");

    [Fact]
    public async Task History_UnchangedPoll_Is304NoBody_NewTurn_Is200WithNewTag()
    {
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic", Sid, ("User", "do the thing"), ("Assistant", "working"));

        using var first = await Get($"sessions/{Sid}/history");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = TagOf(first);
        Assert.Equal("no-cache, private", first.Headers.CacheControl?.ToString());
        Assert.Contains("working", await first.Content.ReadAsStringAsync());

        using var again = await Get($"sessions/{Sid}/history", ifNoneMatch: tag);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
        Assert.Equal(tag, TagOf(again));

        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic", Sid,
            ("User", "do the thing"), ("Assistant", "working"), ("Assistant", "finished"));

        using var changed = await Get($"sessions/{Sid}/history", ifNoneMatch: tag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(tag, TagOf(changed));
        Assert.Contains("finished", await changed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("sessions?envelope=true")]
    public async Task Roster_TagIsTheHashOfTheServedBytes_ChangedRow_Is200WithNewTag(string path)
    {
        // The roster carries two fields computed from the clock on every read (sessions[].idleSeconds and
        // directors[].lastSeenAgeSeconds), so two real polls are never byte-identical and a real roster poll does
        // not get a 304 until those move to absolute timestamps (phase 2). What IS pinned here is what makes the
        // 304 safe when it does fire: the tag is the hash of exactly the bytes served, and an older tag never
        // suppresses a changed row.
        using var first = await Get(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = TagOf(first);
        Assert.Equal("no-cache, private", first.Headers.CacheControl?.ToString());
        Assert.Equal(Api.ConditionalJson.TagFor(await first.Content.ReadAsByteArrayAsync()), tag);

        await _dir.PushDeltaAsync(Row("blue"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string body;
        HttpResponseMessage changed;
        while (true)
        {
            changed = await Get(path, ifNoneMatch: tag);
            body = await changed.Content.ReadAsStringAsync();
            if (body.Contains("\"blue\"", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
                break;
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            changed.Dispose();
            await Task.Delay(50);
        }
        using (changed)
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.NotEqual(tag, TagOf(changed));
            Assert.Contains("\"blue\"", body);
        }
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("sessions?envelope=true")]
    public async Task Roster_MatchingTag_Is304NoBody(string path)
    {
        // "*" matches whatever the current answer is, which is the one way to ask for a roster 304 while its
        // clock fields move under every read - it proves the roster route really answers conditionally.
        using var response = await Get(path, ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-cache, private", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task History_Compressed_DecodesToExactlyTheUncompressedJson(string encoding)
    {
        var turns = Enumerable.Range(0, 40)
            .Select(i => (i % 2 == 0 ? "User" : "Assistant", $"turn {i}: " + string.Concat(Enumerable.Repeat("the same words again ", 20))))
            .ToArray();
        _gateway.SeedStoredConversationForTest(TenantId.Local, "dir-traffic", Sid, turns);

        using var plain = await Get($"sessions/{Sid}/history");
        var plainBytes = await plain.Content.ReadAsByteArrayAsync();
        Assert.Empty(plain.Content.Headers.ContentEncoding);

        using var packed = await Get($"sessions/{Sid}/history", acceptEncoding: encoding);
        Assert.Equal(HttpStatusCode.OK, packed.StatusCode);
        Assert.Equal(new[] { encoding }, packed.Content.Headers.ContentEncoding);
        var packedBytes = await packed.Content.ReadAsByteArrayAsync();
        Assert.True(packedBytes.Length < plainBytes.Length / 3,
            $"expected at least 3x smaller, got {packedBytes.Length} of {plainBytes.Length}");

        Assert.Equal(plainBytes, Decode(packedBytes, encoding));
        // The tag is the tag of the JSON, whichever way it travelled.
        Assert.Equal(TagOf(plain), TagOf(packed));
    }

    [Fact]
    public async Task Roster_Compressed_DecodesToExactlyTheUncompressedJson()
    {
        // Two roster reads are never byte-identical (clock fields), so the decode is checked against the tag the
        // Gateway computed from the uncompressed bytes of THIS response.
        using var packed = await Get("sessions?envelope=true", ifNoneMatch: null, acceptEncoding: "br, gzip");
        Assert.Equal(new[] { "br" }, packed.Content.Headers.ContentEncoding);

        var decoded = Decode(await packed.Content.ReadAsByteArrayAsync(), "br");
        Assert.Equal(TagOf(packed), Api.ConditionalJson.TagFor(decoded));
        Assert.Contains("\"sessions\"", System.Text.Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public async Task Events_NeverCompressed_AndStillStreamsEventByEvent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        using var subscription = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", subscription.Content.Headers.ContentType?.MediaType);
        Assert.Empty(subscription.Content.Headers.ContentEncoding);

        using var reader = new StreamReader(await subscription.Content.ReadAsStreamAsync());
        foreach (var id in new[] { "dir-first", "dir-second" })
        {
            _gateway.Registry.Upsert(new DirectorRegistrationRequest
            {
                DirectorId = id,
                TailnetEndpoint = "http://127.0.0.1:9/",
                MachineName = "local",
                Pid = 1,
                Version = "test",
                StartedAt = DateTime.UtcNow,
            });

            // Each event must arrive on its own, before the next one exists - a compressor would hold it back.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Contains(id, await ReadNextEventDataAsync(reader, timeout.Token));
        }
    }

    private static byte[] Decode(byte[] body, string encoding)
    {
        using var input = new MemoryStream(body);
        using Stream decoder = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<string> ReadNextEventDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line[6..];
        }
        throw new EndOfStreamException("The events feed closed before publishing an event.");
    }
}

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
        _gateway.SeedStoredConversationForTest(_a.Tenant, "dir-a", SharedSid, ("User", "alice asks"), ("Assistant", "alice's private answer"));
        _gateway.SeedStoredConversationForTest(_b.Tenant, "dir-b", SharedSid, ("User", "bob asks"), ("Assistant", "bob's own answer"));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
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
