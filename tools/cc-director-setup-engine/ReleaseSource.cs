using System.Net;
using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>A manifest plus the per-asset download URLs needed to fetch each asset.</summary>
public sealed record ResolvedRelease(ReleaseManifest Manifest, IReadOnlyDictionary<string, string> DownloadUrls);

/// <summary>
/// Resolves the release a plan is computed against. Shared by every front-end and updater (the
/// launcher, the Gateway, the tools updater, both setup wizards and the setup command line). Modes:
///   - a local manifest file (LoadLocalManifest): plan/dry-run only, no URLs.
///   - a local release directory (LoadLocalReleaseDir): offline install/update.
///   - "latest" (FetchLatestAsync): the newest STABLE release on the download server, read from
///     stable.json and that release's version folder. Used by the silent auto-updaters.
///   - "for this setup exe" (FetchReleaseForSetupAsync): the release the running setup executable
///     was BUILT for. A stable setup exe resolves the latest stable (identical to FetchLatestAsync);
///     a pre-release setup exe resolves its own version folder, else the newest release of any kind
///     (issue #1294).
///
/// Every request goes to <see cref="DownloadServer.BaseUrl"/>. There is no second source: if the
/// download server cannot answer, the fetch fails with a message naming the address it asked.
/// </summary>
public sealed class ReleaseSource
{
    private readonly HttpClient _http;

    public ReleaseSource(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("cc-director-setup");
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
        var manifestPath = Path.Combine(dir, DownloadServer.ManifestName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No {DownloadServer.ManifestName} in release dir: {dir}", manifestPath);

        var manifest = ReleaseManifest.Parse(File.ReadAllText(manifestPath));
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetName in manifest.Assets.Keys)
        {
            var assetPath = Path.Combine(dir, assetName);
            if (File.Exists(assetPath)) urls[assetName] = assetPath; // local path acts as the URL
        }
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// Fetch the newest STABLE release: read stable.json, then the manifest in the version folder it
    /// names. Every asset the manifest lists resolves to the same folder. Used by the silent
    /// auto-updaters and by a stable setup exe.
    /// </summary>
    /// <exception cref="HttpRequestException">The download server did not answer, or answered an error.</exception>
    /// <exception cref="InvalidOperationException">The channel file or the manifest it names is not usable.</exception>
    public async Task<ResolvedRelease> FetchLatestAsync(CancellationToken ct)
    {
        EngineLog.Write($"[ReleaseSource] FetchLatestAsync: reading {DownloadServer.FileUrl(DownloadServer.StableChannel)}");
        return await FetchFromChannelAsync(DownloadServer.StableChannel, ct);
    }

    /// <summary>
    /// Resolve the release the running setup executable was BUILT for, decided in memory from the
    /// setup exe's own stamped version - nothing is written to disk (issue #1294).
    ///
    /// A stable setup exe (version with no pre-release suffix, or an unreadable stamp) resolves the
    /// latest stable release - identical to <see cref="FetchLatestAsync"/>. A pre-release setup exe
    /// (for example "1.1.0-rc4") resolves its OWN version folder when that folder exists, so the rc4
    /// installer installs rc4 even when a newer rc5 is out; when it does not exist, the newest release
    /// of any kind named by prerelease.json.
    /// </summary>
    /// <param name="setupVersion">
    /// The setup exe's stamped version. Null reads it from the entry assembly
    /// (<see cref="SetupExecutableVersion.Read"/>); tests pass it explicitly.
    /// </param>
    public async Task<ResolvedRelease> FetchReleaseForSetupAsync(CancellationToken ct, string? setupVersion = null)
    {
        setupVersion ??= SetupExecutableVersion.Read();

        if (!VersionUtil.HasPreReleaseSuffix(setupVersion))
        {
            EngineLog.Write($"[ReleaseSource] Setup version '{setupVersion}' is stable; installing the latest stable release.");
            return await FetchLatestAsync(ct);
        }

        var ownTag = TagForVersion(setupVersion);
        var ownManifestUrl = DownloadServer.ReleaseFileUrl(ownTag, DownloadServer.ManifestName);
        EngineLog.Write($"[ReleaseSource] Setup version '{setupVersion}' is a pre-release; looking for its own folder at {ownManifestUrl}");

        var ownManifest = await GetTextAsync(ownManifestUrl, notFoundIsAbsent: true, ct);
        if (ownManifest is not null)
        {
            EngineLog.Write($"[ReleaseSource] Found the folder for {ownTag}; installing exactly that release.");
            return Resolve(ownTag, ownManifest, ownManifestUrl);
        }

        EngineLog.Write($"[ReleaseSource] No folder for {ownTag}; installing the newest release named by {DownloadServer.PreReleaseChannel}.");
        return await FetchFromChannelAsync(DownloadServer.PreReleaseChannel, ct);
    }

    /// <summary>
    /// Fetch ONE named release: the manifest in its own version folder. Used where a release is already chosen and
    /// "the latest" would be the wrong answer - the launcher bringing the cc-* tools to the version of the Director
    /// it is about to install (issue #4744). A folder that is not there is an error, never a quiet step to another
    /// release.
    /// </summary>
    /// <exception cref="HttpRequestException">The download server did not answer, or has no such release.</exception>
    /// <exception cref="InvalidOperationException">The manifest in that folder is for another release.</exception>
    public async Task<ResolvedRelease> FetchReleaseAsync(string version, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var tag = TagForVersion(version);
        var manifestUrl = DownloadServer.ReleaseFileUrl(tag, DownloadServer.ManifestName);
        EngineLog.Write($"[ReleaseSource] FetchReleaseAsync: reading {manifestUrl}");
        var manifestJson = await GetTextAsync(manifestUrl, notFoundIsAbsent: false, ct)
                           ?? throw new InvalidOperationException($"The download server returned nothing for {manifestUrl}.");
        return Resolve(tag, manifestJson, manifestUrl);
    }

    /// <summary>Read a channel file, then the manifest in the version folder it names.</summary>
    private async Task<ResolvedRelease> FetchFromChannelAsync(string channel, CancellationToken ct)
    {
        var channelUrl = DownloadServer.FileUrl(channel);
        var channelJson = await GetTextAsync(channelUrl, notFoundIsAbsent: false, ct)
                          ?? throw new InvalidOperationException($"The download server returned nothing for {channelUrl}.");
        var tag = ReadChannelTag(channelJson, channelUrl);

        // The channel file is written only after every file in its folder is verified, so a channel that
        // names a folder with no manifest is not a release still being published - it is a broken
        // server. It fails, by name; it is never read as "up to date".
        var manifestUrl = DownloadServer.ReleaseFileUrl(tag, DownloadServer.ManifestName);
        var manifestJson = await GetTextAsync(manifestUrl, notFoundIsAbsent: false, ct)
                           ?? throw new InvalidOperationException($"The download server returned nothing for {manifestUrl}.");
        return Resolve(tag, manifestJson, manifestUrl);
    }

    /// <summary>
    /// The tag a channel file names. It must be a non-empty string with no path characters, because it
    /// becomes a folder name in every download address.
    /// </summary>
    private static string ReadChannelTag(string channelJson, string channelUrl)
    {
        using var doc = JsonDocument.Parse(channelJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag", out var tagEl)
            || tagEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tagEl.GetString()))
            throw new InvalidOperationException($"{channelUrl} does not name a release tag.");

        var tag = tagEl.GetString()!.Trim();
        if (tag.Contains('/') || tag.Contains('\\') || tag.Contains(".."))
            throw new InvalidOperationException($"{channelUrl} names '{tag}', which is not a release tag.");
        return tag;
    }

