using System.Diagnostics;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

/// <summary>
/// Drives an install/update through the shared CcDirector.Setup.Engine (the same
/// engine the headless CLI uses), then layers on the installer-only concerns the
/// engine does not own: per-user PATH, the Start Menu shortcut, and migrating the
/// Director off its retired bin\ location to the canonical app\ location.
///
/// The interactive installer always pulls the published build for every in-scope
/// component (force install). Per-component "is it behind?" skipping is the silent
/// background updater's job (the resident Director/Gateway), not the installer's -
/// and Python tool exes may not carry a readable version stamp to compare anyway.
///
/// Install LOCATIONS are owned by InstallLayout (master spec: docs/install/INSTALLATION.md).
/// </summary>
public sealed class EngineInstallRunner
{
    /// <summary>Workstation (default) or Gateway. Decides which components are in scope.</summary>
    public InstallRole Role { get; init; } = InstallRole.Workstation;

    /// <summary>
    /// Invoked when the Director would be replaced while running. Parameter: process
    /// name. Returns true to retry (user closed it), false to skip the Director.
    /// </summary>
    public Func<string, Task<bool>>? OnProcessBlocking { get; set; }

    /// <summary>
    /// Invoked with the real installed cc-* tool count once the Python tools bundle finishes, so the
    /// UI can show the true number - the bundle is a single row, not one row per tool, so the row
    /// count (1) is not the tool count.
    /// </summary>
    public Action<int>? OnToolsInstalled { get; set; }

    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly ReleaseSource _source = new();

    /// <summary>The per-user tools directory added to PATH (also where the Director's app\ sibling lives).</summary>
    public string BinDir => _layout.BinDir;

    /// <summary>The canonical Director exe path (%LOCALAPPDATA%\cc-director\app\cc-director.exe).</summary>
    public string AppExePath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Everything <see cref="ApplyAsync"/> needs, plus the UI items and up-to-date state.</summary>
    public sealed record Prep(
        string Version,
        ResolvedRelease Release,
        IReadOnlyList<Component> Components,
        List<ToolDownloadItem> Items,
        IReadOnlyDictionary<string, ToolDownloadItem> ItemsByComponentId,
        string? InstalledDirectorVersion,
        bool IsUpToDate);

    /// <summary>Fetch the latest release, resolve the in-scope components, and build the UI item list.</summary>
    public async Task<Prep> PrepareAsync(CancellationToken ct = default)
    {
        SetupLog.Write("[EngineInstallRunner] PrepareAsync: fetching latest release");
        var release = await _source.FetchLatestAsync(ct);
        var version = release.Manifest.Version;

        // The engine places the Director here. The Gateway tray app + Cockpit are installed by the
        // gateway phase (MainWindow.RunGatewayTrayInstallAsync shells the CLI - per-user, no elevation),
        // NOT by this generic placer (it cannot extract the Cockpit zip or start the tray); the wizard
        // shows them as their own "Gateway & Cockpit" card. Every cc-* Python tool ships as ONE
        // shared-venv bundle, shown as a single row.
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, Role);

