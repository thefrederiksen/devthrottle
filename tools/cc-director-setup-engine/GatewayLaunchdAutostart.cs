using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The Gateway's per-user autostart on macOS: a launchd user launch agent at
/// ~/Library/LaunchAgents/com.devthrottle.cc-director-gateway.plist. The macOS twin of
/// <see cref="GatewayAutostart"/> (the Windows Run key), and the sibling of
/// <see cref="LauncherLaunchdAutostart"/> (the same mechanism for the CC Launcher).
///
/// It lives in the engine for the same reason its two siblings do: the installer, the
/// uninstaller, and the Gateway itself must agree on one label, one property list path,
/// and one command-line format.
///
/// Per-user (the gui domain), never a system daemon: the Gateway only does useful work
/// while the user is logged in - the whole fleet is logon-bound - so it belongs in the
/// user's login session, exactly as the Windows side reasons about HKCU.
///
/// The agent is registered with RunAtLoad (start at login) and KeepAlive with
/// SuccessfulExit=false: launchd resurrects the Gateway after a crash or a kill, but a
/// CLEAN exit (POST /shutdown) stays exited - otherwise launchd would relaunch a Gateway
/// that was deliberately stopped.
///
/// Registration is a two-step: write the property list, then hand it to launchd with
/// "launchctl bootstrap gui/&lt;uid&gt;". Bootstrap also starts the agent immediately
/// (RunAtLoad applies at bootstrap time); when a already-running Gateway registers itself
/// at startup, that spawns a duplicate which exits on the port already being held.
/// </summary>
public static class GatewayLaunchdAutostart
{
    /// <summary>The launchd service label (also the property list file name).</summary>
    public const string Label = "com.devthrottle.cc-director-gateway";

    /// <summary>The user launch-agent property list path: ~/Library/LaunchAgents/{Label}.plist.</summary>
    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>
    /// The full property list for the given executable and arguments. Pure, for tests.
    /// Standard output and error go to logDir so a crash before FileLog starts is not silent.
    /// </summary>
    public static string PlistContent(string exePath, string? arguments, string logDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);

