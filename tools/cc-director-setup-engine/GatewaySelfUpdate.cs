namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Gateway self-update attempt.</summary>
public enum SelfUpdateOutcome { Updated, RolledBack, Failed }

/// <summary>Result of a Gateway self-update, with the ordered steps taken (for logs).</summary>
public sealed record SelfUpdateResult(SelfUpdateOutcome Outcome, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// Orchestrates the Gateway tray app replacing its own (file-locked) exe: stop -> swap -> relaunch ->
/// health-check, with AUTO-ROLLBACK to the .old build + a version pin if the new build does not come
/// up (decision DA-1 - a bricked always-on Gateway with no human present is the worst failure mode).
///
/// Runs inside a detached helper process launched from a STAGED copy of the new exe, so the installed
/// target is free to overwrite once the running tray app has exited. Process control and the health
/// probe are injected as delegates so the rollback logic is unit-testable without a real Gateway.
/// </summary>
public sealed class GatewaySelfUpdate
{
    private readonly InstallLayout _layout;
    private readonly TimeSpan _unlockTimeout;

    public GatewaySelfUpdate(InstallLayout? layout = null, TimeSpan? unlockTimeout = null)
    {
        _layout = layout ?? InstallLayout.Default();
        _unlockTimeout = unlockTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// Swap in <paramref name="stagedExePath"/> as the Gateway exe and verify the new build is healthy,
    /// rolling back to .old (and pinning <paramref name="newVersion"/>) if it is not.
    /// </summary>
    public async Task<SelfUpdateResult> ApplyAsync(
        string targetExePath,
        string stagedExePath,
        string newVersion,
        Func<bool> stopGateway,
        Func<bool> startGateway,
        Func<CancellationToken, Task<bool>> isHealthy,
        TimeSpan healthTimeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        ArgumentNullException.ThrowIfNull(stopGateway);
        ArgumentNullException.ThrowIfNull(startGateway);
        ArgumentNullException.ThrowIfNull(isHealthy);

        var steps = new List<string>();
        var target = targetExePath;
        EngineLog.Write($"[GatewaySelfUpdate] applying {newVersion} -> {target}");

        // 1. Stop the running Gateway so its exe unlocks, then swap. The writability wait is the
        // real exit barrier: a single-file exe stays locked until its process has fully exited
        // (which also releases the tray app's single-instance mutex for the relaunch).
        stopGateway();
        steps.Add("stopped the running Gateway");
        if (!WaitUntilWritable(target))
            return Fail(steps, $"Gateway exe still locked after stop ({target}); aborting swap.");

        string? backup;
        try
        {
            backup = InstallSwapper.Place(target, stagedExePath);
            steps.Add($"swapped Gateway exe -> {newVersion} (backup: {backup})");
        }
        catch (Exception ex)
        {
            startGateway(); // leave the Gateway running on the old (still-installed) exe
            return Fail(steps, $"swap failed: {ex.Message}");
        }

        // 2. Relaunch the new build and health-check it.
        startGateway();
        steps.Add("relaunched the Gateway (new build)");
        if (await WaitHealthyAsync(isHealthy, healthTimeout, ct))
        {
            RecordInstalled(newVersion);
            steps.Add($"healthy on {newVersion}");
            EngineLog.Write($"[GatewaySelfUpdate] success: {newVersion}");
            return new SelfUpdateResult(SelfUpdateOutcome.Updated, $"Gateway updated to {newVersion}.", steps);
        }

        // 3. DA-1: the new build did not come up -> roll back to .old and pin the bad version.
        steps.Add($"new build NOT healthy within {healthTimeout.TotalSeconds:F0}s; rolling back");
        EngineLog.Write($"[GatewaySelfUpdate] {newVersion} unhealthy; rolling back");
        stopGateway();
        WaitUntilWritable(target);
        var restored = InstallSwapper.Rollback(target);
        Pin(newVersion);
        startGateway();
        var healthyAfter = await WaitHealthyAsync(isHealthy, healthTimeout, ct);
        steps.Add(restored
            ? $"rolled back to previous build (healthy={healthyAfter}); pinned away from {newVersion}"
            : $"ROLLBACK FAILED - no .old backup; pinned away from {newVersion}");
        return new SelfUpdateResult(
            SelfUpdateOutcome.RolledBack,
            $"Rolled back from bad {newVersion} (healthy after rollback={healthyAfter}).",
            steps);
    }

    private async Task<bool> WaitHealthyAsync(Func<CancellationToken, Task<bool>> isHealthy, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { if (await isHealthy(ct)) return true; }
            catch { /* not up yet */ }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>Wait until the target exe can be opened for write (i.e. the old Gateway process exited and released it).</summary>
    private bool WaitUntilWritable(string path)
    {
        if (!File.Exists(path)) return true; // nothing to unlock (fresh)
        var deadline = DateTime.UtcNow + _unlockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    private void RecordInstalled(string version)
    {
        try
        {
            var m = InstalledManifest.Load(_layout);
            m.Set(ComponentRegistry.Gateway.Id, version);
            m.Save(_layout);
        }
        catch (Exception ex) { EngineLog.Write($"[GatewaySelfUpdate] record version failed: {ex.Message}"); }
    }

    private void Pin(string version)
    {
        try
        {
            var pins = PinStore.Load(_layout);
            pins.Pin(ComponentRegistry.Gateway.Id, version);
            PinStore.Save(_layout, pins);
        }
        catch (Exception ex) { EngineLog.Write($"[GatewaySelfUpdate] pin failed: {ex.Message}"); }
    }

    private static SelfUpdateResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[GatewaySelfUpdate] FAILED: {message}");
        return new SelfUpdateResult(SelfUpdateOutcome.Failed, message, steps);
    }
}
