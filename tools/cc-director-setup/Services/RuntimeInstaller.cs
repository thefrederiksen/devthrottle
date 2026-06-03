using System.Diagnostics;

namespace CcDirectorSetup.Services;

/// <summary>The outcome of a winget runtime install attempt.</summary>
public sealed record RuntimeInstallResult(bool Success, string Message);

/// <summary>
/// Installs a prerequisite runtime via winget. Used by the Prerequisites step to fetch the
/// .NET 10 ASP.NET Core runtime when it is missing. If winget is not available or the install
/// fails, the result is an explicit failure (no silent fallback) so the UI can fall back to the
/// manual download link with a clear message.
/// </summary>
public static class RuntimeInstaller
{
    public static async Task<RuntimeInstallResult> InstallAsync(string wingetId)
    {
        SetupLog.Write($"[RuntimeInstaller] InstallAsync: wingetId={wingetId}");

        var winget = ResolveWinget();
        if (winget == null)
        {
            SetupLog.Write("[RuntimeInstaller] winget not available");
            return new RuntimeInstallResult(false,
                "winget (the Windows Package Manager) was not found. Use the download link to install .NET 10 manually, then click Re-check.");
        }

        var args =
            $"install --id {wingetId} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";

        var (exitCode, output) = await Task.Run(() => RunWinget(winget, args));
        if (exitCode == 0)
        {
            SetupLog.Write("[RuntimeInstaller] install succeeded");
            return new RuntimeInstallResult(true, "Installed. Re-checking...");
        }

        SetupLog.Write($"[RuntimeInstaller] install failed: exit={exitCode}; {Trim(output)}");
        return new RuntimeInstallResult(false,
            $"winget could not install {wingetId} (exit {exitCode}). Use the download link to install .NET 10 manually, then click Re-check.");
    }

    /// <summary>
    /// winget is not always on the process PATH (it lives under WindowsApps). Prefer the PATH
    /// entry, then the per-user WindowsApps app-execution-alias location.
    /// </summary>
    private static string? ResolveWinget()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
            return alias;

        // Fall back to bare "winget" so the OS resolves it from PATH if present.
        var psi = new ProcessStartInfo
        {
            FileName = "where",
            Arguments = "winget",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var probe = Process.Start(psi);
        if (probe == null)
            return null;
        probe.WaitForExit(5_000);
        return probe.ExitCode == 0 ? "winget" : null;
    }

    private static (int exitCode, string output) RunWinget(string winget, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = winget,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "winget did not start");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        // Runtime download + install can take a few minutes on a slow link.
        if (!process.WaitForExit(300_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "winget timed out after 5 minutes");
        }

        var combined = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        return (process.ExitCode, combined);
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
