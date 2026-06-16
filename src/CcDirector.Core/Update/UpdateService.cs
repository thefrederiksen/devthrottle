using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>Configuration for <see cref="UpdateService"/>, resolved by the host app at startup.</summary>
public sealed record UpdateOptions
{
    /// <summary>When false the service is inert -- the gate for dev/slot builds (see csproj UpdaterEnabled).</summary>
    public bool Enabled { get; init; }

    /// <summary>The running build's version (from the entry assembly).</summary>
    public required Version CurrentVersion { get; init; }

    /// <summary>The path a new build must overwrite (exe on Windows, .app on macOS).</summary>
    public required string InstallTarget { get; init; }

    public string Owner { get; init; } = "thefrederiksen";
    public string Repo { get; init; } = "devthrottle";
}

/// <summary>An update that has been downloaded, verified, and is ready to apply.</summary>
public sealed record StagedUpdate(string Version, string StagedExecutable, string InstallTarget);

/// <summary>Lifecycle phase of an update check/download, surfaced to the UI.</summary>
public enum UpdatePhase
{
    /// <summary>Contacting GitHub to see whether a newer build exists.</summary>
    Checking,
    /// <summary>Downloading the new build's asset (byte progress in <see cref="UpdateProgress"/>).</summary>
    Downloading,
    /// <summary>Verifying the downloaded asset's SHA-256 against the release manifest.</summary>
    Verifying,
    /// <summary>A verified build is staged and will apply on next launch.</summary>
    Staged,
    /// <summary>Already on the latest build (or the only newer build was dismissed); nothing to do.</summary>
    UpToDate,
    /// <summary>The check/download failed (<see cref="UpdateProgress.Error"/> has the reason).</summary>
    Failed,
}

