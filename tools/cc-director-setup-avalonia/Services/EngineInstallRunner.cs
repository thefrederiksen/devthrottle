using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

/// <summary>
/// Cross-platform install runner for the Avalonia wizard - the analog of the WPF wizard's
/// EngineInstallRunner. It drives the SHARED CcDirector.Setup.Engine so macOS, Linux and Windows
/// install identically: place the Director (MacAppPlacer on macOS, the generic single-file swap on
/// Windows and Linux), install all cc-* tools as one shared venv (PythonToolsInstaller), then
/// finalize (PATH + app/shortcut).
///
/// Every release-asset name here comes from ComponentRegistry via Component.AssetFor(platform).
/// Do not reintroduce a two-way "Windows or else macOS" read: this is the wizard
/// scripts/install-linux.sh hands over to, and each of those reads gave Linux another platform's
/// download.
///
/// macOS and Linux are Workstation-only (no Gateway/Cockpit). Install LOCATIONS are owned by
/// InstallLayout.
/// </summary>
public sealed class EngineInstallRunner
{
    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly ReleaseSource _source = new();

    /// <summary>
    /// When set (--release-dir / DEVTHROTTLE_RELEASE_DIR, parsed in Program.Main), the wizard
    /// installs from this local directory instead of fetching the release from GitHub - the same
    /// offline override the setup command line offers.
    /// </summary>
    public static string? ReleaseDirectoryOverride { get; set; }

    /// <summary>The on-disk Director path for this OS (~/Applications/Director.app on macOS).</summary>
    public string DirectorPath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Everything ApplyAsync needs, plus the UI rows and up-to-date state.</summary>
    public sealed record Prep(
        string Version, ResolvedRelease Release, List<ToolDownloadItem> Items,
        IReadOnlyDictionary<string, ToolDownloadItem> ItemsById, string? InstalledDirectorVersion, bool IsUpToDate);

