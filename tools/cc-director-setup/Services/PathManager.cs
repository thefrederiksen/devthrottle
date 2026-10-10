using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CcDirectorSetup.Services;

public static class PathManager
{
    public static bool AddToPath(string directory)
    {
        SetupLog.Write($"[PathManager] AddToPath: directory={directory}");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (key == null)
            {
                SetupLog.Write("[PathManager] AddToPath FAILED: could not open Environment key");
                return false;
            }

            var currentPath = key.GetValue("Path", "") as string ?? "";
            var entries = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .ToList();

            // Check case-insensitive if already present
            if (entries.Any(e => string.Equals(e, directory, StringComparison.OrdinalIgnoreCase)))
            {
                SetupLog.Write("[PathManager] AddToPath: already in PATH");
                return false;
            }

            entries.Add(directory);
            var newPath = string.Join(";", entries);
            key.SetValue("Path", newPath, RegistryValueKind.ExpandString);

            BroadcastSettingChange();

            SetupLog.Write("[PathManager] AddToPath: success");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[PathManager] AddToPath FAILED: {ex.Message}");
            _ = WizardProgressReport.Error("path", $"Windows setup wizard could not add the tools folder to PATH: {ex.GetType().Name}: {ex.Message}", ex);
            return false;
        }
    }

    public static bool IsInPath(string directory)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            if (key == null) return false;

            var currentPath = key.GetValue("Path", "") as string ?? "";
            var entries = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim());

            return entries.Any(e => string.Equals(e, directory, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Not reported: a PATH that cannot be read counts as not holding the folder; AddToPath then writes it and reports its own failure.
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private static void BroadcastSettingChange()
    {
        const uint WM_SETTINGCHANGE = 0x1A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        var HWND_BROADCAST = new IntPtr(0xFFFF);

        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 5000, out _);

        SetupLog.Write("[PathManager] BroadcastSettingChange: sent WM_SETTINGCHANGE");
    }
}
