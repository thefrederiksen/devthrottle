using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The CC Launcher tray app's per-user autostart entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// Per-user (HKCU) is the correct scope: the launcher only does useful work while the user is
/// logged in. Lives in the engine (next to <see cref="GatewayAutostart"/>) so the installer, the
/// uninstaller, and the tray app itself all agree on one value name and command-line format.
/// </summary>
public static class LauncherAutostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run-key value name.</summary>
    public const string ValueName = "CcDirectorLauncher";

    /// <summary>The full command line for the Run-key value. Pure, for tests.</summary>
    public static string CommandLine(string exePath, string? arguments = null) =>
        string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";

    /// <summary>
    /// Ensure the Run-key value is exactly <see cref="CommandLine"/> for the given exe and
    /// arguments. Idempotent: returns true if a write was performed (first registration or
    /// command change), false if already correct.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var desired = CommandLine(exePath, arguments);
        EngineLog.Write($"[LauncherAutostart] EnsureRegistered: {desired}");

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
            throw new InvalidOperationException($"Could not open or create HKCU\\{RunKeyPath}");

        var current = key.GetValue(ValueName) as string;
        if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            EngineLog.Write("[LauncherAutostart] EnsureRegistered: already up to date");
            return false;
        }

        key.SetValue(ValueName, desired, RegistryValueKind.String);
        EngineLog.Write("[LauncherAutostart] EnsureRegistered: wrote Run-key value");
        return true;
    }

    /// <summary>The current Run-key command line, or null when not registered.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Registered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>True if the autostart Run-key value exists.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsRegistered() => Registered() is not null;

    /// <summary>Remove the autostart Run-key value if present. Returns true if removed.</summary>
    [SupportedOSPlatform("windows")]
    public static bool Unregister()
    {
        EngineLog.Write("[LauncherAutostart] Unregister");
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not string) return false;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }
}
