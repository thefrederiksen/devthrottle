using System.Diagnostics;

namespace CcDirector.Setup.Engine;

/// <summary>Runs a short external command (sc.exe/reg.exe) and captures its exit code + output.</summary>
internal static class ProcessRunner
{
    public static (int exit, string output) Run(string exe, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
    }

    public static (int exit, string output) Run(ServiceCommand cmd) => Run(cmd.Exe, cmd.Arguments);
}
