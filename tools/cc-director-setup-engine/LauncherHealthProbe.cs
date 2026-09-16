using CcDirector.Core.Configuration;

namespace CcDirector.Setup.Engine;

/// <summary>One reading of the launcher registration file: liveness plus IDENTITY (version, process id).
/// <see cref="Ok"/> means the file names a process that is alive RIGHT NOW - a file left behind by a
/// crashed launcher reads as not ok, because the pid in it is dead.</summary>
/// <param name="CommandSignals">
/// The lifecycle signals the registered launcher says it armed. Carried here so a caller that has read
/// the registration once does not have to read it a second time to find out whether that launcher can
/// be told anything - and so the two answers cannot come from two different reads of a file that is
/// rewritten while it is being read.
/// </param>
public sealed record LauncherHealth(bool Ok, string? Version, int Pid, IReadOnlyList<string> CommandSignals);

/// <summary>Why a readiness wait stopped. "Not ready yet" and "never going to be ready" are
/// different facts and the caller has to be able to tell them apart.</summary>
public enum LauncherWaitStop
{
    /// <summary>The registration certifies this install. The only good end.</summary>
    Healthy,

    /// <summary>The process the caller started is gone, so no registration is ever coming from it.</summary>
    StarterExited,

    /// <summary>The process is still running but the ceiling elapsed without a certifying registration.</summary>
    CeilingReached,

    /// <summary>The caller cancelled the wait.</summary>
    Cancelled,
}

/// <summary>The end of a readiness wait: the last reading seen (null when no registration ever
/// appeared) and why the waiting stopped.</summary>
public sealed record LauncherWaitResult(LauncherHealth? Health, LauncherWaitStop Stop);

/// <summary>
/// Polls the launcher REGISTRATION FILE - the fact the running launcher process writes about itself
/// (<see cref="LauncherDiscovery"/>) - until the registered launcher is provably the one just installed.
/// It used to poll the launcher's /healthz over its loopback port; the remove-the-network-port mission
/// (phase 6) deleted that listener, and the file carries the same identity the route did, so every rule
/// below survives the transport change unchanged:
///
/// LIVENESS IS NOT IDENTITY (issue #2042). The old check accepted ANY 200 from the fixed port - so on a
/// machine where a launcher was already running, a completely failed install of the new binary still
/// reported "healthy": the poll was answered by the pre-existing process. The file has the same trap - a
/// pre-existing launcher's registration is a perfectly valid file - so the caller states which PROCESS it
/// started and the registration has to name that process.
///
/// The version was not enough either, and a Mac proved it. The installer started process 35158; the
/// answer came from orphan 34084, up for seventy-three minutes from a path the installer had just
/// overwritten. <see cref="VersionUtil.TryParse"/> strips build metadata, so the orphan's
/// "1.8.4+71f90bad..." and the freshly placed "1.8.4" compared EQUAL. A version check can only catch
/// a version CHANGE, which is the case that matters least; on a same-version reinstall it was blind.
/// The version stays as a second signal, never the only one.
/// </summary>
public static class LauncherHealthProbe
{
    // Process liveness is an optional PARAMETER on the reading methods rather than a mutable static
    // seam, because this assembly's test classes run in parallel and a shared static would let one
    // class's fake answer leak into another's real question.
    private static bool DefaultProcessIsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wait until the registration at <paramref name="registrationPath"/> names a LIVE process AND, when
    /// known, the matching version AND the matching process. Returns the final reading (identity-verified
    /// on success), or null when no registration ever appeared.
    ///
    /// A registration that is not the expected process keeps being polled until the deadline - during a
    /// swap the OLD launcher's file can legitimately be present for a moment before the new one rewrites
    /// it - and is returned as-is at the deadline so the caller can fail loud, naming what was found.
    /// </summary>
    /// <param name="expectedPid">
    /// The process the caller started, or 0 when it has none to expect (a plain liveness check rather
    /// than certifying an install). Zero keeps the version-only behaviour.
    /// </param>
    public static async Task<LauncherHealth?> WaitForHealthyAsync(
        string registrationPath, string? expectedVersion, TimeSpan timeout, CancellationToken ct,
        int expectedPid = 0, Func<int, bool>? processIsAlive = null)
    {
        var result = await WaitForReadyAsync(
            registrationPath, expectedVersion, expectedPid, static () => true, timeout, ct,
            processIsAlive: processIsAlive);
        return result.Health;
    }

