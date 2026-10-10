namespace CcDirector.Setup.Engine;

/// <summary>
/// Honest health probe for the shared base Python: does the interpreter actually RUN, or is it a hollow
/// install that only looks present? A Python whose standard library (Lib\ or pythonNNN.zip) went missing -
/// the exact field failure in issue #994, where an interrupted install stripped it - dies at startup with
/// "No module named 'encodings'" even though python.exe is still on disk. Every cc-* Python tool shares this
/// one runtime, so when it is hollow they all fail at once. The Director uses this to detect that state and
/// trigger a self-repair (issue #995); the installer uses it to verify a freshly-staged Python before
/// swapping it in and to decide whether an on-disk runtime is healthy enough to skip a rebuild.
/// </summary>
public static class PythonRuntimeProbe
{
    /// <summary>How long to wait for the interpreter to start and import its standard library. A healthy
    /// Python does this in well under a second; the bound only stops a wedged process from hanging a caller.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True when the interpreter at <paramref name="pythonExe"/> can start and import its own standard
    /// library (<c>python -c "import encodings"</c> exits zero). The mere existence of python.exe does NOT
    /// prove this - a stripped standard library or a non-executable file both report as a dead runtime
    /// (false), which the caller treats as "needs (re)provisioning".
    /// </summary>
    public static bool CanImportStdlib(string pythonExe) => CanImportStdlib(pythonExe, ProcessRunner.Run);

    /// <summary>The arguments the probe runs the interpreter with.</summary>
    internal const string ProbeArguments = "-c \"import encodings\"";

    /// <summary><see cref="CanImportStdlib(string)"/>, running the interpreter through <paramref name="run"/>.</summary>
    internal static bool CanImportStdlib(string pythonExe, RunProcess run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe)) return false;
        try
        {
            var (exit, _) = run(pythonExe, ProbeArguments, null, Timeout);
            return exit == 0;
        }
        catch
        {
            // A process that cannot even start (not a valid interpreter) is a dead runtime, not an error to
            // surface here.
            return false;
        }
    }

    /// <summary>
    /// True when the shared base Python for <paramref name="layout"/> is present AND runnable. This is the
    /// Director-facing entry point: false means the shared runtime is hollow and every Python cc-* tool will
    /// fail, so a repair (which re-provisions the base Python) is warranted.
    /// </summary>
    public static bool IsBasePythonHealthy(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return CanImportStdlib(BasePythonExe(layout));
    }

    /// <summary>The base Python interpreter path for a layout: python.exe on Windows, bin/python3 elsewhere.</summary>
    public static string BasePythonExe(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return OperatingSystem.IsWindows()
            ? Path.Combine(layout.PythonDir, "python.exe")
            : Path.Combine(layout.PythonDir, "bin", "python3");
    }
}
