using System.Reflection;
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

        // `--help` is a flag, so "uninstall --help" parses as the uninstall command with a help flag.
        // Short-circuit to usage BEFORE dispatching, so appending --help to ANY command shows help
        // instead of silently running that (destructive) command. (e.g. "uninstall --help".)
        if (args.HasFlag("help"))
            return Help();

        var json = args.HasFlag("json");

        // When launched elevated by the WPF wizard (a hidden console), tee stdout/stderr to a file
        // so the non-elevated parent can tail live progress (UAC's runas verb forbids pipe redirection).
        WireConsoleTee(args.Option("log-file"));

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
                "uninstall" => Commands.Uninstall(args, layout, json),
                "rollback" => Commands.Rollback(args, layout, json),
                "version" or "--version" => VersionCommand(json),
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
        return root is null ? InstallLayout.Default() : new InstallLayout(root);
    }

    private static void WireConsoleTee(string? logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(logFile));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var fileWriter = new StreamWriter(File.Open(logFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));
            Console.SetError(new TeeTextWriter(Console.Error, fileWriter));
        }
        catch { /* a missing tee must never block the install */ }
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

    /// <summary>Print this CLI's own product version (stamped from Directory.Build.props).</summary>
    private static int VersionCommand(bool json)
    {
        var info = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        if (json)
            WriteJson(new { version = info.Split('+')[0], full = info });
        else
            Console.WriteLine(info);
        return ExitOk;
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            cc-director-setup-cli - install and update DevThrottle components

            Commands:
              components                 List known components (apps + tools), roles, assets
              status                     Show installed components and their versions
              prereqs                    Check for the agent framework (Claude Code / Codex)
              plan                       Show what an update/install would change
              update                     Download, verify, and apply updates
              install --role <r>         Install/update all components for a role
              uninstall --role <r>       Remove install-owned files (preserves your data)
              rollback <component>       Restore the previous build and pin away from current
              version                    Print this CLI's product version

            Options:
              --role workstation|gateway     Install type (default workstation)
              --manifest <path|latest>       Release source (default latest)
              --release-dir <dir>            Use a local directory as the release (offline)
              --component <id|all>           Limit update to one component (default all)
              --tools <id,id,...>            Override the tool set
              --root <dir>                   Override the per-user root %LOCALAPPDATA%\cc-director (testing)
              --dry-run                      Plan only; do not download or apply
              --json                         Machine-readable output
              --log-file <path>              Also write console output to this file (live progress)
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

/// <summary>Writes to two TextWriters at once (console + a log file), so elevated runs stay tailable.</summary>
internal sealed class TeeTextWriter(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string? value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    public override void Flush() { a.Flush(); b.Flush(); }
}
