using CcDirector.Core.Utilities;
using Microsoft.Win32;

namespace CcDirector.GatewayApp;

/// <summary>
/// Manages the per-user autostart entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// Per-user (HKCU) is the correct scope: the gateway only does useful work while the
/// user is logged in, so it must start in the user's interactive session - not as a
/// machine service. Writing is idempotent: the value is only touched when missing or
/// pointing at a different exe, so a normal startup is a no-op once registered.
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CcDirectorGateway";

    /// <summary>
    /// Ensure the Run-key value points at <paramref name="exePath"/>. Returns true if a
    /// write was performed (first registration or path change), false if already correct.
    /// </summary>
    public static bool EnsureRegistered(string exePath)
    {
        FileLog.Write($"[Autostart] EnsureRegistered: exePath={exePath}");

        var desired = $"\"{exePath}\"";
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
            throw new InvalidOperationException($"Could not open or create HKCU\\{RunKeyPath}");

        var current = key.GetValue(ValueName) as string;
        if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write("[Autostart] EnsureRegistered: already up to date");
            return false;
        }

        key.SetValue(ValueName, desired, RegistryValueKind.String);
        FileLog.Write("[Autostart] EnsureRegistered: wrote Run-key value");
        return true;
    }

    /// <summary>True if the autostart Run-key value exists.</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Remove the autostart Run-key value if present.</summary>
    public static void Unregister()
    {
        FileLog.Write("[Autostart] Unregister");
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is string)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
