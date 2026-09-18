using System.Diagnostics;
using System.Text;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>The outcome of a browser-harness install, with the steps taken (for the log and the UI).</summary>
/// <param name="Success">True when the harness is installed AND resolvable by the same check the product uses.</param>
/// <param name="Message">One sentence a user can read. On failure this says what went wrong, never a euphemism.</param>
/// <param name="Steps">The ordered steps taken, for the log.</param>
/// <param name="Version">The installed harness version when we could read it, else null.</param>
public sealed record BrowserHarnessInstallResult(
    bool Success, string Message, IReadOnlyList<string> Steps, string? Version);

/// <summary>
/// Installs browser-harness - browser-use's Python tool, the thing that actually drives the automation
/// browsers - onto this machine, without asking the user to install anything themselves.
///
/// WHY IT DOES NOT USE uv. The documented install is
/// <c>uv tool install --python 3.12 browser-harness</c>, but uv is a BUILD-TIME dependency of this
/// repository (scripts/build-python-bundle.ps1 bakes the Python bundle with it) and is never shipped to
/// a user's machine. Shipping it would mean shipping a second package manager to run one pip install,
/// and <c>uv tool install</c> drops its executable in the user's own local bin - a directory we do not
/// manage and cannot guarantee is on the PATH this Director resolves against, so a "successful" install
/// could still fail the detection check.
///
/// WHAT IT DOES INSTEAD. Everything needed already ships:
///   * the relocatable CPython 3.12 under <c>&lt;root&gt;\python</c> (browser-harness needs >= 3.11),
///   * a <c>bin</c> directory we own and that is already on PATH - it is where every cc-* tool shim lives.
/// So: build a DEDICATED venv beside them, pip-install browser-harness into it, and write the same style
/// of shim into that same bin. Detection then cannot miss it, because the shim lands in the directory the
/// resolver provably already searches.
///
/// WHY A DEDICATED VENV AND NOT THE SHARED TOOLS VENV. Two reasons, both proven rather than assumed:
///   * browser-harness pins its dependencies exactly (pillow==12.2.0 among them) and the shared venv
///     carries a different pillow, so sharing would silently downgrade a dependency out from under the
///     cc-* tools.
///   * <see cref="T:CcDirector.Setup.Engine.PythonToolsInstaller"/> DELETES and rebuilds the shared venv
///     on every tools-bundle update, which would remove the harness again on the next release without
///     anyone touching it.
/// Its own directory costs about 40 MB and survives every tools update.
///
/// NO FALLBACK (CLAUDE.md rule 3). Every failure here returns Success=false with the reason. Nothing is
/// substituted, no second route is attempted, and the caller is expected to say so and offer
/// <see cref="AutomationBrowserViewFold.HarnessInstallUrl"/>. A half-install never reports success: the
/// console script must exist AND resolve before this returns true.
/// </summary>
public static class BrowserHarnessInstaller
{
    /// <summary>The PyPI distribution installed. Named here once so the log, the UI and the command agree.</summary>
    public const string Distribution = "browser-harness";

    /// <summary>Who makes it. Shown to the user BEFORE they accept - we never install a third party's
    /// software without naming the third party (issue #1012, recommendation B-4).</summary>
    public const string Vendor = "browser-use";

    /// <summary>
    /// Bound for creating the venv. It takes seconds; the bound only exists so a wedged interpreter
    /// fails loudly in finite time instead of hanging the wizard forever.
    /// </summary>
    public static readonly TimeSpan VenvCreateTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Bound for the pip install. Measured at about twenty seconds on a warm machine for twelve
    /// packages; ten minutes is generous for a slow link and still bounded.
    /// </summary>
    public static readonly TimeSpan PipInstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The harness's own virtual environment, kept apart from the shared cc-* tools environment but in
    /// the same place: one per machine, at the machine root. The shim that puts it on PATH is written
    /// into the machine's <see cref="ShimDir"/>, so an environment inside one Director's folder would
    /// leave every other Director on the machine pointing at a folder it does not own.
    /// </summary>
    public static string EnvDir => Path.Combine(CcStorage.MachineRoot(), "harness-env");

