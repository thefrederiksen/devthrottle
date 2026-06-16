using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CcDirectorSetup.Services;

public static class ShortcutCreator
{
    public static void CreateShortcut(string exePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            CreateStartMenuShortcutWindows(exePath);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            CreateMacOSAlias(exePath);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateStartMenuShortcutWindows(string exePath)
    {
        SetupLog.Write($"[ShortcutCreator] CreateStartMenuShortcut: {exePath}");

        var startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        var shortcutPath = Path.Combine(startMenuDir, "DevThrottle.lnk");

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("WScript.Shell COM object not available");

            var shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])!;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [exePath]);
            shortcutType.InvokeMember("WorkingDirectory",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [Path.GetDirectoryName(exePath)]);
            shortcutType.InvokeMember("IconLocation",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [$"{exePath},0"]);
            shortcutType.InvokeMember("Description",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                ["DevThrottle"]);
            shortcutType.InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            SetupLog.Write($"[ShortcutCreator] Shortcut created: {shortcutPath}");
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[ShortcutCreator] CreateStartMenuShortcut FAILED: {ex.Message}");
            throw;
        }
    }

    private static void CreateMacOSAlias(string exePath)
    {
        SetupLog.Write($"[ShortcutCreator] CreateMacOSAlias: {exePath}");

        try
        {
            // Create a symlink in /usr/local/bin for easy terminal access
            var linkPath = "/usr/local/bin/cc-director";
            if (!File.Exists(linkPath) && !Directory.Exists(Path.GetDirectoryName(linkPath)!))
                return;

            File.CreateSymbolicLink(linkPath, exePath);
            SetupLog.Write($"[ShortcutCreator] Symlink created: {linkPath}");
        }
        catch (Exception ex)
        {
            // Not critical - user may not have permissions for /usr/local/bin
            SetupLog.Write($"[ShortcutCreator] CreateMacOSAlias FAILED (non-critical): {ex.Message}");
        }
    }
}