        var argElements = new StringBuilder();
        argElements.Append($"        <string>{Xml(exePath)}</string>\n");
        foreach (var arg in SplitArguments(arguments))
            argElements.Append($"        <string>{Xml(arg)}</string>\n");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
            {argElements.ToString().TrimEnd('\n')}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>StandardOutPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stdout.log"))}</string>
                <key>StandardErrorPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stderr.log"))}</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>
    /// Ensure the launch agent is registered and loaded for the given executable and
    /// arguments. Idempotent: returns true if a write or a launchd (re)bootstrap was
    /// performed, false if everything was already correct.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var logDir = Path.Combine(InstallLayout.Default().LogsDir, "gateway");
        var desired = PlistContent(exePath, arguments, logDir);
        EngineLog.Write($"[GatewayLaunchdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var plistUnchanged = File.Exists(PlistPath)
            && string.Equals(File.ReadAllText(PlistPath), desired, StringComparison.Ordinal);

        if (plistUnchanged && IsLoaded())
        {
            EngineLog.Write("[GatewayLaunchdAutostart] EnsureRegistered: already up to date and loaded");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(PlistPath, desired);
        EngineLog.Write($"[GatewayLaunchdAutostart] EnsureRegistered: wrote {PlistPath}");

        // A changed definition must be re-bootstrapped: launchd caches the loaded plist.
        if (IsLoaded())
        {
            // The Gateway itself calls this at startup, from inside this very job. A bootout
            // there kills the caller before it reaches bootstrap, and the job is gone until the
            // next login (product #3575). Leave the loaded job alone; launchd reads the new
            // definition at the next login or the next install.
            if (!MayReload(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME")))
            {
                EngineLog.Write("[GatewayLaunchdAutostart] EnsureRegistered: running inside the job - wrote new definition, not booting out");
                return true;
            }

            var (outExit, outText) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[GatewayLaunchdAutostart] bootout -> exit={outExit} {Trim(outText)}");
        }

        var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootstrap gui/{UserId()} \"{PlistPath}\"");
        if (exit != 0)
            throw new InvalidOperationException(
                $"launchctl bootstrap failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[GatewayLaunchdAutostart] EnsureRegistered: bootstrapped launch agent");
        return true;
    }

    /// <summary>
    /// Whether a process may boot out and re-bootstrap the job. launchd sets XPC_SERVICE_NAME to
    /// the job's label in every process the job starts (and its children inherit it), so a match
    /// means a bootout would kill the caller. Pure, for tests.
    /// </summary>
    internal static bool MayReload(string? xpcServiceName) =>
        !string.Equals(xpcServiceName, Label, StringComparison.Ordinal);

    /// <summary>The registered command line (ProgramArguments joined), or null when the property list does not exist.</summary>
    public static string? Registered()
    {
        if (!File.Exists(PlistPath)) return null;
        var content = File.ReadAllText(PlistPath);
        var strings = new List<string>();
        var start = content.IndexOf("<array>", StringComparison.Ordinal);
        var end = content.IndexOf("</array>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        var body = content[start..end];
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<string>", StringComparison.Ordinal) && trimmed.EndsWith("</string>", StringComparison.Ordinal))
                strings.Add(Unxml(trimmed["<string>".Length..^"</string>".Length]));
        }
        return strings.Count == 0 ? null : string.Join(' ', strings);
    }

    /// <summary>True if the launch-agent property list exists.</summary>
    public static bool IsRegistered() => File.Exists(PlistPath);

    /// <summary>
    /// Unload the launch agent from launchd and remove the property list.
    /// Returns true if anything was removed.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Unregister()
    {
        EngineLog.Write("[GatewayLaunchdAutostart] Unregister");
        var existed = File.Exists(PlistPath);

        // The Gateway's settings toggle calls this from inside the job, and a bootout there kills the caller
        // (product #3575). Removing the plist is enough to stop the start at the next login.
        var inJob = !MayReload(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME"));
        if (inJob)
            EngineLog.Write("[GatewayLaunchdAutostart] Unregister: running inside the job - removing the plist, not booting out");

        if (!inJob && IsLoaded())
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[GatewayLaunchdAutostart] bootout -> exit={exit} {Trim(text)}");
        }

        if (existed)
        {
            File.Delete(PlistPath);
            EngineLog.Write($"[GatewayLaunchdAutostart] Unregister: removed {PlistPath}");
        }
        return existed;
    }

    /// <summary>Whether launchd currently has the agent loaded in this user's gui domain.</summary>
    [SupportedOSPlatform("macos")]
    public static bool IsLoaded()
    {
        var (exit, _) = ProcessRunner.Run("/bin/launchctl", $"print gui/{UserId()}/{Label}");
        return exit == 0;
    }

    private static string UserId()
    {
        var (exit, output) = ProcessRunner.Run("/usr/bin/id", "-u");
        if (exit != 0 || !int.TryParse(output.Trim(), out var uid))
            throw new InvalidOperationException($"could not resolve the current user id (id -u exit {exit})");
        return uid.ToString();
    }

    /// <summary>
    /// Split the stored argument string into launchd ProgramArguments tokens, respecting double
    /// quotes so a value containing spaces (for example a path under "Application Support") stays a
    /// single token rather than being torn apart at the space, which would hand the Gateway a wrong
    /// path. Surrounding double quotes are stripped; unquoted runs split on whitespace. Simple
    /// flag/value arguments such as the Gateway's "--port 7878" are unaffected.
    /// </summary>
    private static IEnumerable<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) yield break;

        var token = new StringBuilder();
        var inQuotes = false;
        var started = false;
        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (started) { yield return token.ToString(); token.Clear(); started = false; }
            }
            else
            {
                token.Append(ch);
                started = true;
            }
        }
        if (started) yield return token.ToString();
    }

    private static string Xml(string value) => SecurityElement.Escape(value);

    private static string Unxml(string value) => value
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&amp;", "&");

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
