using System.Net.Http.Headers;
using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>A manifest plus the per-asset download URLs needed to fetch each asset.</summary>
public sealed record ResolvedRelease(ReleaseManifest Manifest, IReadOnlyDictionary<string, string> DownloadUrls);

/// <summary>
/// Resolves the release a plan is computed against. Shared by both front-ends
/// (the WPF installer UI and the CLI). Three modes:
///   - a local manifest file (LoadLocalManifest): plan/dry-run only, no URLs.
///   - a local release directory (LoadLocalReleaseDir): offline install/update.
///   - "latest" (FetchLatestAsync): the GitHub latest release, mapping asset
///     download URLs and parsing release-manifest.json.
/// </summary>
public sealed class ReleaseSource
{
    private const string Owner = "thefrederiksen";
    private const string Repo = "devthrottle";
    private const string ManifestAssetName = "release-manifest.json";

    private readonly HttpClient _http;

    public ReleaseSource(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("cc-director-setup");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static ResolvedRelease LoadLocalManifest(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest file not found: {path}", path);
        var manifest = ReleaseManifest.Parse(File.ReadAllText(path));
        return new ResolvedRelease(manifest, new Dictionary<string, string>());
    }

    /// <summary>
    /// Treat a local directory as a release: it must contain release-manifest.json
    /// plus each asset file. The "download URL" for an asset is its local file
    /// path, so a full install/update can run offline with no network and no admin
    /// (the Workstation flow needs neither). Used for hermetic testing.
    /// </summary>
    public static ResolvedRelease LoadLocalReleaseDir(string dir)
    {
        var manifestPath = Path.Combine(dir, ManifestAssetName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {ManifestAssetName} in release dir: {dir}", manifestPath);

        var manifest = ReleaseManifest.Parse(File.ReadAllText(manifestPath));
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetName in manifest.Assets.Keys)
        {
            var assetPath = Path.Combine(dir, assetName);
            if (File.Exists(assetPath)) urls[assetName] = assetPath; // local path acts as the URL
        }
        return new ResolvedRelease(manifest, urls);
    }

    public async Task<ResolvedRelease> FetchLatestAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        string? manifestUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;
                urls[name] = dl;
                if (name == ManifestAssetName) manifestUrl = dl;
            }
        }

        if (manifestUrl is null)
            throw new InvalidOperationException($"Latest release has no {ManifestAssetName} asset.");

        var manifestJson = await _http.GetStringAsync(manifestUrl, ct);
        var manifest = ReleaseManifest.Parse(manifestJson);
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// Stage an asset to a temp file and return its path. The resolved value is
    /// either an http(s) URL (latest/online mode) or a local file path
    /// (release-dir mode); each is handled explicitly. Throws when no source is
    /// known for the asset.
    /// Byte-level progress (downloaded, total) is reported roughly once per MiB;
    /// total is 0 when the server sends no Content-Length. A final report is
    /// always made on completion.
    /// </summary>
    public async Task<string> DownloadAssetAsync(string assetName, IReadOnlyDictionary<string, string> urls, CancellationToken ct,
        IProgress<(long downloaded, long total)>? progress = null)
    {
        if (!urls.TryGetValue(assetName, out var source))
            throw new InvalidOperationException($"No source for asset '{assetName}'. Use latest or a release dir.");

        var dest = Path.Combine(Path.GetTempPath(), $"cc-setup-{Guid.NewGuid():N}-{assetName}");

        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(dest);

            if (progress is null)
            {
                await src.CopyToAsync(fs, ct);
                return dest;
            }

            var buffer = new byte[81920];
            long downloaded = 0, lastReported = 0;
            const long reportEvery = 1024 * 1024; // ~1 MiB between reports keeps UI marshaling cheap
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (downloaded - lastReported >= reportEvery)
                {
                    lastReported = downloaded;
                    progress.Report((downloaded, total));
                }
            }
            progress.Report((downloaded, total > 0 ? total : downloaded));
            return dest;
        }

        // Local release-dir mode: the source is a file path; stage a copy.
        if (!File.Exists(source))
            throw new FileNotFoundException($"Local asset not found: {source}", source);
        File.Copy(source, dest, overwrite: true);
        var size = new FileInfo(dest).Length;
        progress?.Report((size, size));
        return dest;
    }
}
