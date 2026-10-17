namespace CcDirector.Setup.Engine;

/// <summary>What systemd and the unit file say about one <c>systemd --user</c> unit, read once.</summary>
/// <param name="UnitName">The unit name, e.g. <c>devthrottle-launcher.service</c>.</param>
/// <param name="UnitPath">The unit file the finding was read from.</param>
/// <param name="Exists">True when the unit file is on disk.</param>
/// <param name="ExecStart">The text after <c>ExecStart=</c>, or null when there is no unit file or no such line.</param>
/// <param name="Restarts">systemd's <c>NRestarts</c> counter, or null when systemd did not answer.</param>
/// <param name="ActiveState">systemd's <c>ActiveState</c> (active, activating, failed, inactive), or null.</param>
/// <param name="SubState">systemd's <c>SubState</c> (running, auto-restart, dead), or null.</param>
public sealed record UserUnitFinding(
    string UnitName, string UnitPath, bool Exists, string? ExecStart, int? Restarts, string? ActiveState, string? SubState)
{
    /// <summary>The restart count from which a unit is called crash-looping. A healthy unit restarts a handful
    /// of times in its life; ten is already a unit that cannot stay up.</summary>
    public const int CrashLoopRestarts = 10;

    /// <summary>True when the command is only a runtime host and can never run anything (see
    /// <see cref="SystemdUnitSafety.IsBareRuntimeHost"/>).</summary>
    public bool IsBareRuntimeHost => SystemdUnitSafety.IsBareRuntimeHost(ExecStart);

    /// <summary>True when systemd shows the unit failing over and over: waiting to restart after a failure,
    /// given up as failed, or past <see cref="CrashLoopRestarts"/> restarts.</summary>
    public bool IsCrashLooping =>
        SubState == "auto-restart" || ActiveState == "failed" || Restarts >= CrashLoopRestarts;

    /// <summary>One line for status output: the unit, its state, and its restart count.</summary>
    public string Describe()
    {
        if (!Exists) return $"{UnitName}: no unit file";
        var state = ActiveState is null ? "state unknown" : $"{ActiveState}/{SubState}";
        var restarts = Restarts is null ? "restarts unknown" : $"{Restarts} restarts";
        var problem = IsBareRuntimeHost
            ? $" - its command is only the runtime host ({ExecStart}), so it can never start anything"
            : IsCrashLooping ? " - it is crash-looping" : "";
        return $"{UnitName}: {state}, {restarts}{problem}";
    }
}

