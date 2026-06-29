using System;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests.Tools;

/// <summary>
/// Covers the pure state machine behind the active cc-* tools indicator (issue #829): the
/// Green -> Orange (syncing) -> Green normal cycle, the repeated-failure escalation to red, the
/// auto-update opt-out passive warning, the one-in-flight debounce, and the exponential backoff
/// schedule. No Avalonia, no I/O.
/// </summary>
public class ToolsSyncStateMachineTests
{
    [Fact]
    public void Evaluate_NoDrift_IsInSyncAndDoesNotReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(hasDrift: false, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.InSync, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftAndEnabled_IsSyncingAndAsksToReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Syncing, d.State);
        Assert.True(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftEnabledButReconcileInFlight_IsSyncingButDoesNotStartAnother()
    {
        var sm = new ToolsSyncStateMachine();

        // Debounce: one reconcile at a time - the badge stays orange but no second reconcile is started.
        var d = sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: true);

        Assert.Equal(ToolsIndicatorState.Syncing, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftButAutoUpdateDisabled_IsPassiveWarningAndDoesNotReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(hasDrift: true, autoUpdateEnabled: false, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Warning, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void OnReconcileSucceeded_ReturnsToInSyncAndClearsFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFailed(); // one prior failure on the books

        sm.OnReconcileSucceeded();

        Assert.Equal(ToolsIndicatorState.InSync, sm.State);
        Assert.Equal(0, sm.ConsecutiveFailures);
    }

    [Fact]
    public void OnReconcileFailed_BelowCeiling_StaysSyncing()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFailed();

        Assert.Equal(1, sm.ConsecutiveFailures);
        Assert.Equal(ToolsIndicatorState.Syncing, sm.State);
    }

    [Fact]
    public void OnReconcileFailed_AtCeiling_EscalatesToNeedsAttention()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);

        for (var i = 0; i < ToolsSyncStateMachine.MaxReconcileAttempts; i++)
            sm.OnReconcileFailed();

        Assert.Equal(ToolsSyncStateMachine.MaxReconcileAttempts, sm.ConsecutiveFailures);
        Assert.Equal(ToolsIndicatorState.NeedsAttention, sm.State);
    }

    [Fact]
    public void Evaluate_DriftEnabledAtCeiling_StaysRedAndStopsRetrying()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        for (var i = 0; i < ToolsSyncStateMachine.MaxReconcileAttempts; i++)
            sm.OnReconcileFailed();

        // A fresh snapshot with drift still present must NOT ask for another reconcile - the ceiling holds.
        var d = sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.NeedsAttention, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftClearsAfterFailures_ResetsToInSyncAndForgetsFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFailed();
        sm.OnReconcileFailed();

        // The drift resolves (e.g. another Director fixed it): the badge clears and the budget resets.
        var resolved = sm.Evaluate(hasDrift: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.InSync, resolved.State);
        Assert.Equal(0, sm.ConsecutiveFailures);

        // A brand-new drift gets a fresh full attempt budget rather than inheriting the old failures.
        var fresh = sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.Syncing, fresh.State);
        Assert.True(fresh.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_AutoUpdateDisabled_ClearsAnyPriorFailureBudget()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFailed();

        var d = sm.Evaluate(hasDrift: true, autoUpdateEnabled: false, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Warning, d.State);
        Assert.Equal(0, sm.ConsecutiveFailures);
    }

    [Fact]
    public void NextBackoff_GrowsExponentiallyWithConsecutiveFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFailed();
        var first = sm.NextBackoff();
        sm.OnReconcileFailed();
        var second = sm.NextBackoff();

        Assert.Equal(ToolsSyncStateMachine.BaseBackoff, first);
        Assert.Equal(TimeSpan.FromSeconds(ToolsSyncStateMachine.BaseBackoff.TotalSeconds * 2), second);
    }

    [Fact]
    public void FullCycle_GreenToOrangeToGreen()
    {
        var sm = new ToolsSyncStateMachine();

        // Green at rest.
        Assert.Equal(ToolsIndicatorState.InSync,
            sm.Evaluate(hasDrift: false, autoUpdateEnabled: true, reconcileInFlight: false).State);

        // Drift -> orange + reconcile.
        var drift = sm.Evaluate(hasDrift: true, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.Syncing, drift.State);
        Assert.True(drift.ShouldReconcile);

        // Reconcile fixes it -> back to green.
        sm.OnReconcileSucceeded();
        Assert.Equal(ToolsIndicatorState.InSync, sm.State);
    }
}
