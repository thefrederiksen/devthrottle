using System.Runtime.InteropServices;

namespace CcDirectorSetup.Services;

public static class ShortcutCreator
{
    public static void CreateStartMenuShortcut(string exePath)
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
}
