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
    /// The extras tier needs no separate check here: tiers version in lockstep (one bundleVersion),
    /// and InstallAsync restores an installed extras tier after every venv rebuild (issue #174).
    /// </summary>
    public async Task<PythonToolsResult?> RefreshPythonToolsAsync(ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        // PythonToolsInstaller.ToolsAsset/PythonAsset are OS-aware, so this works on Windows and macOS.
        var toolsAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.ToolsAsset);
        var pyAsset = release.Manifest.TryGetAsset(PythonToolsInstaller.PythonAsset);
        var installedVer = InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId);

        if (toolsAsset is null || pyAsset is null)
        {
            // No bundle for this OS in the release. Distinguish the two cases instead of skipping quietly:
            // if tools ARE installed, the latest release dropping its bundle is a packaging REGRESSION worth
            // surfacing loudly (the installed tools keep working; we just cannot refresh them from here).
            // If nothing is installed, it is genuinely nothing to do. Either way there is no bundle to
            // install from, so we return null - we never silently pretend a refresh happened.
            if (installedVer is not null)
                EngineLog.Write($"[ToolUpdater] WARNING: Python tools bundle ({installedVer}) is installed but the latest release has NO bundle asset ({PythonToolsInstaller.ToolsAsset}) for this OS - leaving tools as-is. This is likely a release packaging regression.");
            else
                EngineLog.Write("[ToolUpdater] no Python tools bundle in this release and none installed - nothing to do.");
            return null;
        }

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

    /// <summary>
    /// On-demand, version-INDEPENDENT repair of the shared Python tools venv (e.g. the Home "Fix it"
    /// button). Unlike <see cref="RefreshPythonToolsAsync"/>, this does not gate on the installed bundle
    /// version - it calls <see cref="PythonToolsInstaller.InstallAsync"/> directly, whose health-check
    /// early-out rebuilds an unhealthy/empty venv and cheaply no-ops a healthy one. This is the path that
    /// actually fixes a half-installed toolset, which the version-gated refresh would silently skip.
    /// Fetches the latest release for the wheelhouse; reports progress for a UI repair affordance.
    /// </summary>
    public async Task<PythonToolsResult> RepairPythonToolsAsync(
        IProgress<string>? progress = null, IProgress<int>? percent = null, CancellationToken ct = default)
    {
        EngineLog.Write("[ToolUpdater] on-demand Python tools repair requested");
        var source = new ReleaseSource();
        var release = await source.FetchLatestAsync(ct);
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, source, progress, percent, ct);
        EngineLog.Write($"[ToolUpdater] Python tools repair: success={result.Success}, count={result.ToolCount}");
        return result;
    }

    private static UpdateRunResult Empty() => new() { Results = Array.Empty<ApplyResult>() };
}
