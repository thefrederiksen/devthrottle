using System.Diagnostics;
using System.IO;

namespace CcDirectorSetup.Services;

/// <summary>
/// Detects an existing Director install. The canonical location is
/// app\cc-director.exe (master spec: docs/install/INSTALLATION.md); the retired
/// bin\cc-director.exe is still recognized so a pre-migration install reads as
/// "installed" (the next install migrates it to app\).
/// </summary>
public static class InstallDetector
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cc-director");

    private static readonly string AppExe = Path.Combine(Root, "app", "cc-director.exe");
    private static readonly string LegacyExe = Path.Combine(Root, "bin", "cc-director.exe");

    private static string? InstalledExe()
    {
        if (File.Exists(AppExe)) return AppExe;
        if (File.Exists(LegacyExe)) return LegacyExe;
        return null;
    }

    public static bool IsInstalled() => InstalledExe() != null;

    public static string? GetInstalledVersion()
    {
        var exe = InstalledExe();
        if (exe == null) return null;
        return FileVersionInfo.GetVersionInfo(exe).ProductVersion;
    }
}
