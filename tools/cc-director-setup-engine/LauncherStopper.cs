using System.Diagnostics;

namespace CcDirector.Setup.Engine;

/// <summary>
/// One launcher process found on this machine: its id and the full command line behind it.
/// </summary>
/// <param name="CommandLine">
/// The WHOLE command line, not a parsed executable. On macOS <c>ps</c> does not quote paths, and the
/// installed launcher lives under "Library/Application Support" - a path WITH A SPACE - so splitting
/// on whitespace produced "/Users/&lt;user&gt;/Library/Application" and the scope check never matched.
/// The executable is the leading text, so a prefix comparison is both correct and space-safe.
/// </param>
public sealed record LauncherProcess(int Pid, string CommandLine);

/// <summary>
/// Stops the INSTALLED launcher, whatever started it, and proves no process of ours remains.
///
/// This exists because a Mac was left unable to install anything. The macOS uninstall only asked
/// launchd to boot the service out, which does nothing for a launcher launchd never started - and
/// reported success. The installer's own first-install path started launchers that way, so the product
/// manufactured a process its own uninstaller could not remove, and every later install collided with
/// it.
///
/// The order here is the fix, and every step of it comes from something observed on that machine:
///
/// 1. Find OUR launchers first, scoped to the install-owned directory. Nothing is asked or stopped
///    before this, because a launcher a developer runs from a repository checkout would otherwise be
///    caught in a net meant for the installed one.
/// 2. Ask ours to quit politely, via the named lifecycle signal scoped to this install's storage root.
/// 3. Stop what is left by process, escalating - the real orphan ignored a polite request.
/// 4. Judge by whether our PROCESSES are gone. Steps 1 to 3 report what was attempted; only this says
///    whether it worked, and trusting an attempt is the whole original bug. (There used to be a port
///    check in the verdict too; the launcher listens on nothing now - remove-the-network-port mission,
///    phase 6 - so the process scan is the entire fact.)
/// </summary>
public sealed class LauncherStopper
{
    /// <summary>What the stop attempt did, and whether the machine is actually clear afterwards.</summary>
    /// <param name="Stopped">
    /// True when no installed launcher process remains.
    /// </param>
    public sealed record Result(bool Stopped, IReadOnlyList<string> Steps);

    private readonly InstallLayout _layout;

    public LauncherStopper(InstallLayout layout) => _layout = layout;

    /// <summary>
    /// Ask the launcher serving this storage root to quit. True when the request was delivered - which
    /// on Windows also means something was listening for it.
    ///
    /// A named lifecycle signal rather than a post to the launcher's loopback interface. That matters
    /// here more than anywhere: this runs during an UNINSTALL, on a machine whose launcher may be
    /// half-started, and the polite path used to need a discovery port, a token file and a live socket -
    /// three ways to be forced into the kill that the steps below correctly refuse to call a clean stop.
    /// </summary>
    public Func<string, bool> RequestQuit { get; init; } = DefaultRequestQuit;

    /// <summary>Every launcher process on the machine (unfiltered - this class does the scoping).</summary>
    public Func<IReadOnlyList<LauncherProcess>> ListLauncherProcesses { get; init; } = DefaultListLauncherProcesses;

    /// <summary>Stop this process, escalating from a polite request to a kill. True when it exited.</summary>
    public Func<int, bool> StopProcess { get; init; } = DefaultStopProcess;

    // Remove-the-network-port mission, phase 4: TokenFilePath and ReadToken are GONE. The polite quit
    // was a post to the launcher's loopback interface behind a bearer token, so an uninstall running
    // AFTER the token file was removed got a 401 and fell through to killing the process. There is no
    // token to be missing now, which removes the ordering hazard rather than documenting it.
    // Phase 6: PortInUse and PortOwnerPid are GONE too - the launcher's listener itself was deleted, so
    // "who holds the port" is no longer a fact about launchers at all. The process scan is the verdict.