    /// <summary>Fetch the latest release and build the two UI rows (Director + the Python tools bundle).</summary>
    public async Task<Prep> PrepareAsync(CancellationToken ct = default)
    {
        SetupLog.Write("[EngineInstallRunner] PrepareAsync: resolving the release for this setup executable");
        // Install the release this setup exe was built for: a pre-release build installs its
        // matching pre-release, a stable build installs the latest stable (issue #1294). A local
        // release directory override wins over both (offline / hermetic install).
        ResolvedRelease release;
        if (ReleaseDirectoryOverride is { } releaseDir)
        {
            SetupLog.Write($"[EngineInstallRunner] PrepareAsync: using local release directory {releaseDir}");
            release = ReleaseSource.LoadLocalReleaseDir(releaseDir);
        }
        else
        {
            release = await _source.FetchReleaseForSetupAsync(ct);
        }
        var version = release.Manifest.Version;

        var items = new List<ToolDownloadItem>();
        var byId = new Dictionary<string, ToolDownloadItem>(StringComparer.OrdinalIgnoreCase);

        // The asset for THIS platform, from the registry. This line used to read
        // "Windows ? WindowsAsset : MacAppPlacer.DirectorAsset", so on Linux it asked the release
        // for the macOS application bundle - in the one wizard scripts/install-linux.sh hands over
        // to. The registry now carries all three names and answers by platform.
        var platform = HostPlatform.Current;
        var directorAssetName = ComponentRegistry.Director.AssetFor(platform);
        var dItem = new ToolDownloadItem { Name = "cc-director", AssetName = directorAssetName ?? "" };
        var dAsset = directorAssetName is null ? null : release.Manifest.TryGetAsset(directorAssetName);
        if (dAsset is null) { dItem.Status = "Skipped"; dItem.SizeText = "Not in release"; }
        else dItem.SizeText = FormatSize(dAsset.Size);
        items.Add(dItem); byId["director"] = dItem;

        // The shared-venv cc-* Python tools bundle is deliberately NOT an install-time row anymore: the
        // installer no longer provisions it (that ~334 MB download + venv build was the dominant install
        // time). The app provisions the bundle from nothing on first launch (ToolReconciler startup
        // reconcile), so the Install screen shows an honest "finishes on first launch" note instead.

        // The launcher installs on macOS only in THIS wizard: the Windows wizard (the WPF one)
        // already installs it there. Older releases have no macOS launcher asset - skip cleanly.
        if (OperatingSystem.IsMacOS() && ComponentRegistry.Launcher.AssetFor(platform) is { } launcherAssetName)
        {
            var lItem = new ToolDownloadItem { Name = ComponentRegistry.Launcher.Id, AssetName = launcherAssetName };
            var lAsset = release.Manifest.TryGetAsset(launcherAssetName);
            if (lAsset is null) { lItem.Status = "Skipped"; lItem.SizeText = "Not in release"; }
            else lItem.SizeText = FormatSize(lAsset.Size);
            items.Add(lItem); byId[ComponentRegistry.Launcher.Id] = lItem;
        }

        var reader = new InstalledStateReader(_layout);
        var installedDirector = reader.Read(ComponentRegistry.Director).Version;

        // "Up to date" skips the ENTIRE apply phase, so it has to be true of everything this wizard
        // installs - not of the Director alone. It used to be the Director alone, which meant a machine
        // with a current Director and a stale or missing launcher was told it was up to date and the
        // launcher was never touched, while its card showed a status nothing had checked.
        // Judged against the assets for THIS platform. Reading the macOS names here meant a Linux
        // machine's "up to date" verdict was decided by the version of a macOS download it was never
        // going to install.
        var upToDate = IsCurrent(reader, release, ComponentRegistry.Director, directorAssetName)
                       && IsCurrent(reader, release, ComponentRegistry.Launcher, ComponentRegistry.Launcher.AssetFor(platform));

        SetupLog.Write($"[EngineInstallRunner] PrepareAsync: version={version}, installedDirector={installedDirector}, upToDate={upToDate}");
        return new Prep(version, release, items, byId, installedDirector, upToDate);
    }

    /// <summary>
    /// Is this component's installed version the one in the release? Used to decide "up to date",
    /// which must hold for EVERY component the wizard installs. A component with no asset in this
    /// release cannot be out of date - there is nothing to install - so it does not block the verdict.
    /// </summary>
    private static bool IsCurrent(InstalledStateReader reader, ResolvedRelease release, Component component, string? assetName)
    {
        if (assetName is null) return true;
        var asset = release.Manifest.TryGetAsset(assetName);
        if (asset is null) return true;

        var installed = reader.Read(component).Version;
        return installed != null
               && VersionUtil.TryParse(installed) is { } iv
               && VersionUtil.TryParse(asset.Version) is { } rv
               && iv == rv;
    }

    /// <summary>Place the Director, install the tools bundle, install the launcher (macOS),
    /// finalize. Returns (installed, skipped).</summary>
    public async Task<(int installed, int skipped)> ApplyAsync(Prep prep, IProgress<string>? status = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prep);

        var directorOk = await PlaceDirectorAsync(prep, status, ct);
        // The installer does NOT provision the Python tools bundle - the app provisions it from nothing on
        // first launch (see InstallerToolsProvisioning). The real provisioner is wired but deliberately not
        // invoked, so re-enabling it is a one-line revert pinned by InstallerToolsProvisioningTests.
        var toolCount = await InstallerToolsProvisioning.ProvisionDuringInstallAsync(
            innerCt => InstallPythonToolsAsync(prep, status, innerCt), ct);
        var launcherOk = await InstallLauncherAsync(prep, status, ct);
        FinalizeInstall();

