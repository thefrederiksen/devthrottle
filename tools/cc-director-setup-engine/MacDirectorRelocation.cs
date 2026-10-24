using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>What <see cref="MacDirectorRelocation.Decide"/> concluded about the bundle the Director runs from.</summary>
public enum MacRelocationDecision
{
    /// <summary>Not an installed Director this applies to: not a bundle, a development slot, or a copy outside the
    /// user's own Applications folder (a disk image, Downloads). Left exactly where it is, and nothing is said.</summary>
    NotApplicable,

    /// <summary>Already in the Applications folder an install puts it in. Nothing to do.</summary>
    AlreadyInPlace,

    /// <summary>Named like the Director but not carrying its bundle identifier: not ours to move.</summary>
    NotOurBundle,

    /// <summary>A Director is already in the right folder, so this one cannot be moved over it. The person is told
    /// where both are.</summary>
    DestinationOccupied,

    /// <summary>Move it: it is the product's own bundle, in <c>~/Applications</c>, and <c>/Applications</c> is
    /// writable and empty of a Director.</summary>
    Move,
}

/// <summary>The outcome of <see cref="MacDirectorRelocation.TryMove"/>: the new bundle path, or why it stayed.</summary>
public sealed record MacRelocationResult(bool Moved, string BundlePath, string Message);

/// <summary>
/// Moves an installed Director out of <c>~/Applications</c> into <c>/Applications</c> (issue #4427).
///
/// Installs before v2.26.0 placed the bundle in the user's own Applications folder, and the auto-updater swaps a new
/// build into the bundle the running process is inside, so an existing Mac never leaves that folder - while the Finder's
/// Applications entry, which is <c>/Applications</c>, shows nothing. It also means the launcher, which looks for the
/// Director where an install places it (<see cref="InstallLayout.PathFor(Component)"/>), does not supervise that copy.
///
/// The move happens at the Director's own start-up, before any session exists, and only for exactly that case: the
/// product's own bundle (its <see cref="MacBundlePurger.ProductBundleIdentifier"/>), under its installed name, in
/// <c>~/Applications</c>, when an install would put it in <c>/Applications</c> and nothing is there yet. Because the
/// launcher never supervised the copy being moved, no update or health wait of the launcher's can be watching it.
/// It is one rename on one volume, so a Dock item that points at the bundle follows it. A copy that cannot move is
/// left where it is and the person is told where it is.
/// </summary>
public static class MacDirectorRelocation
{
    /// <summary>
    /// Decide what to do with <paramref name="runningBundle"/>. Pure: the disk is reached only through
    /// <paramref name="exists"/> and <paramref name="readBundleIdentifier"/>, so a test can take every branch.
    /// </summary>
    /// <param name="runningBundle">The bundle this process runs from (<c>UpdateInstaller.InstallTarget()</c>).</param>
    /// <param name="installedBundle">Where an install places the Director (<see cref="InstallLayout.PathFor(Component)"/>).</param>
    /// <param name="userAppsDir">The user's own Applications folder (<see cref="InstallLayout.MacUserAppsDir"/>).</param>
    public static MacRelocationDecision Decide(string runningBundle, string installedBundle, string userAppsDir,
        Func<string, bool> exists, Func<string, string?> readBundleIdentifier)
    {
        ArgumentNullException.ThrowIfNull(runningBundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedBundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAppsDir);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(readBundleIdentifier);

        var running = Trim(runningBundle);
        var installed = Trim(installedBundle);
        if (!running.EndsWith(".app", StringComparison.Ordinal)) return MacRelocationDecision.NotApplicable;
        if (string.Equals(running, installed, StringComparison.Ordinal)) return MacRelocationDecision.AlreadyInPlace;

        // Only the installed name moves. A development slot ("Director Dev.app", "CC Director 2.app") and a copy
        // somebody renamed are not an install, and the bundle that moves keeps its name.
        if (!string.Equals(Path.GetFileName(running), Path.GetFileName(installed), StringComparison.Ordinal))
            return MacRelocationDecision.NotApplicable;

        // Only out of the user's own Applications folder, and only into a different folder. A copy run from a disk
        // image or from Downloads is not an install; the person decides where that one goes.
        if (!string.Equals(Path.GetDirectoryName(running), Trim(userAppsDir), StringComparison.Ordinal))
            return MacRelocationDecision.NotApplicable;

        if (!string.Equals(readBundleIdentifier(running), MacBundlePurger.ProductBundleIdentifier, StringComparison.Ordinal))
            return MacRelocationDecision.NotOurBundle;

        return exists(installed) ? MacRelocationDecision.DestinationOccupied : MacRelocationDecision.Move;
    }

    /// <summary>
    /// Decide, and move when the decision is <see cref="MacRelocationDecision.Move"/>. Returns null when there is
    /// nothing to do or say; otherwise the result says where the bundle is now and, when it did not move, why -
    /// in words for the person.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static MacRelocationResult? TryMove(string runningBundle, InstallLayout layout, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(log);

        var installed = layout.PathFor(ComponentRegistry.Director);
        var decision = Decide(runningBundle, installed, layout.MacUserAppsDir, Directory.Exists, MacBundlePurger.ReadBundleIdentifier);
        log($"[MacDirectorRelocation] running={runningBundle}, installed={installed}, decision={decision}");

        switch (decision)
        {
            case MacRelocationDecision.NotApplicable:
            case MacRelocationDecision.AlreadyInPlace:
            case MacRelocationDecision.NotOurBundle:
                return null;

            case MacRelocationDecision.DestinationOccupied:
                return new MacRelocationResult(false, runningBundle,
                    $"DevThrottle is running from {runningBundle}, but another copy is already at {installed}. "
                    + "That copy is the one DevThrottle keeps up to date. Quit DevThrottle, move this copy to the Trash, "
                    + $"and open the one in {Path.GetDirectoryName(installed)}.");

            case MacRelocationDecision.Move:
                try
                {
                    Directory.Move(Trim(runningBundle), installed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"[MacDirectorRelocation] move FAILED: {ex.GetType().Name}: {ex.Message}");
                    return new MacRelocationResult(false, runningBundle,
                        $"DevThrottle is in {runningBundle}. It belongs in {Path.GetDirectoryName(installed)}, but it could "
                        + $"not be moved there ({ex.Message}). Drag it into the Applications folder in the Finder.");
                }

                MacAppPlacer.RegisterWithLaunchServices(installed, log);
                log($"[MacDirectorRelocation] moved {runningBundle} -> {installed}");
                return new MacRelocationResult(true, installed, $"DevThrottle moved from {runningBundle} to {installed}.");

            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, "unknown relocation decision");
        }
    }

    private static string Trim(string path) => path.TrimEnd('/');
}
