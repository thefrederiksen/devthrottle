using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

/// <summary>
/// Cross-platform install runner for the Avalonia wizard - the analog of the WPF wizard's
/// EngineInstallRunner. It drives the SHARED CcDirector.Setup.Engine so macOS and Windows install
/// identically: place the Director (MacAppPlacer on macOS, the generic swap on Windows), install all
/// cc-* tools as one shared venv (PythonToolsInstaller), then finalize (PATH + app/shortcut).
///
/// macOS is Workstation-only (no Gateway/Cockpit). Install LOCATIONS are owned by InstallLayout.
/// </summary>
public sealed class EngineInstallRunner
{
    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly ReleaseSource _source = new();

    /// <summary>The on-disk Director path for this OS (~/Applications/CC Director.app on macOS).</summary>
    public string DirectorPath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Everything ApplyAsync needs, plus the UI rows and up-to-date state.</summary>
    public sealed record Prep(
        string Version, ResolvedRelease Release, List<ToolDownloadItem> Items,
        IReadOnlyDictionary<string, ToolDownloadItem> ItemsById, string? InstalledDirectorVersion, bool IsUpToDate);

    /// <summary>Fetch the latest release and build the two UI rows (Director + the Python tools bundle).</summary>
    public async Task<Prep> PrepareAsync(CancellationToken ct = default)
    {
        SetupLog.Write("[EngineInstallRunner] PrepareAsync: fetching latest release");
        var release = await _source.FetchLatestAsync(ct);
        var version = release.Manifest.Version;

        var items = new List<ToolDownloadItem>();
        var byId = new Dictionary<string, ToolDownloadItem>(StringComparer.OrdinalIgnoreCase);

        var directorAssetName = OperatingSystem.IsWindows() ? ComponentRegistry.Director.WindowsAsset : MacAppPlacer.DirectorAsset;
        var dItem = new ToolDownloadItem { Name = "cc-director", AssetName = directorAssetName };
        var dAsset = release.Manifest.TryGetAsset(directorAssetName);
        if (dAsset is null) { dItem.Status = "Skipped"; dItem.SizeText = "Not in release"; }
        else dItem.SizeText = FormatSize(dAsset.Size);
        items.Add(dItem); byId["director"] = dItem;

        var bItem = new ToolDownloadItem { Name = PythonToolsInstaller.ComponentId, AssetName = PythonToolsInstaller.ToolsAsset };
        var pyAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        var toolsAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        if (pyAsset is null || toolsAsset is null) { bItem.Status = "Skipped"; bItem.SizeText = "Not in release"; }
        else bItem.SizeText = FormatSize(pyAsset.Size + toolsAsset.Size);
        items.Add(bItem); byId[PythonToolsInstaller.ComponentId] = bItem;

        var reader = new InstalledStateReader(_layout);
        var installedDirector = reader.Read(ComponentRegistry.Director).Version;
        var upToDate = installedDirector != null && dAsset != null
            && VersionUtil.TryParse(installedDirector) is { } iv
            && VersionUtil.TryParse(dAsset.Version) is { } rv && iv == rv;

        SetupLog.Write($"[EngineInstallRunner] PrepareAsync: version={version}, installedDirector={installedDirector}, upToDate={upToDate}");
        return new Prep(version, release, items, byId, installedDirector, upToDate);
    }

    /// <summary>Place the Director, install the tools bundle, finalize. Returns (installed, skipped).</summary>
    public async Task<(int installed, int skipped)> ApplyAsync(Prep prep, IProgress<string>? status = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prep);

        var directorOk = await PlaceDirectorAsync(prep, status, ct);
        var toolCount = await InstallPythonToolsAsync(prep, status, ct);
        FinalizeInstall();

        var installed = (directorOk ? 1 : 0) + toolCount;
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

        // Windows / other: place the single Director exe via the generic runner.
        var asset = prep.Release.Manifest.TryGetAsset(ComponentRegistry.Director.WindowsAsset);
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
            return _source.DownloadAssetAsync(planItem.AssetName, prep.Release.DownloadUrls, innerCt);
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
        // PythonToolsInstaller uses synchronous process calls (venv, pip); offload so the UI thread is free.
        var res = await Task.Run(() => new PythonToolsInstaller(_layout).InstallAsync(prep.Release, _source, progress, ct), ct);
        if (item is not null) { item.Status = res.Success ? "Done" : "Failed"; if (!res.Success) item.StatusDetail = res.Message; }
        return res.Success ? res.ToolCount : 0;
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