        var items = new List<ToolDownloadItem>();
        var byId = new Dictionary<string, ToolDownloadItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in components)
        {
            // The InstallStep UI keys the Director row by Name == "cc-director".
            var uiName = c.Kind == ComponentKind.Director ? "cc-director" : c.Id;
            var item = new ToolDownloadItem { Name = uiName, AssetName = c.WindowsAsset };
            var asset = release.Manifest.TryGetAsset(c.WindowsAsset);
            if (asset is null) { item.Status = "Skipped"; item.SizeText = "Not in release"; }
            else item.SizeText = FormatSize(asset.Size);
            items.Add(item);
            byId[c.Id] = item;
        }

        // One row for all cc-* Python tools (the shared-venv bundle).
        var bundleItem = new ToolDownloadItem
        {
            Name = PythonToolsInstaller.ComponentId,
            AssetName = PythonToolsInstaller.ToolsAsset,
        };
        var pyAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        var toolsAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        if (pyAsset is null || toolsAsset is null) { bundleItem.Status = "Skipped"; bundleItem.SizeText = "Not in release"; }
        else bundleItem.SizeText = FormatSize(pyAsset.Size + toolsAsset.Size);
        items.Add(bundleItem);
        byId[PythonToolsInstaller.ComponentId] = bundleItem;

        var reader = new InstalledStateReader(_layout);
        var installedDirector = reader.Read(ComponentRegistry.Director).Version;
        var directorAsset = release.Manifest.TryGetAsset(ComponentRegistry.Director.WindowsAsset);
        var upToDate = installedDirector != null && directorAsset != null
            && VersionUtil.TryParse(installedDirector) is { } iv
            && VersionUtil.TryParse(directorAsset.Version) is { } rv
            && iv == rv;

        SetupLog.Write($"[EngineInstallRunner] PrepareAsync: version={version}, components={components.Count}, " +
                       $"installedDirector={installedDirector}, upToDate={upToDate}");
        return new Prep(version, release, components, items, byId, installedDirector, upToDate);
    }

    /// <summary>Install/refresh every in-scope component, then wire PATH + shortcut. Returns (installed, skipped).</summary>
    public async Task<(int installed, int skipped)> ApplyAsync(Prep prep, CancellationToken ct = default)
    {
        var planItems = new List<PlanItem>();
        foreach (var c in prep.Components)
        {
            var asset = prep.Release.Manifest.TryGetAsset(c.WindowsAsset);
            if (asset is null)
            {
                Set(prep, c.Id, "Skipped", "Not in release");
                continue;
            }
            planItems.Add(new PlanItem(c.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256));
        }

        planItems = await HandleDirectorRunningAsync(prep, planItems);

        var runner = new UpdateRunner(_layout, prep.Components, (item, innerCt) =>
        {
            Set(prep, item.ComponentId, "Downloading", null);

            // Live byte progress: drive the row's bar and turn its size label into a
            // "12.3 MB / 45.6 MB" counter while the download runs (restored when done).
            prep.ItemsByComponentId.TryGetValue(item.ComponentId, out var uiItem);
            var expectedSize = prep.Release.Manifest.TryGetAsset(item.AssetName)?.Size ?? 0;
            var download = new Progress<(long downloaded, long total)>(p =>
            {
                if (uiItem is null) return;
                var total = p.total > 0 ? p.total : expectedSize;
                if (total <= 0) return;
                uiItem.Progress = Math.Min(100.0, p.downloaded * 100.0 / total);
                uiItem.SizeText = p.downloaded >= total
                    ? FormatSize(total)
                    : $"{FormatSize(p.downloaded)} / {FormatSize(total)}";
            });
            return _source.DownloadAssetAsync(item.AssetName, prep.Release.DownloadUrls, innerCt, download);
        });

        var result = await runner.ApplyAsync(new UpdatePlan { Items = planItems }, ct);

        foreach (var r in result.Results)
        {
            var status = r.Status switch
            {
                ApplyStatus.Installed or ApplyStatus.Updated => "Done",
                ApplyStatus.Skipped => "Skipped",
                _ => "Failed",
            };
            Set(prep, r.ComponentId, status, r.Error);
        }

        // Install every Python tool as one shared venv (no-ops cleanly if the release has no bundle).
        var toolCount = await InstallPythonToolsAsync(prep, ct);

        MigrateLegacyDirector();
        PathManager.AddToPath(_layout.BinDir);
        if (File.Exists(AppExePath))
            ShortcutCreator.CreateStartMenuShortcut(AppExePath);

        var installed = result.Installed + result.Updated + toolCount;
        var skipped = prep.Items.Count(i => i.Status is "Skipped" or "Failed");
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: installed={installed}, skipped={skipped}");
        return (installed, skipped);
    }

    /// <summary>
    /// Install all cc-* Python tools as one shared venv via the engine's PythonToolsInstaller.
    /// The pip work is synchronous and slow, so it runs on a background thread to keep the UI
    /// responsive; progress messages flow back to the bundle's row through a Progress&lt;string&gt;.
    /// Returns the number of tools installed (0 if the release has no bundle or the install fails).
    /// </summary>
    private async Task<int> InstallPythonToolsAsync(Prep prep, CancellationToken ct)
    {
        prep.ItemsByComponentId.TryGetValue(PythonToolsInstaller.ComponentId, out var bundleItem);

        var pyAsset = prep.Release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        var toolsAsset = prep.Release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        if (pyAsset is null || toolsAsset is null)
        {
            if (bundleItem is not null) { bundleItem.Status = "Skipped"; bundleItem.StatusDetail = "No tools bundle in this release"; }
            SetupLog.Write("[EngineInstallRunner] no Python tools bundle in release; skipping tools");
            return 0;
        }

        if (bundleItem is not null) bundleItem.Status = "Installing tools...";
        var installer = new PythonToolsInstaller(_layout);
        var progress = new Progress<string>(msg => { if (bundleItem is not null) bundleItem.Status = msg; });
        var percent = new Progress<int>(p => { if (bundleItem is not null) bundleItem.Progress = p; });

        // PythonToolsInstaller uses synchronous process calls (venv, pip); offload so the UI thread is free.
        var result = await Task.Run(() => installer.InstallAsync(prep.Release, _source, progress, percent, ct), ct);

        // Report the real tool count before flipping the row to Done, so the UI's summary
        // (driven by the row's status change) already has the true number when it refreshes.
        if (result.Success) OnToolsInstalled?.Invoke(result.ToolCount);
        if (bundleItem is not null)
        {
            bundleItem.Status = result.Success ? "Done" : "Failed";
            if (!result.Success) bundleItem.StatusDetail = result.Message;
        }
        SetupLog.Write($"[EngineInstallRunner] Python tools: success={result.Success}, count={result.ToolCount}");
        return result.Success ? result.ToolCount : 0;
    }

    private async Task<List<PlanItem>> HandleDirectorRunningAsync(Prep prep, List<PlanItem> planItems)
    {
        var director = planItems.FirstOrDefault(i => i.ComponentId == ComponentRegistry.Director.Id);
        if (director is null) return planItems;

        while (IsDirectorRunning())
        {
            if (OnProcessBlocking is null || !await OnProcessBlocking("cc-director"))
            {
                Set(prep, director.ComponentId, "Skipped", "DevThrottle was running");
                return planItems.Where(i => i.ComponentId != director.ComponentId).ToList();
            }
        }
        return planItems;
    }

    /// <summary>
    /// True only when the Director WE are about to overwrite (the canonical app\ exe) is running.
    /// Scoped to the target install path on purpose: a second Director running from a different
    /// location (e.g. a dev/test build under local_builds\) must NOT block this install. Matching by
    /// process name alone would falsely block whenever any cc-director was alive on the machine.
    /// </summary>
    private bool IsDirectorRunning()
    {
        var target = NormalizePath(AppExePath);
        if (target is null) return false;

        var procs = Process.GetProcessesByName("cc-director");
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    var exe = NormalizePath(p.MainModule?.FileName);
                    if (exe is not null && string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // MainModule can throw (access denied / different bitness). A process we cannot
                    // introspect is not assumed to be our target; a genuine same-path conflict would
                    // still surface as a file lock during the swap.
                }
            }
            return false;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>
    /// Move the user off the retired bin\cc-director.exe location: once the Director is
    /// installed at app\cc-director.exe (canonical), delete the stale bin\ copy so it is
    /// not a duplicate on PATH. Best-effort - a failure here must not fail a good install.
    /// </summary>
    private void MigrateLegacyDirector()
    {
        var legacy = Path.Combine(_layout.BinDir, "cc-director.exe");
        if (!File.Exists(AppExePath) || !File.Exists(legacy)) return;
        try
        {
            File.Delete(legacy);
            SetupLog.Write($"[EngineInstallRunner] Removed legacy Director at {legacy}");
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[EngineInstallRunner] Could not remove legacy Director: {ex.Message}");
        }
    }

    private static void Set(Prep prep, string componentId, string status, string? detail)
    {
        if (!prep.ItemsByComponentId.TryGetValue(componentId, out var item)) return;
        item.Status = status;
        if (detail != null) item.StatusDetail = detail;
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / (1024.0 * 1024.0):F1} MB";
}
