using CcDirector.Core.Tools;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>The state a <see cref="ReconcileResult"/> reports after a reconcile pass.</summary>
public enum ReconcileOutcome
{
    /// <summary>No drift was found; nothing was changed (the cheap, idempotent happy path).</summary>
    InSync,

    /// <summary>Drift was found and corrected; <see cref="ReconcileResult.Actions"/> lists what was done.</summary>
    Reconciled,

    /// <summary>A corrective action failed; <see cref="ReconcileResult.Error"/> carries the reason.</summary>
    Failed,
}

/// <summary>
/// The structured outcome of <see cref="ToolReconciler.ReconcileAsync"/>: the overall
/// <see cref="Outcome"/>, the human-readable list of corrective actions performed (empty when
/// <see cref="ReconcileOutcome.InSync"/>), and an optional <see cref="Error"/> when
/// <see cref="ReconcileOutcome.Failed"/>. The supervisor / UI reads this; it is intentionally richer
/// than a bool so the later indicator work can show exactly what happened.
/// </summary>
public sealed record ReconcileResult(ReconcileOutcome Outcome, IReadOnlyList<string> Actions, string? Error = null);

/// <summary>
/// The automatic, idempotent equivalent of the manual "Fix Tools" repair (the Settings dialog's
/// Fix-Tools button -> <see cref="ToolUpdater.RepairPythonToolsAsync"/>). Where the manual repair always
/// runs the heavy release-backed rebuild (fetch the latest release, rebuild the shared venv, reinstall
/// everything), this reconcile is the lightweight sibling meant to run frequently and unattended: it
/// detects drift between the embedded tools manifest and the installed tool layout and corrects ONLY the
/// drift it finds, scaling the corrective action to the problem.
///
/// Drift it considers, reusing the existing detection rather than a parallel comparison:
///   (a) a manifest tool whose venv console-script exe exists but whose bin shim is missing -> create
///       the shim (<see cref="PythonToolsInstaller.WriteShims"/>);
///   (b) orphaned legacy/retired alias shims left by older installs -> purge them
///       (<see cref="PythonToolsInstaller.RemoveLegacyAliasShims"/> / <c>LegacyAliasShimNames</c>, issue #823);
///   (c) a broken/absent shared venv (a recorded console script is missing) -> escalate to the heavy
///       release-backed <see cref="ToolUpdater.RepairPythonToolsAsync"/>.
///
/// It is cheap and idempotent on the happy path: when there is no drift it performs NO filesystem mutation
/// and NO network/release fetch and returns <see cref="ReconcileOutcome.InSync"/> quickly; a second call in
/// a row is a pure no-op. The heavy escalation (c) is guarded by a machine-wide named mutex so two Directors
/// sharing one venv + bin dir never rebuild concurrently - if another process already holds it, this call
/// returns without forcing. The light shim-only fixes (a)/(b) proceed without the heavy lock: they are
/// per-file create/delete operations that are safe to repeat and never rebuild the venv.
/// </summary>
public sealed class ToolReconciler
{
    /// <summary>
    /// Machine-wide named mutex serializing the heavy venv rebuild across Directors that share one install
    /// (same convention as scripts/agent-session-isolation.ps1's launch mutex). The light shim path does not
    /// take it - only the release-backed rebuild does, so concurrent reconciles never rebuild the venv twice.
    /// </summary>
    public const string HeavyRepairMutexName = @"Global\cc-director-tool-reconcile";

    private readonly InstallLayout _layout;
    private readonly Func<CancellationToken, Task<PythonToolsResult>> _heavyRepairAsync;

    /// <summary>
    /// Construct against an install layout (defaults to the production layout) and the heavy-repair
    /// escalation (defaults to <see cref="ToolUpdater.RepairPythonToolsAsync"/>). The heavy-repair delegate is
    /// injectable so tests can drive the reconcile logic without a real release or network access.
    /// </summary>
    public ToolReconciler(
        InstallLayout? layout = null,
        Func<CancellationToken, Task<PythonToolsResult>>? heavyRepairAsync = null)
    {
        _layout = layout ?? InstallLayout.Default();
        _heavyRepairAsync = heavyRepairAsync ?? (ct => new ToolUpdater(_layout).RepairPythonToolsAsync(ct: ct));
    }