    /// <summary>
    /// Parse the manifest and map every asset it names to its address in the same version folder. The
    /// manifest must be for the release its folder is named after - a folder holding another release's
    /// manifest would install the wrong build under the right name.
    /// </summary>
    private static ResolvedRelease Resolve(string tag, string manifestJson, string manifestUrl)
    {
        var manifest = ReleaseManifest.Parse(manifestJson);
        if (VersionUtil.CanonicalTag(manifest.Version) != VersionUtil.CanonicalTag(tag))
            throw new InvalidOperationException(
                $"{manifestUrl} is the manifest of version {manifest.Version}, not of {tag}.");

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in manifest.Assets.Keys)
            urls[name] = DownloadServer.ReleaseFileUrl(tag, name);
        urls[DownloadServer.ManifestName] = manifestUrl;

        EngineLog.Write($"[ReleaseSource] Resolved {tag}: {manifest.Assets.Count} asset(s) at {DownloadServer.ReleaseFileUrl(tag, "")}");
        return new ResolvedRelease(manifest, urls);
    }

    /// <summary>
    /// GET a text file from the download server. A 404 returns null only when the caller asked for
    /// that (the one place an absent file is an answer: a pre-release setup exe looking for its own
    /// folder). Every other non-success, and every 404 the caller did not expect, throws a message that
    /// names the address and the status. A server that cannot be reached at all throws a message that
    /// names the address too, with the transport's own error kept as the inner exception.
    /// </summary>
    private async Task<string?> GetTextAsync(string url, bool notFoundIsAbsent, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            EngineLog.Write($"[ReleaseSource] GET {url} FAILED: could not reach the download server: {ex.Message}");
            throw new HttpRequestException(
                $"Could not reach the download server at {url}: {ex.Message}", ex);
        }

        using var resp = response;
        if (resp.StatusCode == HttpStatusCode.NotFound && notFoundIsAbsent)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            EngineLog.Write($"[ReleaseSource] GET {url} FAILED: HTTP {(int)resp.StatusCode}");
            throw new HttpRequestException(
                $"The download server answered HTTP {(int)resp.StatusCode} for {url}.", null, resp.StatusCode);
        }

        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The folder name for a version: "1.1.0-rc3+abc" and "v1.1.0-rc3" are both "v1.1.0-rc3".</summary>
    private static string TagForVersion(string version)
    {
        var t = version.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        return "v" + t;
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

        var dest = Path.Combine(Path.GetTempPath(), $"devthrottle-setup-{Guid.NewGuid():N}-{assetName}");

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
