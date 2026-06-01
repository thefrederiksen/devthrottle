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

        var toolIds = ComponentRegistry.DiscoverToolIds(release.Manifest);
        var components = ComponentRegistry.ForRole(ComponentRegistry.Build(toolIds), Role);

        var items = new List<ToolDownloadItem>();
        var byId = new Dictionary<string, ToolDownloadItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in components)
        {
            // The InstallStep UI keys the Director row by Name == "cc-director"; tools by their id.
            var uiName = c.Kind == ComponentKind.Director ? "cc-director" : c.Id;
            var item = new ToolDownloadItem { Name = uiName, AssetName = c.WindowsAsset };
            var asset = release.Manifest.TryGetAsset(c.WindowsAsset);
            if (asset is null) { item.Status = "Skipped"; item.SizeText = "Not in release"; }
            else item.SizeText = FormatSize(asset.Size);
            items.Add(item);
            byId[c.Id] = item;
        }

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
            return _source.DownloadAssetAsync(item.AssetName, prep.Release.DownloadUrls, innerCt);
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

        MigrateLegacyDirector();
        PathManager.AddToPath(_layout.BinDir);
        if (File.Exists(AppExePath))
            ShortcutCreator.CreateStartMenuShortcut(AppExePath);

        var installed = result.Installed + result.Updated;
        var skipped = prep.Items.Count(i => i.Status is "Skipped" or "Failed");
        SetupLog.Write($"[EngineInstallRunner] ApplyAsync: installed={installed}, skipped={skipped}");
        return (installed, skipped);
    }

    private async Task<List<PlanItem>> HandleDirectorRunningAsync(Prep prep, List<PlanItem> planItems)
    {
        var director = planItems.FirstOrDefault(i => i.ComponentId == ComponentRegistry.Director.Id);
        if (director is null) return planItems;

        while (IsDirectorRunning())
        {
            if (OnProcessBlocking is null || !await OnProcessBlocking("cc-director"))
            {
                Set(prep, director.ComponentId, "Skipped", "CC Director was running");
                return planItems.Where(i => i.ComponentId != director.ComponentId).ToList();
            }
        }
        return planItems;
    }

    private static bool IsDirectorRunning()
    {
        var procs = Process.GetProcessesByName("cc-director");
        var running = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return running;
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