    /// <summary>The venv's executables directory: Scripts on Windows, bin elsewhere.</summary>
    public static string EnvBinDir => Path.Combine(EnvDir, OperatingSystem.IsWindows() ? "Scripts" : "bin");

    /// <summary>The venv's own interpreter, which is what runs pip.</summary>
    public static string EnvPython => Path.Combine(EnvBinDir, OperatingSystem.IsWindows() ? "python.exe" : "python3");

    /// <summary>The console script pip generates for the harness inside the venv.</summary>
    public static string ConsoleScript =>
        Path.Combine(EnvBinDir, OperatingSystem.IsWindows() ? $"{Distribution}.exe" : Distribution);

    /// <summary>
    /// The CPython that ships with DevThrottle - the same interpreter the cc-* tools venv is built from.
    /// The harness venv is created from it so the machine needs no Python of its own.
    /// </summary>
    public static string BundledPython => OperatingSystem.IsWindows()
        ? Path.Combine(CcStorage.PythonRuntime(), "python.exe")
        : Path.Combine(CcStorage.PythonRuntime(), "bin", "python3");

    /// <summary>
    /// Where the shim goes so the harness is on PATH: the managed <c>bin</c> on Windows (every cc-* shim
    /// lives there), and <c>~/.local/bin</c> on macOS (where the tool symlinks go and which the Director
    /// .app launcher already prepends to PATH).
    /// </summary>
    public static string ShimDir => OperatingSystem.IsWindows()
        ? CcStorage.Bin()
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    /// <summary>
    /// Install browser-harness, reporting each step. Idempotent: a machine that already has it (ours, or
    /// one the user installed themselves) is left alone and reported as already installed.
    /// </summary>
    /// <param name="progress">Receives one short line per step, for the wizard's status text.</param>
    public static async Task<BrowserHarnessInstallResult> InstallAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        void Step(string m)
        {
            steps.Add(m);
            FileLog.Write($"[BrowserHarnessInstaller] {m}");
            progress?.Report(m);
        }

        FileLog.Write("[BrowserHarnessInstaller] InstallAsync");

        // 0. Already there? Never rebuild a working install - and never claim credit for one either.
        //    A user who installed the harness themselves (with uv, or into their own Python) resolves
        //    here and we leave their install completely alone.
        if (AutomationBrowserViewFold.IsHarnessInstalled())
        {
            Step($"{Distribution} is already installed on this machine");
            return new BrowserHarnessInstallResult(true, $"{Distribution} is already installed.", steps, ReadVersion());
        }

        // 1. The interpreter we install into has to actually be here. It ships with DevThrottle, so its
        //    absence means a broken or partial installation - say that, rather than failing later inside
        //    a venv command with a Win32 error nobody can act on.
        if (!File.Exists(BundledPython))
        {
            return Fail(steps,
                $"DevThrottle's bundled Python is missing (expected at {BundledPython}). "
                + "Repair the installation from Home, then set up browsers again.");
        }

        // 2. Create the venv. Rebuilt from scratch each time we get here: we only get here when the
        //    harness does NOT resolve, so anything already in this directory is a leftover from a failed
        //    or interrupted run, and reusing it would be building on a state we never verified.
        Step("Creating a Python environment for the harness");
        try
        {
            if (Directory.Exists(EnvDir)) Directory.Delete(EnvDir, recursive: true);
        }
        catch (Exception ex)
        {
            return Fail(steps, $"could not clear the previous harness environment at {EnvDir}: {ex.Message}");
        }

        var (venvExit, venvOut) = await RunAsync(BundledPython, new[] { "-m", "venv", EnvDir }, VenvCreateTimeout, null, ct)
            .ConfigureAwait(false);
        if (venvExit != 0)
            return Fail(steps, $"creating the Python environment failed ({venvExit}): {Trim(venvOut)}");
        if (!File.Exists(EnvPython))
            return Fail(steps, $"creating the Python environment reported success but produced no interpreter at {EnvPython}.");

