namespace CcDirector.Setup.Cli;

/// <summary>Result of looking for an agent framework on PATH.</summary>
public sealed record FrameworkStatus(string Name, bool Found, string? Location);

/// <summary>
/// Detects whether an agent framework (Claude Code / Codex) is on PATH. Per
/// decision D9 the installer DETECTS and GUIDES - it never installs the
/// framework itself - so this just reports presence and the official install
/// link for whatever is missing.
/// </summary>
public static class FrameworkDetector
{
    public const string ClaudeInstallUrl = "https://docs.claude.com/claude-code";
    public const string CodexInstallUrl = "https://github.com/openai/codex";

    public static FrameworkStatus Detect(string name)
    {
        var location = FindOnPath(name);
        return new FrameworkStatus(name, location != null, location);
    }

    public static IReadOnlyList<FrameworkStatus> DetectAll() =>
        [Detect("claude"), Detect("codex")];

    /// <summary>Find an executable on PATH (checks Windows PATHEXT variants), or null.</summary>
    public static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, exe + ext);
                if (File.Exists(candidate)) return candidate;
            }
            // Bare name (Git Bash launchers have no extension).
            var bare = Path.Combine(dir, exe);
            if (File.Exists(bare)) return bare;
        }
        return null;
    }
}
