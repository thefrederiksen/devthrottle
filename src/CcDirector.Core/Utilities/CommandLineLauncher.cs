namespace CcDirector.Core.Utilities;

/// <summary>
/// Decides the concrete (executable, args) pair to hand to CreateProcess for a resolved
/// command. Batch shims (.cmd/.bat) - the form npm uses for global CLIs like
/// "opencode.cmd" - cannot be launched directly by CreateProcess, which only executes
/// real PE images. Those are wrapped through cmd.exe (ComSpec), exactly as a shell would.
/// Real executables (.exe) are passed through unchanged.
/// </summary>
public static class CommandLineLauncher
{
    public static (string Exe, string Args) Build(string resolvedExe, string? args)
    {
        args ??= string.Empty;

        if (OperatingSystem.IsWindows() && IsBatchShim(resolvedExe))
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comSpec))
                comSpec = "cmd.exe";

            // cmd /s /c ""<program>" <args>"
            // With /s and a leading+trailing quote, cmd strips exactly the outer pair and
            // runs the inner quoted program with the remaining args - the only quoting form
            // that survives paths containing spaces.
            var inner = string.IsNullOrEmpty(args)
                ? $"\"{resolvedExe}\""
                : $"\"{resolvedExe}\" {args}";
            return (comSpec, $"/s /c \"{inner}\"");
        }

        return (resolvedExe, args);
    }

    private static bool IsBatchShim(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
