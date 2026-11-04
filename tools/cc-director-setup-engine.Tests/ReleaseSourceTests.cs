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
}
