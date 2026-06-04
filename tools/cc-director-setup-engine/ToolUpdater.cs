namespace CcDirector.Setup.Engine;

/// <summary>
/// Silent auto-update for the CLI tools, driven by a resident per-user process (the Director). Tool
/// exes are not long-lived processes, so they can be swapped directly - no relauncher needed.
///
/// Policy: REFRESH only. It updates tools that are already installed and behind the latest release;
/// it never INSTALLS tools the user does not have (auto-update keeps your toolset current, it does
/// not add to it). Rollback pins are honored, so a rolled-back bad version is not re-staged.
/// </summary>
public sealed class ToolUpdater
{
    private readonly InstallLayout _layout;

    public ToolUpdater(InstallLayout? layout = null)
    {
        _layout = layout ?? InstallLayout.Default();
    }

    /// <summary>Resolve the latest GitHub release and refresh installed tools that are behind it.</summary>
    public async Task<UpdateRunResult> RefreshAsync(CancellationToken ct = default)
    {
        var source = new ReleaseSource();
        var release = await source.FetchLatestAsync(ct);
        return await RefreshAsync(release, source, ct);
    }

    /// <summary>Testable overload: refresh against an already-resolved release (e.g. a local release dir).</summary>
    public async Task<UpdateRunResult> RefreshAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        // The Python tools now ship as one shared-venv bundle; refresh it independently of the
        // legacy per-tool exe path below (which no-ops for releases that ship only the bundle).
        await RefreshPythonToolsAsync(release, source, ct);

        var tools = ComponentRegistry.DiscoverToolIds(release.Manifest)
            .Select(ComponentRegistry.ToolComponent)
            .ToList();
        if (tools.Count == 0)
        {
            EngineLog.Write("[ToolUpdater] release has no tools; nothing to refresh");
            return Empty();
        }

        var reader = new InstalledStateReader(_layout);
        var installed = reader.ReadAll(tools);
        var pins = PinStore.Load(_layout);
        var plan = UpdatePlanner.Plan(tools, installed, release.Manifest, pins);

        // REFRESH only: apply Updates (installed-but-behind), never Installs (tools the user lacks).
        var updates = plan.ToUpdate;
        if (updates.Count == 0)
        {
            EngineLog.Write("[ToolUpdater] all installed tools are up to date");
            return Empty();
        }

        EngineLog.Write($"[ToolUpdater] refreshing {updates.Count} tool(s): {string.Join(", ", updates.Select(u => u.ComponentId))}");
        var runner = new UpdateRunner(_layout, tools,
            (item, innerCt) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, innerCt));
        var result = await runner.ApplyAsync(new UpdatePlan { Items = updates }, ct);
        EngineLog.Write($"[ToolUpdater] done: updated={result.Updated}, failed={result.Failed}");
        return result;
    }

    /// <summary>
    /// Refresh the shared-venv Python tools bundle. Installs when the bundle is missing (migrating an
    /// older machine off its per-tool exes - PythonToolsInstaller removes those stale exes) and when
    /// the release's bundle version is newer than what is installed. Windows-only; returns null when
    /// there is nothing to do (no bundle in the release, not Windows, or already current).
    /// </summary>
    public async Task<PythonToolsResult?> RefreshPythonToolsAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        // PythonToolsInstaller.ToolsAsset/PythonAsset are OS-aware, so this works on Windows and macOS;
        // a release that lacks this OS's bundle assets simply skips below (TryGetAsset returns null).
        var toolsAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        var pyAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        if (toolsAsset is null || pyAsset is null)
        {
            EngineLog.Write("[ToolUpdater] release has no Python tools bundle; skipping bundle refresh");
            return null;
        }

        var installedVer = InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId);
        var needs = installedVer is null  // missing => migrate from per-tool exes
            || (VersionUtil.TryParse(installedVer) is { } iv
                && VersionUtil.TryParse(toolsAsset.Version) is { } rv && rv > iv);
        if (!needs)
        {
            EngineLog.Write($"[ToolUpdater] Python tools bundle up to date ({installedVer})");
            return null;
        }

        EngineLog.Write($"[ToolUpdater] refreshing Python tools bundle: {installedVer ?? "none"} -> {toolsAsset.Version}");
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, source, ct: ct);
        EngineLog.Write($"[ToolUpdater] Python tools bundle: success={result.Success}, count={result.ToolCount}");
        return result;
    }

    private static UpdateRunResult Empty() => new() { Results = Array.Empty<ApplyResult>() };
}