        // 3. Install the distribution. pip's own lines drive the status text so the user watches real
        //    work rather than a spinner that means nothing.
        Step($"Installing {Distribution} from {Vendor}");
        var pipArgs = new[]
        {
            "-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location",
            "--progress-bar=off", Distribution,
        };
        var (pipExit, pipOut) = await RunAsync(EnvPython, pipArgs, PipInstallTimeout, line =>
        {
            FileLog.Write($"[pip] {line}");
            if (line.StartsWith("Collecting ", StringComparison.Ordinal)
                || line.StartsWith("Downloading ", StringComparison.Ordinal))
            {
                progress?.Report(line.Trim());
            }
            else if (line.StartsWith("Installing collected packages", StringComparison.Ordinal))
            {
                progress?.Report("Installing packages...");
            }
        }, ct).ConfigureAwait(false);

        if (pipExit == TimeoutExitCode)
            return Fail(steps, $"installing {Distribution} took longer than {PipInstallTimeout.TotalMinutes:F0} minutes and was stopped.");
        if (pipExit != 0)
            return Fail(steps, $"installing {Distribution} failed ({pipExit}): {Trim(pipOut)}");

        // 4. pip exiting zero is not proof the command exists. Check the console script is really on
        //    disk BEFORE writing a shim, so we can never leave a shim pointing at nothing.
        if (!File.Exists(ConsoleScript))
            return Fail(steps, $"{Distribution} installed but produced no {Distribution} command at {ConsoleScript}.");

        // 5. Put it on PATH the same way every cc-* tool gets there.
        Step("Putting the harness on the PATH");
        try
        {
            WriteShim();
        }
        catch (Exception ex)
        {
            return Fail(steps, $"could not write the {Distribution} shim into {ShimDir}: {ex.Message}");
        }

        // 6. The shim directory is one WE manage, so a Director that started before it existed on PATH
        //    would otherwise not see the tool until it restarted - and neither would the agents it
        //    launches, which inherit this process's environment. Add it here so the install is usable
        //    immediately. This is not a fallback: it makes the install effective, and the verdict below
        //    is still the product's own resolution check, which is free to fail.
        EnsureShimDirOnProcessPath();

        // 7. The verdict is the SAME check the rest of the product uses to decide whether the harness is
        //    installed. Anything less would let this report success while the Browsers screen keeps
        //    saying "Not installed".
        if (!AutomationBrowserViewFold.IsHarnessInstalled())
        {
            return Fail(steps,
                $"{Distribution} installed to {ConsoleScript}, but the {Distribution} command still does not "
                + $"resolve on this machine's PATH (expected via {ShimDir}).");
        }