    /// <summary>
    /// Detect drift between the embedded manifest and the installed tool layout and correct it. Returns a
    /// structured <see cref="ReconcileResult"/> (never throws for the supervisor's benefit): InSync when there
    /// was no drift, Reconciled when drift was found and fixed, Failed when a corrective action failed.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        FileLog.Write("[ToolReconciler] ReconcileAsync: detecting tool drift");
        try
        {
            // --- DETECT (pure reads, no mutation) -----------------------------------------------------
            var installer = new PythonToolsInstaller(_layout);
            var catalog = new ToolCatalogService(_layout.BinDir);
            var descriptors = catalog.GetCatalog();

            // (a) manifest tools whose venv console-script exe exists but whose managed bin shim is absent.
            var missingShims = descriptors
                .Where(d => File.Exists(PythonToolsInstaller.ConsoleScriptPath(_layout, d.Name))
                            && !File.Exists(ManagedShimPath(d.Name)))
                .Select(d => d.Name)
                .ToList();

            // (b) orphaned legacy/retired alias shims (reuse the installer's name list + path set).
            var orphanedLegacy = installer.FindOrphanedLegacyAliasShims();

            // (c) a broken/absent shared venv: a recorded console script is missing. We only judge this when
            //     we have a recorded expectation (the sidecar). With no expectation we cannot tell a healthy
            //     install from an empty one, so we never escalate the heavy rebuild on a guess.
            var expectedScripts = PythonToolsState.LoadScripts(_layout);
            var venvBroken = expectedScripts.Count > 0
                && !PythonToolsInstaller.VenvHasAllTools(_layout, expectedScripts);

            // Built binaries not in the manifest. We surface these for visibility but do NOT remove them - the
            // only shims we purge are the known retired aliases; a user's own cc-* binary is left untouched.
            var unmanaged = catalog.GetUnmanagedBinaries();
            if (unmanaged.Count > 0)
                FileLog.Write($"[ToolReconciler] observed {unmanaged.Count} unmanaged binaries (not acted on): {string.Join(", ", unmanaged)}");

            if (missingShims.Count == 0 && orphanedLegacy.Count == 0 && !venvBroken)
            {
                FileLog.Write("[ToolReconciler] no drift; tools are in sync (no action taken)");
                return new ReconcileResult(ReconcileOutcome.InSync, Array.Empty<string>());
            }

            FileLog.Write($"[ToolReconciler] drift found: missingShims={missingShims.Count}, orphanedLegacyShims={orphanedLegacy.Count}, venvBroken={venvBroken}");

            // --- CORRECT --------------------------------------------------------------------------------
            var actions = new List<string>();
            var mutated = false;

            // (a) Create missing shims directly - their venv targets already exist, so this is a cheap,
            //     per-file write that never rebuilds the venv.
            if (missingShims.Count > 0)
            {
                installer.WriteShims(missingShims);
                foreach (var name in missingShims)
                    FileLog.Write($"[ToolReconciler] created missing shim for {name}");
                actions.Add($"created {missingShims.Count} missing tool shim(s): {string.Join(", ", missingShims)}");
                mutated = true;
            }

            // (b) Purge orphaned legacy alias shims directly (reuse the installer's purge).
            if (orphanedLegacy.Count > 0)
            {
                installer.RemoveLegacyAliasShims();
                FileLog.Write($"[ToolReconciler] purged {orphanedLegacy.Count} orphaned legacy alias shim(s)");
                actions.Add($"purged {orphanedLegacy.Count} orphaned legacy alias shim(s)");
                mutated = true;
            }

            // (c) Only a genuinely broken venv escalates to the heavy release-backed rebuild, and only under
            //     the machine-wide mutex so two Directors never rebuild the shared venv at once.
            if (venvBroken)
            {
                var heavy = await EscalateHeavyRepairAsync(actions, ct);
                if (heavy is { } failure)
                    return failure; // heavy repair failed -> Failed (light actions already recorded)
                if (actions.Count > 0)
                    mutated = true;
            }

            var outcome = mutated ? ReconcileOutcome.Reconciled : ReconcileOutcome.InSync;
            FileLog.Write($"[ToolReconciler] ReconcileAsync done: outcome={outcome}, actions={actions.Count}");
            return new ReconcileResult(outcome, actions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolReconciler] ReconcileAsync FAILED: {ex.Message}");
            return new ReconcileResult(ReconcileOutcome.Failed, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>
    /// Read-only drift probe: returns true when <see cref="ReconcileAsync"/> would have corrective work to
    /// do right now - a manifest tool whose venv console-script exists but whose managed bin shim is missing
    /// (a), an orphaned legacy/retired alias shim (b), or a broken/absent shared venv (c). It performs the
    /// SAME pure reads <see cref="ReconcileAsync"/> uses to detect drift and mutates nothing (no shim writes,
    /// no venv rebuild, no network). The active corner indicator (issue #829) calls this to decide whether to
    /// show the orange "Syncing tools..." state and start a reconcile, so the badge reflects exactly the drift
    /// the reconcile would act on. Never throws (the indicator must stay responsive): a probe failure is
    /// logged and reported as "no drift".
    /// </summary>
    public bool HasDrift()
    {
        try
        {
            var installer = new PythonToolsInstaller(_layout);
            var catalog = new ToolCatalogService(_layout.BinDir);
            var descriptors = catalog.GetCatalog();

            // (a) a manifest tool whose venv console-script exists but whose managed bin shim is absent.
            var missingShim = descriptors.Any(d =>
                File.Exists(PythonToolsInstaller.ConsoleScriptPath(_layout, d.Name))
                && !File.Exists(ManagedShimPath(d.Name)));
            if (missingShim)
                return true;

            // (b) orphaned legacy/retired alias shims left by older installs.
            if (installer.FindOrphanedLegacyAliasShims().Count > 0)
                return true;

            // (c) a broken/absent shared venv (a recorded console script is missing). Only judged when there
            //     is a recorded expectation, mirroring ReconcileAsync - never escalate on a guess.
            var expectedScripts = PythonToolsState.LoadScripts(_layout);
            if (expectedScripts.Count > 0 && !PythonToolsInstaller.VenvHasAllTools(_layout, expectedScripts))
                return true;

            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ToolReconciler] HasDrift FAILED (treated as no drift): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run the heavy release-backed venv repair under the machine-wide mutex. Returns null on success (the
    /// action is appended to <paramref name="actions"/>) or a skip-note; returns a Failed result when the
    /// repair itself failed. If another Director already holds the mutex, we do NOT force - we log the skip
    /// and leave the rebuild to the holder.
    /// </summary>
    private async Task<ReconcileResult?> EscalateHeavyRepairAsync(List<string> actions, CancellationToken ct)
    {
        using var mutex = new Mutex(initiallyOwned: false, HeavyRepairMutexName, out _);
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { held = true; } // a prior holder died; we now own it

            if (!held)
            {
                FileLog.Write("[ToolReconciler] reconcile skipped - another Director is reconciling");
                actions.Add("heavy venv repair skipped - another Director is reconciling");
                return null;
            }

            FileLog.Write("[ToolReconciler] venv is broken; escalating to the release-backed repair");
            var repair = await _heavyRepairAsync(ct);
            if (!repair.Success)
            {
                FileLog.Write($"[ToolReconciler] heavy venv repair FAILED: {repair.Message}");
                return new ReconcileResult(ReconcileOutcome.Failed, actions, repair.Message);
            }

            FileLog.Write($"[ToolReconciler] heavy venv repair succeeded: {repair.Message}");
            actions.Add($"rebuilt the shared Python tools venv ({repair.Message})");
            return null;
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// The managed shim path the installer writes for a tool: bin\&lt;name&gt;.cmd on Windows, the
    /// ~/.local/bin/&lt;name&gt; symlink on macOS - matching <see cref="PythonToolsInstaller.WriteShims"/>.
    /// </summary>
    private string ManagedShimPath(string name) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(_layout.BinDir, name + ".cmd")
            : Path.Combine(_layout.MacUserBinDir, name);
}