    /// <summary>
    /// Stop every installed launcher. Safe when none is running. MUST be called BEFORE any wipe of the
    /// per-user root, and on macOS AFTER the launch agent is unregistered, or launchd restarts what
    /// this stops.
    /// </summary>
    public Result Stop()
    {
        var steps = new List<string>();

        // 1. Ours, and only ours. Enumerated FIRST and always - a launcher that is still starting,
        //    failed to bind, or runs on another port holds no port yet still has its binary about to
        //    be deleted underneath it.
        var ours = Ours(out var listed);
        if (!listed)
        {
            // We could not read the process list at all. We do not know that anything is stopped, so
            // we must not say it is - the caller uses this to decide whether it is safe to delete the
            // launcher's files. (There is no port to fall back on: the launcher listens on nothing.)
            steps.Add("cannot confirm the launcher is stopped: the process list is unreadable");
            return new Result(false, steps);
        }
        steps.Add(ours.Count == 0
            ? $"no launcher process running from {_layout.LauncherDir}"
            : $"found {ours.Count} installed launcher process(es): {string.Join(", ", ours.Select(p => p.Pid))}");

        if (ours.Count == 0)
        {
            steps.Add("launcher: nothing of ours to stop");
            return new Result(true, steps);
        }

        // 2. Politely first, and only to a launcher that is ours.
        //
        // The scoping lives in the ADDRESS: the signal is named for the storage root this installer is
        // uninstalling, so a launcher serving a different root cannot hear it at all. (It used to be a
        // runtime check on who answered the launcher port; the name-scoped signal replaced that, and
        // phase 6 then removed the port itself.)
        {
            var asked = RequestQuit(_layout.LocalRoot);
            steps.Add(asked
                ? "the launcher was asked to quit"
                : "no launcher of ours is listening for a quit request");
            if (asked) WaitFor(() => Ours(out _).Count == 0, TimeSpan.FromSeconds(5));
        }

        // 3. Whatever is left, by process, escalating.
        foreach (var p in Ours(out _))
        {
            var exited = StopProcess(p.Pid);
            steps.Add(exited
                ? $"stopped installed launcher process {p.Pid}"
                : $"could NOT stop installed launcher process {p.Pid}");
        }

        // 4. The fact: no process of ours left. Give a killed process a moment to exit before judging.
        WaitFor(() => Ours(out _).Count == 0, TimeSpan.FromSeconds(5));

        var remaining = Ours(out var listedAtEnd);
        var stopped = listedAtEnd && remaining.Count == 0;

        steps.Add(stopped
            ? "no installed launcher process remains"
            : listedAtEnd
                ? $"installed launcher process(es) STILL RUNNING: {string.Join(", ", remaining.Select(p => p.Pid))}"
                : "cannot confirm the launcher is stopped: the process list became unreadable");

        return new Result(stopped, steps);
    }

    /// <summary>
    /// The launcher processes that belong to THIS install. The scoping rule itself lives in
    /// <see cref="InstalledLauncherProcesses.Ours"/>, because the Director's ownership of the
    /// launcher's update has to answer the same question the same way.
    /// </summary>
    private List<LauncherProcess> Ours(out bool listed)
    {
        try
        {
            var all = ListLauncherProcesses();
            listed = true;
            return InstalledLauncherProcesses.Ours(_layout.LauncherDir, all);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] listing launcher processes failed: {ex.Message}");
            listed = false;
            return [];
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(250);
        }
        return condition();
    }

    // ---- production implementations ----

    private static bool DefaultRequestQuit(string sharedRoot)
    {
        try
        {
            return CcDirector.Core.Lifecycle.LifecycleSignal.Raise(
                CcDirector.Core.Lifecycle.LifecycleSignalNames.LauncherShutdown(sharedRoot));
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] quit request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Every cc-launcher process on the machine - see <see cref="InstalledLauncherProcesses.List"/>.</summary>
    private static IReadOnlyList<LauncherProcess> DefaultListLauncherProcesses() => InstalledLauncherProcesses.List();

    /// <summary>
    /// Ask, then insist. The real orphan ignored a polite termination request, so a stop that stops at
    /// politeness stops nothing - but killing first would deny a healthy launcher a clean shutdown.
    /// </summary>
    private static bool DefaultStopProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                p.CloseMainWindow();
                if (p.WaitForExit(3000)) return true;
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[LauncherStopper] polite stop of pid={pid} failed: {ex.Message}");
            }

            p.Kill(entireProcessTree: true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;   // already gone
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] kill pid={pid} failed: {ex.Message}");
            return false;
        }
    }

}