/// <summary>
/// Progress of an update check/download, raised on <see cref="UpdateService.ProgressChanged"/>.
/// The host marshals these to the UI thread.
/// </summary>
public sealed record UpdateProgress(
    UpdatePhase Phase,
    string? Version = null,
    long Downloaded = 0,
    long Total = 0,
    string? Error = null)
{
    /// <summary>Download fraction 0..1 when the total size is known; null when it is not.</summary>
    public double? Fraction => Total > 0 ? (double)Downloaded / Total : null;
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads the platform-appropriate
/// asset, verifies it against the release manifest's SHA-256, and stages it for
/// the user to apply via a "Restart now" banner. All network/disk work runs off
/// the UI thread; failures only log (no fallback that hides the problem).
/// </summary>
public sealed class UpdateService
{
    private const string ManifestAssetName = "release-manifest.json";

    private readonly UpdateOptions _options;
    private readonly HttpClient _http;

    /// <summary>Raised when an update has been downloaded and verified. Marshalled by the host to the UI thread.</summary>
    public event Action<StagedUpdate>? UpdateStaged;

    /// <summary>
    /// Raised on every phase transition and during download (roughly once per MiB).
    /// Marshalled by the host to the UI thread. Lets the app show a "checking" /
    /// "downloading N%" indicator and a progress bar instead of staging silently.
    /// </summary>
    public event Action<UpdateProgress>? ProgressChanged;

    public UpdateService(UpdateOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        // The asset is a ~100 MB single-file exe. HttpClient.Timeout governs the whole
        // operation -- including the streamed body read even under ResponseHeadersRead --
        // so a 60s ceiling would abort a legitimate download on a slow link. Give the
        // whole check+download a generous ceiling; the small metadata calls finish in well
        // under a second regardless.
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cc-director");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private void Report(UpdateProgress progress) => ProgressChanged?.Invoke(progress);

    /// <summary>
    /// Check for, download, and stage an update. Safe to fire-and-forget: this is
    /// the root of a background task, so it catches and logs all failures.
    /// </summary>
    public async Task CheckAndStageAsync(CancellationToken ct = default)
    {
        FileLog.Write($"[UpdateService] CheckAndStageAsync: current={_options.CurrentVersion}, enabled={_options.Enabled}");
        try
        {
            if (!_options.Enabled)
            {
                FileLog.Write("[UpdateService] Disabled for this build; skipping.");
                return;
            }

            var assetName = AssetNameFor(GetOSPlatform(), RuntimeInformation.OSArchitecture);
            if (assetName is null)
            {
                FileLog.Write($"[UpdateService] No asset mapping for {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}; skipping.");
                return;
            }

            var state = UpdaterState.Load();
            state.LastCheckedAt = DateTimeOffset.UtcNow;

            Report(new UpdateProgress(UpdatePhase.Checking));
            using var release = await FetchLatestReleaseAsync(ct);
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = TryParseTag(tag);
            if (latest is null)
            {
                FileLog.Write($"[UpdateService] Could not parse version from tag '{tag}'; skipping.");
                Report(new UpdateProgress(UpdatePhase.UpToDate));
                state.Save();
                return;
            }

            if (!ShouldStage(_options.CurrentVersion, latest, state))
            {
                FileLog.Write($"[UpdateService] Up to date or dismissed (latest={latest}, dismissed={state.DismissedVersion}).");
                Report(new UpdateProgress(UpdatePhase.UpToDate));
                state.Save();
                return;
            }

            var assetUrl = FindAssetUrl(release.RootElement, assetName);
            var manifestUrl = FindAssetUrl(release.RootElement, ManifestAssetName);
            if (assetUrl is null || manifestUrl is null)
            {
                FileLog.Write($"[UpdateService] Release {tag} missing asset '{assetName}' or manifest; skipping.");
                Report(new UpdateProgress(UpdatePhase.UpToDate));
                state.Save();
                return;
            }

            var versionText = $"{latest.Major}.{latest.Minor}.{Math.Max(latest.Build, 0)}";
            var staged = await DownloadAndStageAsync(versionText, assetName, assetUrl, manifestUrl, ct);
            if (staged is null)
            {
                Report(new UpdateProgress(UpdatePhase.Failed, versionText, Error: "download or verification failed"));
                state.Save();
                return;
            }

            state.StagedVersion = versionText;
            state.StagedExecutable = staged.StagedExecutable;
            state.InstallTarget = staged.InstallTarget;
            state.Save();

            FileLog.Write($"[UpdateService] Staged update {versionText}: {staged.StagedExecutable}");
            Report(new UpdateProgress(UpdatePhase.Staged, versionText));
            UpdateStaged?.Invoke(staged);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateService] CheckAndStageAsync FAILED: {ex.Message}");
            Report(new UpdateProgress(UpdatePhase.Failed, Error: ex.Message));
        }
    }

    private async Task<JsonDocument> FetchLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_options.Owner}/{_options.Repo}/releases/latest";
        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    private async Task<StagedUpdate?> DownloadAndStageAsync(
        string version, string assetName, string assetUrl, string manifestUrl, CancellationToken ct)
    {
        var dir = Path.Combine(UpdateInstaller.StagingRoot, version);
        Directory.CreateDirectory(dir);

        var assetPath = Path.Combine(dir, assetName);
        Report(new UpdateProgress(UpdatePhase.Downloading, version, 0, 0));
        var progress = new Progress<(long downloaded, long total)>(
            t => Report(new UpdateProgress(UpdatePhase.Downloading, version, t.downloaded, t.total)));
        await DownloadFileAsync(assetUrl, assetPath, progress, ct);

        Report(new UpdateProgress(UpdatePhase.Verifying, version));
        var expectedSha = await FetchExpectedShaAsync(manifestUrl, assetName, ct);
        if (expectedSha is null)
        {
            FileLog.Write($"[UpdateService] Manifest has no sha256 for '{assetName}'; rejecting download.");
            TryDelete(assetPath);
            return null;
        }
        if (!Sha256Matches(assetPath, expectedSha))
        {
            FileLog.Write($"[UpdateService] SHA-256 mismatch for '{assetName}'; rejecting download.");
            TryDelete(assetPath);
            return null;
        }

        string stagedExecutable;
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // macOS: the asset is a zipped .app bundle.
            var appDir = ExtractMacApp(assetPath, dir);
            stagedExecutable = Path.Combine(appDir, "Contents", "MacOS", UpdateInstaller.ExecutableName);
            StripQuarantine(appDir);
            MakeExecutable(stagedExecutable);
        }
        else
        {
            // Windows: the asset is the single-file exe itself.
            stagedExecutable = assetPath;
        }

        return new StagedUpdate(version, stagedExecutable, _options.InstallTarget);
    }

    /// <summary>
    /// Stream the asset to disk, reporting byte progress roughly once per MiB so the UI
    /// can drive a progress bar. <paramref name="progress"/> total is 0 when the server
    /// sends no Content-Length; a final report is always made on completion. Mirrors the
    /// download loop in the setup engine's ReleaseSource.
    /// </summary>
    private async Task DownloadFileAsync(
        string url, string destPath, IProgress<(long downloaded, long total)>? progress, CancellationToken ct)
    {
        FileLog.Write($"[UpdateService] Downloading {url} -> {destPath}");
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);

        if (progress is null)
        {
            await src.CopyToAsync(dst, ct);
            return;
        }

        var buffer = new byte[81920];
        long downloaded = 0, lastReported = 0;
        const long reportEvery = 1024 * 1024; // ~1 MiB between reports keeps UI marshaling cheap
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (downloaded - lastReported >= reportEvery)
            {
                lastReported = downloaded;
                progress.Report((downloaded, total));
            }
        }
        progress.Report((downloaded, total > 0 ? total : downloaded));
    }

    private async Task<string?> FetchExpectedShaAsync(string manifestUrl, string assetName, CancellationToken ct)
    {
        var resp = await _http.GetAsync(manifestUrl, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
        if (!assets.TryGetProperty(assetName, out var entry)) return null;
        if (!entry.TryGetProperty("sha256", out var sha)) return null;
        return sha.GetString();
    }

    // ---- Pure / static helpers (unit tested) -------------------------------

    /// <summary>Map an OS + architecture to the release asset filename, or null if unsupported.</summary>
    public static string? AssetNameFor(OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows && arch == Architecture.X64)
            return "cc-director-win-x64.exe";
        if (os == OSPlatform.OSX && arch == Architecture.Arm64)
            return "cc-director-mac-arm64.zip";
        return null;
    }

    /// <summary>Parse a release tag like "v0.3.3" or "0.3.3" into a normalized Version, or null.</summary>
    public static Version? TryParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        // Drop any pre-release/build suffix (e.g. "0.3.3-rc1") for comparison.
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>True when <paramref name="latest"/> is newer than <paramref name="current"/> and not dismissed.</summary>
    public static bool ShouldStage(Version current, Version latest, UpdaterState state)
    {
        var cur = Normalize(current);
        var lat = Normalize(latest);
        if (lat <= cur) return false;
        if (state.DismissedVersion is { } d && Version.TryParse(d, out var dv) && Normalize(dv) == lat)
            return false;
        return true;
    }

    /// <summary>Compute a file's SHA-256 and compare (case-insensitive hex) to the expected value.</summary>
    public static bool Sha256Matches(string filePath, string expectedHex)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hash);
        return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapse a Version to (Major, Minor, Build) so 4-part assembly versions compare cleanly.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static OSPlatform GetOSPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return OSPlatform.OSX;
        return OSPlatform.Linux;
    }

    private static string? FindAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n) && n.GetString() == assetName)
                return a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
        }
        return null;
    }

    private static string ExtractMacApp(string zipPath, string destDir)
    {
        // Extract under a fixed subfolder so the bundle path is predictable.
        var appRoot = Path.Combine(destDir, "extracted");
        if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
        Directory.CreateDirectory(appRoot);
        ZipFile.ExtractToDirectory(zipPath, appRoot);
        var app = Directory.EnumerateDirectories(appRoot, "*.app").FirstOrDefault()
            ?? throw new InvalidOperationException($"No .app bundle found inside {zipPath}");
        return app;
    }

    private static void StripQuarantine(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/xattr") { UseShellExecute = false };
            psi.ArgumentList.Add("-dr");
            psi.ArgumentList.Add("com.apple.quarantine");
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateService] StripQuarantine FAILED: {ex.Message}");
        }
    }

    private static void MakeExecutable(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/chmod") { UseShellExecute = false };
            psi.ArgumentList.Add("+x");
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateService] MakeExecutable FAILED: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
    }
}