        var installed = (directorOk ? 1 : 0) + toolCount + (launcherOk ? 1 : 0);
        var skipped = prep.Items.Count(i => i.Status is "Skipped" or "Failed");
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: installed={installed}, skipped={skipped}");
        return (installed, skipped);
    }

    private async Task<bool> PlaceDirectorAsync(Prep prep, IProgress<string>? status, CancellationToken ct)
    {
        prep.ItemsById.TryGetValue("director", out var item);

        if (OperatingSystem.IsMacOS())
        {
            if (prep.Release.Manifest.TryGetAsset(MacAppPlacer.DirectorAsset) is null)
            {
                if (item is not null) { item.Status = "Skipped"; item.StatusDetail = "Not in release"; }
                return false;
            }
            if (item is not null) item.Status = "Installing...";
            var res = await MacAppPlacer.PlaceAsync(_layout, prep.Release, _source,
                m => { if (item is not null) item.Status = m; status?.Report(m); }, ct);
            if (item is not null) { item.Status = res.Success ? "Done" : "Failed"; if (!res.Success) item.StatusDetail = res.Message; }
            return res.Success;
        }

        // Windows and Linux: place the single self-contained Director executable via the generic
        // runner. This lookup was hard-coded to WindowsAsset, so on Linux it found
        // cc-director-win-x64.exe in the release, downloaded 118 MB of Windows executable, placed it,
        // marked the row Done and installed a Director that cannot run. A wrong answer that reports
        // success is worse than no answer, which is why AssetFor has no fall-through branch.
        var assetName = ComponentRegistry.Director.AssetFor(HostPlatform.Current);
        var asset = assetName is null ? null : prep.Release.Manifest.TryGetAsset(assetName);
        if (asset is null)
        {
            if (item is not null) { item.Status = "Skipped"; item.StatusDetail = "Not in release"; }
            return false;
        }
        var plan = new UpdatePlan
        {
            Items = [new PlanItem(ComponentRegistry.Director.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256)],
        };
        var runner = new UpdateRunner(_layout, ComponentRegistry.Apps, (planItem, innerCt) =>
        {
            if (item is not null) item.Status = "Downloading";

            // Live byte progress: drive the row's bar and turn its size label into a
            // "12.3 MB / 45.6 MB" counter while the download runs (restored when done).
            var download = new Progress<(long downloaded, long total)>(p =>
            {
                if (item is null) return;
                var total = p.total > 0 ? p.total : asset.Size;
                if (total <= 0) return;
                item.Progress = Math.Min(100.0, p.downloaded * 100.0 / total);
                item.SizeText = p.downloaded >= total
                    ? FormatSize(total)
                    : $"{FormatSize(p.downloaded)} / {FormatSize(total)}";
            });
            return _source.DownloadAssetAsync(planItem.AssetName, prep.Release.DownloadUrls, innerCt, download);
        });
        var result = await runner.ApplyAsync(plan, ct);
        var ok = result.Results.Any(r => r.ComponentId == ComponentRegistry.Director.Id
            && r.Status is ApplyStatus.Installed or ApplyStatus.Updated);
        if (item is not null) item.Status = ok ? "Done" : "Failed";
        return ok;
    }

    private async Task<int> InstallPythonToolsAsync(Prep prep, IProgress<string>? status, CancellationToken ct)
    {
        prep.ItemsById.TryGetValue(PythonToolsInstaller.ComponentId, out var item);

        var pyAsset = prep.Release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        var toolsAsset = prep.Release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        if (pyAsset is null || toolsAsset is null)
        {
            if (item is not null) { item.Status = "Skipped"; item.StatusDetail = "No tools bundle in this release"; }
            return 0;
        }

        if (item is not null) item.Status = "Installing tools...";
        var progress = new Progress<string>(m => { if (item is not null) item.Status = m; status?.Report(m); });
        var percent = new Progress<int>(p => { if (item is not null) item.Progress = p; });
        // PythonToolsInstaller uses synchronous process calls (venv, pip); offload so the UI thread is free.
        var res = await Task.Run(() => new PythonToolsInstaller(_layout).InstallAsync(prep.Release, _source, progress, percent, ct), ct);
        if (item is not null) { item.Status = res.Success ? "Done" : "Failed"; if (!res.Success) item.StatusDetail = res.Message; }
        return res.Success ? res.ToolCount : 0;
    }

    /// <summary>
    /// Place and start the launcher on macOS: download and swap the single-file binary via the
    /// generic runner (which also sets its executable permission), then hand over to
    /// <see cref="LauncherMacInstaller"/> for the first start, the health wait, and the
    /// launch-agent verification. On Windows this wizard does nothing - the Windows wizard
    /// (the WPF one) installs the launcher there. Returns true when the launcher was installed
    /// and is healthy.
    /// </summary>
    private async Task<bool> InstallLauncherAsync(Prep prep, IProgress<string>? status, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        prep.ItemsById.TryGetValue(ComponentRegistry.Launcher.Id, out var item);

        var assetName = ComponentRegistry.Launcher.AssetFor(HostPlatform.Current);
        if (assetName is null) return false;
        var asset = prep.Release.Manifest.TryGetAsset(assetName);
        if (asset is null)
        {
            if (item is not null) { item.Status = "Skipped"; item.StatusDetail = "Not in release"; }
            SetupLog.Write($"[EngineInstallRunner] InstallLauncherAsync: {assetName} not in release; skipping");
            return false;
        }

        SetupLog.Write($"[EngineInstallRunner] InstallLauncherAsync: placing {assetName}");
        status?.Report("Installing the launcher...");
        var plan = new UpdatePlan
        {
            Items = [new PlanItem(ComponentRegistry.Launcher.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256)],
        };
        var runner = new UpdateRunner(_layout, ComponentRegistry.Apps, (planItem, innerCt) =>
        {
            if (item is not null) item.Status = "Downloading";
            var download = new Progress<(long downloaded, long total)>(p =>
            {
                if (item is null) return;
                var total = p.total > 0 ? p.total : asset.Size;
                if (total <= 0) return;
                item.Progress = Math.Min(100.0, p.downloaded * 100.0 / total);
                item.SizeText = p.downloaded >= total
                    ? FormatSize(total)
                    : $"{FormatSize(p.downloaded)} / {FormatSize(total)}";
            });
            return _source.DownloadAssetAsync(planItem.AssetName, prep.Release.DownloadUrls, innerCt, download);
        });
        var placeResult = await runner.ApplyAsync(plan, ct);
        var placed = placeResult.Results.Any(r => r.ComponentId == ComponentRegistry.Launcher.Id
            && r.Status is ApplyStatus.Installed or ApplyStatus.Updated);
        if (!placed)
        {
            var error = placeResult.Results.FirstOrDefault(r => r.ComponentId == ComponentRegistry.Launcher.Id)?.Error;
            if (item is not null) { item.Status = "Failed"; item.StatusDetail = error ?? "Placement failed"; }
            SetupLog.Write($"[EngineInstallRunner] InstallLauncherAsync FAILED to place: {error}");
            return false;
        }

        if (item is not null) item.Status = "Starting...";
        status?.Report("Starting the launcher...");
        var startResult = await new LauncherMacInstaller(_layout).InstallAsync(ct);
        foreach (var step in startResult.Steps)
            SetupLog.Write($"[EngineInstallRunner]   launcher: {step}");
        SetupLog.Write($"[EngineInstallRunner] InstallLauncherAsync: start success={startResult.Success}: {startResult.Message}");
        if (item is not null)
        {
            item.Status = startResult.Success ? "Done" : "Failed";
            if (!startResult.Success) item.StatusDetail = startResult.Message;
        }
        return startResult.Success;
    }

    private void FinalizeInstall()
    {
        if (OperatingSystem.IsWindows())
        {
            InstallFinalizer.AddBinToPath(_layout);
            InstallFinalizer.CreateDirectorShortcut(_layout);
        }
        else if (OperatingSystem.IsMacOS())
        {
            InstallFinalizer.EnsureMacUserBinOnPath();
        }
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";
}
