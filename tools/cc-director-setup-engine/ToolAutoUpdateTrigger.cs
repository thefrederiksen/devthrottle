using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The single gate the Director lifecycle goes through to run an automatic tool reconcile
/// (issue #827). Every automatic trigger point - startup, the periodic auto-update cycle, and once
/// right after a Director self-update applies - calls <see cref="RunIfEnabledAsync"/> so the
/// enabled/disabled decision, the logging, and the never-throw discipline are identical at all three
/// and live in ONE place that is unit-testable away from the Avalonia <c>App</c> lifecycle.
///
/// The reconcile is gated by <see cref="ToolAutoUpdateConfig"/> (the <c>tools.autoUpdate.enabled</c>
/// key, default true). The flag is read fresh on every call so toggling it takes effect without a
/// restart. When the flag is false NO reconcile runs and the only way to fix tools is the manual
/// Settings button. This helper never throws: a failure is logged and swallowed so it can never gate
/// or delay startup. The actual reconcile is injected as a delegate so the lifecycle wiring and the
/// tests drive the exact same gating path.
/// </summary>
public static class ToolAutoUpdateTrigger
{
    /// <summary>
    /// Run a tool reconcile for <paramref name="trigger"/> only when <c>tools.autoUpdate.enabled</c>
    /// is true. Logs the enabled/disabled decision and (when it runs) the reconcile result summary, so
    /// the behavior is observable in the Director log even though it is silent in the UI. Returns the
    /// <see cref="ReconcileResult"/> when a reconcile ran, or null when it was gated off or failed.
    /// </summary>
    /// <param name="layout">The install layout whose config.json holds the flag.</param>
    /// <param name="trigger">A short label for the trigger point (e.g. "startup", "periodic"), logged for observability.</param>
    /// <param name="reconcileAsync">The reconcile to run when enabled (injected for testability).</param>
    /// <param name="ct">Cancellation token forwarded to the reconcile.</param>
    public static async Task<ReconcileResult?> RunIfEnabledAsync(
        InstallLayout layout,
        string trigger,
        Func<CancellationToken, Task<ReconcileResult>> reconcileAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reconcileAsync);

        try
        {
            var enabled = ToolAutoUpdateConfig.Load(layout).Enabled;
            if (!enabled)
            {
                FileLog.Write($"[ToolAutoUpdate] {trigger}: tools.autoUpdate.enabled=false; skipping tool reconcile");
                return null;
            }

            FileLog.Write($"[ToolAutoUpdate] {trigger}: tools.autoUpdate.enabled=true; starting tool reconcile");
            var result = await reconcileAsync(ct);
            FileLog.Write($"[ToolAutoUpdate] {trigger}: tool reconcile done - outcome={result.Outcome}, actions={result.Actions.Count}" +
                          (result.Error is null ? "" : $", error={result.Error}"));
            return result;
        }
        catch (Exception ex)
        {
            // Best-effort: an automatic reconcile must never gate or delay the lifecycle. Swallow + log.
            FileLog.Write($"[ToolAutoUpdate] {trigger}: tool reconcile FAILED (ignored): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convenience overload that builds the production <see cref="ToolReconciler"/> for
    /// <paramref name="layout"/> and reconciles with it, gated by <c>tools.autoUpdate.enabled</c>.
    /// This is what the Director lifecycle calls at each trigger point.
    /// </summary>
    public static Task<ReconcileResult?> RunIfEnabledAsync(
        InstallLayout layout,
        string trigger,
        CancellationToken ct = default)
        => RunIfEnabledAsync(layout, trigger, c => new ToolReconciler(layout).ReconcileAsync(c), ct);
}
