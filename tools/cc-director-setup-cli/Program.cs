using System.Reflection;
using System.Text.Json;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// The headless CLI front-end over CcDirector.Setup.Engine. Same engine the UI
/// uses, so a human and an agent install/update identically (decision D1).
///
/// Exit codes are a CONTRACT for the scripts and coding agents that drive this: see ExitCodes.
/// </summary>
public static class Program
{
    private const int ExitOk = ExitCodes.Ok;
    private const int ExitError = ExitCodes.Error;
    private const int ExitUsage = ExitCodes.Usage;
    private const int ExitPrereqMissing = ExitCodes.PrerequisiteMissing;

    public static async Task<int> Main(string[] argv)
    {
        // Parsing is INSIDE the guarded region. A usage error must leave with the documented code 2 -
        // the whole reason an unattended caller can branch on it. Parsing outside this handler made a
        // malformed command line exit with an unhandled exception instead.
        CliArgs args;
        try
        {
            args = CliArgs.Parse(argv);
        }
        catch (UsageException ux)
        {
            Console.Error.WriteLine($"Error: {ux.Message}");
            Console.Error.WriteLine();
            Help();
            return ExitUsage;
        }

        // `--help` is a flag, so "uninstall --help" parses as the uninstall command with a help flag.
        // Short-circuit to usage BEFORE dispatching, so appending --help to ANY command shows help
        // instead of silently running that (destructive) command. (e.g. "uninstall --help".)
        if (args.HasFlag("help"))
            return Help();

        // On macOS the install is per-user: run as root it fills the user's own Library with root-owned
        // files, and launchd then refuses to start the launcher (#3411). Refused before anything is written.
        // Only the commands that write: a read-only command (status, components) stays observational.
        var writes = WritesTheInstall(args);
        if (OperatingSystem.IsMacOS() && writes && Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine($"Error: {MacFileOwnership.RefuseRootMessage}");
            return ExitError;
        }

        var json = args.HasFlag("json");

        // When launched elevated by the WPF wizard (a hidden console), tee stdout/stderr to a file
        // so the non-elevated parent can tail live progress (UAC's runas verb forbids pipe redirection).
        WireConsoleTee(args.Option("log-file"));

        // Resolve the install layout (roots overridable for testing) and route engine logs to a file.
        var layout = ResolveLayout(args);

        // Before the first write into the install folder (the log below is one): a root-owned install
        // fails every write, so a repair that waited for the launcher step was never reached (#3411).
        // The command line is the headless surface agents drive, so it never opens a password dialog: it
        // stops with the command that repairs the files. The setup wizard offers the prompt.
        if (OperatingSystem.IsMacOS() && writes)
        {
            var ownership = MacFileOwnership.EnsureOwnedByUser(MacFileOwnership.DefaultRunner,
                MacFileOwnership.InstallTargets(layout, LauncherLaunchdAutostart.PlistPath), offerPrompt: false, _ => { });
            if (ownership is not null)
            {
                Console.Error.WriteLine($"Error: {ownership}");
                return ExitError;
            }
        }

        // A read-only command run with sudo must stay read-only: wiring the log creates <root>/logs, and as
        // root that is the root-owned install folder this guard exists to prevent. It answers without a log.
        if (!(OperatingSystem.IsMacOS() && !writes && Environment.IsPrivilegedProcess))
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
                "signin" => await Commands.SignInAsync(args, json),
                "enroll" => await Commands.EnrollAsync(args, layout, json),
                "uninstall" => Commands.Uninstall(args, layout, json),
                "rollback" => Commands.Rollback(args, layout, json),
                "autostart" => Commands.Autostart(args, layout, json),
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

    /// <summary>
    /// Whether the command may write the per-user install (and so must not run as root, and needs the install
    /// to belong to the user). A list of the commands that write NOTHING, so a new command is guarded until
    /// someone shows it is read-only: plan and --dry-run looked read-only and still wrote the release cache
    /// into the install folder. Read-only commands answer from what is there, even on a damaged install.
    /// </summary>
    internal static bool WritesTheInstall(CliArgs args) => args.Option("log-file") is not null || args.Command.ToLowerInvariant() switch
    {
        "status" or "components" or "prereqs" or "version" or "--version" or "help" or "--help" => false,
        "autostart" => args.Positionals.Count > 0 && !args.Positionals[0].Equals("status", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

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
              signin                     Sign in (or create a free account); store it for the Gateway (Windows)
              enroll [--gateway <url>]   Join this workstation to its gateway (sign in, then enroll)
              enroll --hosted            Join DevThrottle's hosted gateway instead of your own
              uninstall --role <r>       Remove install-owned files (preserves your data)
              rollback <component>       Restore the previous build and pin away from current
              autostart on|off|status    Start the Gateway at login (Run key / launch agent / systemd --user)
              version                    Print this CLI's product version

            Options:
              --role workstation|gateway     Install type (default workstation)
              --gateway <url>                Gateway to enroll against (enroll; else auto-discover)
              --hosted                       Enroll at DevThrottle's hosted gateway (enroll; not with --gateway)
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