        var version = ReadVersion();
        Step($"{Distribution} is installed{(version is null ? "" : $" ({version})")}");
        return new BrowserHarnessInstallResult(
            true,
            version is null ? $"{Distribution} is installed." : $"{Distribution} {version} is installed.",
            steps,
            version);
    }

    /// <summary>
    /// The installed harness version, read from the venv's own metadata, or null when it cannot be read.
    /// Decoration for the status line only - never load-bearing, so an unreadable version degrades to
    /// "no version shown" rather than failing an install that otherwise worked.
    /// </summary>
    public static string? ReadVersion()
    {
        try
        {
            if (!File.Exists(EnvPython)) return null;
            var (exit, output) = RunAsync(
                EnvPython,
                new[] { "-c", $"import importlib.metadata as m; print(m.version('{Distribution}'))" },
                TimeSpan.FromSeconds(20), null, CancellationToken.None).GetAwaiter().GetResult();
            if (exit != 0) return null;
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BrowserHarnessInstaller] ReadVersion failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    /// <summary>Write the PATH shim(s) for the harness: .cmd plus a bare-name shell shim on Windows, a
    /// symlink on macOS - exactly the shapes the cc-* tool shims already use.</summary>
    private static void WriteShim()
    {
        Directory.CreateDirectory(ShimDir);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(ShimDir, Distribution);
            if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            File.CreateSymbolicLink(link, ConsoleScript);
            return;
        }

        File.WriteAllText(Path.Combine(ShimDir, $"{Distribution}.cmd"), BuildWindowsShimBody(ConsoleScript));
        // Also a bare-name shell shim: Git Bash does not resolve the .cmd through PATHEXT, and agents
        // driving bash call the tool by bare name. CMD and PowerShell ignore this file (no PATHEXT
        // match), so the two cannot conflict.
        File.WriteAllText(Path.Combine(ShimDir, Distribution), BuildWindowsBashShimBody(ConsoleScript));
    }

    /// <summary>The body of the Windows .cmd shim. It checks its target exists first, so a machine whose
    /// harness environment was removed gets an actionable sentence instead of cmd.exe's raw
    /// "is not recognized".</summary>
    internal static string BuildWindowsShimBody(string consoleScript) =>
        "@echo off\r\n"
        + $"if not exist \"{consoleScript}\" (\r\n"
        + $"  echo {Distribution} is not installed - set it up from the Browsers group in DevThrottle 1>&2\r\n"
        + "  exit /b 1\r\n"
        + ")\r\n"
        + $"\"{consoleScript}\" %*\r\n";

    /// <summary>The body of the bare-name shim Git Bash runs (LF endings, shebang), forwarding to the
    /// same console script.</summary>
    internal static string BuildWindowsBashShimBody(string consoleScript) =>
        "#!/bin/sh\n"
        + $"# bash-runnable bare-name shim for '{Distribution}' (Git Bash does not resolve the .cmd via PATHEXT).\n"
        + $"exec \"{consoleScript.Replace('\\', '/')}\" \"$@\"\n";

    /// <summary>
    /// Prepend the shim directory to THIS process's PATH when it is not already there, so a freshly
    /// installed harness resolves without restarting the Director - and so does every agent session
    /// launched afterwards, since they inherit this environment.
    /// </summary>
    internal static void EnsureShimDirOnProcessPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (PathContains(current, ShimDir))
            return;

        Environment.SetEnvironmentVariable("PATH", ShimDir + Path.PathSeparator + current);
        FileLog.Write($"[BrowserHarnessInstaller] added {ShimDir} to this process's PATH");
    }

    /// <summary>True when <paramref name="searchPath"/> already lists <paramref name="dir"/>, comparing
    /// the way the platform does (case-insensitively on Windows) and ignoring a trailing separator.</summary>
    internal static bool PathContains(string? searchPath, string dir)
    {
        if (string.IsNullOrEmpty(searchPath) || string.IsNullOrWhiteSpace(dir)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return searchPath
            .Split(Path.PathSeparator)
            .Select(p => p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(p => p.Length > 0 && string.Equals(p, target, comparison));
    }

    /// <summary>The exit code reported when a bounded run was killed for exceeding its timeout.</summary>
    internal const int TimeoutExitCode = -3;

    /// <summary>
    /// Run a bounded child process, streaming its output lines to <paramref name="onLine"/> and returning
    /// the exit code with the combined output. A run that exceeds <paramref name="timeout"/> is killed and
    /// reported as <see cref="TimeoutExitCode"/> - it never hangs the caller.
    /// </summary>
    private static async Task<(int exit, string output)> RunAsync(
        string exe, string[] args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void Collect(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            try { onLine?.Invoke(line); }
            catch (Exception ex) { FileLog.Write($"[BrowserHarnessInstaller] output handler threw: {ex.Message}"); }
        }

        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { FileLog.Write($"[BrowserHarnessInstaller] could not kill {exe}: {ex.Message}"); }
            // A cancellation the CALLER asked for is not a timeout - surface it as one.
            ct.ThrowIfCancellationRequested();
            lock (output) return (TimeoutExitCode, output.ToString());
        }

        lock (output) return (process.ExitCode, output.ToString());
    }

    private static string Trim(string s)
    {
        var text = s.Trim();
        return text.Length > 400 ? text[..400] : text;
    }

    private static BrowserHarnessInstallResult Fail(List<string> steps, string message)
    {
        FileLog.Write($"[BrowserHarnessInstaller] FAILED: {message}");
        return new BrowserHarnessInstallResult(false, message, steps, null);
    }
}
