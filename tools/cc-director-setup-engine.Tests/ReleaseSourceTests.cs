using System.Net;
using System.Net.Http;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class ReleaseSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _stagedFiles = [];

    public ReleaseSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-relsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var f in _stagedFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
        }
    }

    /// <summary>Synchronous IProgress so reports are captured deterministically (Progress&lt;T&gt; posts async).</summary>
    private sealed class CaptureProgress : IProgress<(long downloaded, long total)>
    {
        private readonly object _gate = new();
        private readonly List<(long downloaded, long total)> _reports = [];

        public IReadOnlyList<(long downloaded, long total)> Reports
        {
            get { lock (_gate) return _reports.ToList(); }
        }

        public void Report((long downloaded, long total) value)
        {
            lock (_gate) _reports.Add(value);
        }
    }

    /// <summary>Serves the given payload for every request, with Content-Length set.</summary>
    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }

    [Fact]
    public async Task DownloadAssetAsync_HttpSource_ReportsMonotonicByteProgress()
    {
        // Arrange: ~3 MiB payload so the ~1 MiB report throttle yields several reports.
        var payload = new byte[3 * 1024 * 1024 + 123];
        new Random(42).NextBytes(payload);
        var source = new ReleaseSource(new HttpClient(new StubHandler(payload)));
        var urls = new Dictionary<string, string> { ["asset.zip"] = "https://release.test/asset.zip" };
        var progress = new CaptureProgress();

        // Act
        var staged = await source.DownloadAssetAsync("asset.zip", urls, CancellationToken.None, progress);
        _stagedFiles.Add(staged);

        // Assert: staged content is intact.
        Assert.Equal(payload, await File.ReadAllBytesAsync(staged));

        // Assert: several reports, all against the Content-Length total, monotonic,
        // ending with the completion report (downloaded == total).
        var reports = progress.Reports;
        Assert.True(reports.Count >= 3, $"expected >=3 reports, got {reports.Count}");
        Assert.All(reports, r => Assert.Equal(payload.Length, r.total));
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].downloaded >= reports[i - 1].downloaded,
                $"report {i} went backwards: {reports[i].downloaded} < {reports[i - 1].downloaded}");
        Assert.Equal(payload.Length, reports[^1].downloaded);
    }

    [Fact]
    public async Task DownloadAssetAsync_HttpSource_NoProgress_StillStagesFile()
    {
        var payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);
        var source = new ReleaseSource(new HttpClient(new StubHandler(payload)));
        var urls = new Dictionary<string, string> { ["asset.zip"] = "https://release.test/asset.zip" };

        var staged = await source.DownloadAssetAsync("asset.zip", urls, CancellationToken.None);
        _stagedFiles.Add(staged);

        Assert.Equal(payload, await File.ReadAllBytesAsync(staged));
    }

    [Fact]
    public async Task DownloadAssetAsync_LocalReleaseDir_ReportsCompletion()
    {
        // Arrange: local release-dir mode resolves the "URL" to a file path.
        var assetPath = Path.Combine(_dir, "local-asset.zip");
        var payload = new byte[4096];
        new Random(11).NextBytes(payload);
        await File.WriteAllBytesAsync(assetPath, payload);
        var urls = new Dictionary<string, string> { ["local-asset.zip"] = assetPath };
        var progress = new CaptureProgress();

        // Act
        var staged = await new ReleaseSource().DownloadAssetAsync("local-asset.zip", urls, CancellationToken.None, progress);
        _stagedFiles.Add(staged);

        // Assert: copied intact, with one completion report of (size, size).
        Assert.Equal(payload, await File.ReadAllBytesAsync(staged));
        var report = Assert.Single(progress.Reports);
        Assert.Equal(payload.Length, report.downloaded);
        Assert.Equal(payload.Length, report.total);
    }

    [Fact]
    public async Task DownloadAssetAsync_UnknownAsset_Throws()
    {
        var source = new ReleaseSource(new HttpClient(new StubHandler([])));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.DownloadAssetAsync("missing.zip", new Dictionary<string, string>(), CancellationToken.None));
    }

    // ----- Issue #266: GitHub rate-limit hardening of FetchLatestAsync -----

    private const string ManifestUrl = "https://release.test/release-manifest.json";

    /// <summary>A GitHub "latest release" body whose only asset is the release manifest.</summary>
    private static string ReleaseJson() =>
        $$"""
        { "tag_name": "v1.2.3", "assets": [
            { "name": "release-manifest.json", "browser_download_url": "{{ManifestUrl}}" }
        ] }
        """;

    /// <summary>A minimal valid release-manifest.json body served for the manifest asset.</summary>
    private static string ManifestJson() =>
        """
        { "version": "1.2.3", "assets": {
            "release-manifest.json": { "version": "1.2.3", "sha256": "", "platform": "any", "size": 0 }
        } }
        """;

    /// <summary>Builds a 403 rate-limit response carrying the exhausted-budget headers GitHub sends.</summary>
    private static HttpResponseMessage RateLimited(DateTimeOffset? reset = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{ \"message\": \"API rate limit exceeded\" }"),
        };
        resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        if (reset is { } r)
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Reset", r.ToUnixTimeSeconds().ToString());
        return resp;
    }

    private static HttpResponseMessage Ok(string body, string? etag = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (etag is not null)
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return resp;
    }

    /// <summary>
    /// Records every request and returns scripted responses. The latest-release endpoint is driven
    /// by a queue of responses (one per attempt); any request to the manifest URL is served the
    /// manifest body so a successful path can complete.
    /// </summary>
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> latestResponses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<DateTimeOffset> RequestTimes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            RequestTimes.Add(DateTimeOffset.UtcNow);

            if (request.RequestUri!.ToString() == ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ManifestJson()),
                });

            return Task.FromResult(latestResponses.Dequeue());
        }
    }

    private static ReleaseInfoCache TempCache() =>
        new(Path.Combine(Path.GetTempPath(), "cc-relcache-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public async Task FetchLatestAsync_RateLimitedEveryAttempt_ThrowsClassifiedException()
    {
        // Arrange: 403 rate-limit on every attempt. No-op delay so the test does not actually wait.
        var responses = new Queue<HttpResponseMessage>();
        var reset = DateTimeOffset.UtcNow.AddMinutes(5);
        for (var i = 0; i < 8; i++) responses.Enqueue(RateLimited(reset));
        var handler = new ScriptedHandler(responses);
        var source = new ReleaseSource(new HttpClient(handler), TempCache(), (_, _) => Task.CompletedTask);

        // Act / Assert: a classified rate-limit exception, NOT a raw HttpRequestException 403.
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            source.FetchLatestAsync(CancellationToken.None));

        Assert.True(ex.Attempts >= 2, $"expected multiple attempts, got {ex.Attempts}");
        Assert.NotNull(ex.ResetsAtUtc);
        // Every recorded request is the latest-release endpoint (it never reached the manifest).
        Assert.All(handler.Requests, r => Assert.EndsWith("/releases/latest", r.RequestUri!.ToString()));
        Assert.True(handler.Requests.Count >= 2, "expected at least one retry beyond the first attempt");
    }

    [Fact]
    public async Task FetchLatestAsync_403ThenSuccess_RecoversAndDelaysBetweenAttempts()
    {
        // Arrange: first attempt 403, second attempt 200 with the release body.
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(RateLimited(DateTimeOffset.UtcNow.AddSeconds(1)));
        responses.Enqueue(Ok(ReleaseJson(), etag: "\"abc123\""));
        var handler = new ScriptedHandler(responses);

        var delays = new List<TimeSpan>();
        var source = new ReleaseSource(new HttpClient(handler), TempCache(),
            (d, _) => { delays.Add(d); return Task.CompletedTask; });

        // Act
        var release = await source.FetchLatestAsync(CancellationToken.None);

        // Assert: ultimately succeeded.
        Assert.Equal("1.2.3", release.Manifest.Version);
        // A backoff happened between the 403 and the retry (not an immediate same-instant re-request).
        Assert.Single(delays);
        Assert.True(delays[0] > TimeSpan.Zero, "expected a non-zero backoff before the retry");
        // Two latest-release requests (403 then 200), each before the manifest fetch.
        var latestRequests = handler.Requests.Count(r => r.RequestUri!.ToString().EndsWith("/releases/latest"));
        Assert.Equal(2, latestRequests);
    }

    [Fact]
    public async Task FetchLatestAsync_CachedEtag_SendsConditionalRequest()
    {
        // Arrange: a cache already holding an entity tag + the release body.
        var cache = TempCache();
        cache.Write("\"cached-etag\"", ReleaseJson());

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(Ok(ReleaseJson(), etag: "\"fresh-etag\""));
        var handler = new ScriptedHandler(responses);
        var source = new ReleaseSource(new HttpClient(handler), cache, (_, _) => Task.CompletedTask);

        // Act
        await source.FetchLatestAsync(CancellationToken.None);

        // Assert: the latest-release request carried If-None-Match with the cached tag.
        var latest = handler.Requests.First(r => r.RequestUri!.ToString().EndsWith("/releases/latest"));
        Assert.True(latest.Headers.TryGetValues("If-None-Match", out var values));
        Assert.Contains("\"cached-etag\"", values);
    }

    [Fact]
    public async Task FetchLatestAsync_NotModified_ServesCachedReleaseWithoutError()
    {
        // Arrange: cache holds the release; GitHub answers 304 Not Modified (does not consume budget).
        var cache = TempCache();
        cache.Write("\"cached-etag\"", ReleaseJson());

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        var handler = new ScriptedHandler(responses);
        var source = new ReleaseSource(new HttpClient(handler), cache, (_, _) => Task.CompletedTask);

        // Act: serves the cached release (the manifest URL is still fetched to build the result).
        var release = await source.FetchLatestAsync(CancellationToken.None);

        // Assert
        Assert.Equal("1.2.3", release.Manifest.Version);
    }

    [Fact]
    public void ReleaseInfoCache_Read_CorruptFile_ReturnsNullDoesNotThrow()
    {
        // A corrupt cache file must degrade to "no cache" (a normal unconditional fetch), never
        // throw - the cache is best-effort and non-authoritative. Regression for the unguarded Read().
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "corrupt-cache.json");
        File.WriteAllText(path, "{ this is not valid json ]");
        var cache = new ReleaseInfoCache(path);

        var result = cache.Read();

        Assert.Null(result);
    }
}
