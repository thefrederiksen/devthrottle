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

    public string Owner { get; init; } = GitHubRepositoryDefaults.Owner;
    public string Repo { get; init; } = GitHubRepositoryDefaults.Repository;
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
    /// <summary>
    /// A newer release exists but its downloads have not been attached to it yet, so there is nothing
    /// to fetch. NOT a failure and NOT up to date (issue #1079).
    ///
    /// Publishing makes a release "latest" the instant the tag is pushed; the workflow that builds and
    /// attaches its assets finishes about five and a half minutes later. Every release we have ever cut
    /// has had that window, and any machine that checks inside it sees a newest release with no
    /// manifest. Reporting that as <see cref="UpToDate"/> - which is what this code did - meant a
    /// machine that had just failed to update looked exactly like a machine that had nothing to do, and
    /// then waited a full hour before trying again. It gets its own phase so it can say what it is and
    /// be retried in minutes.
    /// </summary>
    ReleaseNotReady,
    /// <summary>
    /// The latest release is COMPLETE - its manifest is attached - and it carries no build for this
    /// computer's operating system and processor.
    ///
    /// It shared a line with <see cref="ReleaseNotReady"/> and the pair reported "up to date", but the
    /// two are opposites: one is a release that has not finished publishing and gets better by itself,
    /// this one is a release that finished and has nothing for this machine. Waiting does not fix it, so
    /// it must not drive the short retry - that would poll a finished release for ever - and a person on
    /// such a machine needs to be told, because their Director will never update again until a release
    /// carries their platform.
    /// </summary>
    NoBuildForThisPlatform,
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
    ///
    /// Returns what the check CONCLUDED, and writes the same conclusion into the updater state
    /// (issue #1030). Both matter for different readers. The caller uses the return value to decide
    /// how soon to try again - a release whose assets have not attached yet is worth another look in
    /// minutes, not in an hour. Anything showing the status reads the persisted copy, because the
    /// conclusion has to survive the check that produced it: a Director started ten minutes after its
    /// last check still has to be able to say what that check found, and before this it could not.
    /// </summary>
    public async Task<UpdatePhase> CheckAndStageAsync(CancellationToken ct = default)
    {
        FileLog.Write($"[UpdateService] CheckAndStageAsync: current={_options.CurrentVersion}, enabled={_options.Enabled}");

        // Every terminal path goes through here, so no conclusion can be reported to the user without
        // also being written down - the split that let a failed check render as "up to date".
        UpdatePhase Conclude(UpdaterState state, UpdatePhase phase, string? version = null, string? error = null)
        {
            state.LastCheckOutcome = phase.ToString();
            state.LastCheckError = error;
            state.LastCheckLatestVersion = version ?? state.LastCheckLatestVersion;
            state.Save();
            Report(new UpdateProgress(phase, version, Error: error));
            return phase;
        }

        UpdaterState? loaded = null;
        try
        {
            if (!_options.Enabled)
            {
                FileLog.Write("[UpdateService] Disabled for this build; skipping.");
                return UpdatePhase.UpToDate;
            }

            var assetName = AssetNameFor(GetOSPlatform(), RuntimeInformation.OSArchitecture);
            if (assetName is null)
            {
                FileLog.Write($"[UpdateService] No asset mapping for {RuntimeInformation.OSDescription}/{RuntimeInformation.OSArchitecture}; skipping.");
                return UpdatePhase.UpToDate;
            }

            var state = loaded = UpdaterState.Load();
            state.LastCheckedAt = DateTimeOffset.UtcNow;

            Report(new UpdateProgress(UpdatePhase.Checking));
            using var release = await FetchLatestReleaseAsync(ct);
            var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = TryParseTag(tag);
            if (latest is null)
            {
                FileLog.Write($"[UpdateService] Could not parse version from tag '{tag}'; skipping.");
                return Conclude(state, UpdatePhase.Failed, error: $"the latest release is tagged '{tag}', which is not a version");
            }

            var versionText = $"{latest.Major}.{latest.Minor}.{Math.Max(latest.Build, 0)}";

            ClearPinIfSuperseded(state, latest);

            if (!ShouldStage(_options.CurrentVersion, latest, state))
            {
                FileLog.Write($"[UpdateService] Up to date or dismissed (latest={latest}, dismissed={state.DismissedVersion}).");
                return Conclude(state, UpdatePhase.UpToDate, versionText);
            }

            if (AlreadyStaged(versionText, state))
            {
                // This exact release was already downloaded, verified, and staged by an earlier check,
                // and its build is still on disk waiting for a restart. A Director that runs for days
                // re-checked every hour, and because this test was missing it re-downloaded the same
                // hundred-megabyte build every one of those hours and announced "downloaded" each time.
                // There is nothing to fetch until either a newer release appears or a restart applies
                // this one; if the staged file has meanwhile been deleted, the test below fails and the
                // download runs again, which is the repair.
                FileLog.Write($"[UpdateService] Release {tag} is already staged at {state.StagedExecutable}; not downloading it again.");
                return Conclude(state, UpdatePhase.Staged, versionText);
            }

            var assetUrl = FindAssetUrl(release.RootElement, assetName);
            var manifestUrl = FindAssetUrl(release.RootElement, ManifestAssetName);

            // These two used to be ONE test, and the pair reported "up to date". They are not the same
            // thing and only one of them gets better on its own.
            if (manifestUrl is null)
            {
                // The release exists and is newer, but nothing has been attached to it yet: the
                // five-and-a-half-minute publish window (issue #1079). The manifest is the sentinel,
                // because it is what names and hashes everything else. Waiting fixes this.
                FileLog.Write($"[UpdateService] Release {tag} has no manifest yet; its downloads have not been attached. "
                              + "Not up to date and not a failure - worth another look shortly.");
                return Conclude(state, UpdatePhase.ReleaseNotReady, versionText);
            }

            if (assetUrl is null)
            {
                // The release is COMPLETE - its manifest is attached - and carries no build for this
                // computer. Waiting does not fix that, so it must not be reported as the window above and
                // must not drive the short retry: a machine would poll a finished release for ever.
                FileLog.Write($"[UpdateService] Release {tag} is complete but has no '{assetName}'; there is no build for "
                              + "this platform in that release.");
                return Conclude(state, UpdatePhase.NoBuildForThisPlatform, versionText,
                    $"the release has no {assetName}");
            }

            var staged = await DownloadAndStageAsync(versionText, assetName, assetUrl, manifestUrl, ct);
            if (staged is null)
                return Conclude(state, UpdatePhase.Failed, versionText, "the download could not be verified");

            state.StagedVersion = versionText;
            state.StagedExecutable = staged.StagedExecutable;
            state.InstallTarget = staged.InstallTarget;

            FileLog.Write($"[UpdateService] Staged update {versionText}: {staged.StagedExecutable}");
            var phase = Conclude(state, UpdatePhase.Staged, versionText);
            UpdateStaged?.Invoke(staged);
            return phase;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateService] CheckAndStageAsync FAILED: {ex.Message}");
            // Write the failure down too. A check that fell over on the network used to leave the last
            // successful conclusion in place, so the display kept claiming the machine was up to date on
            // the strength of a check that had not worked for days.
            if (loaded is not null)
                return Conclude(loaded, UpdatePhase.Failed, error: ex.Message);
            Report(new UpdateProgress(UpdatePhase.Failed, Error: ex.Message));
            return UpdatePhase.Failed;
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

    /// <summary>
    /// True when <paramref name="latest"/> is newer than <paramref name="current"/>, not dismissed, and
    /// not a build already pinned as one that would not start.
    /// </summary>
    public static bool ShouldStage(Version current, Version latest, UpdaterState state)
    {
        var cur = Normalize(current);
        var lat = Normalize(latest);
        if (lat <= cur) return false;
        if (state.DismissedVersion is { } d && Version.TryParse(d, out var dv) && Normalize(dv) == lat)
            return false;

        // A pinned build will be refused at install time by both owners, so downloading it again is a
        // hundred megabytes fetched to be thrown away. This test was missing, and the install-time refusal
        // clears the staged record, so the very next check downloaded it once more: version 2.0.5 was
        // fetched roughly hourly for five days and discarded every time.
        if (state.PinnedBadVersion is { } p && Version.TryParse(p, out var pv) && Normalize(pv) == lat)
            return false;

        return true;
    }

    /// <summary>
    /// Drop a pin once the project has moved past the build it names, which is what
    /// <see cref="UpdaterState.PinnedBadVersion"/> has always documented and nothing has ever done.
    ///
    /// A pin is a permanent judgement written from a single failed start, and until now NOTHING in the
    /// product ever removed one - the field was set and never cleared, and the only cure was deleting the
    /// state file by hand. It survived because a newer version simply fails the string comparison and
    /// slips past, so the stale judgement sat in the file for ever and kept a machine re-downloading a
    /// build it would never install. A release newer than the pinned build settles the question: whatever
    /// was wrong then is not what this machine is being offered now.
    /// </summary>
    public static void ClearPinIfSuperseded(UpdaterState state, Version latest)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.PinnedBadVersion is not { } pinned) return;
        if (!Version.TryParse(pinned, out var pinnedVersion)) return;
        if (Normalize(latest) <= Normalize(pinnedVersion)) return;

        FileLog.Write($"[UpdateService] {latest} is newer than the pinned bad build {pinned}; clearing the pin.");
        state.PinnedBadVersion = null;
    }

    /// <summary>
    /// True when <paramref name="version"/> was already downloaded, verified, and staged by an earlier
    /// check and its staged executable is still on disk. <see cref="UpdaterState.StagedVersion"/> is
    /// only ever written after the SHA-256 verification passes, so a matching record plus a present
    /// file means the work is done; checking the version alone would trust a record whose file has
    /// since been deleted, and checking the file alone would trust a leftover from a different version.
    /// </summary>
    public static bool AlreadyStaged(string version, UpdaterState state)
        => state.StagedVersion == version
           && !string.IsNullOrEmpty(state.StagedExecutable)
           && File.Exists(state.StagedExecutable);

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