/// <summary>The outcome of <see cref="StaleLauncherUnit.Repair"/>: whether anything is still wrong, and the
/// steps taken, one line each.</summary>
public sealed record UserUnitRepair(bool Removed, bool StillBroken, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// The launcher's per-user systemd unit, ~/.config/systemd/user/devthrottle-launcher.service, which current
/// builds never write (issue devthrottle_internal#2475).
///
/// It was written by a branch build of the launcher (fix/3091-linux-launcher-login, commits 04f611947 and
/// 7a06ce01d, never merged to main) that registered itself with <c>Environment.ProcessPath</c>. A launcher run
/// as <c>dotnet cc-launcher.dll</c> sees the runtime host there, so the unit it left behind runs
/// <c>dotnet</c> alone, exits 129, and is restarted every five seconds for ever. Main has no Linux launcher
/// autostart at all, so nothing will ever rewrite it, and an installed machine keeps it until something
/// removes it.
///
/// Repair removes the unit ONLY when it can prove the unit is that broken one: its command is a runtime host
/// with nothing after it, which cannot start anything. Any other unit with this name - one somebody wrote by
/// hand, say - is reported, crash-looping or not, and left in place.
/// </summary>
public static class StaleLauncherUnit
{
    /// <summary>The unit name the branch build wrote.</summary>
    public const string UnitName = "devthrottle-launcher.service";

    /// <summary>Runs a short command and returns its exit code and combined output. Injectable so tests can
    /// fake systemctl on any host without a real systemd.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    /// <summary>The real runner: <see cref="ProcessRunner.Run(string, string)"/>.</summary>
    public static (int Exit, string Output) RunReal(string exe, string args) => ProcessRunner.Run(exe, args);

    /// <summary>The per-user unit file path: ~/.config/systemd/user/devthrottle-launcher.service.</summary>
    public static string UnitPath => UnitPathFor(UnitName);

    /// <summary>The per-user unit file path for any unit name.</summary>
    public static string UnitPathFor(string unitName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", unitName);

    /// <summary>
    /// Read the unit file and ask systemd for the unit's state. systemd is asked only when the unit file
    /// exists, so a machine without systemd, or without the unit, starts no process.
    /// </summary>
    public static UserUnitFinding Inspect(string unitName, string unitPath, CommandRunner run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitPath);
        ArgumentNullException.ThrowIfNull(run);

        if (!File.Exists(unitPath))
            return new UserUnitFinding(unitName, unitPath, false, null, null, null, null);

        var execStart = File.ReadAllLines(unitPath)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))
            .Select(l => l["ExecStart=".Length..].Trim())
            .FirstOrDefault();

        var (exit, output) = run("systemctl", $"--user show {unitName} -p NRestarts -p ActiveState -p SubState");
        var props = exit == 0 ? ParseProperties(output) : new Dictionary<string, string>();
        int? restarts = props.TryGetValue("NRestarts", out var n) && int.TryParse(n, out var parsed) ? parsed : null;
        props.TryGetValue("ActiveState", out var active);
        props.TryGetValue("SubState", out var sub);

        var finding = new UserUnitFinding(unitName, unitPath, true, execStart, restarts,
            string.IsNullOrEmpty(active) ? null : active, string.IsNullOrEmpty(sub) ? null : sub);
        EngineLog.Write($"[StaleLauncherUnit] Inspect: {finding.Describe()} (systemctl show exit={exit})");
        return finding;
    }

    /// <summary>
    /// Remove the unit when it is provably the broken one: stop and disable it, delete the file, reload
    /// systemd, clear its failed state, and then ask systemd again to certify it is gone. With
    /// <paramref name="dryRun"/> nothing is changed and the steps say what would be done.
    /// </summary>
    public static UserUnitRepair Repair(string unitName, string unitPath, bool dryRun, CommandRunner run)
    {
        EngineLog.Write($"[StaleLauncherUnit] Repair: unit={unitName}, path={unitPath}, dryRun={dryRun}");
        var steps = new List<string>();
        var before = Inspect(unitName, unitPath, run);
        steps.Add($"found: {before.Describe()}");

        if (!before.Exists)
            return new UserUnitRepair(false, false, $"{unitName}: none on this machine, nothing to do", steps);

        if (!before.IsBareRuntimeHost)
        {
            var kept = before.IsCrashLooping
                ? $"{unitName} is crash-looping but its command is not the known broken one, so it was left in place; "
                  + $"look at it with: systemctl --user status {unitName}"
                : $"{unitName} looks deliberate, so it was left in place";
            return new UserUnitRepair(false, before.IsCrashLooping, kept, steps);
        }

        if (dryRun)
        {
            steps.Add($"would run: systemctl --user disable --now {unitName}");
            steps.Add($"would delete: {unitPath}");
            steps.Add("would run: systemctl --user daemon-reload");
            steps.Add($"would run: systemctl --user reset-failed {unitName}");
            return new UserUnitRepair(false, true, $"{unitName} would be removed (dry run, nothing changed)", steps);
        }

        var (disableExit, disableText) = run("systemctl", $"--user disable --now {unitName}");
        steps.Add($"systemctl --user disable --now {unitName} -> exit {disableExit} {Trim(disableText)}".TrimEnd());

        File.Delete(unitPath);
        steps.Add($"deleted {unitPath}");

        var (reloadExit, reloadText) = run("systemctl", "--user daemon-reload");
        steps.Add($"systemctl --user daemon-reload -> exit {reloadExit} {Trim(reloadText)}".TrimEnd());

        // A unit that was failing keeps its failed record after the file is gone; clear it so status is honest.
        var (resetExit, resetText) = run("systemctl", $"--user reset-failed {unitName}");
        steps.Add($"systemctl --user reset-failed {unitName} -> exit {resetExit} {Trim(resetText)}".TrimEnd());

        // Certify from systemd, not from the steps: the unit must be unknown to it and not running.
        var (showExit, showText) = run("systemctl", $"--user show {unitName} -p LoadState -p ActiveState");
        var after = showExit == 0 ? ParseProperties(showText) : new Dictionary<string, string>();
        after.TryGetValue("LoadState", out var load);
        after.TryGetValue("ActiveState", out var activeAfter);
        steps.Add($"systemd now reports LoadState={load ?? "?"}, ActiveState={activeAfter ?? "?"}");

        var gone = !File.Exists(unitPath) && load == "not-found" && activeAfter == "inactive";
        var message = gone
            ? $"{unitName} removed: it ran only '{before.ExecStart}' and had restarted {before.Restarts?.ToString() ?? "an unknown number of"} times"
            : $"{unitName} could not be certified removed (LoadState={load ?? "?"}, ActiveState={activeAfter ?? "?"}); "
              + $"run: systemctl --user disable --now {unitName}";
        EngineLog.Write($"[StaleLauncherUnit] Repair: {message}");
        return new UserUnitRepair(true, !gone, message, steps);
    }

    /// <summary>Parse <c>systemctl show</c> output: one <c>Key=Value</c> per line.</summary>
    internal static Dictionary<string, string> ParseProperties(string output)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            props[line[..eq]] = line[(eq + 1)..];
        }
        return props;
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
