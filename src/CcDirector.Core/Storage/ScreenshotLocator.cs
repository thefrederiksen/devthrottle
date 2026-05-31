using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>
/// Detects where the operating system saves screenshots, cross-platform, so the Settings page
/// "Detect" button can fill the screenshots folder without the user hunting for it.
///
///   - Windows: the Pictures known folder's "Screenshots" subfolder (where Win+PrtScn lands).
///     GetFolderPath follows a OneDrive redirect, so this resolves to the OneDrive copy when
///     Pictures is backed up there.
///   - macOS: the user-configurable screencapture location (read via `defaults`), else the
///     Desktop (the macOS default).
///   - Linux: the Pictures/Screenshots folder if present.
///
/// Returns null when no folder is found, so the caller surfaces that truthfully rather than
/// inventing a path. Distinct from <see cref="CcStorage.Screenshots"/>, which resolves the
/// EFFECTIVE folder (honoring the config override and creating it); this reports the OS
/// location so the user can choose to point the config at it.
/// </summary>
public static class ScreenshotLocator
{
    /// <summary>Detect the OS screenshots folder for the current platform, or null if none found.</summary>
    public static string? Detect()
    {
        var result = OperatingSystem.IsWindows() ? DetectWindows()
                   : OperatingSystem.IsMacOS() ? DetectMac()
                   : DetectUnix();
        FileLog.Write($"[ScreenshotLocator] Detect -> {result ?? "(none)"}");
        return result;
    }

    private static string? DetectWindows()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures)) return null;
        var dir = Path.Combine(pictures, "Screenshots");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? DetectMac()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // macOS lets the user move the screenshot location; read it from the defaults system.
        var configured = ParseMacScreencaptureLocation(RunDefaultsScreencaptureLocation(), home);
        if (configured is not null && Directory.Exists(configured))
            return configured;

        // Default location is the Desktop.
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Directory.Exists(desktop) ? desktop : null;
    }

    private static string? DetectUnix()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures)) return null;
        var dir = Path.Combine(pictures, "Screenshots");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Parse the value printed by <c>defaults read com.apple.screencapture location</c>,
    /// expanding a leading <c>~</c> against <paramref name="homeDir"/>. Returns null when the
    /// output is empty (the key is unset). Pure - unit-tested.
    /// </summary>
    public static string? ParseMacScreencaptureLocation(string? defaultsStdout, string homeDir)
    {
        if (string.IsNullOrWhiteSpace(defaultsStdout)) return null;

        var value = defaultsStdout.Trim().Trim('"').Trim();
        if (value.Length == 0) return null;

        // A macOS path: join with '/' explicitly (Path.Combine would use '\' on a Windows host,
        // which matters because this parser is unit-tested cross-platform).
        if (value == "~") return homeDir;
        if (value.StartsWith("~/", StringComparison.Ordinal))
            return $"{homeDir.TrimEnd('/')}/{value[2..]}";
        return value;
    }

    private static string? RunDefaultsScreencaptureLocation()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "defaults",
                Arguments = "read com.apple.screencapture location",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            // Non-zero exit means the key is not set - no custom location, use the default.
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ScreenshotLocator] defaults read failed: {ex.Message}");
            return null;
        }
    }
}
