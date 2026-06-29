namespace CcDirector.Core.Tools;

/// <summary>
/// The visible state of the rail's cc-* tools indicator (the corner badge rendered by
/// <c>MainWindow.UpdateToolsIndicator()</c>). Issue #829 turns the passive "tools are not in sync"
/// warning into an active, self-healing indicator, so the badge is now a small state machine rather
/// than a single on/off warning:
///
///   InSync   -> tools are healthy; the badge hides (the green / all-clear case).
///   Syncing  -> drift was found and a reconcile is running to fix it (orange "Syncing tools...").
///   Warning  -> drift exists but auto-update is OFF; the legacy passive amber warning, click = Settings.
///   NeedsAttention -> reconcile failed repeatedly; red "Tools need attention", a real clickable to-do.
///
/// The normal cycle is Green -> Orange (syncing) -> Green (resolved). Red is only reached after a
/// bounded number of failed/ineffective reconcile attempts; it is the single case that stays a
/// user-facing to-do.
/// </summary>
public enum ToolsIndicatorState
{
    /// <summary>Tools are in sync. The indicator hides (green / all-clear).</summary>
    InSync,

    /// <summary>A reconcile is running to correct detected drift. Orange "Syncing tools...".</summary>
    Syncing,

    /// <summary>Drift exists but auto-update is off. The legacy passive amber warning (click opens Settings).</summary>
    Warning,

    /// <summary>Reconcile failed repeatedly. Red "Tools need attention" (a real, clickable to-do).</summary>
    NeedsAttention,
}

/// <summary>The decision <see cref="ToolsSyncStateMachine.Evaluate"/> returns for a health snapshot.</summary>
/// <param name="State">The visible state the indicator should now show.</param>
/// <param name="ShouldReconcile">True when the caller should START a reconcile now (drift, auto-update on,
/// nothing already in flight, and the retry ceiling not yet hit). Backoff timing between attempts is the
/// caller's job; this only says "a reconcile is warranted right now".</param>
public readonly record struct ToolsSyncDecision(ToolsIndicatorState State, bool ShouldReconcile);

/// <summary>
/// The pure, UI-free state machine behind the active cc-* tools indicator (issue #829). It owns the
/// transition rules - in-sync, syncing, passive warning, and the repeated-failure escalation to red -
/// plus the consecutive-failure count and the exponential backoff schedule, so the decision logic is
/// unit-testable without Avalonia. The owning window (<c>MainWindow</c>) keeps the timers, the
/// in-flight guard, and the actual <c>ToolReconciler</c> call; this class never touches I/O.
///
/// Debounce/no-thrash is a shared responsibility: this machine never asks for a second reconcile while
/// one is in flight (the <c>reconcileInFlight</c> input) and stops asking once the retry ceiling is hit
/// (<see cref="MaxReconcileAttempts"/>); the caller honors <see cref="NextBackoff"/> between attempts so
/// retries are spaced out rather than tight-looped.
/// </summary>
public sealed class ToolsSyncStateMachine
{
    /// <summary>
    /// How many consecutive failed/ineffective reconcile attempts are allowed before the badge falls to
    /// red "Tools need attention" and auto-retrying stops. Bounded so the indicator never spins forever.
    /// </summary>
    public const int MaxReconcileAttempts = 3;

    /// <summary>Base wait before the first retry; doubled per consecutive failure (5s, 10s, ...).</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);

    /// <summary>The current visible state. Starts in sync (badge hidden) until a snapshot says otherwise.</summary>
    public ToolsIndicatorState State { get; private set; } = ToolsIndicatorState.InSync;

    /// <summary>Consecutive failed/ineffective reconcile attempts since the last success or clean snapshot.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// Decide the indicator state for a fresh health snapshot and whether a reconcile should start now.
    /// </summary>
    /// <param name="hasDrift">True when the cc-* tools row is not green (the today-warning condition).</param>
    /// <param name="autoUpdateEnabled">The effective <c>tools.autoUpdate.enabled</c> value.</param>
    /// <param name="reconcileInFlight">True when a reconcile this machine started has not finished yet.</param>
    public ToolsSyncDecision Evaluate(bool hasDrift, bool autoUpdateEnabled, bool reconcileInFlight)
    {
        if (!hasDrift)
        {
            // Resolved (or never drifted): back to green and forget past failures so the next drift
            // starts a fresh attempt budget.
            ConsecutiveFailures = 0;
            State = ToolsIndicatorState.InSync;
            return new ToolsSyncDecision(State, ShouldReconcile: false);
        }

        if (!autoUpdateEnabled)
        {
            // Opt-out: behave exactly as today - a passive warning, never an auto-reconcile against the
            // user's choice. The failure budget is irrelevant here.
            ConsecutiveFailures = 0;
            State = ToolsIndicatorState.Warning;
            return new ToolsSyncDecision(State, ShouldReconcile: false);
        }

        // Drift + auto-update on.
        if (ConsecutiveFailures >= MaxReconcileAttempts)
        {
            // Retry ceiling hit: stay red and stop auto-retrying. The user can still click to open Settings.
            State = ToolsIndicatorState.NeedsAttention;
            return new ToolsSyncDecision(State, ShouldReconcile: false);
        }

        // A reconcile is warranted. Show orange now; only ask to START one when nothing is in flight
        // (debounce: one reconcile at a time).
        State = ToolsIndicatorState.Syncing;
        return new ToolsSyncDecision(State, ShouldReconcile: !reconcileInFlight);
    }

    /// <summary>
    /// Record that a reconcile attempt resolved the drift: clear the failure count and return to green.
    /// </summary>
    public void OnReconcileSucceeded()
    {
        ConsecutiveFailures = 0;
        State = ToolsIndicatorState.InSync;
    }

    /// <summary>
    /// Record a failed/ineffective reconcile attempt (it errored, or it ran but drift still remains).
    /// Increments the failure count and escalates to red once the ceiling is reached; otherwise it stays
    /// orange so the caller can retry after <see cref="NextBackoff"/>.
    /// </summary>
    public void OnReconcileFailed()
    {
        ConsecutiveFailures++;
        State = ConsecutiveFailures >= MaxReconcileAttempts
            ? ToolsIndicatorState.NeedsAttention
            : ToolsIndicatorState.Syncing;
    }

    /// <summary>
    /// The backoff to wait before the next retry, given the current consecutive-failure count: an
    /// exponential schedule off <see cref="BaseBackoff"/> (5s, 10s, 20s, ...) so retries are spaced out
    /// and never tight-loop. Meaningful only while <see cref="State"/> is <see cref="ToolsIndicatorState.Syncing"/>
    /// (below the ceiling); at the ceiling the caller stops retrying.
    /// </summary>
    public TimeSpan NextBackoff()
    {
        var exponent = Math.Max(0, ConsecutiveFailures - 1);
        return TimeSpan.FromSeconds(BaseBackoff.TotalSeconds * Math.Pow(2, exponent));
    }
}
