using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Storage;

/// <summary>One place the OS might be saving screenshots: the folder plus a plain-English reason.</summary>
public sealed record ScreenshotFolderCandidate(string Path, string Provenance);

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
        var result = DetectCandidates().FirstOrDefault()?.Path;
        FileLog.Write($"[ScreenshotLocator] Detect -> {result ?? "(none)"}");
        return result;
    }

    /// <summary>
    /// Every folder the OS might be saving screenshots to, best-first, each with a plain-English
    /// provenance the wizard can show. Only folders that actually exist are returned, deduplicated.
    ///
    ///   - Windows 10/11: the Screenshots KNOWN FOLDER first (via SHGetKnownFolderPath) - it is its
    ///     own known folder, movable independently of Pictures, and it is where Win+PrtScn and the
    ///     Windows 11 Snipping Tool auto-save land, including when OneDrive has redirected it. Then
    ///     the OneDrive Pictures\Screenshots copy (OneDrive's "save screenshots" setting writes there
    ///     even without a folder redirect), then the classic Pictures\Screenshots.
    ///   - macOS: the user's screencapture location setting, else the Desktop (the macOS default).
    ///   - Linux: Pictures/Screenshots.
    /// </summary>
    public static IReadOnlyList<ScreenshotFolderCandidate> DetectCandidates()
    {
        var raw = OperatingSystem.IsWindows() ? WindowsCandidates()
                : OperatingSystem.IsMacOS() ? MacCandidates()
                : UnixCandidates();

        var result = new List<ScreenshotFolderCandidate>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var c in raw)
        {
            if (string.IsNullOrWhiteSpace(c.Path) || !Directory.Exists(c.Path)) continue;
            var full = Path.GetFullPath(c.Path);
            if (seen.Add(full))
                result.Add(c with { Path = full });
        }
        FileLog.Write($"[ScreenshotLocator] DetectCandidates -> {result.Count} folder(s)");
        return result;
    }

    /// <summary>Count the images directly in a folder - the proof line ("312 images"). Never throws.</summary>
    public static int CountImages(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Count(f => IsImageFile(f));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ScreenshotLocator] CountImages failed for {directory} FAILED: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Whether the file name looks like a screenshot image. Pure - unit-tested.</summary>
    public static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ScreenshotFolderCandidate> WindowsCandidates()
    {
        // The authoritative answer on Windows 10 AND 11: the Screenshots known folder itself.
        // Covers a user-moved folder and OneDrive's known-folder redirect.
        var known = TryGetWindowsScreenshotsKnownFolder();
        if (known is not null)
            yield return new ScreenshotFolderCandidate(known, "Where Windows saves Win+PrtScn and Snipping Tool captures");

        // OneDrive's "save screenshots to OneDrive" writes here even when the known folder was
        // never redirected. Both consumer and business env variables are probed.
        foreach (var oneDrive in new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial") })
        {
            if (!string.IsNullOrWhiteSpace(oneDrive))
                yield return new ScreenshotFolderCandidate(Path.Combine(oneDrive, "Pictures", "Screenshots"), "Your OneDrive screenshot backup");
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrEmpty(pictures))
            yield return new ScreenshotFolderCandidate(Path.Combine(pictures, "Screenshots"), "The default Windows screenshots folder");
    }

    private static IEnumerable<ScreenshotFolderCandidate> MacCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = ParseMacScreencaptureLocation(RunDefaultsScreencaptureLocation(), home);
        if (configured is not null)
            yield return new ScreenshotFolderCandidate(configured, "Your macOS screenshot location setting");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
            yield return new ScreenshotFolderCandidate(desktop, "The macOS default - screenshots land on the Desktop");
    }

    private static IEnumerable<ScreenshotFolderCandidate> UnixCandidates()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrEmpty(pictures))
            yield return new ScreenshotFolderCandidate(Path.Combine(pictures, "Screenshots"), "The Pictures/Screenshots folder");
    }

    // ---- Windows Screenshots known folder (FOLDERID_Screenshots) -----------------------------------

    private static readonly Guid FolderIdScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>
    /// Resolve the Screenshots known folder via the shell - the same answer Explorer gives. Returns
    /// null when the folder is not registered (it is created lazily on first Win+PrtScn) or on any
    /// error; callers fall through to the conventional locations.
    /// </summary>
    private static string? TryGetWindowsScreenshotsKnownFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var ptr = IntPtr.Zero;
        try
        {
            // KF_FLAG_DONT_VERIFY (0x4000): return the configured path even if the shell has not
            // touched it lately; existence is checked by the caller like every other candidate.
            var hr = SHGetKnownFolderPath(FolderIdScreenshots, 0x4000, IntPtr.Zero, out ptr);
            return hr == 0 ? Marshal.PtrToStringUni(ptr) : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ScreenshotLocator] SHGetKnownFolderPath FAILED: {ex.Message}");
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
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
            FileLog.Write($"[ScreenshotLocator] defaults read FAILED: {ex.Message}");
            return null;
        }
    }
}
