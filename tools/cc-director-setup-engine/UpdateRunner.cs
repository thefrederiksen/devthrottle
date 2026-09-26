namespace CcDirector.Setup.Engine;

/// <summary>The outcome of applying one plan item.</summary>
public enum ApplyStatus { Installed, Updated, Failed, Skipped }

/// <summary>Result of applying one component.</summary>
public sealed record ApplyResult(
    string ComponentId,
    ApplyStatus Status,
    string? FromVersion,
    string? ToVersion,
    string? Error,
    string? BackupPath);

/// <summary>Result of an entire update/install run.</summary>
public sealed class UpdateRunResult
{
    public required IReadOnlyList<ApplyResult> Results { get; init; }
    public int Installed => Results.Count(r => r.Status == ApplyStatus.Installed);
    public int Updated => Results.Count(r => r.Status == ApplyStatus.Updated);
    public int Failed => Results.Count(r => r.Status == ApplyStatus.Failed);
    public int Skipped => Results.Count(r => r.Status == ApplyStatus.Skipped);
}

/// <summary>
/// Executes an <see cref="UpdatePlan"/>: for each actionable item, download the
/// asset, verify its SHA-256 against the manifest, then swap it into place
/// (keeping a .old backup). Downloading is injected as a delegate so the whole
/// flow is testable without a network: production passes a delegate backed by
/// the GitHub release download; tests pass one that produces a local file.
///
/// Single-file assets (Director, Gateway exe, tools) are placed directly.
/// Archive assets (the Cockpit .zip) require extraction and are reported as
/// Skipped here - that path is handled by the Gateway-side updater, not this
/// generic runner.
/// </summary>
public sealed class UpdateRunner
{
    /// <summary>Downloads the asset for a plan item and returns the local staged file path.</summary>
    public delegate Task<string> Downloader(PlanItem item, CancellationToken ct);

    private readonly InstallLayout _layout;
    private readonly IReadOnlyDictionary<string, Component> _componentsById;
    private readonly Downloader _download;

    public UpdateRunner(InstallLayout layout, IEnumerable<Component> components, Downloader download)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentNullException.ThrowIfNull(components);
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _componentsById = components.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<UpdateRunResult> ApplyAsync(UpdatePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<ApplyResult>();

        foreach (var item in plan.Actionable)
        {
            if (!_componentsById.TryGetValue(item.ComponentId, out var component))
            {
                results.Add(new ApplyResult(item.ComponentId, ApplyStatus.Failed, item.FromVersion, item.ToVersion,
                    "Component not in scope.", null));
                continue;
            }

            if (item.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                EngineLog.Write($"[UpdateRunner] {item.ComponentId}: archive asset, handled by Gateway-side updater; skipping.");
                results.Add(new ApplyResult(item.ComponentId, ApplyStatus.Skipped, item.FromVersion, item.ToVersion,
                    "Archive asset requires extraction (Gateway-side path).", null));
                continue;
            }

            results.Add(await ApplyOneAsync(item, component, ct));
        }

        RecordInstalledVersions(results);

        EngineLog.Write($"[UpdateRunner] ApplyAsync done: installed={results.Count(r => r.Status == ApplyStatus.Installed)}, " +
                        $"updated={results.Count(r => r.Status == ApplyStatus.Updated)}, " +
                        $"failed={results.Count(r => r.Status == ApplyStatus.Failed)}, " +
                        $"skipped={results.Count(r => r.Status == ApplyStatus.Skipped)}");
        return new UpdateRunResult { Results = results };
    }

    private async Task<ApplyResult> ApplyOneAsync(PlanItem item, Component component, CancellationToken ct)
    {
        var wasPresent = item.Kind == PlanItemKind.Update;
        try
        {
            var staged = await _download(item, ct);
            if (!File.Exists(staged))
                return Fail(item, "Download produced no file.");

            if (!Hashing.Sha256Matches(staged, item.Sha256))
            {
                EngineLog.Write($"[UpdateRunner] {item.ComponentId}: SHA-256 mismatch; rejecting.");
                TryDelete(staged);
                return Fail(item, "SHA-256 mismatch; download rejected.");
            }

            var target = _layout.PathFor(component);
            var backup = InstallSwapper.Place(target, staged);
            TryDelete(staged);

            // A downloaded file carries no executable permission on macOS / Unix, so a placed
            // binary would not start. Windows has no such bit, so this is a no-op there.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // macOS refuses a quarantined copy of an ad-hoc signed, never notarized binary, and the
            // refusal reads as a crash: launchd reports OS_REASON_CODESIGNING, then 78: EX_CONFIG, and the
            // program never writes a line (#3411). The self-update paths already strip it through
            // RunnableBuild; the first install is the path that did not.
            // Best effort, never fatal: a Mac whose xattr cannot even start must not fail every install.
            // If the flag stays, the launcher step's own diagnostics report it.
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    var (xattrExit, xattrOutput) = ProcessRunner.Run("/usr/bin/xattr", $"-d com.apple.quarantine \"{target}\"",
                        onStdoutLine: null, TimeSpan.FromSeconds(30));
                    // Non-zero is the normal case: a file that was never quarantined has nothing to remove.
                    EngineLog.Write($"[UpdateRunner] {item.ComponentId}: xattr quarantine strip -> exit={xattrExit} {xattrOutput.Trim()}");
                }
                catch (Exception ex)
                {
                    EngineLog.Write($"[UpdateRunner] {item.ComponentId}: xattr quarantine strip could not run ({ex.GetType().Name}): {ex.Message}");
                }
            }

            var status = wasPresent ? ApplyStatus.Updated : ApplyStatus.Installed;
            return new ApplyResult(item.ComponentId, status, item.FromVersion, item.ToVersion, null, backup);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[UpdateRunner] {item.ComponentId} FAILED: {ex.Message}");
            return Fail(item, ex.Message);
        }
    }

    /// <summary>
    /// Persist the version we just placed for each successfully installed/updated component, so the
    /// planner has a reliable installed version next time (esp. for tools with no file-version stamp).
    /// Best-effort: a bookkeeping write must never fail a good install.
    /// </summary>
    private void RecordInstalledVersions(IReadOnlyList<ApplyResult> results)
    {
        try
        {
            var manifest = InstalledManifest.Load(_layout);
            var changed = false;
            foreach (var r in results)
            {
                if (r.Status is not (ApplyStatus.Installed or ApplyStatus.Updated)) continue;
                var version = r.ToVersion;
                if (string.IsNullOrWhiteSpace(version)) continue; // narrows version to non-null below
                manifest.Set(r.ComponentId, version);
                changed = true;
            }
            if (changed) manifest.Save(_layout);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[UpdateRunner] recording installed versions failed: {ex.Message}");
        }
    }

    private static ApplyResult Fail(PlanItem item, string error) =>
        new(item.ComponentId, ApplyStatus.Failed, item.FromVersion, item.ToVersion, error, null);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { EngineLog.Write($"[UpdateRunner] cleanup delete failed for {path}: {ex.Message}"); }
    }
}
