namespace CcDirectorSetup.Services;

/// <summary>
/// The managed Gateway refresh folded into the wizard's completion state: the new skipped (failed)
/// count, whether the refresh succeeded, and the message to surface (the failure reason on failure).
/// </summary>
public readonly record struct GatewayRefreshOutcome(int Skipped, bool Success, string Message);

/// <summary>
/// The gateway-outcome -> completion-state transition that <c>MainWindow.RunGatewayTrayInstallAsync</c>
/// runs. Extracted UI-free so it is testable off the WPF thread, and it is the ONE path MainWindow
/// uses: a returned failure OR a thrown failure both add one to the skipped (failed) count, so the
/// Complete step reports the honest failure instead of "Everything went perfectly." Swallowing the
/// failure (leaving the count unchanged) is exactly the false-success bug this guards against.
/// </summary>
public static class GatewayRefresh
{
    /// <summary>
    /// Run the Gateway refresh <paramref name="run"/> and fold its result into the completion state.
    /// A returned failure and a thrown failure both count as one skipped component. The try/catch is
    /// the boundary for the external Gateway subprocess launch, which can either return a nonzero
    /// result or throw.
    /// </summary>
    public static async Task<GatewayRefreshOutcome> RunAsync(
        Func<Task<GatewayTrayLauncher.Result>> run, int skippedBefore)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            var result = await run();
            // A failed refresh is a component that did NOT install: count it so the Complete step
            // cannot render success. This is the production line the D2b guard reverts.
            var skipped = result.Success ? skippedBefore : skippedBefore + 1;
            return new GatewayRefreshOutcome(skipped, result.Success, result.Message);
        }
        catch (Exception ex)
        {
            // Not reported: here. The outcome carries the failure, and MainWindow reports every failed outcome.
            return new GatewayRefreshOutcome(skippedBefore + 1, false, $"Gateway install error: {ex.Message}");
        }
    }
}
