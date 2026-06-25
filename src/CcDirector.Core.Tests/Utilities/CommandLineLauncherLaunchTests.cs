using System.Diagnostics;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// End-to-end launch tests for batch-shim (.cmd) agents. The sibling
/// <see cref="CommandLineLauncherTests"/> only assert the produced command-line string; they never
/// run it, so a runtime quoting failure (cmd.exe reporting the program as "not recognized") slips
/// through. These tests actually spawn a temporary .cmd through the real launch path
/// (ExecutableResolver -> CommandLineLauncher) and assert its output appears, for both space-free
/// and spaced paths.
///
/// Capture is done with a redirected stdout pipe rather than ConPtyBackend. The command line that
/// CreateProcess receives is identical either way ("exe" args), so this faithfully exercises the
/// cmd.exe quoting that the launcher produces, while avoiding the pseudo-console attach behavior
/// that makes ConPTY-based capture unreliable when the test host is itself inside a terminal.
/// </summary>
public class CommandLineLauncherLaunchTests
{
    private const string Marker = "MARKER_OK_123";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildOutput_ActuallyLaunchesCmdShim(bool spacesInPath)
    {
        if (!OperatingSystem.IsWindows())
            return; // batch shims are a Windows concern

        var dirName = (spacesInPath ? "cmd shim launch " : "cmdshimlaunch") + Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), dirName);
        Directory.CreateDirectory(tempDir);
        var cmdPath = Path.Combine(tempDir, "marker.cmd");
        File.WriteAllText(cmdPath, "@echo off\r\necho " + Marker + "\r\n");

        try
        {
            // Same sequence SessionManager uses to turn an agent command into a spawnable pair.
            var resolved = ExecutableResolver.Resolve(cmdPath) ?? cmdPath;
            var (exe, args) = CommandLineLauncher.Build(resolved, string.Empty);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args, // verbatim, exactly as ProcessHost passes it to CreateProcess
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            Assert.Contains(Marker, output);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
