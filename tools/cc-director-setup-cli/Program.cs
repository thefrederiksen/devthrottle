using System.Text.Json;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// The headless CLI front-end over CcDirector.Setup.Engine. Same engine the UI
/// uses, so a human and an agent install/update identically (decision D1).
///
/// Exit codes: 0 ok, 1 runtime error, 2 usage error, 3 prerequisite missing.
/// </summary>
public static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsage = 2;
    private const int ExitPrereqMissing = 3;

    public static async Task<int> Main(string[] argv)
    {
        var args = CliArgs.Parse(argv);
        var json = args.HasFlag("json");

        // Resolve the install layout (roots overridable for testing) and route engine logs to a file.
        var layout = ResolveLayout(args);
        WireLogging(layout);

        try
        {
            return args.Command.ToLowerInvariant() switch
            {
                "components" => Commands.Components(args, layout, json),
                "status" => Commands.Status(args, layout, json),
                "prereqs" => Commands.Prereqs(json),
                "plan" => await Commands.PlanAsync(args, layout, json),
                "update" => await Commands.UpdateAsync(args, layout, json, installMode: false),
                "install" => await Commands.UpdateAsync(args, layout, json, installMode: true),
                "rollback" => Commands.Rollback(args, layout, json),
                "help" or "--help" => Help(),
                _ => Unknown(args.Command),
            };
        }
        catch (UsageException ux)
        {
            Console.Error.WriteLine($"usage error: {ux.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            EngineLog.Write($"[Program] FAILED: {ex}");
            return ExitError;
        }
    }

    private static InstallLayout ResolveLayout(CliArgs args)
    {
        var root = args.Option("root");
        var programFiles = args.Option("program-files");
        var programData = args.Option("program-data");
        if (root is null && programFiles is null && programData is null) return InstallLayout.Default();
        var def = InstallLayout.Default();
        return new InstallLayout(
            root ?? def.LocalRoot,
            programFiles ?? def.ProgramFilesRoot,
            programData ?? def.ProgramDataRoot);
    }

    private static void WireLogging(InstallLayout layout)
    {
        try
        {
            var logDir = Path.Combine(layout.LocalRoot, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "setup-cli.log");
            EngineLog.Sink = line =>
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
                catch { /* logging must never throw */ }
            };
        }
        catch { /* logging setup must never block the command */ }
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            cc-director-setup-cli - install and update CC Director components

            Commands:
              components                 List known components (apps + tools), roles, assets
              status                     Show installed components and their versions
              prereqs                    Check for the agent framework (Claude Code / Codex)
              plan                       Show what an update/install would change
              update                     Download, verify, and apply updates
              install --role <r>         Install/update all components for a role
              rollback <component>       Restore the previous build and pin away from current

            Options:
              --role workstation|gateway     Install type (default workstation)
              --manifest <path|latest>       Release source (default latest)
              --release-dir <dir>            Use a local directory as the release (offline)
              --component <id|all>           Limit update to one component (default all)
              --tools <id,id,...>            Override the tool set
              --root <dir>                   Override the per-user root %LOCALAPPDATA%\cc-director (testing)
              --program-files <dir>          Override the service binaries root %ProgramFiles%\CC Director (testing)
              --program-data <dir>           Override the service data root %ProgramData%\cc-director (testing)
              --dry-run                      Plan only; do not download or apply
              --json                         Machine-readable output
            """);
        return ExitOk;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}. Run 'help'.");
        return ExitUsage;
    }

    internal static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Thrown for malformed command invocations; mapped to exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);