    /// <summary>
    /// Wait for the launcher to become ready on the CONDITION rather than on a clock: poll the
    /// registration file until it certifies this install, and stop early only when something is genuinely
    /// wrong - the process the caller started is no longer running, so nothing is ever going to register
    /// for it.
    ///
    /// This exists because a fixed clock reported a healthy install as a failure (issue #1152). On a
    /// clean Windows 11 machine the installer allowed about twenty seconds, called the launcher dead,
    /// and painted a red ERROR and a Failed row - while the launcher it had started was answering
    /// perfectly well. cc-launcher.exe is a ~134 MB single-file binary that unpacks itself on first run,
    /// so it is slow exactly once: on the machine where a first-time user is watching. A bigger fixed
    /// number is the same defect with a longer fuse, and the next slow machine walks into it.
    ///
    /// So the ceiling is a backstop for a genuine hang, not the thing that decides the verdict. The
    /// verdict is the registration appearing.
    /// </summary>
    /// <param name="expectedPid">The process the caller started, or 0 when it has none to expect.</param>
    /// <param name="starterIsRunning">
    /// True while the process the caller started is still alive. Returning false ends the wait at once:
    /// a launcher that has exited will never register, and waiting out the ceiling only delays a failure
    /// that is already certain.
    /// </param>
    /// <param name="ceiling">The backstop for a process that is alive but wedged.</param>
    /// <param name="onStillWaiting">
    /// Called after each poll that did not certify, with the elapsed time, so a caller can tell the
    /// user this is a slow first start rather than a frozen screen.
    /// </param>
    public static async Task<LauncherWaitResult> WaitForReadyAsync(
        string registrationPath,
        string? expectedVersion,
        int expectedPid,
        Func<bool> starterIsRunning,
        TimeSpan ceiling,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        Action<TimeSpan>? onStillWaiting = null,
        Func<int, bool>? processIsAlive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationPath);
        ArgumentNullException.ThrowIfNull(starterIsRunning);

        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var started = DateTime.UtcNow;
        var deadline = started + ceiling;
        LauncherHealth? last = null;

        while (true)
        {
            if (ct.IsCancellationRequested) return new LauncherWaitResult(last, LauncherWaitStop.Cancelled);

            var reading = ReadRegistration(registrationPath, processIsAlive);
            if (reading is not null)
            {
                last = reading;
                if (Certifies(last, expectedVersion, expectedPid))
                    return new LauncherWaitResult(last, LauncherWaitStop.Healthy);
            }

            // Ask AFTER polling, so a launcher that registered and then exited still counts as observed.
            if (!starterIsRunning()) return new LauncherWaitResult(last, LauncherWaitStop.StarterExited);

            var elapsed = DateTime.UtcNow - started;
            if (DateTime.UtcNow >= deadline) return new LauncherWaitResult(last, LauncherWaitStop.CeilingReached);
            onStillWaiting?.Invoke(elapsed);

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return new LauncherWaitResult(last, LauncherWaitStop.Cancelled); }
        }
    }

    /// <summary>One reading of the registration file, or null when it does not exist. A present but
    /// unreadable file reads as a not-ok reading, never a throw - the next poll may catch it mid-write.
    /// <paramref name="processIsAlive"/> is a test seam; production omits it and asks the operating
    /// system.</summary>
    public static LauncherHealth? ReadRegistration(string registrationPath, Func<int, bool>? processIsAlive = null)
    {
        var fact = LauncherDiscovery.Read(registrationPath);
        if (!fact.Installed) return null;
        var pid = fact.Pid ?? 0;
        var alive = pid > 0 && (processIsAlive ?? DefaultProcessIsAlive)(pid);
        return new LauncherHealth(Ok: alive, fact.Version, pid, fact.CommandSignals);
    }

    /// <summary>True when the reading certifies the install: a live process, version-matched when one was
    /// expected, and the process the caller started when it knows which one that is.</summary>
    public static bool Certifies(LauncherHealth? health, string? expectedVersion, int expectedPid = 0) =>
        health is { Ok: true }
        && VersionMatches(expectedVersion, health.Version)
        && PidMatches(expectedPid, health.Pid);

    /// <summary>
    /// Does this registration name the process the caller started? With no expectation (0) anything
    /// passes, because there is nothing to compare. With an expectation, a registration carrying NO
    /// process id fails: identity that cannot be checked must not pass for a match, so silence is a
    /// refusal rather than the benefit of the doubt.
    /// </summary>
    public static bool PidMatches(int expected, int reported) =>
        expected <= 0 || reported == expected;

    /// <summary>No expectation always matches (a registration without a version field cannot be
    /// checked); otherwise both sides must parse and compare equal, build metadata ignored.</summary>
    public static bool VersionMatches(string? expected, string? reported)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var e = VersionUtil.TryParse(expected);
        var r = VersionUtil.TryParse(reported);
        return e is not null && r is not null && e == r;
    }
}
