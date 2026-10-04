using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Money Saver, night traffic: every Director reads the skill register, the workflow catalog and the
/// injected text once a minute, and until this change every read was a full answer - about 6 MB an hour
/// for one account at night (Gateway traffic meter, 4 October 2026). These tests run the REAL Gateway
/// pipeline against the REAL Director readers and prove both halves:
///
///   1. The Gateway answers an unchanged read with 304 and no body, and a changed one in full.
///   2. The Director's readers ask with the tag they hold, get the 304, and still end up with exactly
///      what a full download would have given them - including the skill store, which deletes any skill
///      the register does not name, so a 304 that read as "no skills" would wipe it.
///
/// Revert-proof: put Results.Json back on any of the three routes and its 304 assertions go red; make a
/// reader bypass <see cref="HeldGatewayAnswers"/> and its "second read was a 304" assertion goes red.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DirectorPolledReadsNotModifiedHostTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-polled-304-" + Guid.NewGuid().ToString("N"));
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "cc-polled-304-scratch-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!; // Initialized before each test by InitializeAsync.
    private RecordingHandler _wire = null!; // Initialized before each test by InitializeAsync.
    private HttpClient _client = null!; // Initialized before each test by InitializeAsync.

    public DirectorPolledReadsNotModifiedHostTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-polled-304-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        Directory.CreateDirectory(_scratch);
        _wire = new RecordingHandler();
        _client = new HttpClient(_wire);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root, _scratch })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private string GatewayUrl => $"http://127.0.0.1:{_gateway.Port}";

    private async Task<HttpResponseMessage> Get(string path, string? ifNoneMatch = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GatewayUrl}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Accept.ParseAdd("application/json");
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        var response = await http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [Theory]
    [InlineData("gateway/skills")]
    [InlineData("gateway/workflows")]
    [InlineData("gateway/injected-text")]
    public async Task AnUnchangedRead_Is304WithNoBody(string path)
    {
        using var first = await Get(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = first.Headers.ETag?.ToString() ?? throw new InvalidOperationException($"no ETag on {path}");
        Assert.Equal("no-cache, private", first.Headers.CacheControl?.ToString());
        Assert.Equal(Api.ConditionalJson.TagFor(await first.Content.ReadAsByteArrayAsync()), tag);

        using var again = await Get(path, tag);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ASwitchedOffSkill_ChangesTheTag_AndIsSentInFull()
    {
        using var first = await Get("gateway/skills");
        var tag = first.Headers.ETag!.ToString();

        using (var http = new HttpClient())
        {
            using var disable = new HttpRequestMessage(HttpMethod.Post,
                $"{GatewayUrl}/gateway/skills/move-session/disable?by=test");
            disable.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            (await http.SendAsync(disable)).EnsureSuccessStatusCode();
        }

        using var changed = await Get("gateway/skills", tag);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(tag, changed.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task TheSkillIndex_SecondRefreshIs304_AndTheIndexIsUnchanged()
    {
        var held = new HeldGatewayAnswers();
        var cache = Path.Combine(_scratch, "skill-index.json");
        var store = new SkillIndexStore(cache, _client, GatewayUrl, Token, held);

        await store.RefreshAsync();
        var firstIndex = store.ReadCache()!;
        await Task.Delay(20); // so a re-stamp moves the cached time
        await store.RefreshAsync();
        var secondIndex = store.ReadCache()!;

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, _wire.StatusesFor("/gateway/skills"));
        Assert.Contains("- move-session:", secondIndex.Index);
        Assert.Equal(firstIndex.Index, secondIndex.Index);
        // A 304 confirms the copy is current, so the staleness clock restarts exactly as on a download.
        Assert.True(secondIndex.CachedAtUtc > firstIndex.CachedAtUtc);
    }

    [Fact]
    public async Task TheWorkflowIndex_SecondRefreshIs304_AndTheIndexIsUnchanged()
    {
        var store = new WorkflowIndexStore(Path.Combine(_scratch, "workflow-index.json"), _client, GatewayUrl, Token,
            new HeldGatewayAnswers());

        await store.RefreshAsync();
        var first = store.ReadCache()!.Index;
        await store.RefreshAsync();

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, _wire.StatusesFor("/gateway/workflows"));
        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, store.ReadCache()!.Index);
    }

    [Fact]
    public async Task TheInjectedText_SecondRefreshIs304_AndAnEditIsSentInFull()
    {
        var store = new InjectedTextStore(Path.Combine(_scratch, "injected-text.json"), _client, GatewayUrl, Token,
            new HeldGatewayAnswers());

        await store.RefreshAsync();
        Assert.Equal(InjectedTextSource.Ours, store.ActiveSource());
        await store.RefreshAsync();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, _wire.StatusesFor("/gateway/injected-text"));

        using (var http = new HttpClient())
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"{GatewayUrl}/gateway/injected-text")
            {
                Content = new StringContent("{\"use_yours\":true,\"yours\":\"My own words.\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            (await http.SendAsync(put)).EnsureSuccessStatusCode();
        }

        await store.RefreshAsync();
        Assert.Equal(HttpStatusCode.OK, _wire.StatusesFor("/gateway/injected-text").Last());
        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
    }

    [Fact]
    public async Task TheSkillStore_AnUnchangedRegisterIs304_AndNoSkillIsDropped()
    {
        var storeRoot = Path.Combine(_scratch, "skill-store");
        var refresh = new SkillStoreRefresh(storeRoot, _client, GatewayUrl, Token, new HeldGatewayAnswers());

        var first = await refresh.RefreshAsync();
        Assert.True(first > 0, "the built-in skills should have been materialized");
        var directoriesBefore = Directory.GetDirectories(storeRoot).Select(Path.GetFileName).Order().ToArray();

        var second = await refresh.RefreshAsync();

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotModified }, _wire.StatusesFor("/gateway/skills"));
        Assert.Equal(first, second);
        Assert.Equal(directoriesBefore, Directory.GetDirectories(storeRoot).Select(Path.GetFileName).Order().ToArray());
    }

    /// <summary>Records the status of every answer that crossed the wire, per path.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly ConcurrentQueue<(string Path, HttpStatusCode Status)> _seen = new();

        public RecordingHandler() : base(new HttpClientHandler()) { }

        public HttpStatusCode[] StatusesFor(string path) =>
            _seen.Where(s => s.Path == path).Select(s => s.Status).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            _seen.Enqueue((request.RequestUri!.AbsolutePath, response.StatusCode));
            return response;
        }
    }
}
